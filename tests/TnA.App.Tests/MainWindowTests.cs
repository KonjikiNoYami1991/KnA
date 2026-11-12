using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Controls;
using TnA.App.Platform;
using TnA.App.ViewModels;
using TnA.App.Views;
using TnA.Core.Encoding;
using TnA.Core.Queue;
using TnA.Core.Settings;

[assembly: AvaloniaTestApplication(typeof(TnA.App.Tests.TestAppBuilder))]

namespace TnA.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public class MainWindowTests
{
    private static async Task Pump(Func<bool> until, int timeoutMs = 20000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!until() && sw.ElapsedMilliseconds < timeoutMs)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(30);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static void Shot(Window w, string name)
    {
        Dispatcher.UIThread.RunJobs();
        var dir = Environment.GetEnvironmentVariable("TNA_SCREENSHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        w.CaptureRenderedFrame()?.Save(Path.Combine(dir, name));
    }

    [AvaloniaFact]
    public async Task Main_window_renders_and_runs_a_job()
    {
        var data = Directory.CreateTempSubdirectory("tna-app-").FullName;
        var media = Environment.GetEnvironmentVariable("TNA_SAMPLE_DIR");
        var queue = new QueueService(new SettingsStore(data));
        var vm = new MainViewModel(queue);
        var w = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
        vm.Dialogs = w;
        w.Show();
        App.ApplyTheme("Dark");
        await Pump(() => !vm.IsDetecting && vm.Hardware.Count > 0, 60000);
        Shot(w, "app-01-empty-dark.png");
        Assert.True(vm.IsEmpty);
        Assert.Contains(vm.Hardware, r => r.Name == "CPU" && r.Av1.Ok);

        if (media is null || !Directory.Exists(media) || queue.Tools is null) return;

        vm.FormSettings = new JobSettings { Profile = ProfileKind.Streaming, Codec = VideoCodec.H264, Resolution = 360, Quality = QualityLevel.Draft };
        vm.AddPaths([media]);
        await Pump(() => vm.Jobs.Count > 0 && vm.Jobs.All(j => j.Job.Media is not null));
        Assert.False(vm.IsEmpty);

        // second job: AV1 10-bit through "Apply to selected"
        vm.FormSettings = new JobSettings { Profile = ProfileKind.UniversalMkv, Codec = VideoCodec.AV1, TenBit = true, Resolution = 360, Quality = QualityLevel.Draft };
        vm.Jobs[^1].IsSelected = true;
        Assert.True(vm.ApplyToSelectedCommand.CanExecute(null));
        vm.ApplyToSelectedCommand.Execute(null);
        vm.Jobs[^1].IsSelected = false;
        await Pump(() => false, 300);
        Shot(w, "app-02-queue-dark.png");

        // editor: every tab renders (trim loads thumbnails and frames through the preview service)
        var editor = new JobEditorWindow(queue, vm.Jobs[0].Job, "tracks") { Width = 1280, Height = 860 };
        editor.Show();
        foreach (var (tab, wait) in new[] { ("tracks", 3000), ("trim", 9000), ("filters", 1500), ("output", 2500), ("preview", 6000) })
        {
            var tabs = editor.GetVisualDescendants().OfType<TabControl>().First();
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().First(t => (string?)t.Tag == tab);
            await Pump(() => false, wait);
            Shot(editor, $"app-05-editor-{tab}.png");
        }
        editor.Close();

        var settings = new SettingsWindow(queue) { Width = 860, Height = 680 };
        settings.Show();
        await Pump(() => false, 800);
        Shot(settings, "app-06-settings.png");
        settings.Close();

        var wizard = new PresetWizardWindow(queue);
        wizard.Show();
        await Pump(() => false, 500);
        Shot(wizard, "app-07-wizard.png");
        wizard.Close();

        await vm.StartStopCommand.ExecuteAsync(null);
        await Pump(() => vm.Jobs.Any(j => j.IsRunning && j.Percent > 20), 60000);
        Shot(w, "app-03-running-dark.png");
        await Pump(() => !vm.IsRunning, 240000);
        Assert.All(vm.Jobs, j => Assert.True(j.IsDone, j.Job.Message + "\n" + j.Job.Log));
        App.ApplyTheme("Light");
        await Pump(() => false, 300);
        Shot(w, "app-04-done-light.png");

        foreach (var j in vm.Jobs) if (j.Job.OutputPath is { } o && File.Exists(o)) File.Delete(o);
        queue.Dispose();
    }

    [Fact]
    public void Launch_arguments_are_parsed()
    {
        var a = LaunchArgs.Parse(["--preset", "Hardsub 720p — phones", "a.mkv", "--start", "b dir"]);
        Assert.Equal("Hardsub 720p — phones", a.Preset);
        Assert.True(a.Start);
        Assert.Equal([Path.GetFullPath("a.mkv"), Path.GetFullPath("b dir")], a.Paths);
        Assert.Null(LaunchArgs.Parse(["--preset="]).Preset);
    }

    [Fact]
    public void Send_to_entries_include_presets()
    {
        var e = ShellIntegration.Entries(["My \"AV1\"", "TV"]);
        Assert.Equal(3, e.Count);
        Assert.Equal("TnA", e[0].Name);
        Assert.Equal("--preset \"My AV1\" --start", e[1].Arguments);
    }

    [Fact]
    public async Task Linux_menu_entries_are_written_and_removed()
    {
        if (!OperatingSystem.IsLinux()) return;
        var home = Directory.CreateTempSubdirectory("tna-xdg-").FullName;
        var old = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", home);
        try
        {
            await ShellIntegration.InstallAsync(["TV / Blu-ray player"]);
            Assert.True(ShellIntegration.IsInstalled());
            var desktop = File.ReadAllText(Path.Combine(home, "applications", "tna.desktop"));
            Assert.Contains("--preset \"TV / Blu-ray player\" --start %F", desktop);
            Assert.True(File.Exists(Path.Combine(home, "kio", "servicemenus", "tna.desktop")));
            Assert.Equal(2, Directory.GetFiles(Path.Combine(home, "nautilus", "scripts")).Length);
            ShellIntegration.Uninstall();
            Assert.False(ShellIntegration.IsInstalled());
            Assert.Empty(Directory.GetFiles(Path.Combine(home, "nautilus", "scripts")));
        }
        finally { Environment.SetEnvironmentVariable("XDG_DATA_HOME", old); }
    }
}
