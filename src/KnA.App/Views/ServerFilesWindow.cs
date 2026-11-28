using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using KnA.Core.Localization;
using KnA.Core.Remote;

namespace KnA.App.Views;

/// <summary>The KnA server's own folders (the account's allowed ones): videos and folders picked here are queued where they are.</summary>
public sealed class ServerFilesWindow : Window
{
    private readonly KnaClient _client;
    private readonly TextBlock _path = new() { FontWeight = Avalonia.Media.FontWeight.SemiBold, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
    private readonly StackPanel _list = new() { Spacing = 2 };
    private readonly TextBlock _status = new() { Classes = { "muted" }, FontSize = 12 };
    private readonly HashSet<string> _picked = [];
    private string? _parent;

    /// <summary>The server paths chosen (files and folders).</summary>
    public IReadOnlyList<string> Picked { get; private set; } = [];

    public ServerFilesWindow(KnaClient client)
    {
        _client = client;
        Title = L.T("Files of the KnA server");
        Width = 760; Height = 620; MinWidth = 520; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var up = Ui.Button("↑", async () => await LoadAsync(_parent));
        var roots = Ui.Button(L.T("Folders of the server"), async () => await LoadAsync(null));
        var add = Ui.Button(L.T("Add to the queue"), () => { Picked = [.. _picked]; Close(); }, primary: true);
        var cancel = Ui.Button(L.T("Cancel"), Close);
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var nav = Ui.Row(6, up, roots);
        DockPanel.SetDock(nav, Dock.Left);
        top.Children.Add(nav);
        top.Children.Add(new Border { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Child = _path });
        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var buttons = Ui.Row(8, cancel, add);
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        footer.Children.Add(_status);
        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = _list });
        Content = root;
        Opened += async (_, _) => await LoadAsync(null);
    }

    private async Task LoadAsync(string? path)
    {
        _status.Text = L.T("Loading…");
        try
        {
            var f = await _client.BrowseAsync(path);
            _parent = f.Parent;
            _path.Text = f.Path ?? L.T("Folders of the server");
            _list.Children.Clear();
            foreach (var e in f.Entries.Where(e => e.IsDirectory || e.Kind is "video" or "disc"))
            {
                var check = new CheckBox { IsChecked = _picked.Contains(e.Path), VerticalAlignment = VerticalAlignment.Center };
                check.IsCheckedChanged += (_, _) => { if (check.IsChecked == true) _picked.Add(e.Path); else _picked.Remove(e.Path); Count(); };
                var name = new Button
                {
                    Content = (e.IsDirectory ? "📁 " : "🎞 ") + e.Name + (e.IsDirectory ? "" : $"   ({e.Size / 1048576.0:0.#} MB)"),
                    Classes = { "ghost" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                };
                name.Click += async (_, _) =>
                {
                    if (e.IsDirectory) await LoadAsync(e.Path);
                    else check.IsChecked = check.IsChecked != true;
                };
                var row = new DockPanel();
                DockPanel.SetDock(check, Dock.Left);
                row.Children.Add(check);
                row.Children.Add(name);
                _list.Children.Add(row);
            }
            if (_list.Children.Count == 0) _list.Children.Add(Ui.Muted(L.T("No videos here.")));
            Count();
        }
        catch (RemoteException ex) { _status.Text = ex.Message; }
    }

    private void Count() => _status.Text = _picked.Count == 0
        ? L.T("Tick videos or folders (a folder adds every video inside it).")
        : L.T("{0} selected", _picked.Count);
}
