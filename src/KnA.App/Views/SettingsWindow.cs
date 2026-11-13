using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using KnA.App.Platform;
using KnA.Core.Ai;
using KnA.Core.Encoding;
using KnA.Core.Localization;
using KnA.Core.Queue;
using KnA.Core.Settings;
using KnA.Core.Tools;

namespace KnA.App.Views;

/// <summary>General settings of the program (same sections as the web UI).</summary>
public sealed class SettingsWindow : Window
{
    private readonly QueueService _queue;
    private readonly AppSettings _s;

    // Values edited in the form, applied on Save.
    private string _language, _theme, _template;
    private string? _output, _ffmpeg, _vaapi, _temp;
    private string _extra;
    private bool _checkUpdates, _restoreQueue, _confirmClose;
    private ProcessPriority _priority;
    private PostQueueAction _post;
    private int _stall, _keepLogs;
    private readonly HashSet<string> _extensions;
    private readonly HashSet<string> _sendTo;
    private bool _discAll;
    private int _discMin;
    private string _channel, _builds;
    private readonly Dictionary<string, string> _toolPaths;
    private AiSettings _ai;
    private List<WatchFolder> _watch;
    private readonly WatchFolderService? _watchService;

    public SettingsWindow(QueueService queue, string tab = "general", WatchFolderService? watch = null)
    {
        _queue = queue;
        _watchService = watch;
        _s = queue.Settings;
        _language = _s.Language; _theme = _s.Theme; _template = _s.OutputNameTemplate;
        _output = _s.OutputDirectory; _ffmpeg = _s.FfmpegPath; _vaapi = _s.VaapiDevice; _temp = _s.TempDirectory; _extra = _s.ExtraArguments;
        _checkUpdates = _s.CheckUpdatesOnStart; _restoreQueue = _s.RestoreQueue; _confirmClose = _s.ConfirmOnClose;
        _priority = _s.Priority; _post = _s.PostAction; _stall = _s.StallTimeoutMinutes; _keepLogs = _s.KeepLogs;
        _extensions = [.. _s.FolderExtensions];
        _sendTo = [.. _s.SendToPresets];
        _discAll = _s.DiscAllTitles; _discMin = _s.DiscMinMinutes; _channel = _s.FfmpegChannel;
        _builds = string.Join("\n", _s.CustomFfmpegBuilds);
        _toolPaths = new Dictionary<string, string>(_s.ToolPaths);
        _ai = _s.Ai;
        _watch = [.. _s.WatchFolders];

        Title = L.T("Settings");
        Width = 820; Height = 680; MinWidth = 640; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var tabs = new TabControl();
        void Add(string id, string header, Control content) =>
            tabs.Items.Add(new TabItem { Header = L.T(header), Tag = id, FontSize = 15, Content = new ScrollViewer { Content = new Border { Padding = new Thickness(4, 12, 12, 12), Child = content } } });
        Add("general", "General", General());
        Add("output", "Output", Output());
        Add("processing", "Processing", Processing());
        Add("watch", "Watch folders", WatchPage());
        Add("ai", "AI", AiPage());
        Add("advanced", "Advanced", Advanced());
        Add("updates", "Updates & components", Updates());
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().FirstOrDefault(t => (string?)t.Tag == tab) ?? tabs.Items[0];

        var save = Ui.Button(L.T("Save"), () => { Save(); Close(); }, primary: true);
        var cancel = Ui.Button(L.T("Cancel"), Close);
        save.MinWidth = cancel.MinWidth = 90;
        var footer = Ui.Row(8, cancel, save);
        footer.HorizontalAlignment = HorizontalAlignment.Right;
        footer.Margin = new Thickness(0, 10, 0, 0);
        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(tabs);
        Content = root;
    }

    private static StackPanel Page(params Control[] c)
    {
        var p = new StackPanel { Spacing = 14 };
        foreach (var x in c) p.Children.Add(x);
        return p;
    }

