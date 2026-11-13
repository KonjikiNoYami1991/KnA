using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using KnA.Core.Localization;

namespace KnA.App.Views;

/// <summary>XAML: <c>{v:T 'English text'}</c> → localized string.</summary>
public sealed class TExtension(string text) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(text);
}

/// <summary>Small helpers for the windows built in code (forms are easier to keep in sync with the web UI this way).</summary>
internal static class Ui
{
    public static readonly FontFamily Mono = new("Cascadia Code,Consolas,Menlo,monospace");

    public static TextBlock Label(string text) => new() { Text = L.T(text).ToUpper(CultureInfo.CurrentCulture), Classes = { "label" } };

    public static TextBlock Muted(string text, double size = 12) =>
        new() { Text = text, Classes = { "muted" }, FontSize = size, TextWrapping = TextWrapping.Wrap };

    public static TextBlock H2(string text) => new() { Text = text, Classes = { "h2" } };

    public static Control Field(string label, Control input, string? hint = null)
    {
        var p = new StackPanel { Spacing = 4 };
        p.Children.Add(Label(label));
        p.Children.Add(input);
        if (hint is not null) p.Children.Add(Muted(hint));
        return p;
    }

    public static Grid Columns(params Control[] children)
    {
        var g = new Grid { ColumnSpacing = 10 };
        for (int i = 0; i < children.Length; i++)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            Grid.SetColumn(children[i], i);
            g.Children.Add(children[i]);
        }
        return g;
    }

    public static StackPanel Row(double spacing, params Control[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    public static Border Card(Control child, double padding = 14) => new()
    {
        Classes = { "card" }, Padding = new Thickness(padding), Child = child,
    };

    public static Border Note(string text, bool error = false) => new()
    {
        CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 6),
        Background = new SolidColorBrush(Color.Parse(error ? "#33F87171" : "#2260A5FA")),
        Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 },
    };

    public static ComboBox Combo<T>(IEnumerable<(T Value, string Text, bool Enabled)> items, T current, Action<T> picked)
    {
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        var values = new List<T>();
        int selected = 0;
        foreach (var (value, text, enabled) in items)
        {
            combo.Items.Add(new ComboBoxItem { Content = text, IsEnabled = enabled });
            if (EqualityComparer<T>.Default.Equals(value, current)) selected = values.Count;
            values.Add(value);
        }
        if (values.Count > 0) combo.SelectedIndex = selected;
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex < 0 || combo.SelectedIndex >= values.Count) return;
            var v = values[combo.SelectedIndex];
            if (!EqualityComparer<T>.Default.Equals(v, current)) { current = v; picked(v); }
        };
        return combo;
    }

    public static ComboBox Combo<T>(IEnumerable<(T Value, string Text)> items, T current, Action<T> picked) =>
        Combo(items.Select(i => (i.Value, i.Text, true)), current, picked);

    public static ToggleSwitch Toggle(string text, bool value, Action<bool> changed, bool enabled = true)
    {
        var t = new ToggleSwitch { OnContent = text, OffContent = text, IsChecked = value, IsEnabled = enabled };
        t.IsCheckedChanged += (_, _) => changed(t.IsChecked == true);
        return t;
    }

    public static CheckBox Check(string text, bool value, Action<bool> changed)
    {
        var c = new CheckBox { Content = text, IsChecked = value };
        c.IsCheckedChanged += (_, _) => changed(c.IsChecked == true);
        return c;
    }

    public static NumericUpDown Number(double value, double min, double max, double step, Action<double> changed, string format = "0.##")
    {
        var n = new NumericUpDown
        {
            Value = (decimal)value, Minimum = (decimal)min, Maximum = (decimal)max, Increment = (decimal)step,
            FormatString = format, HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        n.ValueChanged += (_, e) => { if (e.NewValue is { } v) changed((double)v); };
        return n;
    }

    public static TextBox Text(string? value, Action<string> changed, string? watermark = null, bool mono = false, bool multiline = false)
    {
        var t = new TextBox { Text = value ?? "", Watermark = watermark, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.NoWrap : TextWrapping.NoWrap };
        if (mono) { t.FontFamily = Mono; t.FontSize = 12.5; }
        if (multiline) t.MinHeight = 120;
        t.TextChanged += (_, _) => changed(t.Text ?? "");
        return t;
    }

    public static Button Button(string text, Action click, bool primary = false)
    {
        var b = new Button { Content = text };
        if (primary) b.Classes.Add("primary");
        b.Click += (_, _) => click();
        return b;
    }

    public static Button Button(string text, Func<Task> click, bool primary = false)
    {
        var b = new Button { Content = text };
        if (primary) b.Classes.Add("primary");
        b.Click += async (_, _) => await click();
        return b;
    }

    public static string Stamp(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";
    }

    public static string Short(TimeSpan t) => t.ToString(t.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);

    /// <summary>Parses 1:23, 01:02:03.500, 83.5 (seconds).</summary>
    public static double? ParseStamp(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Trim().Replace(',', '.').Split(':');
        double total = 0;
        foreach (var p in parts)
        {
            if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return null;
            total = total * 60 + v;
        }
        return total;
    }
}
