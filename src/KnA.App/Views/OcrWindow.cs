using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using KnA.Core.Localization;
using KnA.Core.Ocr;
using KnA.Core.Queue;

namespace KnA.App.Views;

/// <summary>
/// Subtitle Edit's OCR window, improved: every picture next to the text read, confidence colours and a "to check"
/// filter, italics per line, reading again only the selected lines, find/replace (also regex), keyboard flow
/// (Enter = keep and next), and the result saved as SRT or put straight into the job in place of the picture track.
/// </summary>
public sealed class OcrWindow : Window
{
    private sealed class Row(OcrLine line) : INotifyPropertyChanged
    {
        public OcrLine Line { get; } = line;
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        public string Number => (Line.Index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        public string Times => $"{SubtitleOcr.Stamp(Line.Start)[..^4]} → {SubtitleOcr.Stamp(Line.End)[..^4]}";
        public string Text => Line.Text.Length == 0 ? (Line.Confidence < 0 ? "…" : L.T("(no text)")) : Line.Text.Replace('\n', ' ');
        public string Flags => (Line.Italic ? "𝘪 " : "") + (Line.Forced ? "F " : "") + (Line.Edited ? "✎" : "");
        public IBrush Mark => Line.Confidence < 0 ? Brushes.Gray
            : Line.Text.Trim().Length == 0 || Line.Confidence < 60 ? Brushes.IndianRed
            : Line.Confidence < OcrSession.CheckBelow ? Brushes.Orange : Brushes.MediumSeaGreen;
        public string Score => Line.Confidence < 0 ? "" : $"{Line.Confidence:0}";
    }

    private readonly IEditorEngine _engine;
    private readonly IOcrSession _s;
    private readonly ObservableCollection<Row> _rows = [];
    private readonly ListBox _list = new() { SelectionMode = SelectionMode.Multiple };
    private readonly Image _picture = new() { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
    private readonly TextBox _text = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90, FontSize = 18 };
    private readonly CheckBox _italic = new() { Content = L.T("Italic") };
    private readonly TextBox _start = new() { FontFamily = Ui.Mono, Width = 130 };
    private readonly TextBox _end = new() { FontFamily = Ui.Mono, Width = 130 };
    private readonly TextBlock _status = new() { Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 100, IsVisible = false, Height = 6 };
    private readonly ToggleSwitch _onlyCheck = new() { OnContent = L.T("Only lines to check"), OffContent = L.T("Only lines to check") };
    private CancellationTokenSource? _cts;
    private OcrOptions _opt;
    private bool _loading;

