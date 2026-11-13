using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using KnA.Core.Encoding;
using KnA.Core.Localization;
using KnA.Core.Media;
using KnA.Core.Queue;
using KnA.Core.Settings;

namespace KnA.App.Views;

/// <summary>
/// Per-file editor: tracks (with external files and delays), visual trim, filters (+ VapourSynth/AviSynth),
/// output settings and preview of the final result. Every change is saved to the job and re-validated (debounced).
/// Mirrors wwwroot/editor.js.
/// </summary>
public sealed class JobEditorWindow : Window
{
    private static readonly string[] Kinds = ["video", "audio", "subtitle", "attachment"];

    private readonly QueueService _queue;
    private readonly QueueJob _job;
    private JobSettings _settings;
    private JobEdit _edit;
    private JobAnalysis? _a;
    private double _playhead;
    private bool _final = true;
    /// <summary>Kept segments being edited in the Trim tab (sorted) and the one the handles move.</summary>
    private List<TrimSegment>? _parts;
    private int _seg;

    private readonly StackPanel _issues = new() { Spacing = 4 };
    private readonly TextBlock _saving = new() { Classes = { "muted" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly TabControl _tabs = new();
    private readonly ContentControl _body = new();
    private CancellationTokenSource? _saveCts;
    private CancellationTokenSource _previewCts = new();
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private bool _rerenderAfterSave;

    public JobEditorWindow(QueueService queue, QueueJob job, string tab = "tracks")
    {
        _queue = queue;
        _job = job;
        _settings = job.Settings;
        _edit = job.Edit;
        Title = job.FileName + " — KnA";
        Width = 1180; Height = 820; MinWidth = 860; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        string[] tabs = ["tracks", "trim", "filters", "output", "preview"];
        string[] names = [L.T("Tracks"), L.T("Trim"), L.T("Filters"), L.T("Output"), L.T("Preview")];
        for (int i = 0; i < tabs.Length; i++) _tabs.Items.Add(new TabItem { Header = names[i], Tag = tabs[i], FontSize = 15 });
        _tabs.SelectedIndex = Math.Max(0, Array.IndexOf(tabs, tab));
        _tabs.SelectionChanged += (_, _) => { if (_a is not null) RenderTab(); };

        var header = new DockPanel { Margin = new Thickness(20, 14, 20, 0) };
        var title = new StackPanel { Children = { new TextBlock { Text = job.FileName, FontWeight = FontWeight.SemiBold, FontSize = 16 }, Ui.Muted(job.Folder) } };
        DockPanel.SetDock(_saving, Dock.Right);
        header.Children.Add(_saving);
        header.Children.Add(title);

        var top = new StackPanel { Children = { header, new Border { Margin = new Thickness(12, 6, 12, 0), Child = _tabs }, new Border { Margin = new Thickness(20, 4, 20, 8), Child = _issues } } };
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(new ScrollViewer { Content = new Border { Padding = new Thickness(20, 4, 20, 20), Child = _body } });
        Content = root;
        _body.Content = Ui.Muted(L.T("Loading…"));

        _debounce.Tick += async (_, _) => { _debounce.Stop(); await SaveNowAsync(_rerenderAfterSave); _rerenderAfterSave = false; };
        Opened += async (_, _) => await LoadAsync();
        Closed += (_, _) => { _previewCts.Cancel(); if (_debounce.IsEnabled) { _debounce.Stop(); _ = SaveNowAsync(false); } };
    }

    private string CurrentTab => (_tabs.SelectedItem as TabItem)?.Tag as string ?? "tracks";

    private async Task LoadAsync()
    {
        try
        {
            _a = await Task.Run(() => _queue.AnalyzeAsync(_job));
            var d = _a.Source.Duration.TotalSeconds;
            _playhead = _edit.IsMultiSegment ? _edit.KeptParts(d).FirstOrDefault().Start : _edit.TrimStart ?? Math.Min(d / 3, 60);
            RenderIssues();
            RenderTab();
        }
        catch (Exception ex)
        {
            _body.Content = Ui.Note(ex.Message, true);
        }
    }

    // ------------------------------------------------------------------ saving

    private void Save(bool rerender = false)
    {
        _rerenderAfterSave |= rerender;
        _debounce.Stop();
        _debounce.Start();
    }

    private async Task SaveNowAsync(bool rerender)
    {
        _saveCts?.Cancel();
        var cts = _saveCts = new CancellationTokenSource();
        _saving.Text = L.T("Saving…");
        try
        {
            var edit = _edit; var settings = _settings;
            var a = await Task.Run(() => _queue.SetEditAsync(_job.Id, edit, cts.Token, settings));
            if (cts.IsCancellationRequested) return;
            _a = a;
            if (_edit.Tracks is not null) _edit = _edit with { Tracks = [.. a.Tracks] };
            RenderIssues();
            if (rerender) RenderTab();
            _saving.Text = L.T("Saved");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _saving.Text = "";
            _issues.Children.Insert(0, Ui.Note("✖ " + ex.Message, true));
        }
    }

    private void RenderIssues()
    {
        _issues.Children.Clear();
        if (_a is null) return;
        foreach (var i in _a.Issues) _issues.Children.Add(Ui.Note((i.IsError ? "✖ " : "⚠ ") + i.Message, i.IsError));
        var info = new List<string>();
        if (!string.IsNullOrEmpty(_a.Container)) info.Add($"{L.T("Container")}: {_a.Container.ToUpperInvariant()}");
        if (_a.HdrSummary is { Length: > 0 } h && h != "SDR") info.Add("HDR: " + h);
        if (_a.OutputDuration > TimeSpan.Zero) info.Add($"{L.T("Output duration")}: {Ui.Short(_a.OutputDuration)}");
        if (info.Count > 0) _issues.Children.Add(Ui.Note(string.Join(" · ", info)));
    }

    private void RenderTab()
    {
        _previewCts.Cancel();
        _previewCts = new CancellationTokenSource();
        _body.Content = CurrentTab switch
        {
            "trim" => TrimTab(),
            "filters" => FiltersTab(),
            "output" => OutputTab(),
            "preview" => PreviewTab(),
            _ => TracksTab(),
        };
    }

    // ------------------------------------------------------------------ tracks

    private List<TrackPlan> Tracks => _edit.Tracks ?? [.. _a!.Tracks];

    private List<TrackPlan> Materialize()
    {
        if (_edit.Tracks is null) _edit = _edit with { Tracks = [.. _a!.Tracks] };
        return _edit.Tracks!;
    }

    private void UpdateTrack(int i, Func<TrackPlan, TrackPlan> f, bool rerender = false)
    {
        var list = Materialize();
        list[i] = f(list[i]);
        if (rerender) RenderTab();
        Save();
    }

    private IReadOnlyDictionary<string, MediaInfo> AllMedia()
    {
        var d = new Dictionary<string, MediaInfo>(_a!.External) { [_job.SourcePath] = _a.Source };
        return d;
    }

    private static string Details(StreamBase? s) => s switch
    {
        VideoStream v => $"{v.Width}×{v.Height} · {v.FrameRate:0.###} fps" + (v.BitDepth > 8 ? $" · {v.BitDepth}-bit" : "") + (v.Hdr.IsHdr ? " · " + v.Hdr.Description : "") + (v.Rotation != 0 ? $" · ↻{v.Rotation}°" : ""),
        AudioStream au => $"{au.Channels} ch" + (au.SampleRate > 0 ? $" · {au.SampleRate / 1000.0:0.#} kHz" : "") + (au.IsLossless ? " · lossless" : ""),
        SubtitleStream ss => ss.IsBitmap ? "bitmap" : "text",
        AttachmentStream at => at.MimeType ?? "",
        _ => "",
    };

    private static string KindLabel(string k) => k switch
    {
        "video" => L.T("Video"), "audio" => L.T("Audio"), "subtitle" => L.T("Subtitles"), "attachment" => L.T("Attachments"), _ => k,
    };

    /// <summary>Choices of the "Output" column: copy or convert this track (null = follow the profile).</summary>
    private static IEnumerable<(string? Value, string Text)> OutputChoices(string kind) => kind switch
    {
        "video" => [(null, L.T("Profile (encode)")), ("copy", L.T("Copy (no re-encoding)"))],
        "audio" => new (string?, string)[] { (null, L.T("Job setting")) }
            .Concat(Enum.GetValues<AudioCodec>().Where(c => c != AudioCodec.Auto).Select(c => ((string?)c.ToString(), c.Display()))),
        "subtitle" => new (string?, string)[] { (null, L.T("Automatic")) }
            .Concat(Containers.SubtitleOutputs.Select(o => ((string?)o.Value, o.Value == "copy" ? L.T("Copy (no re-encoding)") : o.Label))),
        _ => [],
    };

    private Control TracksTab()
    {
        var list = Tracks;
        var media = AllMedia();
        var p = _settings.ProfileDefinition;
        bool outputs = p.EncodesVideo || p.IsRemux;
        var root = new StackPanel { Spacing = 12 };

        var bar = Ui.Row(8,
            Ui.Button(L.T("Add external files…"), AddExternalAsync),
            Ui.Button(L.T("Reset to automatic"), () => { _edit = _edit with { Tracks = null }; Save(true); }));
        bar.Children.Add(Ui.Muted(_edit.Tracks is not null ? L.T("Custom track plan.") : L.T("Automatic plan (from the subtitle mode). Any change makes it custom.")));
        ((TextBlock)bar.Children[^1]).VerticalAlignment = VerticalAlignment.Center;
        root.Children.Add(bar);

        if (_edit.ExternalFiles.Count > 0)
        {
            var wrap = new WrapPanel();
            foreach (var f in _edit.ExternalFiles)
            {
                var rm = new Button { Content = "✕", Classes = { "ghost" }, Padding = new Thickness(6, 0) };
                rm.Click += (_, _) =>
                {
                    _edit = _edit with
                    {
                        ExternalFiles = [.. _edit.ExternalFiles.Where(x => x != f)],
                        Tracks = _edit.Tracks?.Where(x => x.File != f).ToList(),
                    };
                    Save(true);
                };
                wrap.Children.Add(new Border
                {
                    Classes = { "chip" }, Margin = new Thickness(0, 0, 6, 6),
                    Child = Ui.Row(4, new TextBlock { Text = Path.GetFileName(f), VerticalAlignment = VerticalAlignment.Center }, rm),
                });
                ToolTip.SetTip(wrap.Children[^1], f);
            }
            root.Children.Add(wrap);
        }
        root.Children.Add(Ui.Muted(p.IsDemux
            ? L.T("Demux: the ticked tracks, attachments and chapters are extracted to separate files.")
            : L.T("Delays shift a single track (positive = later). The trim is applied afterwards, on the final timeline.")));

        foreach (var k in Kinds)
        {
            var rows = list.Select((t, i) => (t, i)).Where(x => x.t.Kind == k).ToList();
            if (rows.Count == 0 && k != "attachment") continue;
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,140,*,64,170,96,Auto,Auto,Auto,Auto,Auto,Auto"), ColumnSpacing = 8, RowSpacing = 6 };
            void Head(int col, string text) { var tb = Ui.Muted(text, 11.5); Grid.SetColumn(tb, col); grid.Children.Add(tb); }
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Head(0, L.T("Use")); Head(1, L.T("Source")); Head(2, L.T("Stream"));
            if (k != "attachment") { Head(3, L.T("Language")); Head(5, L.T("Delay (ms)")); }
            Head(4, k == "attachment" ? L.T("File name") : L.T("Title"));
            if (outputs && k != "attachment") Head(6, L.T("Output"));
            if (outputs && k == "audio") Head(7, "kb/s");
            if (k is "audio" or "subtitle") { Head(8, L.T("Default")); Head(9, L.T("Forced")); }
            if (k == "subtitle" && p.UsesVideoFilters) Head(10, L.T("Burn"));

            int r = 1;
            foreach (var (t, i) in rows)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var st = TrackPlanner.Lookup(t, media);
                void Put(Control c, int col) { Grid.SetRow(c, r); Grid.SetColumn(c, col); c.VerticalAlignment = VerticalAlignment.Center; grid.Children.Add(c); }
                int idx = i;
                bool standalone = TrackPlanner.IsStandaloneFile(t);
                Put(Ui.Check("", t.Include, v => UpdateTrack(idx, x => x with { Include = v })), 0);
                var src = new TextBlock { Text = t.File == _job.SourcePath ? L.T("Source") : Path.GetFileName(t.File), TextTrimming = TextTrimming.CharacterEllipsis };
                ToolTip.SetTip(src, t.File);
                Put(src, 1);
                Put(new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = (t.Stream >= 0 ? "#" + t.Stream + " " : "") + (t.Codec ?? st?.CodecName ?? ""), FontWeight = FontWeight.SemiBold },
                        Ui.Muted(standalone ? TrackPlanner.MimeType(t.File) : Details(st), 11.5),
                    },
                }, 2);
                if (k != "attachment")
                {
                    Put(Ui.Text(t.Language, v => UpdateTrack(idx, x => x with { Language = string.IsNullOrWhiteSpace(v) ? null : v.Trim() }), "und"), 3);
                    var delay = Ui.Number(t.DelayMs, -600000, 600000, 10, v => UpdateTrack(idx, x => x with { DelayMs = v }), "0");
                    delay.ShowButtonSpinner = false;
                    Put(delay, 5);
                }
                var title = Ui.Text(t.Title, v => UpdateTrack(idx, x => x with { Title = string.IsNullOrEmpty(v) ? null : v }));
                if (k == "attachment" && !standalone) title.IsReadOnly = true;   // attachments of the file keep their name
                Put(title, 4);
                if (outputs && k != "attachment")
                {
                    var combo = Ui.Combo(OutputChoices(k), t.Output, v => UpdateTrack(idx, x => x with { Output = v }, rerender: k == "audio"));
                    combo.MinWidth = 150;
                    Put(combo, 6);
                }
                if (outputs && k == "audio")
                {
                    bool copies = string.Equals(t.Output, nameof(AudioCodec.Copy), StringComparison.OrdinalIgnoreCase);
                    var kbps = Ui.Number(t.Bitrate ?? 0, 0, 1536, 16, v => UpdateTrack(idx, x => x with { Bitrate = v > 0 ? (int)v : null }), "0");
                    kbps.ShowButtonSpinner = false;
                    kbps.Width = 80;
                    kbps.IsEnabled = !copies;
                    ToolTip.SetTip(kbps, L.T("Per stereo pair; 0 = job setting"));
                    Put(kbps, 7);
                }
                if (k is "audio" or "subtitle")
                {
                    Put(Ui.Check("", t.Default, v =>
                    {
                        var l = Materialize();
                        for (int j = 0; j < l.Count; j++)
                            if (j == idx) l[j] = l[j] with { Default = v };
                            else if (v && l[j].Kind == k) l[j] = l[j] with { Default = false };
                        RenderTab(); Save();
                    }), 8);
                    Put(Ui.Check("", t.Forced, v => UpdateTrack(idx, x => x with { Forced = v })), 9);
                }
                if (k == "subtitle" && p.UsesVideoFilters)
                {
                    var rb = new RadioButton { GroupName = "burn" + _job.Id, IsChecked = t.Burn };
                    rb.IsCheckedChanged += (_, _) =>
                    {
                        if (rb.IsChecked != true) return;
                        var l = Materialize();
                        for (int j = 0; j < l.Count; j++) if (l[j].Kind == "subtitle") l[j] = l[j] with { Burn = j == idx };
                        Save();
                    };
                    Put(rb, 10);
                }
                var up = new Button { Content = "↑", Classes = { "ghost" } };
                var down = new Button { Content = "↓", Classes = { "ghost" } };
                up.Click += (_, _) => MoveTrack(idx, -1);
                down.Click += (_, _) => MoveTrack(idx, 1);
                var tools = Ui.Row(0, up, down);
                if (standalone)
                {
                    var rm = new Button { Content = "✕", Classes = { "ghost" } };
                    ToolTip.SetTip(rm, L.T("Remove"));
                    rm.Click += (_, _) =>
                    {
                        var l = Materialize();
                        l.RemoveAt(idx);
                        _edit = _edit with { ExternalFiles = [.. _edit.ExternalFiles.Where(f => f != t.File || l.Any(x => x.File == f))] };
                        RenderTab(); Save(true);
                    };
                    tools.Children.Add(rm);
                }
                Put(tools, 11);
                r++;
            }
            var section = new StackPanel { Spacing = 6, Children = { Ui.H2(KindLabel(k)) } };
            if (k == "attachment")
            {
                var addAtt = Ui.Button(L.T("Add attachments…"), AddAttachmentsAsync);
                section.Children[0] = new DockPanel { Children = { WithDock(addAtt, Dock.Right), Ui.H2(KindLabel(k)) } };
                if (rows.Count == 0) section.Children.Add(Ui.Muted(L.T("No attachments. Fonts, covers or any other file can be added.")));
                else if (!p.IsDemux) section.Children.Add(Ui.Muted(L.T("Untick an attachment of the file to leave it out; added files can be removed with ✕. Attachments need MKV.")));
            }
            if (k == "audio" && !p.UsesAudio && !p.IsDemux) section.Children.Add(Ui.Note(L.T("This profile has no audio output.")));
            if (rows.Count > 0) section.Children.Add(grid);
            if (k == "subtitle" && p.UsesVideoFilters && rows.Any(x => x.t.Burn))
                section.Children.Add(Ui.Button(L.T("Don't burn any subtitle"), () =>
                {
                    var l = Materialize();
                    for (int j = 0; j < l.Count; j++) l[j] = l[j] with { Burn = false };
                    RenderTab(); Save();
                }));
            root.Children.Add(Ui.Card(section));
        }
        if (!p.IsKeyframes && !p.IsImages) root.Children.Add(Ui.Card(ChaptersSection()));
        return root;
    }

    private static Control WithDock(Control c, Dock d) { DockPanel.SetDock(c, d); return c; }

    private async Task AddAttachmentsAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = L.T("Add attachments…"), AllowMultiple = true });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().Where(p => p != _job.SourcePath).ToList();
        if (paths.Count == 0) return;
        var l = Materialize();
        foreach (var path in paths.Where(path => !l.Any(t => TrackPlanner.IsStandaloneFile(t) && t.File == path)))
        {
            int last = l.FindLastIndex(t => t.Kind == "attachment");
            if (last < 0) l.Add(TrackPlanner.Attachment(path)); else l.Insert(last + 1, TrackPlanner.Attachment(path));
        }
        await SaveNowAsync(true);
    }

    // ------------------------------------------------------------------ chapters

    private List<ChapterMark> ChapterList => _edit.Chapters ?? Chapters.FromSource(_a!.Source);

    private List<ChapterMark> MaterializeChapters()
    {
        if (_edit.Chapters is null) _edit = _edit with { Chapters = Chapters.FromSource(_a!.Source) };
        return _edit.Chapters!;
    }

    /// <summary>Adds a chapter (sorted) and saves.</summary>
    private void AddChapter(double at)
    {
        var l = MaterializeChapters();
        l.Add(new ChapterMark(Math.Max(0, at), Chapters.DefaultTitle(l.Count + 1)));
        l.Sort((a, b) => a.Start.CompareTo(b.Start));
        Save(true);
    }

    private Control ChaptersSection()
    {
        var dur = _a!.Source.Duration.TotalSeconds;
        var list = ChapterList;
        var box = new StackPanel { Spacing = 8, Children = { Ui.H2(L.T("Chapters")) } };
        box.Children.Add(Ui.Toggle(L.T("Keep chapters"), _edit.KeepChapters, v => { _edit = _edit with { KeepChapters = v }; Save(true); }));
        if (!_edit.KeepChapters) return box;
        box.Children.Add(Ui.Muted(_edit.Chapters is null
            ? L.T("The chapters of the file ({0}). Any change makes them custom.", list.Count)
            : L.T("Edited chapters (times on the original timeline: cuts move them automatically).")));
        if (list.Count > 0)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("40,150,*,Auto"), ColumnSpacing = 8, RowSpacing = 6 };
            for (int i = 0; i < list.Count; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                int idx = i;
                var n = Ui.Muted($"{i + 1}.");
                var start = new TextBox { Text = Ui.Stamp(list[i].Start), FontFamily = Ui.Mono, Watermark = "00:00:00.000" };
                start.LostFocus += (_, _) =>
                {
                    if (Ui.ParseStamp(start.Text) is not { } v) { start.Text = Ui.Stamp(ChapterList[idx].Start); return; }
                    var l = MaterializeChapters();
                    if (Math.Abs(l[idx].Start - v) < 0.0005) return;
                    l[idx] = l[idx] with { Start = Math.Clamp(v, 0, Math.Max(dur, v)) };
                    l.Sort((a, b) => a.Start.CompareTo(b.Start));
                    Save(true);
                };
                var title = Ui.Text(list[i].Title, v => { var l = MaterializeChapters(); l[idx] = l[idx] with { Title = string.IsNullOrWhiteSpace(v) ? null : v }; Save(); });
                var rm = new Button { Content = "✕", Classes = { "ghost" } };
                ToolTip.SetTip(rm, L.T("Remove"));
                rm.Click += (_, _) => { MaterializeChapters().RemoveAt(idx); Save(true); };
                foreach (var (c, col) in new (Control, int)[] { (n, 0), (start, 1), (title, 2), (rm, 3) })
                {
                    Grid.SetRow(c, i); Grid.SetColumn(c, col); c.VerticalAlignment = VerticalAlignment.Center; grid.Children.Add(c);
                }
            }
            box.Children.Add(grid);
        }
        else box.Children.Add(Ui.Muted(L.T("No chapters.")));
        box.Children.Add(Ui.Row(8,
            Ui.Button(L.T("Add chapter"), () => AddChapter(list.Count == 0 ? 0 : Math.Min(list[^1].Start + 300, Math.Max(0, dur - 1)))),
            Ui.Button(L.T("Every 5 minutes"), () =>
            {
                var l = new List<ChapterMark>();
                for (double t = 0; t < Math.Max(dur, 1); t += 300) l.Add(new ChapterMark(t, Chapters.DefaultTitle(l.Count + 1)));
                _edit = _edit with { Chapters = l };
                Save(true);
            }),
            Ui.Button(L.T("Remove all"), () => { _edit = _edit with { Chapters = [] }; Save(true); }),
            Ui.Button(L.T("Back to the file's chapters"), () => { _edit = _edit with { Chapters = null }; Save(true); })));
        return box;
    }

    private void MoveTrack(int i, int delta)
    {
        var l = Materialize();
        var same = l.Select((t, j) => (t, j)).Where(x => x.t.Kind == l[i].Kind).Select(x => x.j).ToList();
        int pos = same.IndexOf(i) + delta;
        if (pos < 0 || pos >= same.Count) return;
        int target = same[pos];
        (l[i], l[target]) = (l[target], l[i]);
        RenderTab(); Save();
    }

    private async Task AddExternalAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = L.T("Add external files"), AllowMultiple = true });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().Where(p => !_edit.ExternalFiles.Contains(p) && p != _job.SourcePath).ToList();
        if (paths.Count == 0) return;
        _edit = _edit with { ExternalFiles = [.. _edit.ExternalFiles, .. paths] };
        await SaveNowAsync(true);
        _issues.Children.Add(Ui.Note(L.T("{0} file(s) added: tick the tracks to use.", paths.Count)));
    }

    // ------------------------------------------------------------------ trim

    private static Bitmap Load(string path)
    {
        using var fs = File.OpenRead(path);
        return new Bitmap(fs);
    }

    private Control TrimTab()
    {
        var dur = Math.Max(_a!.Source.Duration.TotalSeconds, 0.1);
        var fps = _a.Source.PrimaryVideo?.FrameRate is > 0 and var f ? f : 25;
        var ct = _previewCts.Token;
        _parts ??= _edit.IsMultiSegment ? [.. _edit.Segments!.OrderBy(x => x.Start)]
                 : _edit.IsTrimmed ? [new TrimSegment(_edit.TrimStart ?? 0, _edit.TrimEnd)] : [];
        _seg = Math.Clamp(_seg, 0, Math.Max(0, _parts.Count - 1));
        TrimSegment? cur = _parts.Count > 0 ? _parts[_seg] : null;
        double? curStart = cur is { Start: > 0.0005 } ? cur.Start : null, curEnd = cur?.End;
        List<(double, double)> Others() => [.. _parts.Where((_, i) => i != _seg).Select(x => (x.Start, x.End ?? dur))];
        var tl = new TrimTimeline(dur, curStart, curEnd, _playhead);
        tl.SetOthers(Others());
        var startBox = new TextBox { Text = curStart is { } s0 ? Ui.Stamp(s0) : "", Watermark = "00:00:00.000", FontFamily = Ui.Mono };
        var endBox = new TextBox { Text = curEnd is { } e0 ? Ui.Stamp(e0) : "", Watermark = Ui.Stamp(dur), FontFamily = Ui.Mono };
        var segPanel = new StackPanel { Spacing = 6 };
        var stamp = Ui.Muted("");
        var img = new Image { Stretch = Stretch.Uniform, MaxHeight = 420 };
        var spin = Ui.Muted(L.T("Rendering…"));
        var frameTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };

        void Layout() => stamp.Text = $"{L.T("Playhead")}: {Ui.Stamp(_playhead)}";
        frameTimer.Tick += async (_, _) =>
        {
            frameTimer.Stop();
            spin.IsVisible = true;
            try
            {
                var t = _playhead; var fin = _final;
                var path = await Task.Run(() => _queue.Preview.FrameAsync(_job.Id, t, fin, 1280, ct), ct);
                if (!ct.IsCancellationRequested) img.Source = Load(path);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { stamp.Text = ex.Message; }
            catch (OperationCanceledException) { }
            finally { spin.IsVisible = false; }
        };
        void LoadFrame() { frameTimer.Stop(); frameTimer.Start(); }

        void SetTrim(double? s, double? e)
        {
            double? ns = s is > 0.0005 ? Math.Min(s.Value, dur) : null;
            double? ne = e is { } ev && ev < dur - 0.0005 ? Math.Max(ev, (ns ?? 0) + 0.05) : null;
            var part = new TrimSegment(ns ?? 0, ne);
            if (_parts.Count == 0) { _parts.Add(part); _seg = 0; }
            else _parts[_seg] = part;
            _parts.Sort((a, b) => a.Start.CompareTo(b.Start));
            _seg = Math.Max(0, _parts.IndexOf(part));
            _edit = _edit.WithParts(_parts, dur);
            startBox.Text = ns is { } a ? Ui.Stamp(a) : "";
            endBox.Text = ne is { } b ? Ui.Stamp(b) : "";
            tl.SetTrim(ns, ne);
            tl.SetOthers(Others());
            Segments();
            Save();
        }

        // Kept segments: select one to edit it with the handles, add more at the playhead, join or split them.
        void Segments()
        {
            segPanel.Children.Clear();
            if (_parts.Count > 1)
            {
                for (int i = 0; i < _parts.Count; i++)
                {
                    int idx = i;
                    var part = _parts[i];
                    double end = part.End ?? dur;
                    var pick = new Button
                    {
                        Content = $"{(i == _seg ? "●" : "○")} {i + 1}.  {Ui.Stamp(part.Start)} → {Ui.Stamp(end)}   ({end - part.Start:0.0} s)",
                        FontFamily = Ui.Mono,
                    };
                    pick.Classes.Add(i == _seg ? "primary" : "ghost");
                    pick.Click += (_, _) => { _seg = idx; _playhead = part.Start; RenderTab(); };
                    var rm = new Button { Content = "✕", Classes = { "ghost" } };
                    ToolTip.SetTip(rm, L.T("Remove segment"));
                    rm.Click += (_, _) =>
                    {
                        _parts.RemoveAt(idx);
                        _seg = Math.Min(_seg, Math.Max(0, _parts.Count - 1));
                        _edit = _edit.WithParts(_parts, dur);
                        Save(); RenderTab();
                    };
                    segPanel.Children.Add(Ui.Row(4, pick, rm));
                }
                double total = _edit.KeptParts(dur).Sum(x => x.End - x.Start);
                segPanel.Children.Add(Ui.Muted(L.T("{0} segments, {1} in total: they are joined into one file, in time order.", _parts.Count, Ui.Short(TimeSpan.FromSeconds(total)))));
            }
            var add = Ui.Button(L.T("Add segment at playhead"), () =>
            {
                var part = new TrimSegment(_playhead, Math.Min(dur, _playhead + Math.Max(1, Math.Min(30, dur / 10))));
                _parts.Add(part);
                _parts.Sort((a, b) => a.Start.CompareTo(b.Start));
                _seg = _parts.IndexOf(part);
                _edit = _edit.WithParts(_parts, dur);
                Save(); RenderTab();
            });
            ToolTip.SetTip(add, L.T("Keeps another part of the file: the parts are joined, or become separate jobs."));
            var row = Ui.Row(8, add, Ui.Button(L.T("Add chapter at playhead"), () => { AddChapter(_playhead); }));
            if (_parts.Count > 1)
            {
                var split = Ui.Button(L.T("Create {0} jobs in the queue (one per segment)", _parts.Count), async () =>
                {
                    await SaveNowAsync(false);
                    try
                    {
                        var jobs = _queue.SplitSegments(_job.Id);
                        _issues.Children.Add(Ui.Note(L.T("{0} jobs created: one per segment.", jobs.Count)));
                        Close();
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException) { _issues.Children.Add(Ui.Note(ex.Message, true)); }
                });
                row.Children.Add(split);
            }
            segPanel.Children.Add(row);
        }
        Segments();
        void SetPlayhead(double t) { _playhead = Math.Clamp(t, 0, dur); tl.SetPlayhead(_playhead); Layout(); LoadFrame(); }

        tl.TrimChanged += SetTrim;
        tl.PlayheadChanged += t => { _playhead = t; Layout(); LoadFrame(); };
        TrimSegment? Cur() => _parts.Count > 0 ? _parts[_seg] : null;
        startBox.LostFocus += (_, _) => { var v = Ui.ParseStamp(startBox.Text); if (v != (Cur() is { Start: > 0.0005 } c ? c.Start : null)) SetTrim(v ?? 0, Cur()?.End); };
        endBox.LostFocus += (_, _) => { var v = Ui.ParseStamp(endBox.Text); if (v != Cur()?.End) SetTrim(Cur()?.Start ?? 0, v); };

        Control TimeField(string label, TextBox box, Action set)
        {
            var b = new Button { Content = "⟵" };
            ToolTip.SetTip(b, L.T("Set at playhead"));
            b.Click += (_, _) => set();
            var dp = new DockPanel();
            DockPanel.SetDock(b, Dock.Right);
            b.Margin = new Thickness(6, 0, 0, 0);
            dp.Children.Add(b); dp.Children.Add(box);
            return Ui.Field(label, dp);
        }

        var nudges = Ui.Row(6,
            Ui.Button("−1 s", () => SetPlayhead(_playhead - 1)),
            Ui.Button("−1 f", () => SetPlayhead(_playhead - 1 / fps)),
            Ui.Button("+1 f", () => SetPlayhead(_playhead + 1 / fps)),
            Ui.Button("+1 s", () => SetPlayhead(_playhead + 1)),
            Ui.Button(L.T("No trim"), () => { _parts.Clear(); _seg = 0; _edit = _edit.WithParts(_parts, dur); Save(); RenderTab(); }));
        nudges.HorizontalAlignment = HorizontalAlignment.Right;
        nudges.VerticalAlignment = VerticalAlignment.Bottom;

        var finalSwitch = Ui.Row(0, ModeButton(L.T("Final"), true, () => { _final = true; LoadFrame(); }), ModeButton(L.T("Original"), false, () => { _final = false; LoadFrame(); }));

        var root = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Ui.Muted(L.T("The cut is applied last: after imported tracks, delays, filters and burned subtitles.")),
                tl,
                new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), Children = { new TextBlock { Text = "0:00", FontSize = 12 }, WithCol(Ui.Muted(""), 1), WithCol(new TextBlock { Text = Ui.Short(TimeSpan.FromSeconds(dur)), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right }, 2) } },
                Ui.Columns(
                    TimeField(L.T("Trim start"), startBox, () => SetTrim(_playhead, Cur()?.End)),
                    TimeField(L.T("Trim end"), endBox, () => SetTrim(Cur()?.Start ?? 0, _playhead)),
                    nudges),
                segPanel,
                Ui.Row(12, finalSwitch, stamp, spin),
                new Border { Background = Brushes.Black, CornerRadius = new CornerRadius(10), ClipToBounds = true, Child = img, MinHeight = 200 },
            },
        };
        foreach (var c in root.Children.OfType<StackPanel>()) foreach (var tb in c.Children.OfType<TextBlock>()) tb.VerticalAlignment = VerticalAlignment.Center;
        Layout();
        LoadFrame();
        _ = LoadTimelineAsync(tl, dur, ct);
        return root;
    }

    private Button ModeButton(string text, bool final, Action click)
    {
        var b = new Button { Content = text };
        b.Classes.Add(_final == final ? "primary" : "ghost");
        b.Click += (_, _) =>
        {
            click();
            foreach (var sib in ((Panel)b.Parent!).Children.OfType<Button>()) { sib.Classes.Remove("primary"); if (!sib.Classes.Contains("ghost")) sib.Classes.Add("ghost"); }
            b.Classes.Remove("ghost");
            b.Classes.Add("primary");
        };
        return b;
    }

    private static Control WithCol(Control c, int col) { Grid.SetColumn(c, col); return c; }

    private async Task LoadTimelineAsync(TrimTimeline tl, double dur, CancellationToken ct)
    {
        try
        {
            var audio = Tracks.FirstOrDefault(t => t.Kind == "audio" && t.Include);
            if (audio is not null)
            {
                var w = await Task.Run(() => _queue.Preview.WaveformAsync(_job.Id, audio.File, audio.Stream, 1600, 100, ct), ct);
                tl.SetWave(Load(w));
            }
            else tl.SetWave(null);
            for (int i = 0; i < tl.ThumbCount && !ct.IsCancellationRequested; i++)
            {
                var t = (i + 0.5) * dur / tl.ThumbCount;
                var p = await Task.Run(() => _queue.Preview.ThumbnailAsync(_job.Id, t, 192, ct), ct);
                tl.SetThumb(i, Load(p));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _issues.Children.Add(Ui.Note(ex.Message, true)); }
        catch (OperationCanceledException) { }
    }

    // ------------------------------------------------------------------ filters

    private void SetVideo(Func<VideoFilterSettings, VideoFilterSettings> f, bool rerender = false) { _settings = _settings with { VideoFilters = f(_settings.VideoFilters) }; Save(rerender); }
    private void SetAudio(Func<AudioFilterSettings, AudioFilterSettings> f) { _settings = _settings with { AudioFilters = f(_settings.AudioFilters) }; Save(); }
    private void SetFs(Func<FrameserverSettings, FrameserverSettings> f, bool rerender) { _settings = _settings with { Frameserver = f(_settings.Frameserver) }; if (rerender) RenderTab(); Save(rerender); }

    private Control FiltersTab()
    {
        var v = _settings.VideoFilters; var au = _settings.AudioFilters;
        var p = _settings.ProfileDefinition;
        var rot = _a!.Source.PrimaryVideo?.Rotation ?? 0;
        var crop = v.Crop ?? new CropSettings();
        IEnumerable<(FilterStrength, string)> Strengths() => Enum.GetValues<FilterStrength>().Select(x => (x, x.Display()));

        var picture = new StackPanel { Spacing = 12, Children = { Ui.H2(L.T("Picture")) } };
        if (!p.UsesVideoFilters) picture.Children.Add(Ui.Note(L.T("This profile copies the video: only the orientation can be changed (losslessly).")));
        var orient = new List<(int?, string)> { (null, L.T("Automatic (file says {0}°)", rot)) };
        orient.AddRange(new int?[] { 0, 90, 180, 270 }.Select(d => (d, $"{d}° {L.T("clockwise")}")));
        picture.Children.Add(Ui.Columns(
            Ui.Field("Orientation", Ui.Combo(orient, v.Orientation, x => SetVideo(f => f with { Orientation = x }))),
            Ui.Field("Mirror", Ui.Row(12,
                Ui.Check(L.T("Horizontal"), v.FlipHorizontal, x => SetVideo(f => f with { FlipHorizontal = x })),
                Ui.Check(L.T("Vertical"), v.FlipVertical, x => SetVideo(f => f with { FlipVertical = x }))))));
        if (p.UsesVideoFilters)
        {
            picture.Children.Add(Ui.Field("Deinterlace", Ui.Combo(Enum.GetValues<DeinterlaceMode>().Select(x => (x, x.Display())), v.Deinterlace, x => SetVideo(f => f with { Deinterlace = x }))));
            var cropBoxes = new[] { "Left", "Top", "Right", "Bottom" }.Select(k =>
            {
                int val = k switch { "Left" => crop.Left, "Top" => crop.Top, "Right" => crop.Right, _ => crop.Bottom };
                var n = Ui.Number(val, 0, 8192, 2, x => SetVideo(f =>
                {
                    var c = f.Crop ?? new CropSettings();
                    c = k switch { "Left" => c with { Left = (int)x }, "Top" => c with { Top = (int)x }, "Right" => c with { Right = (int)x }, _ => c with { Bottom = (int)x } };
                    return f with { Crop = c };
                }), "0");
                return Ui.Field(k, n);
            }).ToArray();
            var detect = Ui.Button(L.T("Detect black borders"), async () =>
            {
                _saving.Text = L.T("Analysing black borders…");
                await SaveNowAsync(false);
                try
                {
                    var c = await Task.Run(() => _queue.Preview.DetectCropAsync(_job.Id));
                    if (c is null) { _saving.Text = L.T("No black borders found."); return; }
                    SetVideo(f => f with { Crop = c }, true);
                    RenderTab();
                    _saving.Text = L.T("Crop detected: {0}", $"{c.Left}/{c.Top}/{c.Right}/{c.Bottom}");
                }
                catch (Exception ex) { _saving.Text = ex.Message; }
            });
            picture.Children.Add(new StackPanel { Spacing = 6, Children = { Ui.Label("Crop (pixels, after rotation)"), Ui.Columns(cropBoxes), detect } });
            picture.Children.Add(Ui.Columns(
                Ui.Field("Denoise", Ui.Combo(Strengths(), v.Denoise, x => SetVideo(f => f with { Denoise = x }))),
                Ui.Field("Deband", Ui.Combo(Strengths(), v.Deband, x => SetVideo(f => f with { Deband = x }))),
                Ui.Field("Sharpen", Ui.Combo(Strengths(), v.Sharpen, x => SetVideo(f => f with { Sharpen = x })))));
            picture.Children.Add(Ui.Field("Frame rate", Ui.Text(v.Fps?.ToString(System.Globalization.CultureInfo.InvariantCulture), x =>
                SetVideo(f => f with { Fps = double.TryParse(x.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 0 ? d : null }), L.T("Keep original"))));
            picture.Children.Add(Ui.Field("Custom video filters (advanced)", Ui.Text(v.Custom, x => SetVideo(f => f with { Custom = string.IsNullOrWhiteSpace(x) ? null : x }), "eq=saturation=1.1", mono: true)));
        }

        var right = new StackPanel { Spacing = 12 };
        if (p.UsesVideoFilters)
        {
            var fs = _settings.Frameserver;
            var sec = new StackPanel { Spacing = 10, Children = { Ui.H2("VapourSynth / AviSynth") } };
            if (Frameserver.KindOf(_job.SourcePath) != FrameserverKind.None)
                sec.Children.Add(Ui.Muted(L.T("The source is already a script: its video (and audio, if it sets one) is used directly.")));
            else
            {
                var kinds = new[] { (FrameserverKind.None, L.T("None (FFmpeg decodes the file)")), (FrameserverKind.VapourSynth, "VapourSynth"), (FrameserverKind.AviSynth, "AviSynth+") };
                var templates = Frameserver.Templates.Where(t => t.Kind == fs.Kind).ToList();
                sec.Children.Add(Ui.Columns(
                    Ui.Field("Frameserver", Ui.Combo(kinds, fs.Kind, k => SetFs(x => x with
                    {
                        Kind = k,
                        Script = string.IsNullOrWhiteSpace(x.Script) || Frameserver.Templates.Any(t => t.Body == x.Script)
                            ? Frameserver.Templates.FirstOrDefault(t => t.Kind == k)?.Body : x.Script,
                    }, true))),
                    Ui.Field("Template", Ui.Combo(new[] { (-1, "—") }.Concat(templates.Select((t, i) => (i, t.Name))), -1,
                        i => { if (i >= 0) SetFs(x => x with { Script = templates[i].Body }, true); }))));
                if (fs.Kind != FrameserverKind.None)
                {
                    var script = Ui.Text(fs.Script, x => SetFs(f => f with { Script = x }, false), mono: true, multiline: true);
                    script.MinHeight = 200;
                    sec.Children.Add(Ui.Field(L.T("Script") + " ({source} = " + L.T("this file") + ")", script));
                    sec.Children.Add(Ui.Toggle(L.T("Use the audio produced by the script"), fs.AudioFromScript, x => SetFs(f => f with { AudioFromScript = x }, false)));
                    sec.Children.Add(Ui.Muted(L.T("VapourSynth: video = output 0, audio = output 1 (vspipe). AviSynth: AudioDub in the script. Decoding happens in the script, without hardware acceleration.")));
                }
            }
            right.Children.Add(Ui.Card(sec));
        }

        var gainText = new TextBlock { Text = $"{au.GainDb:0.0} dB", FontWeight = FontWeight.Bold };
        var gain = new Slider { Minimum = -20, Maximum = 20, Value = au.GainDb, SmallChange = 0.5, LargeChange = 1, TickFrequency = 0.5, IsSnapToTickEnabled = true };
        gain.ValueChanged += (_, e) => { gainText.Text = $"{e.NewValue:0.0} dB"; SetAudio(f => f with { GainDb = e.NewValue }); };
        var audio = new StackPanel { Spacing = 10, Children = { Ui.H2(L.T("Audio")) } };
        if (!p.UsesAudio) audio.Children.Add(Ui.Note(L.T("This profile has no audio output.")));
        audio.Children.Add(new StackPanel { Children = { Ui.Row(6, Ui.Label("Volume"), gainText), gain } });
        audio.Children.Add(Ui.Toggle(L.T("Normalize loudness (EBU R128)"), au.Normalize, x => SetAudio(f => f with { Normalize = x })));
        audio.Children.Add(Ui.Toggle(L.T("Downmix to stereo"), au.DownmixStereo, x => SetAudio(f => f with { DownmixStereo = x })));
        audio.Children.Add(Ui.Field("Sample rate", Ui.Combo(new (int?, string)[] { (null, L.T("Keep original")), (44100, "44.1 kHz"), (48000, "48 kHz"), (96000, "96 kHz") }, au.SampleRate, x => SetAudio(f => f with { SampleRate = x }))));
        audio.Children.Add(Ui.Field("Custom audio filters (advanced)", Ui.Text(au.Custom, x => SetAudio(f => f with { Custom = string.IsNullOrWhiteSpace(x) ? null : x }), "highpass=f=80", mono: true)));
        audio.Children.Add(Ui.Muted(L.T("Audio filters force re-encoding of the affected tracks.")));
        right.Children.Add(Ui.Card(audio));

        var g = Ui.Columns(Ui.Card(picture), right);
        g.ColumnSpacing = 16;
        right.VerticalAlignment = VerticalAlignment.Top;
        g.Children[0].VerticalAlignment = VerticalAlignment.Top;
        return g;
    }

    // ------------------------------------------------------------------ output

    private Control OutputTab()
    {
        var p = _settings.ProfileDefinition;
        var form = new SettingsForm(_settings, () => _queue.Settings.Capabilities, full: true, () => _queue.Tools?.Fdkaac is not null) { AllowedContainers = _a!.AllowedContainers };
        form.Changed += s => { _settings = s; Save(true); };

        var right = new StackPanel { Spacing = 14 };
        {
            var folder = Ui.Text(_edit.OutputFolder, x => { _edit = _edit with { OutputFolder = string.IsNullOrWhiteSpace(x) ? null : x.Trim() }; Save(); }, L.T("Same as the general setting"));
            var browse = Ui.Button("…", async () =>
            {
                var r = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions());
                if (r.FirstOrDefault()?.TryGetLocalPath() is { } path) folder.Text = path;
            });
            var dp = new DockPanel();
            DockPanel.SetDock(browse, Dock.Right);
            browse.Margin = new Thickness(6, 0, 0, 0);
            dp.Children.Add(browse); dp.Children.Add(folder);
            right.Children.Add(Ui.Field("Destination folder", dp));
        }
        var aiAnalyse = Ui.Button(L.T("Analyse frames with AI"), async () =>
        {
            var w = new AiAssistantWindow(_queue, _settings, _job, analyse: true) { Icon = Icon };
            await w.ShowDialog(this);
            if (w.Accepted is { } accepted) { _settings = accepted with { PresetName = null }; Save(true); }
        });
        right.Children.Add(Ui.Card(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Ui.H2(L.T("AI assistant")), aiAnalyse,
                Ui.Muted(L.T("A vision model looks at a few frames and proposes filters and the upscaler. Nothing changes until you apply it.")),
            },
        }));
        if (p.IsDemux)
            right.Children.Add(Ui.Note(L.T("Demux: choose the streams, attachments and chapters to extract in the Tracks tab.")));

        var name = new TextBox { Text = _settings.PresetName ?? "", Watermark = L.T("Preset name") };
        var presetMsg = Ui.Muted(L.T("Saves the settings of this file (not the tracks or the trim) for reuse."));
        var savePreset = Ui.Button(L.T("Save as preset"), () =>
        {
            try
            {
                var n = (name.Text ?? "").Trim();
                _queue.Presets.Save(new Preset(n, null, _settings with { PresetName = n }));
                _settings = _settings with { PresetName = n };
                presetMsg.Text = L.T("Preset \"{0}\" saved", n);
                Save();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException) { presetMsg.Text = ex.Message; }
        });
        var row = new DockPanel();
        DockPanel.SetDock(savePreset, Dock.Right);
        savePreset.Margin = new Thickness(6, 0, 0, 0);
        row.Children.Add(savePreset); row.Children.Add(name);
        right.Children.Add(Ui.Card(new StackPanel { Spacing = 8, Children = { Ui.H2(L.T("Preset")), row, presetMsg } }));
        right.Children.Add(Ui.Button(L.T("FFmpeg command"), async () =>
        {
            await SaveNowAsync(false);
            var cmd = _a?.CommandLine ?? await _queue.PreviewCommandAsync(_job);
            await TextWindow.ShowAsync(this, L.T("FFmpeg command"), cmd);
        }));
        right.Children[^1].HorizontalAlignment = HorizontalAlignment.Left;

        var g = Ui.Columns(form, right);
        g.ColumnSpacing = 20;
        right.VerticalAlignment = VerticalAlignment.Top;
        return g;
    }

    // ------------------------------------------------------------------ preview

    private Control PreviewTab()
    {
        var dur = Math.Max(_a!.Source.Duration.TotalSeconds, 0.1);
        var ct = _previewCts.Token;
        var img = new Image { Stretch = Stretch.Uniform, MaxHeight = 520 };
        var status = Ui.Muted("");
        var slider = new Slider { Minimum = 0, Maximum = dur, Value = Math.Clamp(_playhead, 0, dur) };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            status.Text = L.T("Rendering…");
            try
            {
                var t = _playhead;
                var path = await Task.Run(() => _queue.Preview.FrameAsync(_job.Id, t, true, 1600, ct), ct);
                if (!ct.IsCancellationRequested) { img.Source = Load(path); status.Text = $"{L.T("Position")}: {Ui.Stamp(t)}"; }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { status.Text = L.T("The preview could not be generated.") + " " + ex.Message; }
        };
        slider.ValueChanged += (_, e) => { _playhead = e.NewValue; timer.Stop(); timer.Start(); };
        var play = Ui.Button("▶ " + L.T("Play a 6 s clip of the result"), async () =>
        {
            status.Text = L.T("Rendering…");
            try
            {
                await SaveNowAsync(false);
                var t = _playhead;
                var clip = await Task.Run(() => _queue.Preview.ClipAsync(_job.Id, t, 6, ct), ct);
                Process.Start(new ProcessStartInfo(clip) { UseShellExecute = true });
                status.Text = clip;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { status.Text = L.T("The preview could not be generated.") + " " + ex.Message; }
        }, primary: true);
        timer.Start();
        return new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Ui.Field(L.T("Position") + " — " + L.T("Final (filters, subtitles, HDR conversion)"), slider),
                Ui.Row(12, play, status),
                Ui.Muted(L.T("Rendered quickly in H.264, SDR: for checking timing and look, not quality.")),
                new Border { Background = Brushes.Black, CornerRadius = new CornerRadius(10), ClipToBounds = true, Child = img, MinHeight = 240 },
            },
        };
    }
}

/// <summary>Read-only text window (logs, commands) with a Copy button.</summary>
public static class TextWindow
{
    public static Task ShowAsync(Window owner, string title, string text)
    {
        var box = new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = Ui.Mono, FontSize = 12 };
        var copy = new Button { Content = L.T("Copy"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var win = new Window
        {
            Title = title, Width = 900, Height = 560, Icon = owner.Icon, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new DockPanel { Margin = new Thickness(14) },
        };
        DockPanel.SetDock(copy, Dock.Bottom);
        ((DockPanel)win.Content).Children.Add(copy);
        ((DockPanel)win.Content).Children.Add(box);
        copy.Click += async (_, _) => { if (win.Clipboard is { } c) { await c.SetTextAsync(text); copy.Content = L.T("Copied"); } };
        return win.ShowDialog(owner);
    }
}
