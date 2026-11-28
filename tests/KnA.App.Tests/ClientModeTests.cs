using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KnA.App.ViewModels;
using KnA.App.Views;
using KnA.Core.Queue;
using KnA.Core.Remote;
using KnA.Core.Settings;

namespace KnA.App.Tests;

/// <summary>The desktop app on a KnA server (client mode).</summary>
public class ClientModeTests
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
    public async Task An_unreachable_server_leaves_this_computers_queue_in_place()
    {
        var data = Directory.CreateTempSubdirectory("kna-client-").FullName;
        var queue = new QueueService(new SettingsStore(data), restoreQueue: false);
        var vm = new MainViewModel(queue);
        var window = new MainWindow { DataContext = vm };
        vm.Dialogs = window;
        window.Show();
        var ex = await Assert.ThrowsAsync<RemoteException>(() => vm.ConnectAsync(new RemoteServerSettings { Url = "127.0.0.1:1", User = "a" }, "b"));
        Assert.Contains("127.0.0.1:1", ex.Message);
        Assert.False(vm.IsRemote);
        Assert.Same(queue, vm.Engine);

        // The settings page of the connection opens on its tab.
        var settings = new SettingsWindow(queue, "server", vm.Watch, vm);
        settings.Show();
        await Pump(() => settings.GetVisualDescendants().OfType<ServerPage>().Any());
        Assert.NotNull(settings.GetVisualDescendants().OfType<ServerPage>().FirstOrDefault());
        Shot(settings, "settings-server.png");
        settings.Close();
        window.Close();
    }

    /// <summary>Against a real server (KNA_TEST_SERVER=url|user|password): its queue in the main window, then back.</summary>
    [AvaloniaFact]
    public async Task The_main_window_shows_the_servers_queue_and_switches_back()
    {
        if (Environment.GetEnvironmentVariable("KNA_TEST_SERVER")?.Split('|') is not [var url, var user, var password]) return;
        var data = Directory.CreateTempSubdirectory("kna-client-").FullName;
        var queue = new QueueService(new SettingsStore(data), restoreQueue: false);
        var vm = new MainViewModel(queue);
        var window = new MainWindow { DataContext = vm, Width = 1300, Height = 800 };
        vm.Dialogs = window;
        window.Show();
        await vm.ConnectAsync(new RemoteServerSettings { Url = url, User = user, Enabled = true }, password);
        Assert.True(vm.IsRemote);
        await Pump(() => vm.Jobs.Count > 0, 5000);
        Assert.Contains("🌐", vm.EngineLabel);
        Shot(window, "main-on-server.png");
        await vm.DisconnectAsync();
        Assert.False(vm.IsRemote);
        Assert.Contains(queue.Settings.Remote.Enabled ? "x" : "🖥", vm.EngineLabel);
        window.Close();
    }
}
