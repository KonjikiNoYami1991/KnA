using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using KnA.App.Platform;
using KnA.App.ViewModels;
using KnA.App.Views;
using KnA.Core.Queue;
using KnA.Core.Settings;

namespace KnA.App;

public partial class App : Application
{
    private readonly CancellationTokenSource _listen = new();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var store = new SettingsStore();
            var queue = new QueueService(store);
            ApplyTheme(queue.Settings.Theme);
            var vm = new MainViewModel(queue);
            var window = new MainWindow { DataContext = vm };
            vm.Dialogs = window;
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) =>
            {
                _listen.Cancel();
                // On a server the jobs go on there; uploads resume at the next start.
                if (vm.Remote is { } remote) remote.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
                queue.StopAsync().Wait(TimeSpan.FromSeconds(10));
                queue.SaveQueue();
                queue.Dispose();
            };
            // Encode on the KnA server chosen last time (falls back to this computer, saying why).
            window.Opened += async (_, _) => { await vm.ConnectSavedServerAsync(); Handle(vm, desktop.Args ?? []); };
            SingleInstance.Listen(args => Dispatcher.UIThread.Post(() =>
            {
                Handle(vm, args);
                if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                window.Activate();
            }), _listen.Token);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static void Handle(MainViewModel vm, IReadOnlyList<string> raw)
    {
        var a = LaunchArgs.Parse(raw);
        if (a.Paths.Count > 0) vm.AddPaths(a.Paths, a.Preset);
        if (a.Start && vm.Remote is { } remote) { if (!remote.IsRunning) remote.Start(); }
        else if (a.Start && vm.Queue.Tools is not null && !vm.Queue.IsRunning) vm.Queue.Start();
    }

    public static void ApplyTheme(string theme)
    {
        if (Current is null) return;
        Current.RequestedThemeVariant = theme switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }
}
