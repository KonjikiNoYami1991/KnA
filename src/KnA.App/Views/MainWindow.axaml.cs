using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using KnA.App.ViewModels;
using KnA.Core.Localization;
using KnA.Core.Queue;
using KnA.Core.Settings;

namespace KnA.App.Views;

public partial class MainWindow : Window, IDialogService
{
    public MainWindow()
    {
        InitializeComponent();
        // Smart drag & drop: overlay while dragging, a job under the pointer receives subtitles/audio/fonts.
        AddHandler(DragDrop.DragEnterEvent, (_, e) => DropOverlay.IsVisible = e.Data.Contains(DataFormats.Files));
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => DropOverlay.IsVisible = false);
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
            DropHint.Text = JobUnder(e.Source) is { } j
                ? L.T("Drop on \"{0}\": subtitles, audio and fonts are attached to it.", j.FileName)
                : L.T("Videos, discs and folders are queued, subtitles / audio / fonts join their video, presets are imported.");
        });
        AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            DropOverlay.IsVisible = false;
            var paths = e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
            if (paths is { Count: > 0 } && DataContext is MainViewModel vm) await vm.DropAsync(paths, JobUnder(e.Source)?.Id);
        });
        KeyDown += async (_, e) =>
        {
            // Ctrl+V / Cmd+V: paste files copied in Explorer / Finder.
            if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Meta))
                await PasteAsync();
        };
        Opened += async (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                Width = Math.Max(MinWidth, vm.Queue.Settings.WindowWidth);
                Height = Math.Max(MinHeight, vm.Queue.Settings.WindowHeight);
                BuildQuickPanel(vm);
                vm.FormReloaded += () => BuildQuickPanel(vm);
                await vm.CheckUpdatesOnStartAsync();
            }
        };
        Closing += async (_, e) =>
        {
            if (DataContext is not MainViewModel vm) return;
            if (!_closeConfirmed && vm.Queue.IsRunning && vm.Queue.Settings.ConfirmOnClose)
            {
                e.Cancel = true;
                if (!await ConfirmAsync("KnA", L.T("Encoding is in progress. Stop it and close KnA? Segmented encodes resume from the last finished segment."))) return;
                _closeConfirmed = true;
                Close();
                return;
            }
            if (WindowState == WindowState.Normal)
            {
                vm.Queue.Settings.WindowWidth = Width;
                vm.Queue.Settings.WindowHeight = Height;
                vm.Queue.SaveSettings();
            }
        };
    }

    private bool _closeConfirmed;

    private static JobViewModel? JobUnder(object? source)
    {
        for (var c = source as Control; c is not null; c = c.Parent as Control)
            if (c.DataContext is JobViewModel j) return j;
        return null;
    }

    /// <summary>Side panel: preset picker + wizard, then the shared settings form.</summary>
    private void BuildQuickPanel(MainViewModel vm)
    {
        var presets = vm.Presets;
        var items = new List<(string?, string)> { (null, L.T("— custom —")) };
        items.AddRange(presets.Select(p => ((string?)p.Name, (p.BuiltIn ? "" : "★ ") + p.Name)));
        var combo = Ui.Combo(items, vm.FormSettings.PresetName is { } n && presets.Any(p => p.Name == n) ? n : null, name =>
        {
            if (name is not null && presets.FirstOrDefault(p => p.Name == name) is { } p) vm.ApplyPreset(p);
        });
        var wizard = IconButton("IconWand");
        ToolTip.SetTip(wizard, L.T("Preset wizard"));
        wizard.Click += async (_, _) => await vm.RunWizardAsync();
        var aiButton = IconButton("IconWand");
        aiButton.Content = new TextBlock { Text = "AI", FontWeight = Avalonia.Media.FontWeight.Bold, FontSize = 12 };
        ToolTip.SetTip(aiButton, L.T("Ask the AI assistant"));
        aiButton.Click += async (_, _) =>
        {
            var w = new AiAssistantWindow(vm.Queue, vm.FormSettings) { Icon = Icon };
            await w.ShowDialog(this);
            if (w.Accepted is { } s) { vm.FormSettings = s with { PresetName = null }; BuildQuickPanel(vm); }
        };
        var saveAs = IconButton("IconSave");
        ToolTip.SetTip(saveAs, L.T("Save as preset"));
        saveAs.Click += async (_, _) =>
        {
            if (await PromptAsync(L.T("Save as preset"), L.T("Preset name"), vm.FormSettings.PresetName ?? "") is { Length: > 0 } name)
            {
                try { vm.SaveAsPreset(name); }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException) { await ShowTextAsync(L.T("Error"), ex.Message); }
            }
        };
        var current = presets.FirstOrDefault(p => p.Name == vm.FormSettings.PresetName);
        var delete = IconButton("IconTrash");
        delete.IsEnabled = current is { BuiltIn: false };
        ToolTip.SetTip(delete, L.T("Delete preset"));
        delete.Click += async (_, _) => { if (current is not null) await vm.DeletePresetAsync(current); };

        var row = new DockPanel();
        var buttons = Ui.Row(4, wizard, aiButton, saveAs, delete);
        buttons.Margin = new Thickness(6, 0, 0, 0);
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(combo);

        var form = new SettingsForm(vm.FormSettings, () => vm.Queue.Settings.Capabilities, full: false, () => vm.Queue.Tools?.Fdkaac is not null);
        form.Changed += s =>
        {
            // Any manual change detaches the form from its preset.
            vm.FormSettings = s with { PresetName = null };
            if (combo.SelectedIndex != 0) combo.SelectedIndex = 0;
            delete.IsEnabled = false;
        };
        QuickPanel.Content = new StackPanel { Spacing = 12, Children = { Ui.Field("Preset", row), form } };
    }

    private Button IconButton(string key) => new()
    {
        Padding = new Thickness(9, 6),
        Content = new PathIcon { Data = this.FindResource(key) as Geometry, Width = 16, Height = 16 },
    };

    private async Task<string?> PromptAsync(string title, string label, string value)
    {
        string? result = null;
        var box = new TextBox { Text = value, Watermark = label, MinWidth = 340 };
        var ok = new Button { Content = L.T("Save"), Classes = { "primary" }, MinWidth = 80, IsDefault = true };
        var cancel = new Button { Content = L.T("Cancel"), MinWidth = 80, IsCancel = true };
        var win = new Window
        {
            Title = title, SizeToContent = SizeToContent.WidthAndHeight, CanResize = false, Icon = Icon,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20), Spacing = 14,
                Children = { Ui.Field(label, box), new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } } },
            },
        };
        ok.Click += (_, _) => { result = box.Text; win.Close(); };
        cancel.Click += (_, _) => win.Close();
        box.AttachedToVisualTree += (_, _) => box.Focus();
        await win.ShowDialog(this);
        return result?.Trim();
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
        if (paths.Count > 0) await vm.DropAsync(paths);
    }

    // ------------------------------------------------------------------ IDialogService

    public async Task<IReadOnlyList<string>> PickFilesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L.T("Add files"),
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType(L.T("Video")) { Patterns = KnA.Core.Settings.MediaExtensions.Video.Select(e => "*" + e).ToList() },
                FilePickerFileTypes.All,
            ],
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    public async Task<string?> PickFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = L.T("Add this folder") });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    public Task ShowTextAsync(string title, string text) => TextWindow.ShowAsync(this, title, text);

    public async Task<bool> ConfirmAsync(string title, string message)
    {
        bool result = false;
        var yes = new Button { Content = L.T("Yes"), Classes = { "primary" }, MinWidth = 80 };
        var no = new Button { Content = L.T("No"), MinWidth = 80 };
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

    public Task ShowSettingsAsync(MainViewModel vm, string tab = "general") => new SettingsWindow(vm.Queue, tab, vm.Watch) { Icon = Icon }.ShowDialog(this);

    public Task ShowEditorAsync(MainViewModel vm, QueueJob job, string tab) => new JobEditorWindow(vm.Queue, job, tab) { Icon = Icon }.ShowDialog(this);

    public async Task<Preset?> ShowWizardAsync(MainViewModel vm)
    {
        var w = new PresetWizardWindow(vm.Queue) { Icon = Icon };
        await w.ShowDialog(this);
        return w.Result;
    }

    public void CloseApp() => Close();
}
