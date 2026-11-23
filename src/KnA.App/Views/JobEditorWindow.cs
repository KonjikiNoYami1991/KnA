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

/// <summary>Tab names of the editor; <see cref="TrimOnly"/> opens just the cut, as a standalone tool.</summary>
public static class JobEditorTabs
{
    public const string TrimOnly = "trim-only";
}

/// <summary>
/// Per-file editor (the pencil): preset import/save, visual trim, video (encoder, rate control and every parameter of
/// the encoder on sub-tabs), audio and subtitles (one sub-tab per kept track), filters (picture, GPU, VapourSynth /
/// AviSynth), output (container, attachments, chapters) and preview. It works on explicit settings only: when it
/// opens, what the profile would decide by itself is written into the fields (<see cref="EditorModel"/>), and a
/// profile or preset can only be imported. Every change is saved to the job and re-validated (debounced).
/// Mirrors wwwroot/editor.js.
/// </summary>
public sealed class JobEditorWindow : Window
{
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
    /// <summary>Sub-tab shown in the Video tab, and the track shown in the Audio / Subtitles tabs.</summary>
    private string _videoSub = "general";
    private readonly Dictionary<string, int> _trackSub = [];

    private readonly StackPanel _issues = new() { Spacing = 4 };
    private readonly TextBlock _saving = new() { Classes = { "muted" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly TabControl _tabs = new();
    private readonly ContentControl _body = new();
    private ScrollViewer _scroll = null!;
    private CancellationTokenSource? _saveCts;
    private CancellationTokenSource _previewCts = new();
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private bool _rerenderAfterSave;

    public JobEditorWindow(QueueService queue, QueueJob job, string tab = "video")
    {
        _queue = queue;
        _job = job;
        _settings = job.Settings;
        _edit = job.Edit;
        Title = job.FileName + " — KnA";
        Width = 1180; Height = 820; MinWidth = 860; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        bool trimOnly = tab == JobEditorTabs.TrimOnly;
        if (tab == "tracks") tab = "video";   // older callers: the tracks are now in the tab of their kind
        // One tab per part of the job: every setting appears in exactly one place.
        string[] tabs = trimOnly ? ["trim"] : ["preset", "trim", "video", "audio", "subtitles", "filters", "output", "preview"];
        string[] names = trimOnly ? [L.T("Trim")] : [L.T("Preset"), L.T("Trim"), L.T("Video"), L.T("Audio"), L.T("Subtitles"), L.T("Filters"), L.T("Output"), L.T("Preview")];
        if (trimOnly) { tab = "trim"; Title = L.T("Trim") + " — " + job.FileName + " — KnA"; _tabs.IsVisible = false; }
        for (int i = 0; i < tabs.Length; i++) _tabs.Items.Add(new TabItem { Header = names[i], Tag = tabs[i], FontSize = 15 });
        _tabs.SelectedIndex = Math.Max(0, Array.IndexOf(tabs, tab) is var at && at >= 0 ? at : Array.IndexOf(tabs, "video"));
        _tabs.SelectionChanged += (_, e) => { if (e.Source == _tabs && _a is not null) RenderTab(); };

        var header = new DockPanel { Margin = new Thickness(20, 14, 20, 0) };
        var title = new StackPanel { Children = { new TextBlock { Text = job.FileName, FontWeight = FontWeight.SemiBold, FontSize = 16 }, Ui.Muted(job.Folder) } };
        DockPanel.SetDock(_saving, Dock.Right);
        header.Children.Add(_saving);
        header.Children.Add(title);

        var top = new StackPanel { Children = { header, new Border { Margin = new Thickness(12, 6, 12, 0), Child = _tabs }, new Border { Margin = new Thickness(20, 4, 20, 8), Child = _issues } } };
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(_scroll = new ScrollViewer { Content = new Border { Padding = new Thickness(20, 4, 20, 20), Child = _body } });
        Content = root;
        _body.Content = Ui.Muted(L.T("Loading…"));

        _debounce.Tick += async (_, _) => { _debounce.Stop(); await SaveNowAsync(_rerenderAfterSave); _rerenderAfterSave = false; };
        Opened += async (_, _) => await LoadAsync();
        Closed += (_, _) => { _previewCts.Cancel(); if (_debounce.IsEnabled) { _debounce.Stop(); _ = SaveNowAsync(false); } };
    }

    private string CurrentTab => (_tabs.SelectedItem as TabItem)?.Tag as string ?? "video";

    private async Task LoadAsync()
    {
        try
        {
            // The profile's automatic choices become visible values: from here on the fields are what runs.
            (_settings, _edit, _a) = await Task.Run(() => EditorModel.MaterializeAsync(_queue, _job.Id, _settings, _edit));
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

    /// <summary>Saves now and writes the profile's choices into the fields left to it (after an import).</summary>
    private async Task MaterializeNowAsync()
    {
        _debounce.Stop();
        _saving.Text = L.T("Saving…");
        try
        {
            (_settings, _edit, _a) = await Task.Run(() => EditorModel.MaterializeAsync(_queue, _job.Id, _settings, _edit));
            _saving.Text = L.T("Saved");
        }
        catch (Exception ex) { _saving.Text = ""; _issues.Children.Insert(0, Ui.Note("✖ " + ex.Message, true)); }
        RenderIssues();
        RenderTab();
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
            "preset" => PresetTab(),
            "trim" => TrimTab(),
            "audio" => AudioTab(),
            "subtitles" => SubtitlesTab(),
            "filters" => FiltersTab(),
            "output" => OutputTab(),
            "preview" => PreviewTab(),
            _ => VideoTab(),
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
        // Controls also raise "changed" when they are rebuilt with the same value: that is not an edit.
        var current = Tracks;
        if (i < 0 || i >= current.Count || f(current[i]) == current[i]) return;
        var list = Materialize();
        list[i] = f(list[i]);
        if (rerender) RenderTab();
        Save(rerender);
    }

    private IReadOnlyDictionary<string, MediaInfo> AllMedia()
    {
        var d = new Dictionary<string, MediaInfo>(_a!.External) { [_job.SourcePath] = _a.Source };
        return d;
    }

    private AudioEncoderSet Encoders => JobRunner.AudioEncodersFor(_queue.Settings.Capabilities, _queue.Tools);

    private static string Details(StreamBase? s) => s switch
    {
        VideoStream v => $"{v.Width}×{v.Height} · {v.FrameRate:0.###} fps" + (v.BitDepth > 8 ? $" · {v.BitDepth}-bit" : "") + (v.Hdr.IsHdr ? " · " + v.Hdr.Description : "") + (v.Rotation != 0 ? $" · ↻{v.Rotation}°" : ""),
        AudioStream au => $"{au.Channels} ch" + (au.SampleRate > 0 ? $" · {au.SampleRate / 1000.0:0.#} kHz" : "") + (au.IsLossless ? " · lossless" : ""),
        SubtitleStream ss => ss.IsBitmap ? "bitmap" : "text",
        AttachmentStream at => at.MimeType ?? "",
        _ => "",
    };

    private string TrackName(TrackPlan t)
    {
        var st = TrackPlanner.Lookup(t, AllMedia());
        return (t.Stream >= 0 ? "#" + t.Stream + " " : "") + (t.Codec ?? st?.CodecName ?? "") + (t.Language is { Length: > 0 } l ? " " + l : "")
               + (t.File != _job.SourcePath ? " · " + Path.GetFileName(t.File) : "");
    }

    /// <summary>
    /// The video tracks, the attachments (Output tab) and, for demux, the tracks to extract: tick to keep, order,
    /// remove an added file. Video rows also carry their labels. Audio and subtitles of an output are tab views
    /// (<see cref="TrackPages"/>).
    /// </summary>
    private Control TrackListCard(string kind)
    {
        var list = Tracks;
        var media = AllMedia();
        var p = _settings.ProfileDefinition;
        var rows = list.Select((t, i) => (t, i)).Where(x => x.t.Kind == kind).ToList();
        string title = kind switch { "video" => L.T("Video tracks"), "audio" => L.T("Audio tracks"), "subtitle" => L.T("Subtitle tracks"), _ => L.T("Attachments") };
        var add = kind == "attachment" ? Ui.Button(L.T("Add attachments…"), AddAttachmentsAsync) : Ui.Button(L.T("Add external files…"), AddExternalAsync);
        var head = new DockPanel { Children = { WithDock(add, Dock.Right), Ui.H2(title) } };
        var box = new StackPanel { Spacing = 6, Children = { head } };
        if (rows.Count == 0)
        {
            box.Children.Add(Ui.Muted(kind == "attachment" ? L.T("No attachments. Fonts, covers or any other file can be added.") : L.T("No tracks of this kind.")));
            return Ui.Card(box);
        }
        bool video = kind == "video";
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(video ? "Auto,*,64,170,96,170,Auto" : "Auto,*,Auto"), ColumnSpacing = 8, RowSpacing = 6 };
        int r = 0;
        if (video)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            foreach (var (text, col) in new[] { (L.T("Use"), 0), (L.T("Stream"), 1), (L.T("Language"), 2), (L.T("Title"), 3), (L.T("Delay (ms)"), 4), (L.T("Output"), 5) })
            { var tb = Ui.Muted(text, 11.5); Grid.SetColumn(tb, col); grid.Children.Add(tb); }
            r = 1;
        }
        foreach (var (t, i) in rows)
        {
            int idx = i;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            void Put(Control c, int col) { Grid.SetRow(c, r); Grid.SetColumn(c, col); c.VerticalAlignment = VerticalAlignment.Center; grid.Children.Add(c); }
            var st = TrackPlanner.Lookup(t, media);
            bool standalone = TrackPlanner.IsStandaloneFile(t);
            Put(Ui.Check("", t.Include, v => UpdateTrack(idx, x => x with { Include = v, Burn = !v && x.Burn }, rerender: true)), 0);
            var name = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = standalone ? t.Title ?? Path.GetFileName(t.File) : TrackName(t) + (t.Title is { Length: > 0 } ti && !video ? " · " + ti : ""), FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                    Ui.Muted(standalone ? TrackPlanner.MimeType(t.File) : Details(st), 11.5),
                },
                Opacity = t.Include || t.Burn ? 1 : 0.55,
            };
            ToolTip.SetTip(name, t.File);
            Put(name, 1);
            int last = 2;
            if (video)
            {
                Put(Ui.Text(t.Language, v => UpdateTrack(idx, x => x with { Language = string.IsNullOrWhiteSpace(v) ? null : v.Trim() }), "und"), 2);
                Put(Ui.Text(t.Title, v => UpdateTrack(idx, x => x with { Title = string.IsNullOrEmpty(v) ? null : v })), 3);
                var delay = Ui.Number(t.DelayMs, -600000, 600000, 10, v => UpdateTrack(idx, x => x with { DelayMs = v }), "0");
                delay.ShowButtonSpinner = false;
                Put(delay, 4);
                if (p.EncodesVideo || p.IsRemux)
                    Put(Ui.Combo([(null, L.T("Encode (Video tab settings)")), ("copy", L.T("Copy (no re-encoding)"))], t.Output, v => UpdateTrack(idx, x => x with { Output = v }, rerender: true)), 5);
                last = 6;
            }
            var up = new Button { Content = "↑", Classes = { "ghost" } };
            var down = new Button { Content = "↓", Classes = { "ghost" } };
            up.Click += (_, _) => MoveTrack(idx, -1);
            down.Click += (_, _) => MoveTrack(idx, 1);
            var tools = Ui.Row(0, up, down);
            if (standalone || t.File != _job.SourcePath && _edit.ExternalFiles.Contains(t.File))
            {
                var rm = new Button { Content = "✕", Classes = { "ghost" } };
                ToolTip.SetTip(rm, standalone ? L.T("Remove") : L.T("Remove the added file ({0})", Path.GetFileName(t.File)));
                var file = t.File;
                rm.Click += (_, _) =>
                {
                    var l = Materialize();
                    if (standalone) l.RemoveAt(idx); else l.RemoveAll(x => x.File == file);
                    _edit = _edit with { ExternalFiles = [.. _edit.ExternalFiles.Where(f => f != file || l.Any(x => x.File == f))] };
                    RenderTab(); Save(true);
                };
                tools.Children.Add(rm);
            }
            Put(tools, last);
            r++;
        }
        box.Children.Add(grid);
        if (kind == "attachment" && !p.IsDemux) box.Children.Add(Ui.Muted(L.T("Untick an attachment of the file to leave it out; added files can be removed with ✕. Attachments need MKV.")));
        if (p.IsDemux) box.Children.Add(Ui.Muted(L.T("Demux: the ticked tracks, attachments and chapters are extracted to separate files.")));
        return Ui.Card(box);
    }

