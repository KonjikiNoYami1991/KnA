using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using KnA.Core.Encoding;
using KnA.Core.Localization;
using KnA.Core.Queue;
using KnA.Core.Settings;

namespace KnA.App.Views;

/// <summary>
/// Preset manager: list on the left, every setting of the selected preset on the right (the same full form as the
/// editor). Create (new / duplicate / from the side panel), rename, update, delete, import/export, and apply to the
/// selected files choosing which parts (video, audio, subtitles, filters, track selection) the preset replaces.
/// Built-in presets are read-only: changing one saves a copy.
/// </summary>
public sealed class PresetManagerWindow : Window
{
    private readonly QueueService _queue;
    private readonly Func<JobSettings> _current;
    private readonly Func<IReadOnlyList<Guid>> _selected;
    private readonly ListBox _list = new() { MinWidth = 260 };
    private readonly ContentControl _detail = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12.5 };
    private Preset? _preset;
    private JobSettings _edited = new();
    private string _name = "", _description = "";
    private PresetParts _parts = PresetParts.All;

    /// <summary>Set when the user chose "Use for new files".</summary>
    public Preset? UseForNewFiles { get; private set; }
    /// <summary>True when presets were added, changed or removed.</summary>
    public bool Changed { get; private set; }

    public PresetManagerWindow(QueueService queue, Func<JobSettings> current, Func<IReadOnlyList<Guid>> selected, string? select = null)
    {
        _queue = queue;
        _current = current;
        _selected = selected;
        Title = L.T("Presets") + " — KnA";
        Width = 1200; Height = 820; MinWidth = 900; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var add = Ui.Button(L.T("New from the side panel"), () =>
        {
            var name = Unique(L.T("New preset"));
            Save(new Preset(name, null, _current() with { PresetName = name }), name);
        });
        var import = Ui.Button(L.T("Import…"), ImportAsync);
        var left = new DockPanel { Margin = new Thickness(14, 14, 0, 14) };
        var bar = Ui.Row(6, add, import);
        bar.Margin = new Thickness(0, 8, 0, 0);
        DockPanel.SetDock(bar, Dock.Bottom);
        left.Children.Add(bar);
        left.Children.Add(new Border { Classes = { "card" }, Padding = new Thickness(4), Child = _list });
        _list.SelectionChanged += (_, _) => { if (_list.SelectedItem is ListBoxItem { Tag: Preset p }) Show(p); };

        var right = new ScrollViewer { Content = new Border { Padding = new Thickness(16, 14, 16, 16), Child = _detail } };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("300,*") };
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        Content = grid;
        Fill(select);
    }

    private void Fill(string? select)
    {
        _list.Items.Clear();
        foreach (var p in _queue.Presets.All)
        {
            var item = new ListBoxItem
            {
                Tag = p,
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = (p.BuiltIn ? "🔒 " : "★ ") + p.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                        Ui.Muted(p.Settings.Label, 11.5),
                    },
                },
            };
            _list.Items.Add(item);
            if (p.Name == select) _list.SelectedItem = item;
        }
        if (_list.SelectedItem is null && _list.Items.Count > 0) _list.SelectedIndex = 0;
    }

    private string Unique(string baseName)
    {
        var name = baseName;
        for (int i = 2; _queue.Presets.Get(name) is not null; i++) name = $"{baseName} ({i})";
        return name;
    }

    private void Show(Preset p)
    {
        _preset = p;
        _edited = p.Settings;
        _name = p.Name;
        _description = p.Description ?? "";
        _status.Text = "";
        var root = new StackPanel { Spacing = 12 };
        SettingsForm? formRef = null;

        var nameBox = Ui.Text(p.Name, v => _name = v.Trim());
        var descBox = Ui.Text(p.Description, v => _description = v.Trim(), L.T("What it is for (optional)"));
        root.Children.Add(Ui.Columns(Ui.Field("Preset name", nameBox), Ui.Field("Description", descBox)));
        if (p.BuiltIn) root.Children.Add(Ui.Note(L.T("Built-in preset: your changes are saved as a new preset.")));

        // ------------------------------------------------ actions
        var save = Ui.Button(p.BuiltIn ? L.T("Save as new preset") : L.T("Save changes"), () =>
        {
            var name = p.BuiltIn && _name == p.Name ? Unique(p.Name + " — " + L.T("custom")) : _name;
            Save(new Preset(name, string.IsNullOrWhiteSpace(_description) ? null : _description, _edited with { PresetName = name }), p.BuiltIn ? null : p.Name);
        }, primary: true);
        var duplicate = Ui.Button(L.T("Duplicate"), () =>
        {
            var name = Unique(_name + " — " + L.T("copy"));
            Save(new Preset(name, string.IsNullOrWhiteSpace(_description) ? null : _description, _edited with { PresetName = name }), null);
        });
        var fromPanel = Ui.Button(L.T("Take the side panel settings"), () =>
        {
            _edited = _current();
            if (formRef is not null) { formRef.AllowedContainers = Containers.Allowed(_edited); formRef.Settings = _edited; }
        });
        ToolTip.SetTip(fromPanel, L.T("Replaces the settings below with the ones in the side panel (save to keep them)."));
        var export = Ui.Button(L.T("Export…"), ExportAsync);
        var delete = Ui.Button(L.T("Delete"), () =>
        {
            if (p.BuiltIn) return;
            _queue.Presets.Delete(p.Name);
            Changed = true;
            Fill(null);
        });
        delete.IsEnabled = !p.BuiltIn;
        root.Children.Add(new WrapPanel { Children = { Spaced(save), Spaced(duplicate), Spaced(fromPanel), Spaced(export), Spaced(delete) } });

        // ------------------------------------------------ apply
        var apply = new StackPanel { Spacing = 8, Children = { Ui.H2(L.T("Apply")) } };
        var parts = new WrapPanel();
        foreach (var (part, label) in new[]
        {
            (PresetParts.Video, L.T("Video (profile, codec, quality, resolution, HDR, encoder)")), (PresetParts.Audio, L.T("Audio")),
            (PresetParts.Subtitles, L.T("Subtitles")), (PresetParts.Filters, L.T("Picture filters")), (PresetParts.Tracks, L.T("Track selection")),
        })
        {
            var pr = part;
            parts.Children.Add(Spaced(Ui.Check(label, _parts.HasFlag(pr), v => _parts = v ? _parts | pr : _parts & ~pr)));
        }
        apply.Children.Add(Ui.Muted(L.T("Choose what the preset replaces; everything else of each file (and its tracks, cuts, chapters) stays as it is.")));
        apply.Children.Add(parts);
        var ids = _selected();
        var toSelected = Ui.Button(L.T("Apply to the {0} selected files", ids.Count), () =>
        {
            if (_parts == PresetParts.None) { _status.Text = L.T("Choose at least one part."); return; }
            var preset = _edited with { PresetName = _name };
            _queue.UpdateSettings(_selected(), s => s.Merge(preset, _parts));
            _status.Text = L.T("Applied to {0} file(s).", _selected().Count);
        });
        toSelected.IsEnabled = ids.Count > 0;
        var forNew = Ui.Button(L.T("Use for new files"), () =>
        {
            UseForNewFiles = new Preset(_name, _description, _edited with { PresetName = _name }, p.BuiltIn);
            Close();
        });
        apply.Children.Add(Ui.Row(8, toSelected, forNew));
        apply.Children.Add(_status);
        root.Children.Add(Ui.Card(apply));

        // ------------------------------------------------ every setting
        var form = new SettingsForm(_edited, () => _queue.Settings.Capabilities, full: true, () => _queue.Tools?.Fdkaac is not null)
        {
            AllowedContainers = Containers.Allowed(_edited),
        };
        form.Changed += s => { _edited = s; form.AllowedContainers = Containers.Allowed(s); };
        formRef = form;
        root.Children.Add(Ui.Card(new StackPanel { Spacing = 10, Children = { Ui.H2(L.T("Settings")), form } }));
        _detail.Content = root;
    }

    private static Control Spaced(Control c) { c.Margin = new Thickness(0, 0, 8, 8); return c; }

    private void Save(Preset preset, string? replacing)
    {
        try
        {
            if (replacing is not null && !replacing.Equals(preset.Name, StringComparison.OrdinalIgnoreCase)
                && _queue.Presets.Get(preset.Name) is not null)
                throw new InvalidOperationException(L.T("A preset with this name already exists."));
            var saved = _queue.Presets.Save(preset);
            if (replacing is not null && !replacing.Equals(saved.Name, StringComparison.OrdinalIgnoreCase)) _queue.Presets.Delete(replacing);
            Changed = true;
            Fill(saved.Name);
            _status.Text = L.T("Preset \"{0}\" saved", saved.Name);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException) { _status.Text = ex.Message; }
    }

    private async Task ExportAsync()
    {
        if (_preset is null) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = L.T("Export…"), SuggestedFileName = OutputNaming.Safe(_name) + ".json",
            FileTypeChoices = [new FilePickerFileType("KnA preset") { Patterns = ["*.json"] }],
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        var preset = new Preset(_name, string.IsNullOrWhiteSpace(_description) ? null : _description, _edited with { PresetName = _name });
        await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(preset, SettingsStore.Json));
        _status.Text = L.T("Exported to {0}", path);
    }

    private async Task ImportAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L.T("Import…"), AllowMultiple = true, FileTypeFilter = [new FilePickerFileType("KnA preset") { Patterns = ["*.json"] }],
        });
        string? last = null;
        int n = 0;
        foreach (var path in files.Select(f => f.TryGetLocalPath()).OfType<string>())
            if (DropClassifier.ReadPreset(path) is { } p)
            {
                var name = _queue.Presets.Get(p.Name) is { BuiltIn: true } ? Unique(p.Name) : p.Name;
                last = _queue.Presets.Save(p with { Name = name }).Name;
                n++;
            }
        if (n > 0) { Changed = true; Fill(last); }
        _status.Text = L.T("{0} preset(s) imported", n);
    }
}
