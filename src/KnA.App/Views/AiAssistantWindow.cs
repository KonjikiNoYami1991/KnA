using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using KnA.Core.Ai;
using KnA.Core.Encoding;
using KnA.Core.Localization;
using KnA.Core.Queue;

namespace KnA.App.Views;

/// <summary>
/// LLM assistant (same as the web UI): a request in plain words, or a vision analysis of a job's frames,
/// becomes a settings proposal that the user applies or discards.
/// </summary>
public sealed class AiAssistantWindow : Window
{
    private readonly IEditorEngine _engine;
    private readonly JobSettings _current;
    private readonly QueueJob? _job;
    private readonly StackPanel _result = new() { Spacing = 8 };
    private readonly TextBlock _status = Ui.Muted("");

    /// <summary>The accepted settings (null when the window was closed without applying).</summary>
    public JobSettings? Accepted { get; private set; }

    public AiAssistantWindow(IEditorEngine engine, JobSettings current, QueueJob? job = null, bool analyse = false)
    {
        _engine = engine; _current = current; _job = job;
        Title = L.T("AI assistant");
        Width = 640; Height = 600; MinWidth = 480; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var ai = engine.Ai;
        var prompt = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 80,
            Watermark = L.T("e.g. small MP4 for Telegram under 2 GB, 720p, burned Italian subtitles"),
        };
        var ask = Ui.Button(L.T("Suggest settings"), async () =>
        {
            if (string.IsNullOrWhiteSpace(prompt.Text)) return;
            await RunAsync(() => _engine.SuggestAsync(prompt.Text!, _current, _job));
        }, primary: true);
        var page = new StackPanel
        {
            Spacing = 12, Margin = new Thickness(18),
            Children =
            {
                Ui.Muted(ai.Model is { } m ? $"{ai.Provider} · {m}" : L.T("No model chosen: Settings → AI")),
                Ui.Field("What do you need?", prompt),
                Ui.Row(10, ask, _status),
                _result,
            },
        };
        if (job is not null)
            page.Children.Insert(3, Ui.Button(L.T("Analyse frames with AI"), AnalyseAsync));
        if (ai.Model is null && engine is LocalEditorEngine local)
            page.Children.Insert(1, Ui.Button(L.T("Set up the AI assistant"), async () =>
            {
                var w = new AiSetupWindow(local.Queue) { Icon = Icon };
                await w.ShowDialog(this);
                if (w.Saved) ((TextBlock)page.Children[0]).Text = $"{local.Queue.Settings.Ai.Provider} · {local.Queue.Settings.Ai.Model}";
            }, primary: true));
        else if (ai.Model is null)
            page.Children.Insert(1, Ui.Note(L.T("The KnA server's AI assistant is not set up: an administrator sets it up in its web page (Settings → AI).")));
        Content = new ScrollViewer { Content = page };
        if (analyse && job is not null) Opened += async (_, _) => await AnalyseAsync();
    }

    private async Task AnalyseAsync()
    {
        if (_job is null) return;
        await RunAsync(async () =>
        {
            _status.Text = L.T("Extracting frames and asking the vision model… (this can take a minute on a local model)");
            return await _engine.AnalyzeFramesAsync(_job, _current);
        });
    }

    private async Task RunAsync(Func<Task<AiSuggestion>> work)
    {
        _result.Children.Clear();
        if (string.IsNullOrEmpty(_status.Text)) _status.Text = L.T("Thinking…");
        try { Show(await Task.Run(work)); }
        catch (Exception ex) { _result.Children.Add(Ui.Note(ex.Message, error: true)); }
        finally { _status.Text = ""; }
    }

    private void Show(AiSuggestion r)
    {
        var s = r.Settings;
        if (r.ContentType is { } ct) _result.Children.Add(new TextBlock { Text = ct, FontWeight = FontWeight.Bold });
        if (r.Explanation.Length > 0) _result.Children.Add(new TextBlock { Text = r.Explanation, TextWrapping = TextWrapping.Wrap });
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowSpacing = 4, ColumnSpacing = 14 };
        var rows = new (string, string)[]
        {
            (L.T("Profile"), s.ProfileDefinition.Name), (L.T("Video codec"), s.EffectiveCodec.Display()), (L.T("Encoder"), s.Backend.Display()),
            (L.T("Quality"), s.Quality.Display()), (L.T("Resolution"), Resolutions.Display(s.Resolution)), (L.T("Subtitles"), s.Subtitles.Display()),
            (L.T("Audio"), s.AudioCodec.Display()), (L.T("Upscaling / AI"), UpscaleText.Display(s.Upscale.Method) + (s.Upscale.Model is { } mm ? " · " + mm : "")),
        };
        for (int i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var k = Ui.Muted(rows[i].Item1);
            var v = new TextBlock { Text = rows[i].Item2, FontWeight = FontWeight.SemiBold };
            Grid.SetRow(k, i); Grid.SetRow(v, i); Grid.SetColumn(v, 1);
            grid.Children.Add(k); grid.Children.Add(v);
        }
        _result.Children.Add(Ui.Card(grid));
        foreach (var w in r.Warnings) _result.Children.Add(Ui.Note(w));
        var apply = Ui.Button(L.T("Use these settings"), () => { Accepted = s; Close(); }, primary: true);
        apply.HorizontalAlignment = HorizontalAlignment.Left;
        _result.Children.Add(apply);
    }
}
