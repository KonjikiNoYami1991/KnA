using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using TnA.App.ViewModels;
using TnA.App.Views;
using TnA.Core.Queue;
using TnA.Core.Settings;

namespace TnA.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var queue = new QueueService(new SettingsStore());
            ApplyTheme(queue.Settings.Theme);
            var vm = new MainViewModel(queue);
            var window = new MainWindow { DataContext = vm };
            vm.Dialogs = window;
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) =>
            {
                queue.StopAsync().Wait(TimeSpan.FromSeconds(10));
                queue.Dispose();
            };
            if (desktop.Args is { Length: > 0 } args) vm.AddPaths(args);
        }
        base.OnFrameworkInitializationCompleted();
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
