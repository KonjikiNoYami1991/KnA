using System.Globalization;
using KnA.Core.Localization;

namespace KnA.Core.Media;

/// <summary>Kind of value of a catalogue filter's parameter.</summary>
public enum FilterParamType { Int, Float, Bool, Choice, Text }

/// <summary>One parameter of a catalogue filter; <see cref="Default"/> is written in the script's own syntax.</summary>
public sealed record FilterParam(string Name, string EnglishLabel, FilterParamType Type, string Default,
    double Min = 0, double Max = 0, IReadOnlyList<string>? Choices = null, string? EnglishHelp = null)
{
    public string Label => L.T(EnglishLabel);
    public string? Help => EnglishHelp is null ? null : L.T(EnglishHelp);
}

/// <summary>
/// A VapourSynth or AviSynth+ filter of the catalogue: what it does, the plugin it needs and its parameters. The
/// template is the script line(s) with "{parameter}" where the values go.
/// </summary>
public sealed record CatalogFilter(string Id, FrameserverKind Kind, string EnglishCategory, string Name, string EnglishDescription,
    string Requires, IReadOnlyList<FilterParam> Params, string Template, bool NeedsHavsfunc = false)
{
    public string Category => L.T(EnglishCategory);
    public string Description => L.T(EnglishDescription);
}

/// <summary>
/// The filters of the VapourSynth / AviSynth+ scripts, StaxRip-style but without writing code: pick a filter from a
/// category, set its parameters in a form, put the filters in order; the script is written from the chain (and can
/// still be written by hand instead). Function names and parameters follow each plugin's documentation.
/// </summary>
public static class FilterCatalog
{
    private static readonly IReadOnlyList<string> QtgmcPresets =
        ["Placebo", "Very Slow", "Slower", "Slow", "Medium", "Fast", "Faster", "Very Fast", "Super Fast", "Ultra Fast", "Draft"];
    private static readonly IReadOnlyList<string> Kernels = ["Spline36", "Spline64", "Lanczos", "Bicubic", "Bilinear", "Point"];

    private const string Deint = "Deinterlace / IVTC", Denoise = "Denoise", Deband = "Deband", Sharp = "Sharpen / lines",
        Resize = "Crop / resize", Colour = "Colour / bit depth", Grain = "Grain", Rate = "Frame rate", Other = "Other";

    private static FilterParam I(string n, string l, int d, int min, int max, string? help = null) => new(n, l, FilterParamType.Int, d.ToString(CultureInfo.InvariantCulture), min, max, null, help);
    private static FilterParam F(string n, string l, double d, double min, double max, string? help = null) => new(n, l, FilterParamType.Float, d.ToString("0.###", CultureInfo.InvariantCulture), min, max, null, help);
    private static FilterParam B(string n, string l, bool d, string? help = null) => new(n, l, FilterParamType.Bool, d ? "true" : "false", 0, 0, null, help);
    private static FilterParam C(string n, string l, string d, IReadOnlyList<string> choices, string? help = null) => new(n, l, FilterParamType.Choice, d, 0, 0, choices, help);
    private static FilterParam T(string n, string l, string d, string? help = null) => new(n, l, FilterParamType.Text, d, 0, 0, null, help);

