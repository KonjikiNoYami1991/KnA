using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TnA.Core.Encoding;
using TnA.Core.Localization;
using TnA.Core.Queue;
using TnA.Core.Settings;

namespace TnA.App.Views;

/// <summary>Plain-language questions → a saved preset (same steps as the web wizard).</summary>
public sealed class PresetWizardWindow : Window
{
    private sealed record Step(string Key, string Title, (string Value, string Label, string Hint)[] Choices, Func<Dictionary<string, string>, bool>? Skip = null, bool Input = false);

    private readonly QueueService _queue;
    private readonly Dictionary<string, string> _a = new()
    {
        ["goal"] = "Archive", ["codec"] = "Balanced", ["speed"] = "BestQuality", ["quality"] = "Normal", ["subtitles"] = "Hardsub", ["audio"] = "Keep", ["name"] = "",
    };
    private int _step;
    private readonly ContentControl _body = new();
    private readonly TextBlock _counter = Ui.Muted("");
    private readonly TextBlock _error = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
    private readonly Button _back = new() { Content = L.T("Back") };
    private readonly Button _next = new() { Classes = { "primary" }, MinWidth = 110 };

    public Preset? Result { get; private set; }

    public PresetWizardWindow(QueueService queue)
    {
        _queue = queue;
        Title = L.T("Preset wizard");
        Width = 620; Height = 600; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _back.Click += (_, _) => { _step--; Render(); };
        _next.Click += (_, _) => Next();
        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var buttons = Ui.Row(8, _back, _next);
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        footer.Children.Add(_counter);
        _counter.VerticalAlignment = VerticalAlignment.Center;
        var root = new DockPanel { Margin = new Thickness(20) };
        DockPanel.SetDock(footer, Dock.Bottom);
        DockPanel.SetDock(_error, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(_error);
        root.Children.Add(new ScrollViewer { Content = _body });
        Content = root;
        Render();
    }

    private static bool In(Dictionary<string, string> a, params string[] goals) => goals.Contains(a["goal"]);

    private List<Step> Steps() => new List<Step>
    {
        new("goal", L.T("What is the result for?"), [
            ("Archive", L.T("Archive / collection"), L.T("MKV, best quality per MB, subtitles kept")),
            ("Tv", L.T("TV or Blu-ray player"), L.T("H.264 that every player reads")),
            ("Web", L.T("Web / streaming"), L.T("MP4 with fast start")),
            ("Phone", L.T("Phone / tablet"), L.T("Small 720p files")),
            ("OldPlayer", L.T("Old DivX player"), L.T("XviD + MP3 in AVI")),
            ("Editing", L.T("Timing / typesetting"), L.T("Workraw for Aegisub")),
            ("Images", L.T("Images"), L.T("Frames as PNG/JPEG…")),
            ("Extract", L.T("Extract tracks"), L.T("Demux without re-encoding"))]),
        new("codec", L.T("Compatibility or size?"), [
            ("Compatible", "H.264", L.T("Plays everywhere, biggest files")),
            ("Balanced", "HEVC", L.T("Half the size of H.264, very compatible")),
            ("Smallest", "AV1", L.T("Smallest files, newer devices"))],
            a => In(a, "Tv", "Phone", "OldPlayer", "Editing", "Images", "Extract")),
        new("speed", L.T("Speed or compression?"), [
            ("BestQuality", L.T("Best compression (CPU)"), L.T("Slower, smaller files")),
            ("Fast", L.T("Fast (GPU)"), L.T("Uses a working hardware encoder when available"))],
            a => In(a, "OldPlayer", "Images", "Extract")),
        new("quality", L.T("Quality"), Enum.GetValues<QualityLevel>().Select(q => (q.ToString(), q.Display(), "")).ToArray(),
            a => In(a, "Images", "Extract")),
        new("subtitles", L.T("Subtitles"), [
            ("Hardsub", L.T("Burn into the picture"), L.T("Always visible, any player")),
            ("Softsub", L.T("Keep as tracks"), L.T("Can be turned off, needs MKV")),
            ("None", L.T("No subtitles"), "")],
            a => In(a, "Editing", "Extract")),
        new("audio", L.T("Audio"), [
            ("Keep", L.T("Keep as it is"), L.T("Copied when the format allows it")),
            ("Stereo", L.T("Stereo"), L.T("Downmix surround")),
            ("Normalize", L.T("Normalize loudness"), L.T("Even volume between episodes"))],
            a => In(a, "Images", "Extract")),
        new("name", L.T("Name your preset"), [], Input: true),
    }.Where(s => s.Skip?.Invoke(_a) != true).ToList();

    private void Render()
    {
        var steps = Steps();
        _step = Math.Clamp(_step, 0, steps.Count - 1);
        var s = steps[_step];
        _counter.Text = $"{_step + 1} / {steps.Count}";
        _back.IsEnabled = _step > 0;
        _next.Content = _step == steps.Count - 1 ? L.T("Create preset") : L.T("Next");
        _error.Text = "";
        var panel = new StackPanel { Spacing = 8, Children = { new TextBlock { Text = s.Title, FontSize = 18, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 6) } } };
        if (s.Input)
        {
            var box = new TextBox { Text = _a["name"], Watermark = L.T("e.g. Anime 1080p AV1") };
            box.TextChanged += (_, _) => _a["name"] = box.Text ?? "";
            panel.Children.Add(box);
            box.AttachedToVisualTree += (_, _) => box.Focus();
        }
        else
        {
            foreach (var (value, label, hint) in s.Choices)
            {
                var b = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(14, 10),
                    Content = new StackPanel { Children = { new TextBlock { Text = label, FontWeight = FontWeight.SemiBold }, Ui.Muted(hint) } },
                };
                if (_a[s.Key] == value) b.Classes.Add("primary");
                b.Click += (_, _) => { _a[s.Key] = value; Render(); };
                b.DoubleTapped += (_, _) => { _a[s.Key] = value; Next(); };
                panel.Children.Add(b);
            }
        }
        _body.Content = panel;
    }

    private void Next()
    {
        var steps = Steps();
        if (_step < steps.Count - 1) { _step++; Render(); return; }
        var name = _a["name"].Trim();
        try
        {
            var answers = new WizardAnswers(
                Enum.Parse<WizardGoal>(_a["goal"]), Enum.Parse<WizardCodec>(_a["codec"]), Enum.Parse<WizardSpeed>(_a["speed"]),
                Enum.Parse<QualityLevel>(_a["quality"]), 0, Enum.Parse<SubtitleMode>(_a["subtitles"]), Enum.Parse<WizardAudio>(_a["audio"]));
            var settings = PresetWizard.Build(answers, _queue.Settings.Capabilities);
            Result = _queue.Presets.Save(new Preset(name, null, settings with { PresetName = name }));
            Close();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
        {
            _error.Text = ex.Message;
        }
    }
}
