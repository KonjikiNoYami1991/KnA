using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using KnA.Core.Encoding;
using KnA.Core.Localization;
using KnA.Core.Media;
using KnA.Core.Tools;

namespace KnA.App.Views;

/// <summary>
/// The FFmpeg filters of the GPU that encodes (scale_cuda, vpp_qsv, denoise_vaapi, libplacebo…): pick one from the
/// list the installed FFmpeg offers for that GPU, set its options (read from "ffmpeg -h filter=…"), order them.
/// Stored as an FFmpeg filter chain in <see cref="VideoFilterSettings.Gpu"/>.
/// </summary>
public sealed class GpuFilterEditor : StackPanel
{
    private readonly List<FilterCall> _chain;
    private readonly Action<string?> _changed;
    private readonly Func<string, CancellationToken, Task<(string Program, IReadOnlyList<ToolOption> Options)>> _load;
    private readonly StackPanel _rows = new() { Spacing = 8 };
    private readonly ComboBox _add = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private IReadOnlyList<ToolOption> _available = [];

    public GpuFilterEditor(string? chain, Func<string, CancellationToken, Task<(string Program, IReadOnlyList<ToolOption> Options)>> load, Action<string?> changed)
    {
        _chain = FilterChain.Parse(chain);
        _changed = changed;
        _load = load;
        Spacing = 8;
        _add.PlaceholderText = L.T("Add a GPU filter…");
        _add.SelectionChanged += (_, _) =>
        {
            if (_add.SelectedIndex < 0 || _add.SelectedIndex >= _available.Count) return;
            _chain.Add(new FilterCall(_available[_add.SelectedIndex].Name, []));
            _add.SelectedIndex = -1;
            Emit();
            Render();
        };
        Children.Add(_add);
        Children.Add(_rows);
        Render();
        AttachedToVisualTree += async (_, _) =>
        {
            try
            {
                var (_, list) = await Task.Run(() => _load(KnA.Core.Queue.QueueService.ParameterTools.GpuFilters, CancellationToken.None));
                _available = list;
                _add.Items.Clear();
                foreach (var f in list) _add.Items.Add(new ComboBoxItem { Content = $"{f.Name} — {f.Description}" });
                if (list.Count == 0) _add.PlaceholderText = L.T("This FFmpeg has no filters for this GPU.");
            }
            catch (Exception ex) { _add.PlaceholderText = ex.Message; }
        };
    }

    private void Emit() => _changed(FilterChain.Format(_chain));