    public static IReadOnlyList<CatalogFilter> All { get; } =
    [
        // ------------------------------------------------------------------ VapourSynth
        new("vs.qtgmc", FrameserverKind.VapourSynth, Deint, "QTGMC", "The reference deinterlacer: motion-compensated, very clean (slow).",
            "havsfunc, mvtools, znedi3/nnedi3, fmtconv",
            [C("preset", "Preset", "Slower", QtgmcPresets), B("tff", "Top field first", true), C("fpsdiv", "Frame rate", "1", ["1", "2"], "1 = double rate (every field a frame), 2 = same rate.")],
            "clip = haf.QTGMC(clip, Preset=\"{preset}\", TFF={tff}, FPSDivisor={fpsdiv})", NeedsHavsfunc: true),
        new("vs.bwdif", FrameserverKind.VapourSynth, Deint, "Bwdif", "Fast, good deinterlacer (w3fdif + yadif).", "bwdif",
            [C("field", "Mode", "1", ["0", "1", "2", "3"], "0/1 = same rate (bottom/top field kept), 2/3 = double rate (bottom/top first).")],
            "clip = core.bwdif.Bwdif(clip, field={field})"),
        new("vs.ivtc", FrameserverKind.VapourSynth, Deint, "VFM + VDecimate", "Inverse telecine: back to the 23.976 fps film frames.", "vivtc",
            [C("order", "Field order", "1", ["1", "0"], "1 = top field first, 0 = bottom field first.")],
            "clip = core.vivtc.VFM(clip, order={order})\nclip = core.vivtc.VDecimate(clip)"),
        new("vs.knlm", FrameserverKind.VapourSynth, Denoise, "KNLMeansCL", "Non-local means on the GPU (OpenCL): strong, detail-friendly.", "knlmeanscl",
            [I("d", "Temporal radius", 1, 0, 8), I("a", "Search radius", 2, 1, 8), I("s", "Similarity radius", 4, 0, 8), F("h", "Strength", 1.2, 0.1, 20),
             C("channels", "Planes", "Y", ["Y", "UV", "YUV"])],
            "clip = core.knlm.KNLMeansCL(clip, d={d}, a={a}, s={s}, h={h}, channels=\"{channels}\")"),
        new("vs.dfttest", FrameserverKind.VapourSynth, Denoise, "DFTTest", "Frequency-domain denoiser: smooth, good on grain.", "dfttest",
            [F("sigma", "Strength (sigma)", 8, 0.1, 100), I("tbsize", "Temporal size", 3, 1, 15)],
            "clip = core.dfttest.DFTTest(clip, sigma={sigma}, tbsize={tbsize})"),
        new("vs.smdegrain", FrameserverKind.VapourSynth, Denoise, "SMDegrain", "Motion-compensated temporal denoiser (MVTools).", "havsfunc, mvtools",
            [I("tr", "Temporal radius", 2, 1, 6), I("thsad", "Strength (thSAD)", 300, 50, 2000)],
            "clip = haf.SMDegrain(clip, tr={tr}, thSAD={thsad})", NeedsHavsfunc: true),
        new("vs.f3kdb", FrameserverKind.VapourSynth, Deband, "neo_f3kdb", "Removes banding in gradients (anime, dark scenes).", "neo_f3kdb",
            [I("range", "Range", 15, 1, 31), I("y", "Luma threshold", 48, 0, 255), I("cb", "Cb threshold", 48, 0, 255), I("cr", "Cr threshold", 48, 0, 255),
             I("grainy", "Luma grain", 0, 0, 255), I("grainc", "Chroma grain", 0, 0, 255), C("depth", "Output bit depth", "16", ["8", "10", "16"])],
            "clip = core.neo_f3kdb.Deband(clip, range={range}, y={y}, cb={cb}, cr={cr}, grainy={grainy}, grainc={grainc}, output_depth={depth})"),
        new("vs.placebo", FrameserverKind.VapourSynth, Deband, "placebo.Deband", "libplacebo's debanding on the GPU (Vulkan).", "vs-placebo",
            [I("iterations", "Iterations", 1, 1, 16), F("threshold", "Threshold", 4, 0, 50), F("radius", "Radius", 16, 1, 64), F("grain", "Grain", 6, 0, 50)],
            "clip = core.placebo.Deband(clip, iterations={iterations}, threshold={threshold}, radius={radius}, grain={grain})"),
        new("vs.cas", FrameserverKind.VapourSynth, Sharp, "CAS", "AMD Contrast Adaptive Sharpening: natural, few halos.", "cas",
            [F("sharpness", "Sharpness", 0.5, 0, 1)], "clip = core.cas.CAS(clip, sharpness={sharpness})"),
        new("vs.lsfmod", FrameserverKind.VapourSynth, Sharp, "LSFmod", "LimitedSharpenFaster: strong sharpening with halo control.", "havsfunc",
            [I("strength", "Strength", 100, 0, 500)], "clip = haf.LSFmod(clip, strength={strength})", NeedsHavsfunc: true),
        new("vs.awarpsharp", FrameserverKind.VapourSynth, Sharp, "AWarpSharp2", "Thins and straightens lines (anime).", "warpsharp",
            [I("thresh", "Threshold", 128, 0, 255), I("blur", "Blur", 2, 0, 20), I("depth", "Depth", 16, -128, 127)],
            "clip = core.warp.AWarpSharp2(clip, thresh={thresh}, blur={blur}, depth={depth})"),
        new("vs.dehalo", FrameserverKind.VapourSynth, Sharp, "DeHalo_alpha", "Removes halos around edges.", "havsfunc",
            [F("rx", "Horizontal radius", 2, 1, 5), F("ry", "Vertical radius", 2, 1, 5), F("darkstr", "Dark halo strength", 1, 0, 2), F("brightstr", "Bright halo strength", 1, 0, 2)],
            "clip = haf.DeHalo_alpha(clip, rx={rx}, ry={ry}, darkstr={darkstr}, brightstr={brightstr})", NeedsHavsfunc: true),
        new("vs.crop", FrameserverKind.VapourSynth, Resize, "Crop", "Cuts the borders (pixels; even values for 4:2:0).", "core",
            [I("left", "Left", 0, 0, 4096), I("right", "Right", 0, 0, 4096), I("top", "Top", 0, 0, 4096), I("bottom", "Bottom", 0, 0, 4096)],
            "clip = core.std.Crop(clip, left={left}, right={right}, top={top}, bottom={bottom})"),
        new("vs.resize", FrameserverKind.VapourSynth, Resize, "Resize", "Changes the size with the chosen kernel.", "core",
            [C("kernel", "Kernel", "Spline36", Kernels), I("width", "Width", 1920, 16, 16384), I("height", "Height", 1080, 16, 16384)],
            "clip = core.resize.{kernel}(clip, width={width}, height={height})"),
        new("vs.depth", FrameserverKind.VapourSynth, Colour, "Bit depth", "Converts the bit depth (filters in 16-bit, output in 10-bit…).", "core",
            [C("format", "Format", "YUV420P16", ["YUV420P8", "YUV420P10", "YUV420P16", "YUV444P16"])],
            "clip = core.resize.Point(clip, format=vs.{format})"),
        new("vs.levels", FrameserverKind.VapourSynth, Colour, "Levels", "Remaps the luma range (8-bit values).", "core",
            [I("min_in", "Input black", 16, 0, 255), I("max_in", "Input white", 235, 0, 255), F("gamma", "Gamma", 1, 0.1, 10), I("min_out", "Output black", 16, 0, 255), I("max_out", "Output white", 235, 0, 255)],
            "clip = core.std.Levels(clip, min_in={min_in}, max_in={max_in}, gamma={gamma}, min_out={min_out}, max_out={max_out}, planes=0)"),
        new("vs.grain", FrameserverKind.VapourSynth, Grain, "AddGrain", "Adds film grain (hides banding, keeps texture).", "addgrain",
            [F("var", "Luma grain", 1, 0, 20), F("uvar", "Chroma grain", 0, 0, 20)], "clip = core.grain.Add(clip, var={var}, uvar={uvar})"),
        new("vs.fps", FrameserverKind.VapourSynth, Rate, "AssumeFPS", "Sets the frame rate without dropping frames (speeds up or slows down).", "core",
            [I("num", "Numerator", 24000, 1, 1000000), I("den", "Denominator", 1001, 1, 1000000)],
            "clip = core.std.AssumeFPS(clip, fpsnum={num}, fpsden={den})"),
        new("vs.custom", FrameserverKind.VapourSynth, Other, "Custom line", "Any VapourSynth line, written as in a script.", "",
            [T("code", "Code", "clip = core.std.FlipHorizontal(clip)")], "{code}"),

        // ------------------------------------------------------------------ AviSynth+
        new("avs.qtgmc", FrameserverKind.AviSynth, Deint, "QTGMC", "The reference deinterlacer: motion-compensated, very clean (slow).",
            "QTGMC.avsi, MVTools2, NNEDI3, RgTools, masktools2",
            [C("order", "Field order", "TFF", ["TFF", "BFF"]), C("preset", "Preset", "Slower", QtgmcPresets), C("fpsdiv", "Frame rate", "1", ["1", "2"], "1 = double rate (every field a frame), 2 = same rate.")],
            "Assume{order}()\nQTGMC(Preset=\"{preset}\", FPSDivisor={fpsdiv})"),
        new("avs.bwdif", FrameserverKind.AviSynth, Deint, "BWDIF", "Fast, good deinterlacer (w3fdif + yadif).", "BWDIF",
            [C("field", "Mode", "1", ["0", "1", "2", "3"], "0/1 = same rate (bottom/top field kept), 2/3 = double rate (bottom/top first).")],
            "BWDIF(field={field})"),
        new("avs.ivtc", FrameserverKind.AviSynth, Deint, "TFM + TDecimate", "Inverse telecine: back to the 23.976 fps film frames.", "TIVTC",
            [C("order", "Field order", "1", ["1", "0", "-1"], "1 = top field first, 0 = bottom field first, -1 = from the source.")],
            "TFM(order={order})\nTDecimate()"),
        new("avs.knlm", FrameserverKind.AviSynth, Denoise, "KNLMeansCL", "Non-local means on the GPU (OpenCL): strong, detail-friendly.", "KNLMeansCL",
            [I("d", "Temporal radius", 1, 0, 8), I("a", "Search radius", 2, 1, 8), I("s", "Similarity radius", 4, 0, 8), F("h", "Strength", 1.2, 0.1, 20),
             C("channels", "Planes", "Y", ["Y", "UV", "YUV"])],
            "KNLMeansCL(d={d}, a={a}, s={s}, h={h}, channels=\"{channels}\")"),
        new("avs.dfttest", FrameserverKind.AviSynth, Denoise, "dfttest", "Frequency-domain denoiser: smooth, good on grain.", "dfttest",
            [F("sigma", "Strength (sigma)", 8, 0.1, 100), I("tbsize", "Temporal size", 3, 1, 15)], "dfttest(sigma={sigma}, tbsize={tbsize})"),
        new("avs.smdegrain", FrameserverKind.AviSynth, Denoise, "SMDegrain", "Motion-compensated temporal denoiser (MVTools).", "SMDegrain.avsi, MVTools2",
            [I("tr", "Temporal radius", 2, 1, 6), I("thsad", "Strength (thSAD)", 300, 50, 2000)], "SMDegrain(tr={tr}, thSAD={thsad})"),
        new("avs.fft3d", FrameserverKind.AviSynth, Denoise, "FFT3DFilter", "3D frequency denoiser, light and fast.", "FFT3DFilter",
            [F("sigma", "Strength (sigma)", 2, 0.1, 20)], "FFT3DFilter(sigma={sigma})"),
        new("avs.f3kdb", FrameserverKind.AviSynth, Deband, "neo_f3kdb", "Removes banding in gradients (anime, dark scenes).", "neo_f3kdb",
            [I("range", "Range", 15, 1, 31), I("y", "Luma threshold", 48, 0, 255), I("cb", "Cb threshold", 48, 0, 255), I("cr", "Cr threshold", 48, 0, 255),
             I("grainy", "Luma grain", 0, 0, 255), I("grainc", "Chroma grain", 0, 0, 255)],
            "neo_f3kdb(range={range}, Y={y}, Cb={cb}, Cr={cr}, grainY={grainy}, grainC={grainc})"),
        new("avs.cas", FrameserverKind.AviSynth, Sharp, "CAS", "AMD Contrast Adaptive Sharpening: natural, few halos.", "CAS",
            [F("sharpness", "Sharpness", 0.5, 0, 1)], "CAS(sharpness={sharpness})"),
        new("avs.lsfmod", FrameserverKind.AviSynth, Sharp, "LSFmod", "LimitedSharpenFaster: strong sharpening with halo control.", "LSFmod.avsi, masktools2, RgTools",
            [I("strength", "Strength", 100, 0, 500)], "LSFmod(strength={strength})"),
        new("avs.awarpsharp", FrameserverKind.AviSynth, Sharp, "aWarpSharp2", "Thins and straightens lines (anime).", "aWarpSharp2",
            [I("thresh", "Threshold", 128, 0, 255), I("blur", "Blur", 2, 0, 20), I("depth", "Depth", 16, -128, 127)],
            "aWarpSharp2(thresh={thresh}, blur={blur}, depth={depth})"),
        new("avs.dehalo", FrameserverKind.AviSynth, Sharp, "DeHalo_alpha", "Removes halos around edges.", "Dehalo_alpha.avsi, masktools2",
            [F("rx", "Horizontal radius", 2, 1, 5), F("ry", "Vertical radius", 2, 1, 5), F("darkstr", "Dark halo strength", 1, 0, 2), F("brightstr", "Bright halo strength", 1, 0, 2)],
            "DeHalo_alpha(rx={rx}, ry={ry}, darkstr={darkstr}, brightstr={brightstr})"),
        new("avs.crop", FrameserverKind.AviSynth, Resize, "Crop", "Cuts the borders (pixels; even values for 4:2:0).", "core",
            [I("left", "Left", 0, 0, 4096), I("right", "Right", 0, 0, 4096), I("top", "Top", 0, 0, 4096), I("bottom", "Bottom", 0, 0, 4096)],
            "Crop({left}, {top}, -{right}, -{bottom})"),
        new("avs.resize", FrameserverKind.AviSynth, Resize, "Resize", "Changes the size with the chosen kernel.", "core",
            [C("kernel", "Kernel", "Spline36", Kernels), I("width", "Width", 1920, 16, 16384), I("height", "Height", 1080, 16, 16384)],
            "{kernel}Resize({width}, {height})"),
        new("avs.depth", FrameserverKind.AviSynth, Colour, "ConvertBits", "Converts the bit depth (filters in 16-bit, output in 10-bit…).", "core",
            [C("bits", "Bit depth", "16", ["8", "10", "16"])], "ConvertBits({bits})"),
        new("avs.levels", FrameserverKind.AviSynth, Colour, "Levels", "Remaps the luma range (8-bit values).", "core",
            [I("min_in", "Input black", 16, 0, 255), F("gamma", "Gamma", 1, 0.1, 10), I("max_in", "Input white", 235, 0, 255), I("min_out", "Output black", 16, 0, 255), I("max_out", "Output white", 235, 0, 255)],
            "Levels({min_in}, {gamma}, {max_in}, {min_out}, {max_out})"),
        new("avs.tweak", FrameserverKind.AviSynth, Colour, "Tweak", "Hue, saturation, brightness and contrast.", "core",
            [F("hue", "Hue (degrees)", 0, -180, 180), F("sat", "Saturation", 1, 0, 10), F("bright", "Brightness", 0, -255, 255), F("cont", "Contrast", 1, 0, 10)],
            "Tweak(hue={hue}, sat={sat}, bright={bright}, cont={cont})"),
        new("avs.grain", FrameserverKind.AviSynth, Grain, "AddGrainC", "Adds film grain (hides banding, keeps texture).", "AddGrainC",
            [F("var", "Luma grain", 1, 0, 20), F("uvar", "Chroma grain", 0, 0, 20)], "AddGrainC(var={var}, uvar={uvar})"),
        new("avs.fps", FrameserverKind.AviSynth, Rate, "AssumeFPS", "Sets the frame rate without dropping frames (speeds up or slows down).", "core",
            [I("num", "Numerator", 24000, 1, 1000000), I("den", "Denominator", 1001, 1, 1000000)], "AssumeFPS({num}, {den})"),
        new("avs.custom", FrameserverKind.AviSynth, Other, "Custom line", "Any AviSynth line, written as in a script.", "",
            [T("code", "Code", "FlipHorizontal()")], "{code}"),
    ];

