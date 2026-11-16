using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using KnA.Core.Localization;
using KnA.Core.Tools;

namespace KnA.App.Views;

/// <summary>
/// Every parameter of a program, without memorising its documentation: the options are read from the installed
/// program's own help and can be searched by name or description; a picked option becomes a row with its value
/// (a list when the program names the allowed values). The result is the usual command-line text, which power
/// users can also edit directly. Nothing is refused: an option KnA doesn't know is passed on as written.
/// </summary>
public sealed class ParameterEditor : StackPanel
{
    private readonly Func<CancellationToken, Task<(string Program, IReadOnlyList<ToolOption> Options)>> _load;
    private readonly Action<string?> _changed;
    private readonly StackPanel _rows = new() { Spacing = 6 };
    private readonly AutoCompleteBox _search;
    private readonly TextBlock _status;
    private readonly TextBox _raw;
    private List<(string Option, string? Value)> _pairs;
    private IReadOnlyList<ToolOption> _options = [];
    private bool _syncing;

    private sealed record Item(ToolOption Option)
    {
        public override string ToString() => Option.Name + (Option.Argument is { } a ? $" <{a}>" : "") + " — " + Option.Description;
    }

    public ParameterEditor(string? text, Func<CancellationToken, Task<(string Program, IReadOnlyList<ToolOption> Options)>> load,
        Action<string?> changed, string? example = null)
    {
        _load = load;
        _changed = changed;
        _pairs = ToolHelp.Parse(text);
        Spacing = 8;
        _status = Ui.Muted(L.T("Reading the program's options…"));
        _search = new AutoCompleteBox
        {
            Watermark = L.T("Search a parameter (name or description)…"),
            FilterMode = AutoCompleteFilterMode.ContainsOrdinal, MinimumPrefixLength = 1, MaxDropDownHeight = 360,
            HorizontalAlignment = HorizontalAlignment.Stretch, FontFamily = Ui.Mono,
        };
        _search.SelectionChanged += (_, _) =>
        {
            if (_search.SelectedItem is not Item it) return;
            Add(it.Option);
            _search.SelectedItem = null;
            _search.Text = "";
        };
        _raw = new TextBox { Text = text ?? "", FontFamily = Ui.Mono, Watermark = example, IsVisible = false, AcceptsReturn = false };
        _raw.LostFocus += (_, _) =>
        {
            if (_syncing) return;
            _pairs = ToolHelp.Parse(_raw.Text);
            Emit();
            RenderRows();
        };
        var asText = new ToggleButton { Content = L.T("Edit as text"), Classes = { "ghost" } };
        asText.IsCheckedChanged += (_, _) => { _raw.IsVisible = asText.IsChecked == true; _rows.IsVisible = !_raw.IsVisible; };
        var top = new DockPanel();
        DockPanel.SetDock(asText, Dock.Right);
        asText.Margin = new Thickness(6, 0, 0, 0);
        top.Children.Add(asText);
        top.Children.Add(_search);
        Children.Add(top);
        Children.Add(_status);
        Children.Add(_rows);
        Children.Add(_raw);
        RenderRows();
        AttachedToVisualTree += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var (program, options) = await Task.Run(() => _load(CancellationToken.None));
            _options = options;
            _search.ItemsSource = options.Select(o => new Item(o)).ToList();
            _status.Text = options.Count > 0
                ? L.T("{0} parameters of {1}, read from its help. Applied last: they win over KnA's values.", options.Count, program)
                : L.T("{0}: its help could not be read; parameters can still be written as text.", program);
            RenderRows();
        }
        catch (Exception ex) { _status.Text = ex.Message; }
    }

    private void Add(ToolOption o)
    {
        if (_pairs.Any(p => p.Option == o.Name)) return;
        _pairs.Add((o.Name, o.IsFlag ? null : o.Values.FirstOrDefault() ?? ""));
        Emit();
        RenderRows();
    }

    private void Emit()
    {
        var text = ToolHelp.Format(_pairs.Where(p => p.Value is null || p.Value.Length > 0));
        _syncing = true;
        _raw.Text = text;
        _syncing = false;
        _changed(string.IsNullOrWhiteSpace(text) ? null : text);
    }

    private void RenderRows()
    {
        _rows.Children.Clear();
        for (int i = 0; i < _pairs.Count; i++)
        {
            int idx = i;
            var (name, value) = _pairs[i];
            var opt = _options.FirstOrDefault(o => o.Name == name);
            Control editor;
            if (value is null && (opt?.IsFlag ?? true))
                editor = Ui.Muted(L.T("switch (no value)"));
            else if (opt is { Values.Count: > 0 })
            {
                var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
                var vals = opt.Values.Contains(value ?? "") || string.IsNullOrEmpty(value) ? opt.Values : [value!, .. opt.Values];
                foreach (var v in vals) combo.Items.Add(v);
                combo.SelectedItem = string.IsNullOrEmpty(value) ? vals[0] : value;
                combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is string v) { _pairs[idx] = (name, v); Emit(); } };
                editor = combo;
            }
            else
            {
                var box = new TextBox { Text = value ?? "", FontFamily = Ui.Mono, Watermark = opt?.Argument };
                box.LostFocus += (_, _) => { _pairs[idx] = (name, box.Text ?? ""); Emit(); };
                editor = box;
            }
            var rm = new Button { Content = "✕", Classes = { "ghost" } };
            ToolTip.SetTip(rm, L.T("Remove"));
            rm.Click += (_, _) => { _pairs.RemoveAt(idx); Emit(); RenderRows(); };
            var label = new TextBlock { Text = name, FontFamily = Ui.Mono, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*,Auto"), ColumnSpacing = 8 };
            Grid.SetColumn(editor, 1); Grid.SetColumn(rm, 2);
            row.Children.Add(label); row.Children.Add(editor); row.Children.Add(rm);
            var cell = new StackPanel { Spacing = 2, Children = { row } };
            if (opt is { Description.Length: > 0 }) cell.Children.Add(Ui.Muted(opt.Description, 11.5));
            else if (_options.Count > 0) cell.Children.Add(Ui.Muted(L.T("Not in this program's help: passed on as written."), 11.5));
            _rows.Children.Add(cell);
        }
        if (_pairs.Count == 0) _rows.Children.Add(Ui.Muted(L.T("No parameter set by hand: KnA's values are used.")));
    }
}