    private void Render()
    {
        _rows.Children.Clear();
        if (_chain.Count == 0) { _rows.Children.Add(Ui.Muted(L.T("No GPU filter: the frames go to the encoder as the filters above leave them."))); return; }
        for (int i = 0; i < _chain.Count; i++)
        {
            int idx = i;
            var call = _chain[i];
            var up = new Button { Content = "↑", Classes = { "ghost" } };
            var down = new Button { Content = "↓", Classes = { "ghost" } };
            var rm = new Button { Content = "✕", Classes = { "ghost" } };
            ToolTip.SetTip(rm, L.T("Remove"));
            up.Click += (_, _) => { if (idx > 0) { (_chain[idx - 1], _chain[idx]) = (_chain[idx], _chain[idx - 1]); Emit(); Render(); } };
            down.Click += (_, _) => { if (idx < _chain.Count - 1) { (_chain[idx + 1], _chain[idx]) = (_chain[idx], _chain[idx + 1]); Emit(); Render(); } };
            rm.Click += (_, _) => { _chain.RemoveAt(idx); Emit(); Render(); };
            var head = new DockPanel();
            var tools = Ui.Row(0, up, down, rm);
            DockPanel.SetDock(tools, Dock.Right);
            head.Children.Add(tools);
            head.Children.Add(new TextBlock { Text = $"{idx + 1}. {call.Name}", FontFamily = Ui.Mono, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            var pages = new ParamPages(new FilterOptionsModel(call, Emit), ct => _load(KnA.Core.Queue.QueueService.ParameterTools.Filter + call.Name, ct), tabs: false, example: "w=1280:h=720");
            _rows.Children.Add(Ui.Card(new StackPanel
            {
                Spacing = 6,
                Children = { head, new Expander { Header = L.T("Options"), Content = pages, IsExpanded = call.Options.Count == 0, HorizontalAlignment = HorizontalAlignment.Stretch } },
            }, 10));
        }
    }
}

/// <summary>
/// VapourSynth / AviSynth+ like StaxRip, without its clunkiness: the filters come from a catalogue grouped by purpose
/// (deinterlace, denoise, deband, sharpen, resize, colour…), each with a form for its parameters and the plugins it
/// needs; the script is written from the chain and shown read-only. "Write the script by hand" keeps full control.
/// </summary>
public sealed class FrameserverEditor : StackPanel
{
    private FrameserverSettings _fs;
    private readonly Action<FrameserverSettings, bool> _changed;
    private string _category = "";

    public FrameserverEditor(FrameserverSettings fs, Action<FrameserverSettings, bool> changed)
    {
        _fs = fs;
        _changed = changed;
        Spacing = 10;
        Render();
    }

    private void Set(FrameserverSettings fs, bool rerender = true)
    {
        _fs = fs;
        _changed(fs, false);
        if (rerender) Render();
    }

    private void Render()
    {
        Children.Clear();
        var fs = _fs;
        var kinds = new[] { (FrameserverKind.None, L.T("None (FFmpeg decodes the file)")), (FrameserverKind.VapourSynth, "VapourSynth"), (FrameserverKind.AviSynth, "AviSynth+") };
        Children.Add(Ui.Field("Frameserver", Ui.Combo(kinds, fs.Kind, k => Set(fs with
        {
            Kind = k, Chain = k == FrameserverKind.None ? null : fs.Kind == k ? fs.Chain : [], Script = fs.Kind == k ? fs.Script : null,
        }))));
        if (fs.Kind == FrameserverKind.None) return;

        bool byHand = fs.Chain is null;
        var mode = Ui.Combo(new[] { (false, L.T("Filters from the list")), (true, L.T("Script written by hand")) }, byHand, hand =>
            Set(hand ? fs with { Chain = null, Script = fs.EffectiveScript } : fs with { Chain = [] }));
        Children.Add(Ui.Columns(
            Ui.Field("Mode", mode),
            Ui.Field("Source filter", Ui.Combo(FilterCatalog.Sources.Select(x => (x.Id, x.Name)), fs.Source ?? "lsmas", v => Set(fs with { Source = v })))));
        if (byHand)
        {
            if (string.IsNullOrWhiteSpace(fs.Script)) _fs = fs = fs with { Script = Frameserver.Templates.FirstOrDefault(t => t.Kind == fs.Kind)?.Body };
            var templates = Frameserver.Templates.Where(t => t.Kind == fs.Kind).ToList();
            Children.Add(Ui.Field("Template", Ui.Combo(new[] { (-1, "—") }.Concat(templates.Select((t, i) => (i, t.Name))), -1,
                i => { if (i >= 0) Set(fs with { Script = templates[i].Body }); })));
            var script = Ui.Text(fs.Script, x => Set(_fs with { Script = x }, false), mono: true, multiline: true);
            script.MinHeight = 220;
            Children.Add(Ui.Field(L.T("Script") + " ({source} = " + L.T("this file") + ")", script));
        }
        else
        {
            var filters = FilterCatalog.For(fs.Kind).ToList();
            var cats = filters.Select(f => f.EnglishCategory).Distinct().ToList();
            if (!cats.Contains(_category)) _category = cats.FirstOrDefault() ?? "";
            var catCombo = Ui.Combo(cats.Select(c => (c, L.T(c))), _category, c => { _category = c; Render(); });
            var inCat = filters.Where(f => f.EnglishCategory == _category).ToList();
            // Filters whose plugin this VapourSynth lacks are listed but can't be picked.
            var add = Ui.Combo(new[] { ("", L.T("Add a filter…"), true) }.Concat(inCat.Select(f => (f.Id,
                    $"{f.Name} — {f.Description}" + (VapourSynthRuntime.Has(f.Plugin) ? "" : " — " + L.T("not available here ({0})", f.Plugin!)), VapourSynthRuntime.Has(f.Plugin)))), "",
                id => { if (id.Length > 0) Set(_fs with { Chain = [.. _fs.Chain ?? [], new FrameserverFilter(id)] }); });
            Children.Add(Ui.Columns(Ui.Field("Category", catCombo), Ui.Field("Filter", add)));
            var chain = fs.Chain ?? [];
            if (chain.Count == 0) Children.Add(Ui.Muted(L.T("No filter yet: the script only opens the file. Add filters in the order they should run.")));
            for (int i = 0; i < chain.Count; i++) Children.Add(FilterCard(i, chain[i]));
            var preview = new TextBox { Text = fs.EffectiveScript, IsReadOnly = true, AcceptsReturn = true, FontFamily = Ui.Mono, FontSize = 12, MinHeight = 120 };
            Children.Add(new Expander { Header = L.T("Script written from the list"), Content = preview, HorizontalAlignment = HorizontalAlignment.Stretch });
        }
        Children.Add(Ui.Toggle(L.T("Use the audio produced by the script"), fs.AudioFromScript, x => Set(_fs with { AudioFromScript = x })));
        Children.Add(Ui.Muted(L.T("VapourSynth: video = output 0, audio = output 1 (vspipe). AviSynth: AudioDub in the script. Decoding happens in the script, without hardware acceleration.")));
    }

    private Control FilterCard(int idx, FrameserverFilter f)
    {
        var c = FilterCatalog.Get(f.Id);
        var chain = _fs.Chain!;
        void Replace(FrameserverFilter nf, bool rerender) { var l = _fs.Chain!.ToList(); l[idx] = nf; Set(_fs with { Chain = l }, rerender); }
        var up = new Button { Content = "↑", Classes = { "ghost" } };
        var down = new Button { Content = "↓", Classes = { "ghost" } };
        var rm = new Button { Content = "✕", Classes = { "ghost" } };
        ToolTip.SetTip(rm, L.T("Remove"));
        up.Click += (_, _) => { if (idx > 0) { var l = chain.ToList(); (l[idx - 1], l[idx]) = (l[idx], l[idx - 1]); Set(_fs with { Chain = l }); } };
        down.Click += (_, _) => { if (idx < chain.Count - 1) { var l = chain.ToList(); (l[idx + 1], l[idx]) = (l[idx], l[idx + 1]); Set(_fs with { Chain = l }); } };
        rm.Click += (_, _) => { var l = chain.ToList(); l.RemoveAt(idx); Set(_fs with { Chain = l }); };
        var head = new DockPanel();
        var tools = Ui.Row(0, up, down, rm);
        DockPanel.SetDock(tools, Dock.Right);
        head.Children.Add(tools);
        head.Children.Add(new StackPanel
        {
            Children =
            {
                new TextBlock { Text = $"{idx + 1}. {c?.Name ?? f.Id}", FontWeight = FontWeight.SemiBold },
                Ui.Muted(c is null ? L.T("Unknown filter: left out of the script.") : c.Description + (c.Requires.Length > 0 && c.Requires != "core" ? " · " + L.T("Needs: {0}", c.Requires) : ""), 11.5),
            },
        });
        var box = new StackPanel { Spacing = 8, Children = { head } };
        if (c is not null && c.Params.Count > 0)
        {
            var fields = new List<Control>();
            foreach (var p in c.Params)
            {
                var cur = f.Values?.GetValueOrDefault(p.Name) ?? p.Default;
                void SetValue(string v, bool rerender = false)
                {
                    var now = _fs.Chain![idx];
                    var values = new Dictionary<string, string>(now.Values ?? []) { [p.Name] = v };
                    Replace(now with { Values = values }, rerender);
                }
                Control input = p.Type switch
                {
                    FilterParamType.Bool => Ui.Check("", cur.Equals("true", StringComparison.OrdinalIgnoreCase), v => SetValue(v ? "true" : "false")),
                    FilterParamType.Choice => Ui.Combo((p.Choices ?? []).Select(x => (x, x)), cur, v => SetValue(v)),
                    FilterParamType.Int => Ui.Number(double.TryParse(cur, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var iv) ? iv : 0,
                        p.Min, p.Max, 1, v => SetValue(((long)v).ToString(System.Globalization.CultureInfo.InvariantCulture)), "0"),
                    FilterParamType.Float => Ui.Number(double.TryParse(cur, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fv) ? fv : 0,
                        p.Min, p.Max, p.Max - p.Min > 20 ? 1 : 0.05, v => SetValue(v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)), "0.###"),
                    _ => Ui.Text(cur, v => SetValue(v), mono: true),
                };
                fields.Add(Ui.Field(p.Label, input, p.Help));
            }
            for (int i = 0; i < fields.Count; i += 3) box.Children.Add(Ui.Columns([.. fields.Skip(i).Take(3)]));
        }
        return Ui.Card(box, 10);
    }
}
