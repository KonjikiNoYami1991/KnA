using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using TnA.App.Platform;
using TnA.App.ViewModels;
using TnA.App.Views;
using TnA.Core.Queue;
using TnA.Core.Settings;

namespace TnA.App;

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
                queue.StopAsync().Wait(TimeSpan.FromSeconds(10));
                queue.SaveQueue();
                queue.Dispose();
            };
            Handle(vm, desktop.Args ?? []);
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
        if (a.Start && vm.Queue.Tools is not null && !vm.Queue.IsRunning) vm.Queue.Start();
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
