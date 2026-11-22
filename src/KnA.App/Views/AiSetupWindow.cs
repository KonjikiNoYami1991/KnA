using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using KnA.Core.Ai;
using KnA.Core.Localization;
using KnA.Core.Queue;

namespace KnA.App.Views;

/// <summary>
/// Guided set-up of the AI assistant (same steps as the web one): which server, how to install and start it,
/// address and key with a connection check that explains failures, text model with a real test question, optional
/// vision model, summary. Saves to Settings → AI.
/// </summary>
public sealed class AiSetupWindow : Window
{
    private readonly QueueService _queue;
    private AiSettings _ai;
    private int _step;
    private AiCheck? _check;
    private readonly ContentControl _body = new();
    private readonly TextBlock _counter = Ui.Muted("");
    private readonly Button _back = new() { Content = L.T("Back") };
    private readonly Button _next = new() { Classes = { "primary" }, MinWidth = 110 };
    private const int Steps = 5;

    public bool Saved { get; private set; }

    public AiSetupWindow(QueueService queue)
    {
        _queue = queue;
        _ai = queue.Settings.Ai;
        Title = L.T("Set up the AI assistant");
        Width = 640; Height = 600; CanResize = false;
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
        root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = _body });
        Content = root;
        Render();
    }

    private static TextBlock Title2(string t) => new() { Text = t, FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };

    private void Render()
    {
        _counter.Text = $"{_step + 1} / {Steps}";
        _back.IsEnabled = _step > 0;
        _next.Content = _step == Steps - 1 ? L.T("Save") : L.T("Next");
        var p = new StackPanel { Spacing = 10 };
        switch (_step)
        {
            case 0:
                p.Children.Add(Title2(L.T("Which program runs the model?")));
                p.Children.Add(Ui.Muted(L.T("The assistant uses a language model on your computer (or a server you choose): nothing is sent outside unless you pick a cloud service.")));
                foreach (var pr in AiSettings.Providers)
                {
                    var b = new Button
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(14, 8),
                        Content = new StackPanel { Children = { new TextBlock { Text = pr.Name, FontWeight = FontWeight.SemiBold }, Ui.Muted(pr.Id == "ollama" ? L.T("Recommended: the simplest, free") : pr.DefaultUrl) } },
                    };
                    if (_ai.Provider == pr.Id) b.Classes.Add("primary");
                    var id = pr.Id;
                    b.Click += (_, _) => { if (_ai.Provider != id) { _ai = _ai with { Provider = id, BaseUrl = null }; _check = null; } Render(); };
                    b.DoubleTapped += (_, _) => { _ai = _ai with { Provider = id }; Next(); };
                    p.Children.Add(b);
                }
                break;
            case 1:
            {
                p.Children.Add(Title2(L.T("Install and start it")));
                var list = new StackPanel { Spacing = 6 };
                int n = 1;
                foreach (var line in AiSetup.Install(_ai.Provider))
                    list.Children.Add(new SelectableTextBlock { Text = $"{n++}. {line}", TextWrapping = TextWrapping.Wrap });
                p.Children.Add(Ui.Card(list, 12));
                var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
                var hint = Ui.Muted("");
                void Show(AiCheck c)
                {
                    status.Text = (c.Ok ? "✔ " : "✖ ") + c.Message;
                    status.Foreground = c.Ok ? Brushes.MediumSeaGreen : Brushes.IndianRed;
                    hint.Text = c.Hint ?? "";
                }
                var url = Ui.Text(_ai.BaseUrl, v => { _ai = _ai with { BaseUrl = string.IsNullOrWhiteSpace(v) ? null : v.Trim() }; _check = null; }, _ai.EffectiveUrl, mono: true);
                p.Children.Add(Ui.Field("Address", url, L.T("Empty = the usual address of this program.")));
                if (AiSetup.NeedsKey(_ai.Provider) || _ai.HasApiKey)
                {
                    var key = new TextBox { PasswordChar = '•', Text = _ai.ApiKey ?? "" };
                    key.TextChanged += (_, _) => { _ai = _ai with { ApiKey = string.IsNullOrEmpty(key.Text) ? null : key.Text }; _check = null; };
                    p.Children.Add(Ui.Field("API key", key));
                }
                p.Children.Add(Ui.Button(L.T("Check the connection"), async () =>
                {
                    status.Text = L.T("Checking…"); status.Foreground = Brushes.Gray; hint.Text = "";
                    _check = await AiSetup.CheckAsync(_ai);
                    Show(_check);
                }, primary: true));
                p.Children.Add(status);
                p.Children.Add(hint);
                if (_check is { } c) Show(c);
                break;
            }
            case 2:
            case 3:
            {
                bool vision = _step == 3;
                p.Children.Add(Title2(vision ? L.T("Model for the frame analysis (optional)") : L.T("Which model answers?")));
                p.Children.Add(Ui.Muted(vision
                    ? L.T("Looks at a few frames of a file and proposes filters and upscaling. It must be a vision model; empty = the text model.")
                    : L.T("A 7B instruction model is a good balance; 3B is faster on weak computers.")));
                var models = (vision ? _check?.VisionModels : _check?.Models) ?? [];
                var current = vision ? _ai.VisionModel : _ai.Model;
                var box = new ComboBox { IsEditable = true, ItemsSource = models, Text = current ?? (vision ? models.FirstOrDefault() : models.FirstOrDefault(m => !AiSetup.IsVision(m)) ?? models.FirstOrDefault()), HorizontalAlignment = HorizontalAlignment.Stretch };
                void Set() { var v = string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim(); _ai = vision ? _ai with { VisionModel = v } : _ai with { Model = v }; }
                Set();
                box.PropertyChanged += (_, e) => { if (e.Property == ComboBox.TextProperty) Set(); };
                p.Children.Add(box);
                var (sText, sVision) = AiSetup.Suggested(_ai.Provider);
                if (models.Count == 0)
                    p.Children.Add(Ui.Note(L.T("No suitable model on the server. Download one ({0}), then go back and check again.", vision ? sVision : sText)));
                var result = new TextBlock { TextWrapping = TextWrapping.Wrap };
                p.Children.Add(Ui.Button(L.T("Try it"), async () =>
                {
                    var m = vision ? _ai.VisionModel ?? _ai.Model : _ai.Model;
                    if (m is null) return;
                    result.Text = L.T("Asking {0}… (the first answer can take a minute while the model loads)", m); result.Foreground = Brushes.Gray;
                    var err = await AiSetup.TestAsync(_ai, m);
                    result.Text = err is null ? "✔ " + L.T("{0} answers correctly.", m) : "✖ " + err;
                    result.Foreground = err is null ? Brushes.MediumSeaGreen : Brushes.IndianRed;
                }));
                p.Children.Add(result);
                break;
            }
            default:
            {
                p.Children.Add(Title2(L.T("Ready")));
                var lines = new StackPanel { Spacing = 4 };
                foreach (var l in new[]
                {
                    $"{L.T("Server")}: {AiSettings.Providers.FirstOrDefault(x => x.Id == _ai.Provider)?.Name} · {_ai.EffectiveUrl}",
                    $"{L.T("Model")}: {_ai.Model ?? "—"}",
                    $"{L.T("Vision model")}: {_ai.VisionModel ?? _ai.Model ?? "—"}",
                })
                    lines.Children.Add(new TextBlock { Text = "• " + l, TextWrapping = TextWrapping.Wrap });
                p.Children.Add(Ui.Card(lines, 12));
                p.Children.Add(Ui.Muted(L.T("Then: the AI assistant button of the encode settings panel turns a request in plain words into settings; in the editor (Filters tab) \"Analyse frames with AI\" proposes filters for a file. Nothing changes until you apply it.")));
                break;
            }
        }
        _body.Content = p;
    }

    private void Next()
    {
        if (_step < Steps - 1) { _step++; Render(); return; }
        _queue.Settings.Ai = _ai;
        _queue.SaveSettings();
        Saved = true;
        Close();
    }
}