    private static Control WithDock(Control c, Dock d) { DockPanel.SetDock(c, d); return c; }

    /// <summary>Language, title, delay, default and forced of a track (the first row of its page).</summary>
    private Control Labels(int idx, TrackPlan t)
    {
        var delay = Ui.Number(t.DelayMs, -600000, 600000, 10, v => UpdateTrack(idx, x => x with { DelayMs = v }), "0");
        delay.ShowButtonSpinner = false;
        var g = Ui.Columns(
            Ui.Field("Language", Ui.Text(t.Language, v => UpdateTrack(idx, x => x with { Language = string.IsNullOrWhiteSpace(v) ? null : v.Trim() }), "und")),
            Ui.Field("Title", Ui.Text(t.Title, v => UpdateTrack(idx, x => x with { Title = string.IsNullOrEmpty(v) ? null : v }))),
            Ui.Field("Delay (ms)", delay, L.T("Positive = later.")),
            Ui.Field(" ", Ui.Row(12,
                Ui.Check(L.T("Default"), t.Default, v =>
                {
                    var l = Materialize();
                    for (int j = 0; j < l.Count; j++)
                        if (j == idx) l[j] = l[j] with { Default = v };
                        else if (v && l[j].Kind == t.Kind) l[j] = l[j] with { Default = false };
                    Save();
                }),
                Ui.Check(L.T("Forced"), t.Forced, v => UpdateTrack(idx, x => x with { Forced = v })))));
        return g;
    }

