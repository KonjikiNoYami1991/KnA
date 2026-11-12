using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TnA.App.Platform;
using TnA.Core.Encoding;
using TnA.Core.Localization;
using TnA.Core.Queue;
using TnA.Core.Settings;
using TnA.Core.Tools;

namespace TnA.App.Views;

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

    public SettingsWindow(QueueService queue, string tab = "general")
    {
        _queue = queue;
        _s = queue.Settings;
        _language = _s.Language; _theme = _s.Theme; _template = _s.OutputNameTemplate;
        _output = _s.OutputDirectory; _ffmpeg = _s.FfmpegPath; _vaapi = _s.VaapiDevice; _temp = _s.TempDirectory; _extra = _s.ExtraArguments;
        _checkUpdates = _s.CheckUpdatesOnStart; _restoreQueue = _s.RestoreQueue; _confirmClose = _s.ConfirmOnClose;
        _priority = _s.Priority; _post = _s.PostAction; _stall = _s.StallTimeoutMinutes; _keepLogs = _s.KeepLogs;
        _extensions = [.. _s.FolderExtensions];
        _sendTo = [.. _s.SendToPresets];

        Title = L.T("Settings");
        Width = 820; Height = 680; MinWidth = 640; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var tabs = new TabControl();
        void Add(string id, string header, Control content) =>
            tabs.Items.Add(new TabItem { Header = L.T(header), Tag = id, FontSize = 15, Content = new ScrollViewer { Content = new Border { Padding = new Thickness(4, 12, 12, 12), Child = content } } });
        Add("general", "General", General());
        Add("output", "Output", Output());
        Add("processing", "Processing", Processing());
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
                    new TextBlock { Text = $"TnA {AppInfo.Version} · {System.Runtime.InteropServices.RuntimeInformation.OSDescription}", FontWeight = FontWeight.SemiBold },
                    Ui.Muted($"{L.T("Data folder")}: {_queue.Store.DataDirectory}"),
                },
            },
        };
        return Page(
            Ui.Columns(
                Ui.Field("Language", Ui.Combo(new[] { ("Auto", L.T("Automatic")), ("it", "Italiano"), ("en", "English") }, _language, v => _language = v),
                    L.T("Automatic = Italian on Italian systems/browsers, English otherwise.") + " " + L.T("Restart TnA to apply the language.")),
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
        foreach (var ext in TnA.Core.Settings.MediaExtensions.Video)
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
            ? L.T("Adds \"TnA\" to the Send to menu of Explorer, plus one entry for each preset ticked below (adds the files with that preset and starts the queue). Many files sent together go to the same window.")
            : L.T("Adds TnA to \"Open with\", to the Nautilus scripts and to the Dolphin menu, plus one entry for each preset ticked below (adds the files with that preset and starts the queue).")));
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
        return Page(
            Ui.Field("FFmpeg path", WithBrowse(ff, false)),
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
            Children = { Ui.H2($"TnA {AppInfo.Version}"), appStatus, Ui.Row(8, checkBtn, install) },
        }));

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
        s.FolderExtensions = TnA.Core.Settings.MediaExtensions.Video.Where(_extensions.Contains).ToList();
        s.Priority = _priority;
        s.PostAction = _post;
        s.StallTimeoutMinutes = Math.Max(1, _stall);
        s.KeepLogs = Math.Max(10, _keepLogs);
        s.VaapiDevice = _vaapi;
        s.FfmpegPath = _ffmpeg;
        s.ExtraArguments = _extra;
        s.TempDirectory = _temp;
        s.SendToPresets = [.. _sendTo];
        _queue.SaveSettings();
        _queue.RefreshTools();
        App.ApplyTheme(s.Theme);
    }
}

internal static class ControlExtensions
{
    public static T Also<T>(this T c, Action<T> a) { a(c); return c; }
}
