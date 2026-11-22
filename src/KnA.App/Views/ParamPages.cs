using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using KnA.Core.Encoding;
using KnA.Core.Localization;
using KnA.Core.Tools;

namespace KnA.App.Views;

/// <summary>Where the values of a parameter page are kept: command-line options or the options of one FFmpeg filter.</summary>
public interface IParamModel
{
    /// <summary>Null = not set; "" = set without a value (switch).</summary>
    string? Get(string name);
    void Set(string name, string? value);
    void Unset(string name);
    /// <summary>Options that are not in the program's list, as text.</summary>
    string Others(IReadOnlyCollection<string> known);
    void SetOthers(IReadOnlyCollection<string> known, string text);
}

/// <summary>"-opt value --flag" text (FFmpeg, NVEncC/QSVEncC/VCEEncC, fdkaac, qaac).</summary>
public sealed class CommandLineModel(string? text, Action<string?> changed) : IParamModel
{
    private readonly List<(string Option, string? Value)> _pairs = ToolHelp.Parse(text);

    public string? Get(string name) => _pairs.FindIndex(p => p.Option == name) is var i and >= 0 ? _pairs[i].Value ?? "" : null;

    public void Set(string name, string? value)
    {
        int i = _pairs.FindIndex(p => p.Option == name);
        if (i >= 0) _pairs[i] = (name, value); else _pairs.Add((name, value));
        Emit();
    }

    public void Unset(string name) { _pairs.RemoveAll(p => p.Option == name); Emit(); }

    public string Others(IReadOnlyCollection<string> known) => ToolHelp.Format(_pairs.Where(p => !known.Contains(p.Option)));

    public void SetOthers(IReadOnlyCollection<string> known, string text)
    {
        _pairs.RemoveAll(p => !known.Contains(p.Option));
        _pairs.AddRange(ToolHelp.Parse(text).Where(p => !known.Contains(p.Option)));
        Emit();
    }

    private void Emit()
    {
        var t = ToolHelp.Format(_pairs.Where(p => p.Value is null || p.Value.Length > 0));
        changed(string.IsNullOrWhiteSpace(t) ? null : t);
    }
}

/// <summary>The key=value options of one filter of an FFmpeg chain.</summary>
public sealed class FilterOptionsModel(FilterCall call, Action changed) : IParamModel
{
    public string? Get(string name) => call.Options.FindIndex(o => o.Key == name) is var i and >= 0 ? call.Options[i].Value : null;

    public void Set(string name, string? value)
    {
        int i = call.Options.FindIndex(o => o.Key == name);
        var kv = new KeyValuePair<string, string>(name, value ?? "");
        if (i >= 0) call.Options[i] = kv; else call.Options.Add(kv);
        changed();
    }

    public void Unset(string name) { call.Options.RemoveAll(o => o.Key == name); changed(); }

    public string Others(IReadOnlyCollection<string> known) =>
        string.Join(':', call.Options.Where(o => !known.Contains(o.Key)).Select(o => o.Key.Length == 0 ? o.Value : o.Key + "=" + o.Value));

    public void SetOthers(IReadOnlyCollection<string> known, string text)
    {
        call.Options.RemoveAll(o => !known.Contains(o.Key));
        var parsed = FilterChain.Parse("x=" + text).FirstOrDefault()?.Options ?? [];
        call.Options.AddRange(parsed.Where(o => !known.Contains(o.Key) && o.Value.Length > 0));
        changed();
    }
}

/// <summary>
/// Every parameter of a program on pages: the options are read from the installed program's own help and shown
/// all together, one page per group (rate control, GOP, analysis, colour/HDR…), each with its value field and
/// description; ticking an option sets it. A filter box narrows a page. Options the help doesn't list can still be
/// written as text (last page). The ones KnA sets from its own fields are left out by the server, so nothing is set
/// twice. With a host tab control the pages become its sub-tabs (Video tab), otherwise they are listed here.
/// </summary>
public sealed class ParamPages : StackPanel
{
    private readonly Func<CancellationToken, Task<(string Program, IReadOnlyList<ToolOption> Options)>> _load;
    private readonly IParamModel _model;
    private readonly TabControl? _host;
    private readonly string? _example;
    private readonly TextBlock _status = Ui.Muted(L.T("Reading the program's options…"));
    private IReadOnlyList<ToolOption> _options = [];
    private bool _loaded;

    /// <summary>Called when the pages are added to the host (to restore the sub-tab the user was on).</summary>
    public event Action? PagesAdded;