    private Control WithBrowse(TextBox box, bool folder)
    {
        var b = new Button { Content = "…", Margin = new Thickness(8, 0, 0, 0) };
        b.Click += async (_, _) =>
        {
            if (folder)
            {
                var r = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions());
                if (r.FirstOrDefault()?.TryGetLocalPath() is { } p) box.Text = p;
            }
            else
            {
                var r = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "ffmpeg" });
                if (r.FirstOrDefault()?.TryGetLocalPath() is { } p) box.Text = p;
            }
        };
        var dp = new DockPanel();
        DockPanel.SetDock(b, Dock.Right);
        dp.Children.Add(b);
        dp.Children.Add(box);
        return dp;
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ------------------------------------------------------------------ pages

    private Control General()
    {
        var info = new Border
        {
            CornerRadius = new CornerRadius(10), Padding = new Thickness(12),
            Background = Application.Current?.FindResource(Application.Current.ActualThemeVariant, "CardBackground2") as IBrush ?? Brushes.Transparent,
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = $"KnA {AppInfo.Version} · {System.Runtime.InteropServices.RuntimeInformation.OSDescription}", FontWeight = FontWeight.SemiBold },
                    Ui.Muted($"{L.T("Data folder")}: {_queue.Store.DataDirectory}"),
                },
            },
        };
        return Page(
            Ui.Columns(
                Ui.Field("Language", Ui.Combo(new[] { ("Auto", L.T("Automatic")), ("it", "Italiano"), ("en", "English") }, _language, v => _language = v),
                    L.T("Automatic = Italian on Italian systems/browsers, English otherwise.") + " " + L.T("Restart KnA to apply the language.")),
                Ui.Field("Theme", Ui.Combo(new[] { ("System", L.T("Same as the system")), ("Light", L.T("Light theme")), ("Dark", L.T("Dark theme")) }, _theme, v => _theme = v))),
            Ui.Toggle(L.T("Check for updates at startup"), _checkUpdates, v => _checkUpdates = v),
            Ui.Toggle(L.T("Restore the queue at the next start"), _restoreQueue, v => _restoreQueue = v),
            Ui.Toggle(L.T("Ask before closing while encoding"), _confirmClose, v => _confirmClose = v),
            info);
    }

    private Control Output()
    {
        var output = Ui.Text(_output, v => _output = NullIfEmpty(v), L.T("Same folder as the source file"));
        var exts = new WrapPanel();
        foreach (var ext in KnA.Core.Settings.MediaExtensions.Video)
            exts.Children.Add(new CheckBox
            {
                Content = ext, IsChecked = _extensions.Contains(ext), Margin = new Thickness(0, 0, 12, 0),
            }.Also(c => c.IsCheckedChanged += (_, _) => { if (c.IsChecked == true) _extensions.Add(ext); else _extensions.Remove(ext); }));
        return Page(
            Ui.Field("Output folder", WithBrowse(output, true), L.T("Empty = next to each source file. Existing files are never overwritten.")),
            Ui.Field("File name template", Ui.Text(_template, v => _template = string.IsNullOrWhiteSpace(v) ? OutputNaming.DefaultTemplate : v, OutputNaming.DefaultTemplate, mono: true),
                L.T("Placeholders: {name} source name · {label} profile summary · {profile} · {codec} · {res} · {quality} · {date}")),
            Ui.Field("Extensions imported from folders", exts),
            SendToSection());
    }

    private Control SendToSection()
    {
        var title = OperatingSystem.IsWindows() ? L.T("\"Send to\" menu") : L.T("File manager menu");
        var panel = new StackPanel { Spacing = 8, Children = { Ui.H2(title) } };
        if (!ShellIntegration.IsSupported)
        {
            panel.Children.Add(Ui.Muted(L.T("Not available on this system: drop files on the window or the Dock icon.")));
            return Ui.Card(panel);
        }
        panel.Children.Add(Ui.Muted(OperatingSystem.IsWindows()
            ? L.T("Adds \"KnA\" to the Send to menu of Explorer, plus one entry for each preset ticked below (adds the files with that preset and starts the queue). Many files sent together go to the same window.")
            : L.T("Adds KnA to \"Open with\", to the Nautilus scripts and to the Dolphin menu, plus one entry for each preset ticked below (adds the files with that preset and starts the queue).")));
        var presets = new WrapPanel();
        foreach (var p in _queue.Presets.All)
            presets.Children.Add(new CheckBox { Content = p.Name, IsChecked = _sendTo.Contains(p.Name), Margin = new Thickness(0, 0, 14, 0) }
                .Also(c => c.IsCheckedChanged += (_, _) => { if (c.IsChecked == true) _sendTo.Add(p.Name); else _sendTo.Remove(p.Name); }));
        panel.Children.Add(presets);
        var status = Ui.Muted(ShellIntegration.IsInstalled() ? "✓ " + L.T("Installed") : L.T("not installed"));
        panel.Children.Add(Ui.Row(8,
            Ui.Button(L.T("Install / update"), async () =>
            {
                try
                {
                    await ShellIntegration.InstallAsync(_sendTo);
                    _s.SendToPresets = [.. _sendTo];
                    _queue.SaveSettings();
                    status.Text = "✓ " + L.T("Installed");
                }
                catch (Exception ex) { status.Text = ex.Message; }
            }, primary: true),
            Ui.Button(L.T("Remove"), () => { ShellIntegration.Uninstall(); status.Text = L.T("not installed"); }),
            status));
        status.VerticalAlignment = VerticalAlignment.Center;
        return Ui.Card(panel);
    }

    private Control Processing()
    {
        var page = Page(
            Ui.Field("Process priority", Ui.Combo(new[]
            {
                (ProcessPriority.Normal, L.T("Normal")), (ProcessPriority.BelowNormal, L.T("Below normal")), (ProcessPriority.Idle, L.T("Lowest (idle)")),
            }, _priority, v => _priority = v), L.T("Below normal keeps the computer responsive while encoding.")),
            Ui.Field("When the queue is finished", Ui.Combo(Enum.GetValues<PostQueueAction>().Select(a => (a, a.Display())), _post, v => _post = v)),
            Ui.Columns(
                Ui.Field("Stuck FFmpeg timeout (minutes)", Ui.Number(_stall, 1, 1440, 1, v => _stall = (int)v, "0")),
                Ui.Field("Logs to keep", Ui.Number(_keepLogs, 10, 100000, 10, v => _keepLogs = (int)v, "0"))));
        if (OperatingSystem.IsLinux())
            page.Children.Add(Ui.Field("VA-API device", Ui.Text(_vaapi, v => _vaapi = NullIfEmpty(v), VideoEncoders.DefaultVaapiDevice, mono: true)));
        page.Children.Add(Ui.H2(L.T("Blu-ray and DVD")));
        page.Children.Add(Ui.Columns(
            Ui.Field("Titles to queue", Ui.Combo(new[] { (true, L.T("Every episode-length title")), (false, L.T("Only the main (longest) title")) }, _discAll, v => _discAll = v)),
            Ui.Field("Ignore titles shorter than (minutes)", Ui.Number(_discMin, 0, 600, 1, v => _discMin = (int)v, "0"))));
        var main = _s.Capabilities?.Main;
        if (main is not null)
            page.Children.Add(Ui.Muted(L.T("This FFmpeg: Blu-ray {0} (libbluray, also ISO) · DVD {1} (dvdvideo, also ISO). Without them KnA reads BDMV and VIDEO_TS folders itself.",
                main.HasBluray ? "✓" : "✕", main.HasDvdVideo ? "✓" : "✕")));
        return page;
    }

    private Control Advanced()
    {
        var ff = Ui.Text(_ffmpeg, v => _ffmpeg = NullIfEmpty(v), L.T("Auto-detect"));
        var temp = Ui.Text(_temp, v => _temp = NullIfEmpty(v), Path.Combine(_queue.Store.DataDirectory, "temp"));
        var t = _queue.Tools;
        var tools = Ui.Muted(t is null
            ? L.T("FFmpeg not found.") + " " + L.T("Install FFmpeg from Settings → Components, or with your package manager.")
            : $"FFmpeg: {t.Ffmpeg}\nFFprobe: {t.Ffprobe}\nSCXviD: {t.Scxvid ?? L.T("not found (keyframes use FFmpeg scene detection)")}\n" +
              $"dovi_tool: {t.DoviTool ?? "—"}\nhdr10plus_tool: {t.Hdr10PlusTool ?? "—"}\nvspipe (VapourSynth): {t.VsPipe ?? "—"}");
        tools.FontFamily = Ui.Mono;
        var toolGrid = new List<Control>();
        foreach (var id in new[] { ToolIds.Ffprobe, ToolIds.Fdkaac, ToolIds.DoviTool, ToolIds.Hdr10PlusTool, ToolIds.VsPipe, ToolIds.Scxvid })
        {
            var box = Ui.Text(_toolPaths.GetValueOrDefault(id), v => { if (string.IsNullOrWhiteSpace(v)) _toolPaths.Remove(id); else _toolPaths[id] = v.Trim(); }, L.T("auto"), mono: true);
            toolGrid.Add(Ui.Field(id, WithBrowse(box, false)));
        }
        return Page(
            Ui.Field("FFmpeg path", WithBrowse(ff, false)),
            Ui.Field("Custom FFmpeg builds (one per line)", Ui.Text(_builds, v => _builds = v, "/opt/ffmpeg-fdk/bin/ffmpeg", mono: true, multiline: true),
                L.T("Tested by the hardware detection; each job runs on the build that has its encoders (custom builds first, libfdk_aac preferred).")),
            Ui.Field("Companion programs", Ui.Columns(toolGrid.Take(3).ToArray())),
            Ui.Columns(toolGrid.Skip(3).ToArray()),
            Ui.Field("Extra FFmpeg arguments", Ui.Text(_extra, v => _extra = v, "-x265-params …", mono: true)),
            Ui.Field("Temporary folder", WithBrowse(temp, true), L.T("Subtitles, fonts, HDR metadata and previews. A fast SSD helps.")),
            Ui.Card(tools));
    }

    private Control Updates()
    {
        var page = new StackPanel { Spacing = 14 };
        var appStatus = Ui.Muted(L.T("Not checked yet."));
        var install = new Button { Content = L.T("Update"), IsVisible = false, Classes = { "primary" } };
        UpdateCheck? check = null;
        var checkBtn = Ui.Button(L.T("Check for updates"), async () =>
        {
            appStatus.Text = L.T("Loading…");
            try
            {
                check = await AppUpdater.CheckAsync("desktop");
                _s.LastUpdateCheck = DateTimeOffset.Now;
                appStatus.Text = check.Message;
                install.IsVisible = check.IsNewer && check.Asset is not null;
            }
            catch (Exception ex) { appStatus.Text = L.T("Cannot reach GitHub: {0}", ex.Message); }
        });
        install.Click += async (_, _) =>
        {
            if (check?.Asset is null) return;
            install.IsEnabled = false;
            try
            {
                if (_queue.IsRunning) throw new InvalidOperationException(L.T("Stop the queue before updating components."));
                var progress = new Progress<double>(p => Dispatcher.UIThread.Post(() => appStatus.Text = $"{L.T("Downloading…")} {p:0}%"));
                await AppUpdater.InstallAsync(check.Asset, _queue.Store.DataDirectory, progress, CancellationToken.None);
                _queue.SaveQueue();
                AppUpdater.Restart();
            }
            catch (Exception ex) { appStatus.Text = ex.Message; install.IsEnabled = true; }
        };
        page.Children.Add(Ui.Card(new StackPanel
        {
            Spacing = 8,
            Children = { Ui.H2($"KnA {AppInfo.Version}"), appStatus, Ui.Row(8, checkBtn, install) },
        }));
        page.Children.Add(Ui.Field("FFmpeg channel",
            Ui.Combo(new[] { ("release", L.T("Newest release branch")), ("master", "master (nightly)") }, _channel, v => { _channel = v; _s.FfmpegChannel = v; _queue.SaveSettings(); }),
            L.T("Release = newest stable branch (recommended); master = nightly with the latest encoders.")));

        var comps = new StackPanel { Spacing = 8 };
        page.Children.Add(Ui.Card(new StackPanel { Spacing = 10, Children = { Ui.H2(L.T("Components")), comps } }));
        async Task LoadComponents()
        {
            comps.Children.Clear();
            comps.Children.Add(Ui.Muted(L.T("Loading…")));
            IReadOnlyList<ComponentStatus> list;
            try { list = await Task.Run(() => _queue.Components.CheckAsync(_queue.Tools, online: true)); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            { list = await Task.Run(() => _queue.Components.CheckAsync(_queue.Tools, online: false)); }
            comps.Children.Clear();
            foreach (var c in list)
            {
                var line = Ui.Muted($"{c.InstalledVersion ?? L.T("not installed")}" + (c.LatestVersion is { } lv ? $" · {L.T("latest")}: {lv}" : "") + (c.InstalledPath is { } ip ? $"\n{ip}" : ""));
                var row = new DockPanel();
                var desc = new StackPanel { Children = { new TextBlock { Text = c.Name, FontWeight = FontWeight.SemiBold }, Ui.Muted(c.Description), line } };
                if (c.Note is { Length: > 0 } note) desc.Children.Add(Ui.Muted(note));
                if (c.CanInstall)
                {
                    var b = new Button
                    {
                        Content = c.InstalledVersion is null ? L.T("Install") : c.UpdateAvailable ? L.T("Update") : L.T("Reinstall"),
                        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
                    };
                    if (c.InstalledVersion is null || c.UpdateAvailable) b.Classes.Add("primary");
                    b.Click += async (_, _) =>
                    {
                        if (_queue.IsRunning) { line.Text = L.T("Stop the queue before updating components."); return; }
                        b.IsEnabled = false;
                        try
                        {
                            var progress = new Progress<double>(p => Dispatcher.UIThread.Post(() => line.Text = $"{L.T("Downloading {0}…", c.Name)} {p:0}%"));
                            var log = new Progress<string>(l => Dispatcher.UIThread.Post(() => line.Text = l));
                            await Task.Run(() => _queue.Components.InstallAsync(c.Id, progress, log));
                            _queue.RefreshTools();
                            line.Text = "✓ " + L.T("{0} installed", c.Name);
                        }
                        catch (Exception ex) { line.Text = ex.Message; b.IsEnabled = true; }
                    };
                    DockPanel.SetDock(b, Dock.Right);
                    row.Children.Add(b);
                }
                row.Children.Add(desc);
                comps.Children.Add(row);
            }
        }
        page.AttachedToVisualTree += async (_, _) => { if (comps.Children.Count == 0) await LoadComponents(); };
        return page;
    }

    private Control WatchPage()
    {
        var page = new StackPanel { Spacing = 12 };
        page.Children.Add(Ui.Muted(L.T("New files in these folders are queued automatically with their preset. Scans work on NAS shares and Docker bind mounts; a file is taken once it stops growing.")));
        var list = new StackPanel { Spacing = 10 };
        var presets = new List<(string, string)> { ("", L.T("Default settings")) };
        presets.AddRange(_queue.Presets.All.Select(p => (p.Name, "★ " + p.Name)));
        void Render()
        {
            list.Children.Clear();
            for (int i = 0; i < _watch.Count; i++)
            {
                int k = i;
                var f = _watch[k];
                void Upd(Func<WatchFolder, WatchFolder> u) => _watch[k] = u(_watch[k]);
                var path = Ui.Text(f.Path, v => Upd(w => w with { Path = v.Trim() }), "/media/incoming", mono: true);
                var output = Ui.Text(f.OutputFolder, v => Upd(w => w with { OutputFolder = NullIfEmpty(v) }), L.T("Same as the general setting"), mono: true);
                var remove = Ui.Button(L.T("Remove"), () => { _watch.RemoveAt(k); Render(); });
                var status = _watchService?.Status.FirstOrDefault(x => x.Id == f.Id);
                list.Children.Add(Ui.Card(new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        Ui.Row(8, Ui.Toggle("", f.Enabled, v => Upd(w => w with { Enabled = v })), remove),
                        Ui.Field("Folder", WithBrowse(path, true)),
                        Ui.Columns(
                            Ui.Field("Preset", Ui.Combo(presets, f.Preset ?? "", v => Upd(w => w with { Preset = v.Length == 0 ? null : v }))),
                            Ui.Field("After success", Ui.Combo(new[]
                            {
                                (WatchAfterSuccess.Keep, L.T("Leave the source where it is")),
                                (WatchAfterSuccess.MoveToDoneFolder, L.T("Move the source into \"_done\"")),
                                (WatchAfterSuccess.Delete, L.T("Delete the source")),
                            }, f.AfterSuccess, v => Upd(w => w with { AfterSuccess = v })))),
                        Ui.Field("Output folder", WithBrowse(output, true)),
                        Ui.Columns(
                            Ui.Field("Scan every (s)", Ui.Number(f.IntervalSeconds, 5, 86400, 5, v => Upd(w => w with { IntervalSeconds = (int)v }), "0")),
                            Ui.Field("Stable for (s)", Ui.Number(f.SettleSeconds, 0, 3600, 5, v => Upd(w => w with { SettleSeconds = (int)v }), "0"))),
                        Ui.Row(14,
                            Ui.Check(L.T("Subfolders"), f.Recursive, v => Upd(w => w with { Recursive = v })),
                            Ui.Check(L.T("Start the queue"), f.AutoStart, v => Upd(w => w with { AutoStart = v })),
                            Ui.Check(L.T("Also the files already there"), f.ImportExisting, v => Upd(w => w with { ImportExisting = v }))),
                        Ui.Muted(status?.LastScan is { } ls ? L.T("last scan {0} · {1} queued · {2} waiting", ls.ToLocalTime().ToString("T", System.Globalization.CultureInfo.CurrentCulture), status.Queued, status.Waiting)
                                 + (status.Error is { } er ? " — " + er : "") : ""),
                    },
                }));
            }
        }
        Render();
        page.Children.Add(list);
        page.Children.Add(Ui.Button(L.T("Add a watch folder"), () => { _watch.Add(new WatchFolder()); Render(); }));
        if (_watchService is null) page.Children.Add(Ui.Muted(L.T("Watch folders run while KnA is open.")));
        return page;
    }

    private Control AiPage()
    {
        var providers = AiSettings.Providers.Select(p => (p.Id, p.Name)).ToList();
        var note = Ui.Muted(AiSettings.Providers.FirstOrDefault(p => p.Id == _ai.Provider)?.Notes ?? "");
        var models = new ComboBox { IsEditable = true, MinWidth = 220, Text = _ai.Model ?? "", PlaceholderText = "qwen2.5:7b" };
        models.PropertyChanged += (_, e) => { if (e.Property == ComboBox.TextProperty) _ai = _ai with { Model = NullIfEmpty(models.Text ?? "") }; };
        var vision = new ComboBox { IsEditable = true, MinWidth = 220, Text = _ai.VisionModel ?? "", PlaceholderText = "qwen2.5vl:7b" };
        vision.PropertyChanged += (_, e) => { if (e.Property == ComboBox.TextProperty) _ai = _ai with { VisionModel = NullIfEmpty(vision.Text ?? "") }; };
        var status = Ui.Muted("");
        var list = Ui.Button(L.T("Load list"), async () =>
        {
            status.Text = L.T("Loading…");
            try
            {
                var names = await new LlmClient(_ai).ListModelsAsync();
                models.ItemsSource = names; vision.ItemsSource = names;
                status.Text = L.T("{0} model(s) found", names.Count);
            }
            catch (Exception ex) { status.Text = L.T("Cannot reach the LLM server at {0}: {1}", _ai.EffectiveUrl, ex.Message); }
        });
        var key = new TextBox { PasswordChar = '•', Text = _ai.ApiKey ?? "", Watermark = L.T("Only for servers that need one (Unsloth Studio, cloud).") };
        key.TextChanged += (_, _) => _ai = _ai with { ApiKey = NullIfEmpty(key.Text ?? "") };
        var runtime = Ui.Muted("");
        runtime.FontFamily = Ui.Mono;
        var check = Ui.Button(L.T("Check the AI runtime"), async () =>
        {
            var py = UpscaleSidecar.Python(_ai, _queue.Store.DataDirectory);
            var script = UpscaleSidecar.Script();
            if (py is null || script is null) { runtime.Text = L.T("Python not found: install the AI upscaler in Updates & components."); return; }
            var r = await KnA.Core.Processing.ProcessRunner.RunAsync(py, [script, "--check"], timeout: TimeSpan.FromMinutes(1));
            runtime.Text = $"{py}\n{(r.ExitCode == 0 ? r.StdOut.Trim() : r.StdErr.Trim())}";
        });
        return Page(
            Ui.H2(L.T("LLM assistant")),
            Ui.Columns(
                Ui.Field("Server", Ui.Combo(providers, _ai.Provider, v =>
                {
                    _ai = _ai with { Provider = v };
                    note.Text = AiSettings.Providers.FirstOrDefault(p => p.Id == v)?.Notes ?? "";
                })),
                Ui.Field("URL", Ui.Text(_ai.BaseUrl, v => _ai = _ai with { BaseUrl = NullIfEmpty(v) }, "http://localhost:11434/v1", mono: true))),
            note,
            Ui.Columns(Ui.Field("Model", Ui.Row(8, models, list)), Ui.Field("Vision model (frame analysis)", vision)),
            status,
            Ui.Columns(Ui.Field("API key", key), Ui.Field("Timeout (s)", Ui.Number(_ai.TimeoutSeconds, 10, 3600, 10, v => _ai = _ai with { TimeoutSeconds = (int)v }, "0"))),
            Ui.H2(L.T("Neural upscaler")),
            Ui.Columns(
                Ui.Field("Device", Ui.Combo(new[] { "auto", "cuda", "rocm", "mps", "xpu", "cpu" }.Select(d => (d, d == "auto" ? L.T("Automatic") : d.ToUpperInvariant())), _ai.Device, v => _ai = _ai with { Device = v })),
                Ui.Field("Tile size (0 = whole frame)", Ui.Number(_ai.Tile, 0, 4096, 64, v => _ai = _ai with { Tile = (int)v }, "0"))),
            Ui.Field("Python", WithBrowse(Ui.Text(_ai.Python, v => _ai = _ai with { Python = NullIfEmpty(v) }, L.T("AI component, then python3 on the PATH"), mono: true), false)),
            Ui.Toggle(L.T("Half precision on the GPU (faster)"), _ai.Fp16, v => _ai = _ai with { Fp16 = v }),
            Ui.Row(8, check),
            runtime);
    }

    // ------------------------------------------------------------------ save

    private void Save()
    {
        var s = _s;
        s.Language = _language;
        s.Theme = _theme;
        s.CheckUpdatesOnStart = _checkUpdates;
        s.RestoreQueue = _restoreQueue;
        s.ConfirmOnClose = _confirmClose;
        s.OutputDirectory = _output;
        s.OutputNameTemplate = _template;
        s.FolderExtensions = KnA.Core.Settings.MediaExtensions.Video.Where(_extensions.Contains).ToList();
        s.Priority = _priority;
        s.PostAction = _post;
        s.StallTimeoutMinutes = Math.Max(1, _stall);
        s.KeepLogs = Math.Max(10, _keepLogs);
        s.VaapiDevice = _vaapi;
        s.FfmpegPath = _ffmpeg;
        s.ExtraArguments = _extra;
        s.TempDirectory = _temp;
        s.SendToPresets = [.. _sendTo];
        s.DiscAllTitles = _discAll;
        s.DiscMinMinutes = Math.Max(0, _discMin);
        s.FfmpegChannel = _channel;
        s.CustomFfmpegBuilds = _builds.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
        s.ToolPaths = new Dictionary<string, string>(_toolPaths);
        s.Ai = _ai;
        if (_watchService is not null) _watchService.Save(_watch.Where(w => w.Path.Length > 0).ToList());
        else s.WatchFolders = _watch.Where(w => w.Path.Length > 0).ToList();
        _queue.SaveSettings();
        _queue.RefreshTools();
        App.ApplyTheme(s.Theme);
    }
}

internal static class ControlExtensions
{
    public static T Also<T>(this T c, Action<T> a) { a(c); return c; }
}
