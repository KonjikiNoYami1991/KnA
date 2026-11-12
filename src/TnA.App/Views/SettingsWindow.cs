using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TnA.App.ViewModels;
using TnA.Core.Encoding;
using TnA.Core.Queue;
using TnA.Core.Settings;
using TnA.Core.Tools;

namespace TnA.App.Views;

/// <summary>Settings dialog (built in code: it is a plain form).</summary>
public sealed class SettingsWindow : Window
{
    private readonly QueueService _queue;
    private readonly TextBox _output = new() { Watermark = "Same folder as the source file" };
    private readonly TextBox _ffmpeg = new() { Watermark = "Auto-detect" };
    private readonly TextBox _vaapi = new() { Watermark = VideoEncoders.DefaultVaapiDevice };
    private readonly TextBox _extra = new() { Watermark = "-x265-params …", FontFamily = new FontFamily("Cascadia Code,Consolas,Menlo,monospace") };
    private readonly ComboBox _post = new() { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = OptionLists.PostActions };
    private readonly WrapPanel _extensions = new();
    private readonly TextBlock _tools = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly Button _download = new() { Content = "Download FFmpeg" };

    public SettingsWindow(QueueService queue)
    {
        _queue = queue;
        Title = "Settings";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var s = queue.Settings;
        _output.Text = s.OutputDirectory;
        _ffmpeg.Text = s.FfmpegPath;
        _vaapi.Text = s.VaapiDevice;
        _extra.Text = s.ExtraArguments;
        _post.SelectedItem = OptionLists.PostActions.First(o => o.Value == s.PostAction);
        foreach (var ext in TnA.Core.Settings.MediaExtensions.Video)
            _extensions.Children.Add(new CheckBox { Content = ext, IsChecked = s.FolderExtensions.Contains(ext), Margin = new Thickness(0, 0, 10, 0) });

        _download.IsVisible = FfmpegDownloader.DownloadUrl is not null;
        _download.Click += async (_, _) => await DownloadAsync();
        RefreshTools();

        var save = new Button { Content = "Save", Classes = { "primary" }, MinWidth = 90 };
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        save.Click += (_, _) => { Save(); Close(); };
        cancel.Click += (_, _) => Close();

        var form = new StackPanel { Spacing = 14, Margin = new Thickness(20) };
        form.Children.Add(Field("OUTPUT FOLDER", WithBrowse(_output, folder: true), "Empty = next to each source file. Existing files are never overwritten."));
        form.Children.Add(Field("WHEN THE QUEUE IS FINISHED", _post));
        form.Children.Add(Field("EXTENSIONS IMPORTED FROM FOLDERS", _extensions));
        form.Children.Add(Field("FFMPEG", WithBrowse(_ffmpeg, folder: false), null));
        if (OperatingSystem.IsLinux()) form.Children.Add(Field("VA-API DEVICE", _vaapi));
        form.Children.Add(Field("EXTRA FFMPEG ARGUMENTS (ADVANCED)", _extra));
        form.Children.Add(new Border
        {
            Background = (IBrush?)this.FindResource("CardBackground2") ?? Brushes.Transparent,
            CornerRadius = new CornerRadius(10), Padding = new Thickness(12), Child = _tools,
        });
        form.Children.Add(new DockPanel
        {
            Children =
            {
                _download,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, save } },
            },
        });
        DockPanel.SetDock(_download, Dock.Left);
        Content = form;
    }

    private Control Field(string label, Control input, string? hint = null)
    {
        var p = new StackPanel { Spacing = 4 };
        p.Children.Add(new TextBlock { Text = label, Classes = { "label" } });
        p.Children.Add(input);
        if (hint is not null) p.Children.Add(new TextBlock { Text = hint, Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        return p;
    }

    private Control WithBrowse(TextBox box, bool folder)
    {
        var b = new Button { Content = "Browse…", Margin = new Thickness(8, 0, 0, 0) };
        b.Click += async (_, _) =>
        {
            if (folder)
            {
                var r = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions());
                if (r.FirstOrDefault()?.TryGetLocalPath() is { } p) box.Text = p;
            }
            else
            {
                var r = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Select ffmpeg" });
                if (r.FirstOrDefault()?.TryGetLocalPath() is { } p) box.Text = p;
            }
        };
        var dp = new DockPanel();
        DockPanel.SetDock(b, Dock.Right);
        dp.Children.Add(b);
        dp.Children.Add(box);
        return dp;
    }

    private void RefreshTools()
    {
        var t = _queue.Tools;
        _tools.Text = t is null
            ? "FFmpeg not found. " + FfmpegDownloader.ManualInstructions
            : $"FFmpeg: {t.Ffmpeg}\nFFprobe: {t.Ffprobe}\nSCXviD: {t.Scxvid ?? "not found (keyframes use FFmpeg scene detection)"}\nData folder: {_queue.Store.DataDirectory}";
    }

    private async Task DownloadAsync()
    {
        _download.IsEnabled = false;
        try
        {
            var progress = new Progress<double>(p => Dispatcher.UIThread.Post(() => _download.Content = $"Downloading… {p:0}%"));
            var path = await Task.Run(() => FfmpegDownloader.DownloadAsync(_queue.Store.DataDirectory, progress));
            _ffmpeg.Text = "";
            _queue.Settings.FfmpegPath = null;
            _queue.RefreshTools();
            RefreshTools();
            _download.Content = "FFmpeg installed ✓";
            _ = path;
        }
        catch (Exception ex)
        {
            _download.Content = "Download failed";
            _tools.Text = ex.Message;
        }
        finally
        {
            _download.IsEnabled = true;
        }
    }

    private void Save()
    {
        var s = _queue.Settings;
        s.OutputDirectory = string.IsNullOrWhiteSpace(_output.Text) ? null : _output.Text.Trim();
        s.FfmpegPath = string.IsNullOrWhiteSpace(_ffmpeg.Text) ? null : _ffmpeg.Text.Trim();
        s.VaapiDevice = string.IsNullOrWhiteSpace(_vaapi.Text) ? null : _vaapi.Text.Trim();
        s.ExtraArguments = _extra.Text ?? "";
        if (_post.SelectedItem is Option<PostQueueAction> pa) s.PostAction = pa.Value;
        s.FolderExtensions = _extensions.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Content!).ToList();
        _queue.SaveSettings();
        _queue.RefreshTools();
    }
}
