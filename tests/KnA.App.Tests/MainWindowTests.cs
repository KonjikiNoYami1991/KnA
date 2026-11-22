using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Controls;
using KnA.App.Platform;
using KnA.App.ViewModels;
using KnA.App.Views;
using KnA.Core.Encoding;
using KnA.Core.Queue;
using KnA.Core.Settings;

[assembly: AvaloniaTestApplication(typeof(KnA.App.Tests.TestAppBuilder))]

namespace KnA.App.Tests;

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
        var dir = Environment.GetEnvironmentVariable("KNA_SCREENSHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        w.CaptureRenderedFrame()?.Save(Path.Combine(dir, name));
    }

    [AvaloniaFact]
    public async Task Ocr_window_reads_and_shows_picture_subtitles()
    {
        var data = Directory.CreateTempSubdirectory("kna-ocr-").FullName;
        var queue = new QueueService(new SettingsStore(data), restoreQueue: false);
        var font = "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf";
        if (queue.Tools is null || !File.Exists(font) || KnA.Core.Ocr.SubtitleOcr.LocateTesseract(data) is null) return;
        async Task<KnA.Core.Ocr.PgsWriter.Picture> Pic(string text, double a, double b)
        {
            var raw = Path.Combine(data, Guid.NewGuid().ToString("N") + ".gray");
            await KnA.Core.Processing.ProcessRunner.RunAsync(queue.Tools.Ffmpeg, ["-v", "error", "-y", "-f", "lavfi", "-i", "color=black:s=900x80",
                "-vf", $"drawtext=fontfile={font}:text='{text}':fontcolor=white:fontsize=46:x=20:y=14:borderw=3:bordercolor=0x101010",
                "-frames:v", "1", "-pix_fmt", "gray", "-f", "rawvideo", raw]);
            var g = File.ReadAllBytes(raw);
            return new(a, b, 900, 80, g.Select(v => v > 200 ? (byte)1 : v > 90 ? (byte)3 : v > 8 ? (byte)2 : (byte)0).ToArray());
        }
        var sup = Path.Combine(data, "s.sup");
        File.WriteAllBytes(sup, KnA.Core.Ocr.PgsWriter.Write([await Pic("Nice to meet you", 1, 2), await Pic("The weather is fine", 3, 4), await Pic("See you tomorrow", 5, 6)]));
        var mkv = Path.Combine(data, "film.mkv");
        await KnA.Core.Processing.ProcessRunner.RunAsync(queue.Tools.Ffmpeg, ["-v", "error", "-y", "-f", "lavfi", "-i", "color=black:s=320x180:d=7",
            "-i", sup, "-copyts", "-map", "0:v", "-map", "1:s", "-c:v", "libx264", "-preset", "ultrafast", "-c:s", "copy", "-metadata:s:s:0", "language=eng", mkv]);
        var job = queue.AddPaths([mkv], new JobSettings()).Single();
        await Pump(() => job.Media is not null);
        var session = await KnA.Core.Ocr.OcrSession.OpenAsync(queue, job, mkv, job.Media!.Subtitles[0].Index);
        var w = new OcrWindow(queue, session) { Width = 1200, Height = 760 };
        w.Show();
        App.ApplyTheme("Dark");
        await Pump(() => session.Lines.All(l => l.Confidence >= 0), 60000);
        await Pump(() => false, 800);
        Shot(w, "app-08-ocr.png");
        Assert.Contains("weather", session.Lines[1].Text);
        w.Close();
        queue.Dispose();
    }

    [AvaloniaFact]
    public async Task Main_window_renders_and_runs_a_job()
    {
        var data = Directory.CreateTempSubdirectory("kna-app-").FullName;
        var media = Environment.GetEnvironmentVariable("KNA_SAMPLE_DIR");
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
        var editor = new JobEditorWindow(queue, vm.Jobs[0].Job, "video") { Width = 1280, Height = 860 };
        editor.Show();
        foreach (var (tab, wait) in new[] { ("preset", 3000), ("trim", 9000), ("video", 3000), ("audio", 3000), ("subtitles", 1500), ("filters", 1500), ("output", 2500), ("preview", 6000) })
        {
            var tabs = editor.GetVisualDescendants().OfType<TabControl>().First();
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().First(t => (string?)t.Tag == tab);
            await Pump(() => false, wait);
            Shot(editor, $"app-05-editor-{tab}.png");
            if (tab == "video")
            {
                // every parameter of the encoder on sub-tabs of the Video tab, read from its help
                TabControl Sub() => editor.GetVisualDescendants().OfType<TabControl>().First(t => t.Items.OfType<TabItem>().Any(i => (string?)i.Tag == "general"));
                await Pump(() => Sub().Items.OfType<TabItem>().Any(i => i.Tag is string tg && tg.StartsWith("p:", StringComparison.Ordinal)), 8000);
                var sub = Sub();
                Assert.Contains(sub.Items.OfType<TabItem>(), i => (string?)i.Tag == "rate");
                sub.SelectedItem = sub.Items.OfType<TabItem>().First(i => (string?)i.Tag == "rate");
                await Pump(() => false, 1500);
                Shot(editor, "app-05-editor-video-rate.png");
                sub.SelectedItem = sub.Items.OfType<TabItem>().First(i => i.Tag is string tg && tg.StartsWith("p:", StringComparison.Ordinal));
                await Pump(() => false, 2500);
                Shot(editor, "app-05-editor-video-parameters.png");
            }
        }
        editor.Close();

        // standalone trim on a small window: only the cut, and the frame fits the visible area
        var trim = new JobEditorWindow(queue, vm.Jobs[0].Job, JobEditorTabs.TrimOnly) { Width = 960, Height = 640 };
        trim.Show();
        await Pump(() => false, 9000);
        Shot(trim, "app-05-trim-only.png");
        Assert.False(trim.GetVisualDescendants().OfType<TabControl>().First().IsVisible);
        trim.Close();

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
        Assert.Equal("KnA", e[0].Name);
        Assert.Equal("--preset \"My AV1\" --start", e[1].Arguments);
    }

    [Fact]
    public async Task Linux_menu_entries_are_written_and_removed()
    {
        if (!OperatingSystem.IsLinux()) return;
        var home = Directory.CreateTempSubdirectory("kna-xdg-").FullName;
        var old = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", home);
        try
        {
            await ShellIntegration.InstallAsync(["TV / Blu-ray player"]);
            Assert.True(ShellIntegration.IsInstalled());
            var desktop = File.ReadAllText(Path.Combine(home, "applications", "kna.desktop"));
            Assert.Contains("--preset \"TV / Blu-ray player\" --start %F", desktop);
            Assert.True(File.Exists(Path.Combine(home, "kio", "servicemenus", "kna.desktop")));
            Assert.Equal(2, Directory.GetFiles(Path.Combine(home, "nautilus", "scripts")).Length);
            ShellIntegration.Uninstall();
            Assert.False(ShellIntegration.IsInstalled());
            Assert.Empty(Directory.GetFiles(Path.Combine(home, "nautilus", "scripts")));
        }
        finally { Environment.SetEnvironmentVariable("XDG_DATA_HOME", old); }
    }
}
