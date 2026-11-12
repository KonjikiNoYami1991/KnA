using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using TnA.App.ViewModels;
using TnA.Core.Settings;

namespace TnA.App.Views;

public partial class MainWindow : Window, IDialogService
{
    public MainWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
        });
        AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            var paths = e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
            if (paths is { Count: > 0 } && DataContext is MainViewModel vm) vm.AddPaths(paths);
        });
        KeyDown += async (_, e) =>
        {
            // Ctrl+V / Cmd+V: paste files copied in Explorer / Finder.
            if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Meta))
                await PasteAsync();
        };
        Opened += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                Width = Math.Max(MinWidth, vm.Queue.Settings.WindowWidth);
                Height = Math.Max(MinHeight, vm.Queue.Settings.WindowHeight);
            }
        };
        Closing += (_, _) =>
        {
            if (DataContext is MainViewModel vm && WindowState == WindowState.Normal)
            {
                vm.Queue.Settings.WindowWidth = Width;
                vm.Queue.Settings.WindowHeight = Height;
                vm.Queue.SaveSettings();
            }
        };
    }

    private async Task PasteAsync()
    {
        if (Clipboard is null || DataContext is not MainViewModel vm) return;
        var paths = new List<string>();
#pragma warning disable CS0618 // classic clipboard API is the one available on every platform in Avalonia 11
        if (await Clipboard.GetDataAsync(DataFormats.Files) is IEnumerable<IStorageItem> items)
            paths.AddRange(items.Select(i => i.TryGetLocalPath()).OfType<string>());
        else if (await Clipboard.GetTextAsync() is { } text)
            paths.AddRange(text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
#pragma warning restore CS0618
        if (paths.Count > 0) vm.AddPaths(paths);
    }

    // ------------------------------------------------------------------ IDialogService

    public async Task<IReadOnlyList<string>> PickFilesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add video files",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Video files") { Patterns = TnA.Core.Settings.MediaExtensions.Video.Select(e => "*" + e).ToList() },
                FilePickerFileTypes.All,
            ],
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    public async Task<string?> PickFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Add a folder" });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    public Task ShowTextAsync(string title, string text)
    {
        var box = new TextBox
        {
            Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Cascadia Code,Consolas,Menlo,monospace"), FontSize = 12,
        };
        var copy = new Button { Content = "Copy", HorizontalAlignment = HorizontalAlignment.Right };
        var win = new Window
        {
            Title = title, Width = 900, Height = 560, Icon = Icon, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new DockPanel
            {
                Margin = new Thickness(14),
                Children = { WithDock(copy, Dock.Bottom), box },
            },
        };
        copy.Margin = new Thickness(0, 10, 0, 0);
        copy.Click += async (_, _) => { if (win.Clipboard is { } c) await c.SetTextAsync(text); };
        return win.ShowDialog(this);
    }

    public async Task<bool> ConfirmAsync(string title, string message)
    {
        bool result = false;
        var yes = new Button { Content = "Yes", Classes = { "primary" }, MinWidth = 80 };
        var no = new Button { Content = "No", MinWidth = 80 };
        var win = new Window
        {
            Title = title, SizeToContent = SizeToContent.WidthAndHeight, CanResize = false, Icon = Icon,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20), Spacing = 16, MaxWidth = 420,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { no, yes } },
                },
            },
        };
        yes.Click += (_, _) => { result = true; win.Close(); };
        no.Click += (_, _) => win.Close();
        await win.ShowDialog(this);
        return result;
    }

    public Task ShowSettingsAsync(MainViewModel vm) => new SettingsWindow(vm.Queue) { Icon = Icon }.ShowDialog(this);

    public void CloseApp() => Close();

    private static Control WithDock(Control c, Dock dock)
    {
        DockPanel.SetDock(c, dock);
        return c;
    }
}