    /// <summary>
    /// The Audio / Subtitles tab view: one tab per active track (shown as tabs even with a single track), "+" to add
    /// a track of the file or of an added file, duplicate the current one or add external files; ✕ on a tab removes
    /// it; ← → order the tracks (= order in the output file). The last tab shown is remembered.
    /// </summary>
    private Control TrackPages(string kind, Func<int, TrackPlan, Control> page)
    {
        var list = Tracks;
        var active = TrackTabs.Active(list, kind);
        int current = active.Contains(_trackSub.GetValueOrDefault(kind, -1)) ? _trackSub[kind] : active.FirstOrDefault(-1);
        var plus = PlusButton(kind, current);
        if (active.Count == 0)
            return new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    Ui.Muted(kind == "audio" ? L.T("No audio track in the output: add one with +.") : L.T("No subtitle track in the output: add one with +.")),
                    plus,
                },
            };
        var tabs = new TabControl();
        int selected = 0;
        foreach (var i in active)
        {
            var t = list[i];
            int idx = i;
            var close = new Button { Content = "✕", Classes = { "ghost" }, Padding = new Thickness(4, 0), FontSize = 11, Margin = new Thickness(6, 0, 0, 0) };
            ToolTip.SetTip(close, t.Copy > 0 ? L.T("Delete this copy") : L.T("Remove from the output (it can be added again with +)"));
            close.Click += (_, _) => RemoveTrack(kind, idx);
            var header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { new TextBlock { Text = (t.Burn ? "🔥 " : "") + TrackTabs.TabLabel(t, _job.SourcePath), VerticalAlignment = VerticalAlignment.Center }, close },
            };
            ToolTip.SetTip(header, t.File);
            var content = new StackPanel { Spacing = 12, Children = { TrackToolbar(kind, idx, active), page(idx, t) } };
            tabs.Items.Add(new TabItem { Header = header, Tag = idx, FontSize = 13, Content = new Border { Padding = new Thickness(0, 12, 0, 0), Child = content } });
            if (i == current) selected = tabs.Items.Count - 1;
        }
        // "+" right after the last tab: a header that is only a button (the tab itself is never selected).
        tabs.Items.Add(new TabItem { Header = plus, Tag = -1, Padding = new Thickness(2, 0) });
        tabs.SelectedIndex = selected;
        _trackSub[kind] = current;
        tabs.SelectionChanged += (_, e) =>
        {
            if (e.Source != tabs) return;
            if (tabs.SelectedItem is TabItem { Tag: int ti } && ti >= 0) { _trackSub[kind] = ti; selected = tabs.SelectedIndex; }
            else tabs.SelectedIndex = selected;
        };
        return tabs;
    }

    /// <summary>← → and "Duplicate" above a track's page.</summary>
    private Control TrackToolbar(string kind, int idx, List<int> active)
    {
        int pos = active.IndexOf(idx);
        var left = new Button { Content = "←", Classes = { "ghost" }, IsEnabled = pos > 0 };
        var right = new Button { Content = "→", Classes = { "ghost" }, IsEnabled = pos < active.Count - 1 };
        ToolTip.SetTip(left, L.T("Earlier in the output file"));
        ToolTip.SetTip(right, L.T("Later in the output file"));
        left.Click += (_, _) => MoveActive(kind, idx, -1);
        right.Click += (_, _) => MoveActive(kind, idx, 1);
        var dup = Ui.Button(L.T("Duplicate"), () => DuplicateTrack(kind, idx));
        ToolTip.SetTip(dup, kind == "audio"
            ? L.T("The same source track once more, with its own settings (e.g. in Opus and in Vorbis).")
            : L.T("The same source track once more, with its own settings (e.g. as ASS and as SRT)."));
        var t = Tracks[idx];
        var where = Ui.Muted(L.T("Track {0} of {1} in the output", pos + 1, active.Count) + (t.Copy > 0 ? " · " + L.T("copy {0} of #{1}", t.Copy, t.Stream) : "")
                             + " · " + TrackName(t) + " · " + Details(TrackPlanner.Lookup(t, AllMedia())), 11.5);
        where.VerticalAlignment = VerticalAlignment.Center;
        return Ui.Row(6, left, right, dup, where);
    }

    /// <summary>"+": the tracks not in the output yet (file and added files), duplicate the current track, add files.</summary>
    private Button PlusButton(string kind, int current)
    {
        var list = Tracks;
        var menu = new MenuFlyout();
        foreach (var i in TrackTabs.Inactive(list, kind))
        {
            var t = list[i];
            int idx = i;
            var item = new MenuItem { Header = L.T("Add {0}", TrackName(t) + (t.Title is { Length: > 0 } ti ? " · " + ti : "") + " · " + Details(TrackPlanner.Lookup(t, AllMedia()))) };
            item.Click += async (_, _) => await ActivateTrackAsync(kind, idx);
            menu.Items.Add(item);
        }
        if (current >= 0)
        {
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
            var dup = new MenuItem { Header = L.T("Duplicate the current track") };
            dup.Click += (_, _) => DuplicateTrack(kind, current);
            menu.Items.Add(dup);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        var ext = new MenuItem { Header = L.T("Add external files…") };
        ext.Click += async (_, _) => await AddExternalAsync();
        menu.Items.Add(ext);
        var b = new Button { Content = "+", FontWeight = FontWeight.Bold, Padding = new Thickness(10, 2), Flyout = menu };
        ToolTip.SetTip(b, kind == "audio" ? L.T("Add an audio track") : L.T("Add a subtitle track"));
        return b;
    }

    private async Task ActivateTrackAsync(string kind, int idx)
    {
        _trackSub[kind] = TrackTabs.Activate(Materialize(), idx);
        await MaterializeNowAsync();   // the profile fills codec/format of the track just added
    }

    private void DuplicateTrack(string kind, int idx)
    {
        _trackSub[kind] = TrackTabs.Duplicate(Materialize(), idx);
        RenderTab(); Save(true);
    }

    private void RemoveTrack(string kind, int idx)
    {
        var l = Materialize();
        var active = TrackTabs.Active(l, kind);
        int pos = active.IndexOf(idx);
        TrackTabs.Remove(l, idx);
        var left = TrackTabs.Active(l, kind);
        _trackSub[kind] = left.Count == 0 ? -1 : left[Math.Clamp(pos, 0, left.Count - 1)];
        RenderTab(); Save(true);
    }

    private void MoveActive(string kind, int idx, int delta)
    {
        _trackSub[kind] = TrackTabs.Move(Materialize(), idx, delta);
        RenderTab(); Save();
    }

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

    private async Task AddExternalAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = L.T("Add external files"), AllowMultiple = true });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().Where(p => !_edit.ExternalFiles.Contains(p) && p != _job.SourcePath).ToList();
        if (paths.Count == 0) return;
        _edit = _edit with { ExternalFiles = [.. _edit.ExternalFiles, .. paths] };
        await SaveNowAsync(true);
        _issues.Children.Add(Ui.Note(L.T("{0} file(s) added: tick the tracks to use.", paths.Count)));
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

    // ------------------------------------------------------------------ settings form

    /// <summary>The settings form for one part of a tab, wired to this job (size budget, program parameters, rate values).</summary>
    private SettingsForm Form(FormSection section)
    {
        var form = new SettingsForm(_settings, () => _queue.Settings.Capabilities, full: true, () => _queue.Tools, section)
        {
            AllowedContainers = _a!.AllowedContainers,
            SizeInfo = SizeText,
            ParameterOptions = (tool, s, ct) => _queue.ParameterOptionsAsync(tool, s, ct),
            AiRuntimeInstalled = () => File.Exists(KnA.Core.Tools.ComponentManager.VenvPython(_queue.Store.DataDirectory)),
            InstallComponent = id => Task.Run(() => _queue.Components.InstallAsync(id)),
            AutoRate = s => EditorModel.ExplicitRate(_queue, s, _a?.Source.PrimaryVideo, s.Container ?? _a?.Container ?? s.EffectiveContainer),
            GpuEstimate = s => _a is null ? null : GpuBenchmark.EstimateFor(s, _a.Source, _edit, _queue.Settings.GpuBenchmarks),
            RunGpuBenchmark = s => Task.Run(() => GpuBenchmark.RunAsync(_queue, s.Backend, s.EffectiveCodec)),
        };
        form.Rebuild();   // the properties above are set after the constructor's first build
        form.Changed += OnFormChanged;
        return form;
    }

    private void OnFormChanged(JobSettings s)
    {
        var old = _settings;
        if (s.Profile != old.Profile) { _ = ChangeProfileAsync(old, s.Profile); return; }
        // Another encoder: its quality scale, speed presets and parameters are not the same; KnA's values for the
        // same quality level are written in their place (a bitrate or a maximum size stays as it is).
        bool encoderChanged = s.EffectiveCodec != old.EffectiveCodec || s.EffectiveBackend != old.EffectiveBackend || s.Encoder.Program != old.Encoder.Program;
        if (encoderChanged)
        {
            var e = s.Encoder with { Speed = null, Tune = null, Extra = null, GpuFilters = s.Encoder.Program == old.Encoder.Program && s.EffectiveBackend == old.EffectiveBackend ? s.Encoder.GpuFilters : null };
            if (e.RateControl is RateControl.Quality or RateControl.Auto) e = e with { RateControl = RateControl.Auto, QualityValue = null };
            s = s with { Encoder = EditorModel.ExplicitRate(_queue, s with { Encoder = e }, _a?.Source.PrimaryVideo, s.Container ?? _a?.Container ?? s.EffectiveContainer) };
        }
        _settings = s;
        Save(true);
    }

    /// <summary>A profile picked in the Video tab: after a confirmation the video parameters follow the profile again.</summary>
    private async Task ChangeProfileAsync(JobSettings old, ProfileKind kind)
    {
        var p = Profiles.Get(kind);
        if (!await Ui.ConfirmAsync(this, L.T("Profile"), L.T("Use the profile \"{0}\"? The video parameters (codec, encoder, depth, container, quality and encoder parameters) and the audio and subtitle formats of the tracks are reset to what this profile allows and chooses.", p.Name)))
        {
            _settings = old;
            RenderTab();
            return;
        }
        (_settings, _edit) = EditorModel.ApplyProfile(old, _edit, kind);
        await MaterializeNowAsync();
    }

    /// <summary>What a target size leaves for each stream of this file, with the current tracks and trim.</summary>
    private string? SizeText(JobSettings s)
    {
        if (_queue.SizeBudget(_job, s, _edit) is not { } b) return null;
        var text = SizePlanner.Describe(b);
        return b.TooSmall ? "⚠ " + text + "\n" + L.T("Too small for this duration: the video gets the minimum and the file will be bigger.") : text;
    }

    // ------------------------------------------------------------------ preset

    private Control PresetTab()
    {
        var presets = _queue.Presets.All;
        Preset? pick = presets.FirstOrDefault(x => x.Name == _settings.PresetName) ?? presets.FirstOrDefault();
        var parts = PresetParts.All;
        var partsPanel = new WrapPanel();
        foreach (var (part, label) in new[]
        {
            (PresetParts.Video, L.T("Video")), (PresetParts.Audio, L.T("Audio")), (PresetParts.Subtitles, L.T("Subtitles")),
            (PresetParts.Filters, L.T("Picture filters")), (PresetParts.Tracks, L.T("Track selection")),
        })
        {
            var pr = part;
            var c = Ui.Check(label, true, v => parts = v ? parts | pr : parts & ~pr);
            c.Margin = new Thickness(0, 0, 10, 0);
            partsPanel.Children.Add(c);
        }
        var presetCombo = Ui.Combo(presets.Select(x => (x.Name, (x.BuiltIn ? "" : "★ ") + x.Name)), pick?.Name ?? "", n => pick = presets.FirstOrDefault(x => x.Name == n));
        var apply = Ui.Button(L.T("Import into the fields"), async () =>
        {
            if (pick is null || parts == PresetParts.None) return;
            if (!await Ui.ConfirmAsync(this, L.T("Preset"), L.T("Import \"{0}\"? The chosen parts of this file's settings are replaced by the preset's values (written into the fields, as if set by hand).", pick.Name))) return;
            (_settings, _edit) = EditorModel.ApplyPreset(_settings, _edit, pick, parts);
            await MaterializeNowAsync();
        }, primary: true);
        var import = Ui.Card(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Ui.H2(L.T("Import a preset")), presetCombo, partsPanel, apply,
                Ui.Muted(L.T("The preset only fills the fields: afterwards every value can be changed in its tab and nothing follows the preset any more. Subtitles or track selection rebuild the track list from the preset's rules.")),
            },
        });

        var name = new TextBox { Text = _settings.PresetName ?? "", Watermark = L.T("Preset name") };
        var presetMsg = Ui.Muted(L.T("Saves the settings of this file (not the tracks or the trim) for reuse."));
        var savePreset = Ui.Button(L.T("Save as preset"), () =>
        {
            try
            {
                var n = (name.Text ?? "").Trim();
                _queue.Presets.Save(new Preset(n, null, EditorModel.ForPreset(_settings, _edit) with { PresetName = n }));
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
        var save = Ui.Card(new StackPanel { Spacing = 8, Children = { Ui.H2(L.T("Save as preset")), row, presetMsg } });

        var rules = Ui.Card(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Ui.H2(L.T("What the preset decides for other files")),
                Ui.Muted(L.T("Saved with the preset and used when it is applied to other files (their automatic track plan). They don't change this file: its tracks, codecs and formats are set in the Video, Audio and Subtitles tabs.")),
                Form(FormSection.PresetRules),
            },
        });
        var left = new StackPanel { Spacing = 14, Children = { import, save } };
        var g = Ui.Columns(left, rules);
        g.ColumnSpacing = 16;
        left.VerticalAlignment = VerticalAlignment.Top;
        rules.VerticalAlignment = VerticalAlignment.Top;
        return g;
    }

    // ------------------------------------------------------------------ video

    private Control VideoTab()
    {
        var p = _settings.ProfileDefinition;
        var root = new StackPanel { Spacing = 14, Children = { TrackListCard("video") } };
        var sub = new TabControl();
        void Page(string tag, string header, Control content) =>
            sub.Items.Add(new TabItem { Header = header, Tag = tag, FontSize = 13.5, Content = new Border { Padding = new Thickness(0, 12, 0, 0), Child = content } });
        Page("general", L.T("General"), Form(FormSection.Video));
        if (p.EncodesVideo && !p.IsImages)
        {
            var info = new StackPanel { Spacing = 8 };
            var v = _a!.Source.PrimaryVideo;
            if (v is not null) info.Children.Add(Ui.Muted($"{L.T("Source")}: {v.CodecName} · {Details(v)}"));
            info.Children.Add(Ui.Button(L.T("Encoder command"), async () =>
            {
                await SaveNowAsync(false);
                var cmd = _a?.CommandLine ?? await _queue.PreviewCommandAsync(_job);
                await TextWindow.ShowAsync(this, L.T("Encoder command"), cmd);
            }));
            // Compressibility check: a few short samples encoded exactly as the job will be predict the final size.
            var estimate = Ui.Muted(L.T("Encodes a few seconds in several places with these settings and predicts the final size."));
            info.Children.Add(Ui.Button(L.T("Estimate the final size"), async () =>
            {
                await SaveNowAsync(false);
                estimate.Text = L.T("Encoding samples…");
                try
                {
                    var settings = _settings; var edit = _edit;
                    var e = await Task.Run(() => SizeEstimator.EstimateAsync(_queue, _job, settings, edit, ct: _previewCts.Token));
                    estimate.Text = e.Describe();
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { estimate.Text = ex.Message; }
            }));
            info.Children.Add(estimate);
            var g = Ui.Columns(Form(FormSection.VideoRate), Ui.Card(info));
            g.ColumnSpacing = 20;
            g.ColumnDefinitions[1].Width = new GridLength(300);
            info.VerticalAlignment = VerticalAlignment.Top;
            ((Control)g.Children[1]).VerticalAlignment = VerticalAlignment.Top;
            Page("rate", L.T("Quality and speed"), g);
            // Every parameter of the encoder that will run, one sub-tab per group, read from its own help.
            var pages = new ParamPages(new CommandLineModel(_settings.Encoder.Extra, x => { _settings = _settings with { Encoder = _settings.Encoder with { Extra = x } }; Save(); }),
                ct => _queue.ParameterOptionsAsync(QueueService.ParameterTools.Video, _settings, ct),
                example: RigayaEncoders.Wanted(_settings) ? "--aq-temporal --bref-mode each" : "-x265-params psy-rd=2:aq-strength=0.8", host: sub);
            pages.PagesAdded += () => { foreach (var it in sub.Items.OfType<TabItem>()) if ((string?)it.Tag == _videoSub) sub.SelectedItem = it; };
            root.Children.Add(pages);
        }
        sub.SelectedItem = sub.Items.OfType<TabItem>().FirstOrDefault(i => (string?)i.Tag == _videoSub) ?? sub.Items[0];
        sub.SelectionChanged += (_, e) => { if (e.Source == sub && sub.SelectedItem is TabItem { Tag: string tag }) _videoSub = tag; };
        root.Children.Add(sub);
        return root;
    }

    // ------------------------------------------------------------------ audio

    private static bool IsLossless(AudioCodec c) => c is AudioCodec.Flac or AudioCodec.Alac or AudioCodec.WavPack or AudioCodec.TrueHd
        or AudioCodec.Pcm16 or AudioCodec.Pcm24 or AudioCodec.Pcm32Float;

    private Control AudioTab()
    {
        var p = _settings.ProfileDefinition;
        if (p.IsDemux) return TrackListCard("audio");   // demux: tick the tracks to extract
        if (!p.UsesAudio) return Ui.Note(L.T("This profile has no audio output."));
        return TrackPages("audio", AudioPage);
    }

    private Control AudioPage(int idx, TrackPlan t)
    {
        var box = new StackPanel { Spacing = 12, Children = { Labels(idx, t) } };
        var encoders = Encoders;
        bool copy = string.Equals(t.Output, nameof(AudioCodec.Copy), StringComparison.OrdinalIgnoreCase);
        var codec = Enum.TryParse<AudioCodec>(t.Output, true, out var c) && c != AudioCodec.Auto ? c : AudioCodec.Copy;
        var codecs = new List<(AudioCodec, string, bool)> { (AudioCodec.Copy, AudioCodec.Copy.Display(), true) };
        foreach (var x in Enum.GetValues<AudioCodec>().Where(x => x is not (AudioCodec.Auto or AudioCodec.Copy)))
        {
            bool ok = encoders.Available(x).Count > 0;
            codecs.Add((x, x.Display() + (ok ? "" : " — " + L.T("no encoder here")), ok));
        }
        var fields = new List<Control>
        {
            Ui.Field("Codec", Ui.Combo(codecs, codec, v => UpdateTrack(idx, x => TrackTabs.Retitle(x, x with
            {
                Output = v.ToString(), Encoder = v == AudioCodec.Copy ? null : encoders.Best(v), Arguments = null,
                Bitrate = v == AudioCodec.Copy || IsLossless(v) ? null : x.Bitrate,
                // A copied track is not filtered: its filters and channel count go away.
                Filters = v == AudioCodec.Copy ? null : x.Filters, Channels = v == AudioCodec.Copy ? null : x.Channels,
            }), rerender: true))),
        };
        if (!copy)
        {
            var available = encoders.Available(codec);
            var enc = t.Encoder is { } te && available.Contains(te) ? te : available.FirstOrDefault();
            fields.Add(Ui.Field("Encoder", Ui.Combo(available.Select(e => (e, AudioEncoderSet.Describe(e))), enc ?? "", v => UpdateTrack(idx, x => x with { Encoder = v, Arguments = null }, rerender: true))));
            if (!IsLossless(codec))
            {
                var kbps = Ui.Number(t.Bitrate ?? 0, 0, 1536, 16, v => UpdateTrack(idx, x => x with { Bitrate = v > 0 ? (int)v : null }), "0");
                fields.Add(Ui.Field("Bitrate (kb/s)", kbps, L.T("Per stereo pair (scaled for more channels); 0 = KnA's value for the codec.")));
            }
        }
        box.Children.Add(Ui.Columns([.. fields]));
        if (copy) box.Children.Add(Ui.Muted(L.T("The track is copied as it is: no encoder, no audio filter.")));
        else
        {
            box.Children.Add(AudioFilters(idx, t));
            if (encoders.Available(codec).FirstOrDefault() is { } first)
            {
                var enc = t.Encoder is { } te && encoders.Has(te) ? te : first;
                box.Children.Add(Ui.H2(L.T("All {0} parameters", enc)));
                box.Children.Add(new ParamPages(new CommandLineModel(t.Arguments, x => UpdateTrack(idx, y => y with { Arguments = x })),
                    ct => _queue.ParameterOptionsAsync(QueueService.ParameterTools.AudioEncoder + enc, _settings, ct), tabs: false,
                    example: enc is AudioEncoderSet.Fdkaac ? "--afterburner 1" : enc is AudioEncoderSet.Qaac ? "--quality 2" : "-aac_coder twoloop"));
            }
        }
        return box;
    }

    /// <summary>The track's own audio filters: volume, loudness normalisation, channels (downmix), sample rate, custom.</summary>
    private Control AudioFilters(int idx, TrackPlan t)
    {
        var f = t.Filters ?? new AudioFilterSettings();
        void Set(Func<AudioFilterSettings, AudioFilterSettings> g) =>
            UpdateTrack(idx, x => { var n = g(x.Filters ?? new AudioFilterSettings()); return x with { Filters = n.IsActive ? n : null }; });
        var gainText = new TextBlock { Text = $"{f.GainDb:0.0} dB", FontWeight = FontWeight.Bold };
        var gain = new Slider { Minimum = -20, Maximum = 20, Value = f.GainDb, SmallChange = 0.5, LargeChange = 1, TickFrequency = 0.5, IsSnapToTickEnabled = true };
        gain.ValueChanged += (_, e) => { gainText.Text = $"{e.NewValue:0.0} dB"; Set(x => x with { GainDb = e.NewValue }); };
        var chans = new (int?, string)[] { (null, L.T("As the source")), (8, "7.1"), (6, "5.1"), (2, L.T("Stereo (downmix)")), (1, L.T("Mono")) };
        var box = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Ui.H2(L.T("Audio filters of this track")),
                Ui.Columns(
                    new StackPanel { Children = { Ui.Row(6, Ui.Label("Volume"), gainText), gain } },
                    Ui.Field("Channels", Ui.Combo(chans, t.Channels, v => UpdateTrack(idx, x => x with { Channels = v }))),
                    Ui.Field("Sample rate", Ui.Combo(new (int?, string)[] { (null, L.T("Keep original")), (44100, "44.1 kHz"), (48000, "48 kHz"), (96000, "96 kHz") }, f.SampleRate, x => Set(y => y with { SampleRate = x })))),
                Ui.Toggle(L.T("Normalize loudness (EBU R128)"), f.Normalize, x => Set(y => y with { Normalize = x })),
                Ui.Field("Custom audio filters (advanced)", Ui.Text(f.Custom, x => Set(y => y with { Custom = string.IsNullOrWhiteSpace(x) ? null : x }), "highpass=f=80", mono: true)),
            },
        };
        return Ui.Card(box);
    }

    // ------------------------------------------------------------------ subtitles

    private Control SubtitlesTab()
    {
        var p = _settings.ProfileDefinition;
        if (p.IsDemux) return TrackListCard("subtitle");
        if (!p.UsesTrackPlan) return Ui.Note(L.T("This profile has no subtitle output."));
        return TrackPages("subtitle", SubtitlePage);
    }

    private Control SubtitlePage(int idx, TrackPlan t)
    {
        var p = _settings.ProfileDefinition;
        var box = new StackPanel { Spacing = 12 };
        var st = TrackPlanner.Lookup(t, AllMedia()) as SubtitleStream;
        var container = _a!.Container is { Length: > 0 } ac ? ac : _settings.EffectiveContainer;
        if (t.Burn)
        {
            box.Children.Add(Ui.Note(L.T("Burned into the picture: it is drawn on the video (fonts of the file included) and is not a separate track.")));
            box.Children.Add(Ui.Field("Delay (ms)", Ui.Number(t.DelayMs, -600000, 600000, 10, v => UpdateTrack(idx, x => x with { DelayMs = v }), "0")));
        }
        else
        {
            box.Children.Add(Labels(idx, t));
            var formats = new List<(string, string, bool)>();
            foreach (var (value, label) in Containers.SubtitleOutputs)
            {
                bool ok = Containers.ChosenSubtitleCodec(container, st, t.Codec, value) is not null;
                formats.Add((value, (value == "copy" ? L.T("Copy (no re-encoding)") : label) + (ok ? "" : " — " + L.T("not possible in {0}", container.ToUpperInvariant())), ok));
            }
            if (KnA.Core.Ocr.SubtitleOcr.CanRead(st?.CodecName ?? t.Codec))
                formats.Add((KnA.Core.Ocr.SubtitleOcr.OutputValue, L.T("Text (OCR, picture subtitles)"), true));
            var cur = t.Output ?? "copy";
            box.Children.Add(Ui.Columns(Ui.Field("Format", Ui.Combo(formats, cur, v => UpdateTrack(idx, x => TrackTabs.Retitle(x, x with { Output = v, Arguments = null }), rerender: true)))));
            if (cur is not ("copy" or KnA.Core.Ocr.SubtitleOcr.OutputValue))
            {
                var enc = cur == "srt" ? "srt" : cur;
                box.Children.Add(Ui.H2(L.T("All {0} parameters", enc)));
                box.Children.Add(new ParamPages(new CommandLineModel(t.Arguments, x => UpdateTrack(idx, y => y with { Arguments = x })),
                    ct => _queue.ParameterOptionsAsync(QueueService.ParameterTools.SubtitleEncoder + enc, _settings, ct), tabs: false, example: "-alignment 1"));
            }
        }
        if (st is { CodecName: var cn } && KnA.Core.Ocr.SubtitleOcr.CanRead(cn))
        {
            // Subtitle Edit's OCR window (improved): read, check and correct, then save or use in this job
            var ocrBtn = new Button { Content = L.T("OCR editor…") };
            ToolTip.SetTip(ocrBtn, L.T("Opens the OCR editor: every picture next to its text, to check and correct, then save as SRT or use in this job."));
            var track = t;
            ocrBtn.Click += async (_, _) =>
            {
                ocrBtn.IsEnabled = false;
                ocrBtn.Content = L.T("Reading…");
                try
                {
                    await SaveNowAsync(false);
                    var session = await Task.Run(() => KnA.Core.Ocr.OcrSession.OpenAsync(_queue, _job, track.File, track.Stream, default, track.Copy));
                    var w = new OcrWindow(_queue, session) { Icon = Icon };
                    await w.ShowDialog(this);
                    if (w.UsedInJob)
                    {
                        _edit = _job.Edit;
                        await MaterializeNowAsync();
                    }
                }
                catch (Exception ex) { _issues.Children.Add(Ui.Note(ex.Message, true)); }
                finally { ocrBtn.Content = L.T("OCR editor…"); ocrBtn.IsEnabled = true; }
            };
            box.Children.Add(ocrBtn);
        }
        if (p.UsesVideoFilters && !t.Burn)
            box.Children.Add(Ui.Button(L.T("Burn this track into the picture"), () =>
            {
                TrackTabs.Burn(Materialize(), idx);
                RenderTab(); Save(true);
            }));
        else if (t.Burn)
            box.Children.Add(Ui.Button(L.T("Keep it as a track instead"), async () =>
            {
                var l = Materialize();
                l[idx] = l[idx] with { Burn = false, Include = true, Output = null };
                await MaterializeNowAsync();
            }));
        return box;
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
                var title = Ui.Text(list[i].Title, v =>
                {
                    var t = string.IsNullOrWhiteSpace(v) ? null : v;
                    if (idx >= ChapterList.Count || ChapterList[idx].Title == t) return;   // same value: not an edit
                    var l = MaterializeChapters(); l[idx] = l[idx] with { Title = t }; Save();
                });
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
        CancellationTokenSource? frameCts = null;
        frameTimer.Tick += async (_, _) => await RenderFrame();
        async Task RenderFrame()
        {
            frameTimer.Stop();
            spin.IsVisible = true;
            // Latest request wins: the frame for a position the playhead already left is cancelled (its FFmpeg killed).
            frameCts?.Cancel();
            var mine = frameCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                var t = _playhead; var fin = _final;
                var path = await Task.Run(() => _queue.Preview.FrameAsync(_job.Id, t, fin, 1280, mine.Token), mine.Token);
                if (!mine.IsCancellationRequested) img.Source = Load(path);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { stamp.Text = ex.Message; }
            catch (OperationCanceledException) { }
            finally { if (frameCts == mine) spin.IsVisible = false; }
        }
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
        // Zapping: while a handle or the playhead is dragged only the nearest strip thumbnail is shown (rendering every
        // position would queue FFmpeg runs and lag behind); the real frame is rendered once the pointer is released.
        tl.PlayheadChanged += t =>
        {
            _playhead = t; Layout();
            if (tl.Scrubbing)
            {
                frameTimer.Stop();
                frameCts?.Cancel();
                if (tl.ThumbAt(t) is { } thumb) img.Source = thumb;
            }
            else LoadFrame();
        };
        tl.ScrubEnded += () => _ = RenderFrame();
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
            },
        };
        foreach (var c in root.Children.OfType<StackPanel>()) foreach (var tb in c.Children.OfType<TextBlock>()) tb.VerticalAlignment = VerticalAlignment.Center;
        Layout();
        LoadFrame();
        _ = LoadTimelineAsync(tl, dur, ct);
        return FitPicture(root, img);
    }

    /// <summary>
    /// Controls on top, picture below filling the rest of the window: the frame always fits the visible area (it no
    /// longer runs off the bottom of a small window). Very small windows still scroll, with a picture of at least 180 px.
    /// </summary>
    private Control FitPicture(Control header, Image img)
    {
        img.MaxHeight = double.PositiveInfinity;
        img.Stretch = Stretch.Uniform;
        var picture = new Border { Background = Brushes.Black, CornerRadius = new CornerRadius(10), ClipToBounds = true, Child = img, Margin = new Thickness(0, 10, 0, 0) };
        var panel = new StackPanel { Children = { header, picture } };
        void Fit()
        {
            double h = _scroll.Viewport.Height - header.Bounds.Height - 10 - 24;
            picture.Height = Math.Max(180, h);
        }
        void Viewport(object? _, AvaloniaPropertyChangedEventArgs e) { if (e.Property == ScrollViewer.ViewportProperty) Fit(); }
        header.SizeChanged += (_, _) => Fit();
        panel.AttachedToVisualTree += (_, _) => { _scroll.PropertyChanged += Viewport; Fit(); };
        panel.DetachedFromVisualTree += (_, _) => _scroll.PropertyChanged -= Viewport;
        return panel;
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
        // Thumbnails first and in parallel (the service limits how many FFmpeg run at once), the waveform alongside:
        // on a long film the waveform decodes the whole track and must not keep the strip empty.
        async Task Thumb(int i)
        {
            try
            {
                var t = (i + 0.5) * dur / tl.ThumbCount;
                var p = await Task.Run(() => _queue.Preview.ThumbnailAsync(_job.Id, t, 192, ct), ct);
                if (!ct.IsCancellationRequested) tl.SetThumb(i, Load(p));
            }
            catch (OperationCanceledException) { }
            catch (Exception) { /* one missing thumbnail is not worth an error banner */ }
        }
        async Task Wave()
        {
            try
            {
                var audio = Tracks.FirstOrDefault(t => t.Kind == "audio" && t.Include);
                if (audio is null) { tl.SetWave(null); return; }
                var w = await Task.Run(() => _queue.Preview.WaveformAsync(_job.Id, audio.File, audio.Stream, 1600, 100, ct), ct);
                if (!ct.IsCancellationRequested) tl.SetWave(Load(w));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _issues.Children.Add(Ui.Note(ex.Message, true)); }
        }
        // Middle first: the user usually looks there before the ends.
        var order = Enumerable.Range(0, tl.ThumbCount).OrderBy(i => Math.Abs(i - tl.ThumbCount / 2.0)).ToList();
        await Task.WhenAll(order.Select(Thumb).Append(Wave()));
    }

    // ------------------------------------------------------------------ filters

    private void SetVideo(Func<VideoFilterSettings, VideoFilterSettings> f, bool rerender = false) { _settings = _settings with { VideoFilters = f(_settings.VideoFilters) }; Save(rerender); }
    private void SetFs(Func<FrameserverSettings, FrameserverSettings> f, bool rerender) { _settings = _settings with { Frameserver = f(_settings.Frameserver) }; if (rerender) RenderTab(); Save(rerender); }

    private Control FiltersTab()
    {
        var v = _settings.VideoFilters;
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
        if (p.UsesVideoFilters && p.EncodesVideo && !p.IsImages)
        {
            var gpu = new StackPanel { Spacing = 10, Children = { Ui.H2(L.T("GPU filters")) } };
            var backend = _settings.EffectiveBackend;
            if (RigayaEncoders.Wanted(_settings))
            {
                if (_a?.Gpu is { } pipeline) gpu.Children.Add(PipelineView(pipeline));
                gpu.Children.Add(Ui.Muted(L.T("{0}'s own filters (--vpp-*): the filters on the left become them by themselves; add others here (they run after KnA's).", RigayaEncoders.DisplayName(backend))));
                gpu.Children.Add(new ParamPages(new CommandLineModel(_settings.Encoder.GpuFilters, x => { _settings = _settings with { Encoder = _settings.Encoder with { GpuFilters = x } }; Save(); }),
                    ct => _queue.ParameterOptionsAsync(QueueService.ParameterTools.Vpp, _settings, ct), tabs: false, example: "--vpp-resize spline36 --vpp-nlmeans"));
            }
            else if (backend == EncoderBackend.Software)
                gpu.Children.Add(Ui.Muted(L.T("The video is encoded on the CPU: choose a GPU encoder in the Video tab to use its filters.")));
            else if (VideoEncoders.GpuFilterSuffixes(backend).Count == 0)
                gpu.Children.Add(Ui.Muted(L.T("FFmpeg has no filters for {0}: choose VCEEncC as the encoder program (Video tab) to use its GPU filters.", backend.Display())));
            else
            {
                gpu.Children.Add(Ui.Muted(L.T("FFmpeg filters of {0}: the frames are uploaded to the GPU after the filters on the left and filtered there, right before the encoder.", backend.Display())));
                gpu.Children.Add(new GpuFilterEditor(_settings.VideoFilters.Gpu, (tool, ct) => _queue.ParameterOptionsAsync(tool, _settings, ct),
                    x => SetVideo(f => f with { Gpu = x })));
            }
            right.Children.Add(Ui.Card(gpu));
        }
        if (p.UsesVideoFilters)
        {
            var sec = new StackPanel { Spacing = 10, Children = { Ui.H2("VapourSynth / AviSynth") } };
            if (Frameserver.KindOf(_job.SourcePath) != FrameserverKind.None)
                sec.Children.Add(Ui.Muted(L.T("The source is already a script: its video (and audio, if it sets one) is used directly.")));
            else
                sec.Children.Add(new FrameserverEditor(_settings.Frameserver, (fs, quiet) =>
                {
                    var old = _settings.Frameserver;
                    _settings = _settings with { Frameserver = fs };
                    // Switching the frameserver (or its audio) changes which tracks exist: the track list follows.
                    if (fs.Kind != old.Kind || fs.AudioFromScript != old.AudioFromScript) _ = MaterializeNowAsync();
                    else Save();
                }));
            // The whole chain of this encode as a VapourSynth script, each step marked with where it runs.
            if (_a?.Script is { Length: > 0 } scriptText)
                sec.Children.Add(new Expander
                {
                    Header = L.T("Script of this encode (where each step runs)"), HorizontalAlignment = HorizontalAlignment.Stretch,
                    Content = new TextBox { Text = scriptText, IsReadOnly = true, FontFamily = Ui.Mono, FontSize = 11.5, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, MaxHeight = 320 },
                });
            right.Children.Add(Ui.Card(sec));
        }


        var aiAnalyse = Ui.Button(L.T("Analyse frames with AI"), async () =>
        {
            var w = new AiAssistantWindow(_queue, _settings, _job, analyse: true) { Icon = Icon };
            await w.ShowDialog(this);
            if (w.Accepted is { } accepted) { _settings = accepted with { PresetName = null }; await MaterializeNowAsync(); }
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

        var g = Ui.Columns(Ui.Card(picture), right);
        g.ColumnSpacing = 16;
        right.VerticalAlignment = VerticalAlignment.Top;
        g.Children[0].VerticalAlignment = VerticalAlignment.Top;
        return g;
    }

    /// <summary>
    /// Where each step runs with NVEncC / QSVEncC / VCEEncC: GPU or CPU (FFmpeg / script), the option used, why a step
    /// is on the CPU and the GPU filter that could replace it ("Use the GPU filter instead").
    /// </summary>
    private Control PipelineView(GpuPipeline pipeline)
    {
        var box = new StackPanel { Spacing = 6 };
        box.Children.Add(new TextBlock
        {
            Text = pipeline.AllOnGpu ? "⚡ " + L.T("Everything runs on the GPU (faster).") : L.T("Part of the work runs on the CPU:"),
            FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
        });
        foreach (var st in pipeline.Steps)
        {
            var badge = new Border
            {
                CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1), VerticalAlignment = VerticalAlignment.Top,
                Background = st.Place == StepPlace.Gpu ? new SolidColorBrush(Color.FromArgb(0x40, 0x22, 0xC5, 0x5E)) : new SolidColorBrush(Color.FromArgb(0x40, 0xF5, 0x9E, 0x0B)),
                Child = new TextBlock { Text = GpuPipeline.Place(st.Place), FontSize = 11, FontWeight = FontWeight.SemiBold },
            };
            var text = new StackPanel { Spacing = 2, Children = { new TextBlock { Text = st.Name, TextWrapping = TextWrapping.Wrap } } };
            if (st.Option is { Length: > 0 } opt) text.Children.Add(new TextBlock { Text = opt, FontFamily = Ui.Mono, FontSize = 11.5, Opacity = 0.75, TextWrapping = TextWrapping.Wrap });
            if (st.Note is { Length: > 0 } note) text.Children.Add(Ui.Muted(note, 11.5));
            if (st.GpuAlternative is { Length: > 0 } alt)
            {
                var row = new StackPanel { Spacing = 4, Children = { Ui.Muted("GPU: " + alt, 11.5) } };
                ((TextBlock)row.Children[0]).TextWrapping = TextWrapping.Wrap;
                if (st.ChainIndex is int ci)
                {
                    var use = Ui.Button(L.T("Use the GPU filter instead"), async () =>
                    {
                        _settings = RigayaFilters.UseGpuInstead(_settings, ci);
                        await MaterializeNowAsync();
                    });
                    use.HorizontalAlignment = HorizontalAlignment.Left;
                    row.Children.Add(use);
                }
                text.Children.Add(row);
            }
            var line = new DockPanel { Children = { WithDock(badge, Dock.Left), text } };
            badge.Margin = new Thickness(0, 0, 8, 0);
            box.Children.Add(line);
        }
        return new Border { Padding = new Thickness(0, 0, 0, 8), Child = box };
    }

    // ------------------------------------------------------------------ output

    private Control OutputTab()
    {
        var p = _settings.ProfileDefinition;
        var left = new StackPanel { Spacing = 14 };
        left.Children.Add(Form(FormSection.Output));
        if (_a!.AllowedContainers.Count > 0 && (p.EncodesVideo || p.IsRemux))
            left.Children.Add(Ui.Muted(L.T("Only the containers that can hold every final stream (video, audio and subtitle formats, attachments) are offered.")));
        if (p.UsesTrackPlan || p.IsDemux) left.Children.Add(TrackListCard("attachment"));
        if (!p.IsKeyframes && !p.IsImages) left.Children.Add(Ui.Card(ChaptersSection()));

        var right = new StackPanel { Spacing = 14 };
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
        right.Children.Add(Ui.Button(L.T("FFmpeg command"), async () =>
        {
            await SaveNowAsync(false);
            var cmd = _a?.CommandLine ?? await _queue.PreviewCommandAsync(_job);
            await TextWindow.ShowAsync(this, L.T("FFmpeg command"), cmd);
        }));
        right.Children[^1].HorizontalAlignment = HorizontalAlignment.Left;

        var g = Ui.Columns(left, right);
        g.ColumnSpacing = 20;
        g.ColumnDefinitions[1].Width = new GridLength(320);
        left.VerticalAlignment = VerticalAlignment.Top;
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
        CancellationTokenSource? frameCts = null;
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            status.Text = L.T("Rendering…");
            frameCts?.Cancel();
            var mine = frameCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                var t = _playhead;
                var path = await Task.Run(() => _queue.Preview.FrameAsync(_job.Id, t, true, 1600, mine.Token), mine.Token);
                if (!mine.IsCancellationRequested) { img.Source = Load(path); status.Text = $"{L.T("Position")}: {Ui.Stamp(t)}"; }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { status.Text = L.T("The preview could not be generated.") + " " + ex.Message; }
        };
        // Zapping: nothing is rendered while the slider is dragged; the frame comes when the pointer is released
        // (keyboard and clicks still render after a short pause).
        bool dragging = false;
        slider.AddHandler(PointerPressedEvent, (_, _) => dragging = true, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        void Released() { if (!dragging) return; dragging = false; timer.Stop(); timer.Start(); }
        slider.AddHandler(PointerReleasedEvent, (_, _) => Released(), Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        slider.AddHandler(PointerCaptureLostEvent, (_, _) => Released(), Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        slider.ValueChanged += (_, e) =>
        {
            _playhead = e.NewValue;
            timer.Stop();
            if (dragging) { frameCts?.Cancel(); status.Text = $"{L.T("Position")}: {Ui.Stamp(e.NewValue)} · {L.T("release to render")}"; }
            else timer.Start();
        };
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
        return FitPicture(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Ui.Field(L.T("Position") + " — " + L.T("Final (filters, subtitles, HDR conversion)"), slider),
                Ui.Row(12, play, status),
                Ui.Muted(L.T("Rendered quickly in H.264, SDR: for checking timing and look, not quality.")),
            },
        }, img);
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