    public OcrWindow(IEditorEngine editorEngine, IOcrSession session)
    {
        _engine = editorEngine;
        _s = session;
        _opt = editorEngine.OcrDefaults;
        // PaddleOCR's presence is checked here only on this computer; a server says it when the reading starts.
        var localData = (editorEngine as LocalEditorEngine)?.Queue.Store.DataDirectory;
        Title = $"OCR — {Path.GetFileName(session.Job.SourcePath)} #{session.Stream} {session.Language} — KnA";
        Width = 1200; Height = 820; MinWidth = 900; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // ---------------------------------------------------------------- engine bar
        // PaddleOCR is not in the packages (about 1 GB): when chosen and missing, one click installs it here
        var paddleInstall = new Button { Content = L.T("Install PaddleOCR (about 1 GB)"), Classes = { "primary" }, IsVisible = false };
        void PaddleCheck() => paddleInstall.IsVisible = _opt.Engine == "paddleocr" && localData is not null
            && (PaddleOcr.Locate(localData) is null || !PaddleOcr.ModelsInstalled(localData));
        paddleInstall.Click += async (_, _) =>
        {
            paddleInstall.IsEnabled = false;
            try
            {
                var log = new Progress<string>(l => _status.Text = l);
                var pr = new Progress<double>(v => { _bar.IsVisible = true; _bar.Value = v; });
                foreach (var id in new[] { "paddleocr", "paddleocr-models" })
                    if (id == "paddleocr" ? PaddleOcr.Locate(localData!) is null : !PaddleOcr.ModelsInstalled(localData!))
                        await Task.Run(() => _engine.InstallComponentAsync(id, pr, log));
                _status.Text = L.T("Installed {0}.", "PaddleOCR");
            }
            catch (Exception ex) { _status.Text = ex.Message; }
            finally { _bar.IsVisible = false; paddleInstall.IsEnabled = true; PaddleCheck(); }
        };
        var engine = Ui.Combo([("tesseract", "Tesseract 5"), ("paddleocr", "PaddleOCR (PP-OCRv5)")], _opt.Engine, v => { _opt = _opt with { Engine = v, Language = null }; PaddleCheck(); });
        var lang = new TextBox { Text = _opt.Language ?? "", Watermark = L.T("from the track ({0})", session.Language ?? "und"), Width = 150 };
        lang.LostFocus += (_, _) => _opt = _opt with { Language = string.IsNullOrWhiteSpace(lang.Text) ? null : lang.Text.Trim() };
        ToolTip.SetTip(lang, L.T("Tesseract: ita, eng, jpn… (several: ita+eng). PaddleOCR: it, en, japan…"));
        var italic = Ui.Check(L.T("Detect italics"), _opt.DetectItalic, v => _opt = _opt with { DetectItalic = v });
        var fixes = Ui.Check(L.T("Fix common OCR errors"), _opt.FixCommonErrors, v => _opt = _opt with { FixCommonErrors = v });
        var all = Ui.Button(L.T("Read all"), () => RunAsync(null), primary: true);
        var sel = Ui.Button(L.T("Read selected again"), () => RunAsync(_list.SelectedItems!.OfType<Row>().Select(r => r.Line.Index).ToList()));
        var stop = Ui.Button(L.T("Stop"), () => { _cts?.Cancel(); return Task.CompletedTask; });
        var bar = new WrapPanel { Orientation = Orientation.Horizontal };
        PaddleCheck();
        foreach (var c in new Control[] { Ui.Field("Engine", engine), paddleInstall, Ui.Field("Language", lang), italic, fixes, all, sel, stop })
        {
            c.Margin = new Thickness(0, 0, 12, 6);
            c.VerticalAlignment = VerticalAlignment.Bottom;
            bar.Children.Add(c);
        }

        // ---------------------------------------------------------------- list
        _list.ItemsSource = _rows;
        _list.ItemTemplate = new FuncDataTemplate<Row>((r, _) =>
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("6,44,150,*,Auto,36"), ColumnSpacing = 8 };
            var mark = new Border { CornerRadius = new CornerRadius(3), Width = 6, Height = 22 };
            mark.Bind(Border.BackgroundProperty, new Avalonia.Data.Binding(nameof(Row.Mark)));
            var num = Ui.Muted(""); num.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(Row.Number)));
            var times = new TextBlock { FontFamily = Ui.Mono, FontSize = 12 }; times.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(Row.Times)));
            var text = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis }; text.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(Row.Text)));
            var flags = Ui.Muted(""); flags.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(Row.Flags)));
            var score = Ui.Muted(""); score.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(Row.Score)));
            score.HorizontalAlignment = HorizontalAlignment.Right;
            Control[] cells = [mark, num, times, text, flags, score];
            for (int i = 0; i < cells.Length; i++) { Grid.SetColumn(cells[i], i); cells[i].VerticalAlignment = VerticalAlignment.Center; g.Children.Add(cells[i]); }
            return g;
        });
        _list.SelectionChanged += (_, _) => ShowCurrent();
        _onlyCheck.IsCheckedChanged += (_, _) => Fill();

        // ---------------------------------------------------------------- current line
        _text.KeyDown += (_, e) =>
        {
            // Enter keeps the text and moves on; Shift+Enter is a new line
            if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift)) { e.Handled = true; Commit(); Move(1); }
        };
        _text.LostFocus += (_, _) => Commit();
        _italic.IsCheckedChanged += (_, _) => { if (!_loading && Current is { } r) { _s.Edit(r.Line.Index, italic: _italic.IsChecked == true); r.Refresh(); } };
        _start.LostFocus += (_, _) => { if (Current is { } r && Ui.ParseStamp(_start.Text) is { } v) { _s.Edit(r.Line.Index, start: TimeSpan.FromSeconds(v)); r.Refresh(); } };
        _end.LostFocus += (_, _) => { if (Current is { } r && Ui.ParseStamp(_end.Text) is { } v) { _s.Edit(r.Line.Index, end: TimeSpan.FromSeconds(v)); r.Refresh(); } };
        var prev = Ui.Button("↑", () => { Commit(); Move(-1); return Task.CompletedTask; });
        var next = Ui.Button("↓", () => { Commit(); Move(1); return Task.CompletedTask; });
        ToolTip.SetTip(prev, "Alt+↑"); ToolTip.SetTip(next, "Alt+↓ / Enter");
        var nextCheck = Ui.Button(L.T("Next to check"), () => { Commit(); NextToCheck(); return Task.CompletedTask; });
        var pictureBox = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#2A2A30")), CornerRadius = new CornerRadius(10), Padding = new Thickness(16),
            MinHeight = 160, Child = _picture,
        };
        var editor = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                pictureBox,
                _text,
                new WrapPanel { Children = { _italic, Ui.Field("Start", _start), Ui.Field("End", _end), prev, next, nextCheck } },
            },
        };
        foreach (var c in ((WrapPanel)editor.Children[2]).Children) { c.VerticalAlignment = VerticalAlignment.Bottom; c.Margin = new Thickness(0, 0, 10, 6); }

        // ---------------------------------------------------------------- find / replace, save
        var find = new TextBox { Watermark = L.T("Find"), Width = 180 };
        var repl = new TextBox { Watermark = L.T("Replace with"), Width = 180 };
        var rx = new CheckBox { Content = "Regex" };
        var replaceAll = Ui.Button(L.T("Replace all"), () =>
        {
            try { int n = _s.Replace(find.Text ?? "", repl.Text ?? "", rx.IsChecked == true, false); _status.Text = L.T("{0} lines changed.", n); RefreshAll(); }
            catch (ArgumentException ex) { _status.Text = ex.Message; }
            return Task.CompletedTask;
        });
        var save = Ui.Button(L.T("Save SRT…"), SaveAsync);
        var use = Ui.Button(L.T("Use in this job"), UseAsync, primary: true);
        ToolTip.SetTip(use, L.T("Saves the SRT next to the video and puts it in the job in place of the picture track."));
        var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var right = Ui.Row(8, save, use);
        DockPanel.SetDock(right, Dock.Right);
        bottom.Children.Add(right);
        bottom.Children.Add(Ui.Row(8, find, repl, rx, replaceAll));

        var split = new Grid { ColumnDefinitions = new ColumnDefinitions("5*,4*"), ColumnSpacing = 16 };
        var left = new DockPanel();
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(_onlyCheck, Dock.Left);
        _status.Margin = new Thickness(12, 0, 0, 0);
        top.Children.Add(_onlyCheck);
        top.Children.Add(_status);
        DockPanel.SetDock(top, Dock.Top);
        left.Children.Add(top);
        left.Children.Add(new Border { Child = _list, BorderBrush = new SolidColorBrush(Color.Parse("#33888888")), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8) });
        Grid.SetColumn(editor, 1);
        split.Children.Add(left);
        split.Children.Add(new ScrollViewer { Content = editor, [Grid.ColumnProperty] = 1 });

        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(bar, Dock.Top);
        DockPanel.SetDock(_bar, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bar);
        root.Children.Add(_bar);
        root.Children.Add(bottom);
        root.Children.Add(split);
        Content = root;

        KeyDown += (_, e) =>
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) && e.Key is Key.Up or Key.Down) { Commit(); Move(e.Key == Key.Up ? -1 : 1); e.Handled = true; }
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.I && Current is not null) { _italic.IsChecked = _italic.IsChecked != true; e.Handled = true; }
        };
        Closed += (_, _) => _cts?.Cancel();
        Fill();
        _status.Text = L.T("{0} subtitle pictures.", _s.Lines.Count);
        Opened += async (_, _) => { if (_s.Lines.All(l => l.Confidence < 0)) await RunAsync(null); };
    }

    private Row? Current => _list.SelectedItems?.Count == 1 ? _list.SelectedItem as Row : _list.SelectedItems?.OfType<Row>().FirstOrDefault();

    private void Fill()
    {
        var keep = Current?.Line.Index;
        _rows.Clear();
        foreach (var l in _s.Lines.Where(l => _onlyCheck.IsChecked != true || _s.ToCheck(l))) _rows.Add(new Row(l));
        _list.SelectedItem = _rows.FirstOrDefault(r => r.Line.Index == keep) ?? _rows.FirstOrDefault();
    }

    private void RefreshAll() { foreach (var r in _rows) r.Refresh(); ShowCurrent(); }

    private void ShowCurrent()
    {
        if (Current is not { } r) return;
        _loading = true;
        _text.Text = r.Line.Text;
        _italic.IsChecked = r.Line.Italic;
        _start.Text = Ui.Stamp(r.Line.Start.TotalSeconds);
        _end.Text = Ui.Stamp(r.Line.End.TotalSeconds);
        _picture.Source = ToBitmap(r.Line.Image);
        _loading = false;
    }

    private void Commit()
    {
        if (_loading || Current is not { } r) return;
        var t = (_text.Text ?? "").Replace("\r", "");
        if (t != r.Line.Text) { _s.Edit(r.Line.Index, text: t); r.Refresh(); }
    }

    private void Move(int delta)
    {
        if (_rows.Count == 0) return;
        int i = Current is { } r ? _rows.IndexOf(r) : -1;
        _list.SelectedItem = _rows[Math.Clamp(i + delta, 0, _rows.Count - 1)];
        _list.ScrollIntoView(_list.SelectedItem!);
        _text.Focus();
        _text.CaretIndex = _text.Text?.Length ?? 0;
    }

    private void NextToCheck()
    {
        int from = Current is { } r ? _rows.IndexOf(r) : -1;
        var next = _rows.Skip(from + 1).FirstOrDefault(x => _s.ToCheck(x.Line)) ?? _rows.FirstOrDefault(x => _s.ToCheck(x.Line));
        if (next is null) { _status.Text = L.T("Nothing left to check."); return; }
        _list.SelectedItem = next;
        _list.ScrollIntoView(next);
        _text.Focus();
    }

    private async Task RunAsync(List<int>? only)
    {
        if (_s.Busy) return;
        Commit();
        _cts = new CancellationTokenSource();
        _s.RememberOptions(_opt);   // the engine and switches chosen here become the default
        int total = only?.Count ?? _s.Lines.Count(l => !l.Edited);
        _bar.IsVisible = true;
        _bar.Value = 0;
        _status.Text = L.T("Reading {0} pictures…", total);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var progress = new Progress<int>(n => { _bar.Value = n * 100.0 / Math.Max(1, total); _status.Text = $"{n} / {total}"; });
            await Task.Run(() => _s.RecognizeAsync(_opt, only, progress, _cts.Token));
            _status.Text = L.T("Done in {0} s: {1} lines to check.", (int)sw.Elapsed.TotalSeconds, _s.Lines.Count(_s.ToCheck));
        }
        catch (OperationCanceledException) { _status.Text = L.T("Stopped."); }
        catch (Exception ex) { _status.Text = ex.Message; }
        finally { _bar.IsVisible = false; }
        if (_onlyCheck.IsChecked == true) Fill(); else RefreshAll();
    }

    private async Task SaveAsync()
    {
        Commit();
        var pick = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = Path.GetFileName(_s.DefaultSrtPath()),
            FileTypeChoices = [new FilePickerFileType("SRT") { Patterns = ["*.srt"] }],
        });
        if (pick?.TryGetLocalPath() is not { } path) return;
        int n = await _s.SaveAsync(path);
        _status.Text = L.T("OCR: {0} subtitles written to {1}.", n, path);
    }

    /// <summary>Set when the text was put in the job (the editor reloads its tracks).</summary>
    public bool UsedInJob { get; private set; }

    private async Task UseAsync()
    {
        Commit();
        try
        {
            await _s.UseInJobAsync();
            UsedInJob = true;
            Close();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or KnA.Core.Remote.RemoteException) { _status.Text = ex.Message; }
    }

    private static WriteableBitmap ToBitmap(SubtitleImage img)
    {
        var bmp = new WriteableBitmap(new PixelSize(Math.Max(1, img.Width), Math.Max(1, img.Height)), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        using var fb = bmp.Lock();
        for (int y = 0; y < img.Height; y++)
            Marshal.Copy(img.Rgba, y * img.Width * 4, fb.Address + y * fb.RowBytes, img.Width * 4);
        return bmp;
    }
}