    public ParamPages(IParamModel model, Func<CancellationToken, Task<(string Program, IReadOnlyList<ToolOption> Options)>> load,
        bool tabs = true, string? example = null, TabControl? host = null)
    {
        _model = model;
        _load = load;
        _host = host;
        _example = example;
        Spacing = 8;
        Children.Add(_status);
        AttachedToVisualTree += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            var (program, options) = await Task.Run(() => _load(CancellationToken.None));
            _options = options;
            _status.Text = options.Count > 0
                ? L.T("{0} parameters of {1}, read from its help. Applied last: they win over KnA's values.", options.Count, program)
                : L.T("{0}: its help could not be read; parameters can still be written as text.", program);
            var known = _options.Select(o => o.Name).ToHashSet();
            var others = new TextBox { FontFamily = Ui.Mono, FontSize = 12.5, Watermark = _example, Text = _model.Others(known) };
            others.LostFocus += (_, _) => _model.SetOthers(known, others.Text ?? "");
            var othersField = Ui.Field(L.T("Other parameters (written as text)"), others);
            if (_host is null)
            {
                Children.Add(Page(options));
                Children.Add(othersField);
                return;
            }
            foreach (var cat in ToolHelp.Categories)
            {
                var list = options.Where(o => o.Category == cat).ToList();
                if (list.Count == 0) continue;
                int set = list.Count(o => _model.Get(o.Name) is not null);
                var page = new StackPanel { Spacing = 8, Margin = new Thickness(0, 10, 0, 0), Children = { Ui.Muted(_status.Text, 11.5), Page(list) } };
                if (cat == "Other") page.Children.Add(othersField);
                _host.Items.Add(new TabItem { Header = L.T(cat) + (set > 0 ? $" ●{set}" : ""), Tag = "p:" + cat, FontSize = 13, Content = page });
            }
            if (!options.Any(o => o.Category == "Other"))
                _host.Items.Add(new TabItem { Header = L.T("Other"), Tag = "p:Other", FontSize = 13, Content = new StackPanel { Margin = new Thickness(0, 10, 0, 0), Children = { othersField } } });
            _status.Text = L.T("{0}: every parameter is on the pages next to this one, one per group.", program);
            PagesAdded?.Invoke();
        }
        catch (Exception ex) { _status.Text = ex.Message; }
    }

    /// <summary>One page: a filter box and the rows of its options (built when shown, at most 400 at a time).</summary>
    private Control Page(IReadOnlyList<ToolOption> options)
    {
        var filter = new TextBox { Watermark = L.T("Filter the parameters (name or description)…") };
        var rows = new StackPanel { Spacing = 8 };
        void Render()
        {
            var f = (filter.Text ?? "").Trim();
            var list = f.Length == 0 ? options.ToList()
                : options.Where(o => o.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || o.Description.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
            rows.Children.Clear();
            foreach (var o in list.Take(400)) rows.Children.Add(Row(o));
            if (list.Count == 0) rows.Children.Add(Ui.Muted(L.T("No parameter matches.")));
            else if (list.Count > 400) rows.Children.Add(Ui.Muted(L.T("{0} more: narrow the filter.", list.Count - 400)));
        }
        filter.TextChanged += (_, _) => Render();
        var panel = new StackPanel { Spacing = 8, Children = { filter, rows } };
        bool built = false;
        panel.AttachedToVisualTree += (_, _) => { if (!built) { built = true; Render(); } };
        return panel;
    }

    private Control Row(ToolOption o)
    {
        var value = _model.Get(o.Name);
        var check = new CheckBox { IsChecked = value is not null, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(check, L.T("Set this parameter"));
        var name = new TextBlock
        {
            Text = o.Name + (o.Argument is { } a && !o.IsFlag ? $" <{a}>" : ""), FontFamily = Ui.Mono, FontSize = 12.5,
            FontWeight = value is not null ? FontWeight.SemiBold : FontWeight.Normal, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Control editor;
        string Initial() => o.IsFlag ? "" : o.Default ?? o.Values.FirstOrDefault() ?? "";
        if (o.IsFlag) editor = Ui.Muted(L.T("switch (no value)"));
        else if (o.Values.Count > 0)
        {
            var vals = value is { Length: > 0 } && !o.Values.Contains(value) ? [value, .. o.Values] : o.Values.ToList();
            var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = value is not null };
            foreach (var v in vals) combo.Items.Add(v);
            combo.SelectedItem = value is { Length: > 0 } ? value : o.Default is { } d && vals.Contains(d) ? d : vals[0];
            combo.SelectionChanged += (_, _) => { if (check.IsChecked == true && combo.SelectedItem is string v) _model.Set(o.Name, v); };
            editor = combo;
        }
        else
        {
            var box = new TextBox { Text = value ?? "", Watermark = o.Default ?? o.Argument, FontFamily = Ui.Mono, FontSize = 12.5, IsEnabled = value is not null };
            box.LostFocus += (_, _) => { if (check.IsChecked == true) _model.Set(o.Name, box.Text ?? ""); };
            editor = box;
        }
        check.IsCheckedChanged += (_, _) =>
        {
            bool on = check.IsChecked == true;
            editor.IsEnabled = on;
            name.FontWeight = on ? FontWeight.SemiBold : FontWeight.Normal;
            if (!on) { _model.Unset(o.Name); return; }
            var v = editor switch
            {
                ComboBox c => c.SelectedItem as string ?? Initial(),
                TextBox t when (t.Text ?? "").Length > 0 => t.Text,
                TextBox t => t.Text = Initial(),
                _ => null,
            };
            _model.Set(o.Name, o.IsFlag ? null : v);
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,260,*"), ColumnSpacing = 8 };
        Grid.SetColumn(name, 1); Grid.SetColumn(editor, 2);
        grid.Children.Add(check); grid.Children.Add(name); grid.Children.Add(editor);
        ToolTip.SetTip(name, o.Name);
        var cell = new StackPanel { Spacing = 1, Children = { grid } };
        if (o.Description.Length > 0) cell.Children.Add(new TextBlock { Text = o.Description, Classes = { "muted" }, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(28, 0, 0, 2) });
        return cell;
    }
}
