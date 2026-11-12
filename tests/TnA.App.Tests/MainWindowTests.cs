using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
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

    private static void Shot(MainWindow w, string name)
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

        vm.SelectedProfile = Profiles.Streaming;
        vm.SelectedCodec = vm.CodecOptions.First(c => c.Value == VideoCodec.H264);
        vm.SelectedResolution = vm.ResolutionOptions.First(r => r.Value == 360);
        vm.SelectedQuality = vm.QualityOptions.First(q => q.Value == QualityLevel.Draft);
        vm.AddPaths([media]);
        await Pump(() => vm.Jobs.Count > 0 && vm.Jobs.All(j => j.Job.Media is not null));
        Assert.False(vm.IsEmpty);

        // second job: AV1 10-bit through "Apply to selected"
        vm.SelectedProfile = Profiles.UniversalMkv;
        vm.SelectedCodec = vm.CodecOptions.First(c => c.Value == VideoCodec.AV1);
        vm.TenBit = true;
        vm.Jobs[1].IsSelected = true;
        Assert.True(vm.ApplyToSelectedCommand.CanExecute(null));
        vm.ApplyToSelectedCommand.Execute(null);
        vm.Jobs[1].IsSelected = false;
        await Pump(() => false, 300);
        Shot(w, "app-02-queue-dark.png");

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
}
