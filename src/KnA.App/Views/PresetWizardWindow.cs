using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using KnA.Core.Localization;
using KnA.Core.Queue;
using KnA.Core.Settings;

namespace KnA.App.Views;

/// <summary>
/// The preset wizard: the questions come from <see cref="PresetWizard.Steps"/> (same as the web wizard), only the
/// ones that apply to the earlier answers, and the last page shows what the preset will do before saving it.
/// </summary>
public sealed class PresetWizardWindow : Window
{
    private readonly QueueService _queue;
    /// <summary>Answers the user gave; the others are the steps' defaults (recomputed when an earlier answer changes).</summary>
    private readonly Dictionary<string, string> _given = [];
    private int _step;
    private readonly ContentControl _body = new();
    private readonly TextBlock _counter = Ui.Muted("");
    private readonly TextBlock _error = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
    private readonly Button _back = new() { Content = L.T("Back") };
    private readonly Button _next = new() { Classes = { "primary" }, MinWidth = 110 };
    private readonly ProgressBar _progress = new() { Height = 4, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 0, 0, 12) };

    public Preset? Result { get; private set; }

    public PresetWizardWindow(QueueService queue)
    {
        _queue = queue;
        Title = L.T("Preset wizard");
        Width = 660; Height = 640; CanResize = false;
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
        DockPanel.SetDock(_progress, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        DockPanel.SetDock(_error, Dock.Bottom);
        root.Children.Add(_progress);
        root.Children.Add(footer);
        root.Children.Add(_error);
        root.Children.Add(new ScrollViewer { Content = _body });
        Content = root;
        Render();
    }

    /// <summary>The answers given plus the defaults of the steps that apply to them.</summary>
    private (IReadOnlyList<WizardStep> Steps, Dictionary<string, string> Answers) Resolve()
    {
        var caps = _queue.Settings.Capabilities;
        var audio = JobRunner.AudioEncodersFor(caps, _queue.Tools);
        var answers = new Dictionary<string, string>(_given);
        IReadOnlyList<WizardStep> steps = [];
        // Defaults can make more steps apply (e.g. the rate step decides the next one): a few rounds settle it.
        for (int round = 0; round < 4; round++)
        {
            steps = PresetWizard.Steps(answers, caps, audio);
            bool added = false;
            foreach (var s in steps)
                if (s.Kind is WizardStepKind.Choice or WizardStepKind.Number && !answers.ContainsKey(s.Key)) { answers[s.Key] = s.Default; added = true; }
            if (!added) break;
        }
        foreach (var key in answers.Keys.Except(_given.Keys).ToList())
            if (steps.All(s => s.Key != key)) answers.Remove(key);
        return (steps, answers);
    }

    private void Render()
    {
        var (steps, answers) = Resolve();
        _step = Math.Clamp(_step, 0, steps.Count - 1);
        var s = steps[_step];
        _counter.Text = $"{_step + 1} / {steps.Count}";
        _progress.Value = (_step + 1.0) / steps.Count;
        _back.IsEnabled = _step > 0;
        _next.Content = _step == steps.Count - 1 ? L.T("Create preset") : L.T("Next");
        _error.Text = "";
        var panel = new StackPanel { Spacing = 8, Children = { new TextBlock { Text = s.Title, FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap } } };
        if (s.Help is { Length: > 0 } help && s.Kind != WizardStepKind.Summary) panel.Children.Add(Ui.Muted(help));
        panel.Children.Add(new Border { Height = 4 });
        var current = answers.GetValueOrDefault(s.Key, s.Default);
        switch (s.Kind)
        {
            case WizardStepKind.Choice:
                foreach (var c in s.Choices)
                {
                    var b = new Button
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(14, 9),
                        IsEnabled = c.Enabled,
                        Content = new StackPanel { Children = { new TextBlock { Text = c.Label, FontWeight = FontWeight.SemiBold } } },
                    };
                    if (c.Hint.Length > 0) ((StackPanel)b.Content).Children.Add(Ui.Muted(c.Hint));
                    if (current == c.Value) b.Classes.Add("primary");
                    var value = c.Value;
                    b.Click += (_, _) => { _given[s.Key] = value; Render(); };
                    b.DoubleTapped += (_, _) => { _given[s.Key] = value; Next(); };
                    panel.Children.Add(b);
                }
                break;
            case WizardStepKind.Number:
                double.TryParse(current, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n);
                var num = Ui.Number(n, s.Min, s.Max, s.Max - s.Min > 1000 ? 100 : 0.5,
                    v => _given[s.Key] = v.ToString(System.Globalization.CultureInfo.InvariantCulture), s.Max - s.Min > 1000 ? "0" : "0.#");
                panel.Children.Add(s.Unit is { } unit ? Ui.Row(8, num, Ui.Muted(unit)) : num);
                num.MinWidth = 200;
                break;
            case WizardStepKind.Text:
                var box = new TextBox { Text = current, Watermark = "ita, jpn" };
                box.TextChanged += (_, _) => _given[s.Key] = (box.Text ?? "").Trim();
                panel.Children.Add(box);
                box.AttachedToVisualTree += (_, _) => box.Focus();
                break;
            case WizardStepKind.Summary:
                var settings = PresetWizard.Build(answers, _queue.Settings.Capabilities);
                var summary = new StackPanel { Spacing = 3 };
                foreach (var line in PresetWizard.Summary(settings)) summary.Children.Add(new TextBlock { Text = "• " + line, TextWrapping = TextWrapping.Wrap });
                panel.Children.Add(Ui.Muted(s.Help ?? ""));
                panel.Children.Add(Ui.Card(summary, 12));
                var name = new TextBox { Text = _given.GetValueOrDefault("name", ""), Watermark = L.T("e.g. Anime 1080p AV1") };
                name.TextChanged += (_, _) => _given["name"] = name.Text ?? "";
                var desc = new TextBox { Text = _given.GetValueOrDefault("description", ""), Watermark = L.T("Optional") };
                desc.TextChanged += (_, _) => _given["description"] = desc.Text ?? "";
                panel.Children.Add(Ui.Field("Name", name));
                panel.Children.Add(Ui.Field("Description", desc));
                panel.Children.Add(Ui.Muted(L.T("Everything can be changed later in the editor (pencil) of each file, or by applying the preset and saving it again.")));
                name.AttachedToVisualTree += (_, _) => name.Focus();
                break;
        }
        _body.Content = panel;
    }

    private void Next()
    {
        var (steps, answers) = Resolve();
        if (_step < steps.Count - 1)
        {
            if (steps[_step].Kind is WizardStepKind.Choice or WizardStepKind.Number && !_given.ContainsKey(steps[_step].Key))
                _given[steps[_step].Key] = answers[steps[_step].Key];   // the default shown is the answer
            _step++;
            Render();
            return;
        }
        var name = _given.GetValueOrDefault("name", "").Trim();
        try
        {
            var settings = PresetWizard.Build(answers, _queue.Settings.Capabilities);
            var description = _given.GetValueOrDefault("description", "").Trim();
            Result = _queue.Presets.Save(new Preset(name, description.Length > 0 ? description : null, settings with { PresetName = name }));
            Close();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
        {
            _error.Text = ex.Message;
        }
    }
}