    /// <summary>Source filters a catalogue script can open the file with.</summary>
    public static IReadOnlyList<(string Id, string Name)> Sources { get; } =
        [("lsmas", "L-SMASH Works"), ("bs", "BestSource"), ("ffms2", "FFMS2")];

    public static CatalogFilter? Get(string id) => All.FirstOrDefault(f => f.Id == id);

    public static IEnumerable<CatalogFilter> For(FrameserverKind kind) => All.Where(f => f.Kind == kind);

    /// <summary>A parameter's value in the script's syntax (booleans: True/False in Python, true/false in AviSynth).</summary>
    public static string Format(FrameserverKind kind, FilterParam p, string? value)
    {
        var v = string.IsNullOrWhiteSpace(value) ? p.Default : value.Trim();
        switch (p.Type)
        {
            case FilterParamType.Bool:
                bool b = v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1";
                return kind == FrameserverKind.VapourSynth ? (b ? "True" : "False") : (b ? "true" : "false");
            case FilterParamType.Int:
                return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var i)
                    ? ((long)Math.Round(Math.Clamp(i, p.Min, p.Max))).ToString(CultureInfo.InvariantCulture) : p.Default;
            case FilterParamType.Float:
                return double.TryParse(v.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var f)
                    ? Math.Clamp(f, p.Min, p.Max).ToString("0.0###", CultureInfo.InvariantCulture) : p.Default;
            case FilterParamType.Choice:
                return p.Choices is { } c && c.Contains(v) ? v : p.Default;
            default:
                return v;
        }
    }

    /// <summary>The script line(s) of one filter of the chain, or null when the id is unknown.</summary>
    public static string? Line(FrameserverKind kind, FrameserverFilter f)
    {
        if (Get(f.Id) is not { } c || c.Kind != kind) return null;
        var text = c.Template;
        foreach (var p in c.Params)
            text = text.Replace("{" + p.Name + "}", Format(kind, p, f.Values?.GetValueOrDefault(p.Name)));
        return text;
    }

    /// <summary>The whole script of a catalogue chain ("{source}" stays for the file).</summary>
    public static string Build(FrameserverSettings fs)
    {
        var chain = fs.Chain ?? [];
        var src = fs.Source is "bs" or "ffms2" ? fs.Source : "lsmas";
        var lines = new List<string>();
        if (fs.Kind == FrameserverKind.VapourSynth)
        {
            lines.Add("import vapoursynth as vs");
            if (chain.Any(f => Get(f.Id)?.NeedsHavsfunc == true)) lines.Add("import havsfunc as haf");
            lines.Add("core = vs.core");
            lines.Add(src switch
            {
                "bs" => "clip = core.bs.VideoSource(source=r\"{source}\")",
                "ffms2" => "clip = core.ffms2.Source(source=r\"{source}\")",
                _ => "clip = core.lsmas.LWLibavSource(source=r\"{source}\")",
            });
            foreach (var f in chain) if (Line(fs.Kind, f) is { } l) lines.Add(l);
            lines.Add("clip.set_output(0)");
            if (fs.AudioFromScript)
            {
                lines.Add("audio = core.bs.AudioSource(source=r\"{source}\")");
                lines.Add("audio.set_output(1)");
            }
        }
        else
        {
            lines.Add(src switch
            {
                "bs" => "BSVideoSource(\"{source}\")",
                "ffms2" => "FFVideoSource(\"{source}\")",
                _ => "LWLibavVideoSource(\"{source}\")",
            });
            if (fs.AudioFromScript)
                lines.Add(src switch
                {
                    "bs" => "AudioDub(last, BSAudioSource(\"{source}\"))",
                    "ffms2" => "AudioDub(last, FFAudioSource(\"{source}\"))",
                    _ => "AudioDub(last, LWLibavAudioSource(\"{source}\"))",
                });
            foreach (var f in chain) if (Line(fs.Kind, f) is { } l) lines.Add(l);
        }
        return string.Join('\n', lines) + "\n";
    }
}
