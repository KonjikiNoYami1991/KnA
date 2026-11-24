using System.Globalization;
using KnA.Core.Localization;
using KnA.Core.Media;

namespace KnA.Core.Encoding;

/// <summary>Where one step of the picture chain runs.</summary>
public enum StepPlace
{
    /// <summary>On the GPU, inside the encoder (rigaya's --vpp-* filters, GPU decoding).</summary>
    Gpu,
    /// <summary>On the CPU through FFmpeg (decoding / filters), frames sent to the GPU encoder on a pipe.</summary>
    CpuFfmpeg,
    /// <summary>On the CPU through VapourSynth / AviSynth (the script), read by the encoder.</summary>
    CpuScript,
}

/// <summary>One step of the picture chain: what it is, where it runs, the option used and, when it is not on the GPU, the GPU filter to use instead.</summary>
public sealed record PipelineStep(string Name, StepPlace Place, string? Option = null, string? Note = null, string? GpuAlternative = null)
{
    /// <summary>Position of the filter in the VapourSynth / AviSynth catalogue chain (for "use the GPU filter instead").</summary>
    public int? ChainIndex { get; init; }
}

/// <summary>
/// What the GPU-first plan of a rigaya encode looks like: the steps in order and where each runs. Shown in the
/// editor (Filters tab) and in the log, so a CPU step is never silent.
/// </summary>
public sealed record GpuPipeline(IReadOnlyList<PipelineStep> Steps)
{
    /// <summary>Every step on the GPU (decode → filters → encode never leave it).</summary>
    public bool AllOnGpu => Steps.All(s => s.Place == StepPlace.Gpu);
    public bool UsesScript => Steps.Any(s => s.Place == StepPlace.CpuScript);
    public bool UsesFfmpeg => Steps.Any(s => s.Place == StepPlace.CpuFfmpeg);

    /// <summary>One line for the log / the editor.</summary>
    public string Summary => AllOnGpu
        ? L.T("Runs on the GPU (faster): {0}", string.Join(" → ", Steps.Select(s => s.Name)))
        : L.T("GPU and CPU: {0}", string.Join(" → ", Steps.Select(s => s.Name + " [" + Place(s.Place) + "]")));

    public static string Place(StepPlace p) => p switch
    {
        StepPlace.Gpu => "GPU",
        StepPlace.CpuScript => L.T("CPU, script"),
        _ => L.T("CPU, FFmpeg"),
    };
}

/// <summary>
/// KnA's picture filters translated into the GPU filters of NVEncC / QSVEncC / VCEEncC, so the frame stays on the
/// GPU: the encoder decodes the file itself (--avhw, or --avsw for formats its GPU reader lacks), crops, deinterlaces,
/// tone-maps, denoises, debands, resizes, sharpens and burns subtitles with its --vpp-* filters. A filter without a
/// GPU equivalent is named (never dropped silently) and the frames go through FFmpeg on the CPU instead.
/// Every option is checked against NVEncC_Options.en.md (9.41), QSVEncC_Options.en.md (8.38) and
/// VCEEncC_Options.en.md (9.23).
/// </summary>
public static class RigayaFilters
{
    /// <summary>What the translation needs to know about the job.</summary>
    public sealed record Input
    {
        public required EncoderBackend Backend { get; init; }
        public required VideoStream Video { get; init; }
        public required JobSettings Settings { get; init; }
        public required HdrPlan Hdr { get; init; }
        /// <summary>Target size after crop (null = no resize).</summary>
        public ScaleResult? Scale { get; init; }
        /// <summary>The subtitle to burn and its prepared file (text) or stream (pictures).</summary>
        public TrackPlan? Burn { get; init; }
        public PreparedSubtitles? Prepared { get; init; }
        /// <summary>The file the encoder opens (to number the subtitle tracks for --vpp-subburn track=).</summary>
        public MediaInfo? SourceMedia { get; init; }
        /// <summary>The video comes from a VapourSynth / AviSynth script (read with --vpy / --avs).</summary>
        public bool Script { get; init; }
        public JobEdit Edit { get; init; } = new();
        /// <summary>Video track delay (seconds).</summary>
        public double DelaySeconds { get; init; }
        /// <summary>AI / libplacebo upscaling chosen (no GPU filter of the encoder does it).</summary>
        public string? Upscaler { get; init; }
    }

    /// <summary>The translation: encoder options, the steps and the reasons that force FFmpeg (empty = native).</summary>
    public sealed record Result(List<string> Args, List<PipelineStep> Steps, List<string> Blockers, double TrimStart)
    {
        public bool Native => Blockers.Count == 0;
    }

    private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static int Even(int v) => Math.Max(0, v) & ~1;

    public static Result Translate(Input i)
    {
        var a = new List<string>();
        var steps = new List<PipelineStep>();
        var blockers = new List<string>();
        var f = i.Settings.VideoFilters;
        var v = i.Video;
        string enc = RigayaEncoders.DisplayName(i.Backend);
        void Block(string name, string reason, string? alternative = null)
        {
            blockers.Add(reason);
            steps.Add(new PipelineStep(name, StepPlace.CpuFfmpeg, null, reason, alternative));
        }
        void Gpu(string name, params string[] args)
        {
            a.AddRange(args);
            steps.Add(new PipelineStep(name, StepPlace.Gpu, args.Length > 0 ? string.Join(' ', args) : null));
        }

        // ------------------------------------------------------------ reading / decoding
        if (i.Script) steps.Add(new PipelineStep(L.T("Script (VapourSynth / AviSynth)"), StepPlace.CpuScript, null, L.T("Read by {0} directly (no vspipe, no FFmpeg).", enc)));
        else if (RigayaEncoders.HwDecodable(v.CodecName)) steps.Add(new PipelineStep(L.T("GPU decoding"), StepPlace.Gpu, "--avhw"));
        else steps.Add(new PipelineStep(L.T("Decoding"), StepPlace.CpuFfmpeg, "--avsw",
            L.T("{0} cannot be decoded by the GPU: {1} decodes it on the CPU, the filters still run on the GPU.", v.CodecName, enc)));

        if (Math.Abs(i.DelaySeconds) >= 0.0005) Block(L.T("Video delay"), L.T("video delay"));
        if (v.Rotation != 0 || f.OverridesOrientation)
            Block(L.T("Rotation"), L.T("rotation"), "--vpp-rotate / --vpp-transform");

        // ------------------------------------------------------------ trim (exact frames)
        double trimStart = 0;
        var parts = i.Edit.KeptParts(v.FrameCount > 0 && v.FrameRate > 0 ? v.FrameCount / v.FrameRate : 0);
        if (parts.Count > 0)
        {
            // --trim counts frames: exact on a constant frame rate only.
            bool cfr = i.Script || (v.FrameRate > 0 && !v.VariableFrameRate);
            if (!cfr) Block(L.T("Trim"), L.T("trim of a variable-frame-rate video"));
            else
            {
                double fps = v.FrameRate;
                long duration = v.FrameCount;
                // "N:0" = from frame N to the end of the file.
                string Range((double Start, double End) p)
                {
                    long first = (long)Math.Round(p.Start * fps), last = (long)Math.Round(p.End * fps) - 1;
                    bool toEnd = p.End >= 1e9 || (duration > 0 && last >= duration - 1);
                    return $"{first}:{(toEnd ? 0 : Math.Max(first, last))}";
                }
                var ranges = parts.Select(Range);
                Gpu(L.T("Trim"), "--trim", string.Join(',', ranges));
                trimStart = parts.Count == 1 ? parts[0].Start : 0;
            }
        }

        // ------------------------------------------------------------ deinterlace
        switch (FilterBuilder.Deinterlacing(v, f))
        {
            case DeinterlaceMode.Auto or DeinterlaceMode.Force:
                // Only NVEncC's bwdif has "deint=interlaced" (QSVEncC 8.38 / VCEEncC 9.23 deinterlace every frame).
                bool onlyFlagged = f.Deinterlace == DeinterlaceMode.Auto && i.Backend == EncoderBackend.Nvenc;
                Gpu(L.T("Deinterlace (Bwdif)"), onlyFlagged ? ["--vpp-bwdif", "deint=interlaced"] : ["--vpp-bwdif"]);
                break;
            case DeinterlaceMode.Ivtc:
                // Field matching + 3:2 decimation back to the film frames, on the GPU (cycle=auto decimates at 26 fps and up).
                Gpu(L.T("Inverse telecine"), "--vpp-ivtc");
                break;
        }

        // ------------------------------------------------------------ crop
        if (f.Crop is { IsEmpty: false } c)
            Gpu(L.T("Crop"), "--crop", $"{Even(c.Left)},{Even(c.Top)},{Even(c.Right)},{Even(c.Bottom)}");

        // ------------------------------------------------------------ HDR → SDR
        if (i.Hdr.Action == HdrAction.ToneMap)
        {
            var h = v.Hdr;
            if (h.IsHlg) Block(L.T("HDR → SDR"), L.T("HLG tone mapping"));
            else if (h.DolbyVisionWithoutFallback) Block(L.T("HDR → SDR"), L.T("Dolby Vision without HDR10 base (only libplacebo maps it correctly)"));
            else
            {
                var curve = i.Settings.ToneMap switch
                {
                    ToneMapCurve.Hable => "hable",
                    ToneMapCurve.Mobius => "mobius",
                    _ => "bt2390",
                };
                double peak = h.MaxCll is > 0 and var cll ? cll : h.Mastering?.MaxLuminance is > 0 and var ml ? ml : 1000;
                // The input colours are written out: a script (--vpy/--avs) or the y4m pipe carries none, and "auto" only
                // works with the encoder's own file reader.
                Gpu(L.T("HDR → SDR ({0})", curve), "--vpp-colorspace",
                    $"matrix=bt2020nc:bt709,colorprim=bt2020:bt709,transfer=smpte2084:bt709,range=limited:limited,hdr2sdr={curve},source_peak={N(peak)},ldr_nits=100");
                if (i.Settings.ToneMap is ToneMapCurve.Spline or ToneMapCurve.Bt2446a)
                    steps[^1] = steps[^1] with { Note = L.T("{0} has no {1} curve: BT.2390 is used (choose Hable, Mobius or BT.2390 to match exactly).", enc, i.Settings.ToneMap.ToString()) };
                if (i.Settings.ToneMapDynamic)
                    steps[^1] = steps[^1] with { Note = ((steps[^1].Note ?? "") + " " + L.T("Scene-by-scene peak detection is not available on the GPU path: the static peak is used.")).Trim() };
            }
        }

        // ------------------------------------------------------------ denoise / deband (the same values as FFmpeg's hqdn3d)
        var dn = f.Denoise switch
        {
            FilterStrength.Light => "luma_spatial=2,chroma_spatial=1.5,luma_temporal=3,chroma_temporal=2.25",
            FilterStrength.Medium => "luma_spatial=4,chroma_spatial=3,luma_temporal=6,chroma_temporal=4.5",
            FilterStrength.Strong => "luma_spatial=7,chroma_spatial=5.5,luma_temporal=10,chroma_temporal=8",
            _ => null,
        };
        if (dn is not null) Gpu(L.T("Denoise (hqdn3d)"), "--vpp-hqdn3d", dn);
        var db = f.Deband switch
        {
            FilterStrength.Light => "range=12,thre=10,dither=10",
            FilterStrength.Medium => "range=15,thre=15,dither=15",
            FilterStrength.Strong => "range=22,thre=22,dither=15",
            _ => null,
        };
        if (db is not null) Gpu(L.T("Deband"), "--vpp-deband", db);

        // ------------------------------------------------------------ burned subtitles
        if (i.Burn is { } burn)
        {
            var p = i.Prepared;
            if (p?.RelativeSubtitleFile is { } sub)
                Gpu(L.T("Burned subtitles"), "--vpp-subburn", $"filename={sub}" + (p.RelativeFontsDir is { } fonts ? $",fontsdir={fonts}" : ""));
            else if (p?.BitmapStreamIndex is { } idx && !i.Script && Math.Abs(p.BitmapDelaySeconds) < 0.0005
                     && (p.BitmapFile is null || p.BitmapFile == i.SourceMedia?.Path)
                     && i.SourceMedia?.Subtitles.Select(s => s.Index).ToList() is { } subs && subs.IndexOf(idx) is >= 0 and var n)
                Gpu(L.T("Burned subtitles"), "--vpp-subburn", $"track={(n + 1).ToString(CultureInfo.InvariantCulture)}");
            else Block(L.T("Burned subtitles"), L.T("picture subtitles from another file or with a delay"));
        }

        // ------------------------------------------------------------ resize
        if (i.Upscaler is { } up) Block(up, L.T("{0} upscaling", up));
        else if (i.Scale is { } sc)
        {
            if (!sc.SquarePixels) Block(L.T("Resize"), L.T("non-square pixels"));
            else Gpu(L.T("Resize to {0}×{1} (Lanczos)", sc.Width, sc.Height), "--output-res", $"{sc.Width}x{sc.Height}", "--vpp-resize", "lanczos3");
        }

        // ------------------------------------------------------------ sharpen (CAS, as FFmpeg's cas)
        var sh = f.Sharpen switch { FilterStrength.Light => 0.3, FilterStrength.Medium => 0.5, FilterStrength.Strong => 0.8, _ => 0.0 };
        if (sh > 0)
            Gpu(L.T("Sharpen (CAS)"), "--vpp-cas", $"sharpness={N(sh)}" + (i.Hdr.Action == HdrAction.Preserve ? ",hdr=true" : ""));

        // ------------------------------------------------------------ what has no GPU equivalent
        if (f.Fps is > 0) Block(L.T("Frame rate"), L.T("frame rate change"));
        if (!string.IsNullOrWhiteSpace(f.Custom))
            Block(L.T("Custom FFmpeg filters"), L.T("custom FFmpeg filters"), L.T("write them as --vpp-* in the GPU filters"));

        // The user's own GPU filters (Filters tab) run after KnA's.
        if (!string.IsNullOrWhiteSpace(i.Settings.Encoder.GpuFilters))
            steps.Add(new PipelineStep(L.T("Your GPU filters"), StepPlace.Gpu, i.Settings.Encoder.GpuFilters!.Trim()));
        steps.Add(new PipelineStep(L.T("Encode ({0})", enc), StepPlace.Gpu));
        return new Result(a, steps, blockers, trimStart);
    }

    // ---------------------------------------------------------------- VapourSynth / AviSynth catalogue

    /// <summary>
    /// The GPU filter of the encoders that does the job of a catalogue filter: (option, explanation, same algorithm).
    /// "Same algorithm" filters can replace the script step without changing the result in a visible way.
    /// </summary>
    public static (string Option, string Why, bool Same)? Alternative(string catalogueId) => catalogueId.Split('.').Last() switch
    {
        "bwdif" => ("--vpp-bwdif", L.T("the same Bwdif deinterlacer on the GPU"), true),
        "qtgmc" => ("--vpp-rtgmc", L.T("rigaya's QTGMC for the GPU (relaxed implementation, much faster)"), false),
        "ivtc" => ("--vpp-ivtc --vpp-decimate", L.T("inverse telecine and decimation on the GPU"), false),
        "knlm" or "nlmeans" => ("--vpp-nlmeans", L.T("the same non-local means algorithm on the GPU"), false),
        "dfttest" => ("--vpp-fft3d", L.T("frequency-domain denoiser on the GPU"), false),
        "fft3d" => ("--vpp-fft3d", L.T("FFT3D denoiser on the GPU"), false),
        "smdegrain" => ("--vpp-degrain", L.T("motion-compensated temporal denoiser on the GPU"), false),
        "f3kdb" => ("--vpp-deband", L.T("the flash3kyuu deband algorithm on the GPU"), false),
        "placebo" => ("--vpp-libplacebo-deband", L.T("libplacebo's deband inside the encoder"), false),
        "cas" => ("--vpp-cas", L.T("the same AMD CAS on the GPU"), true),
        "lsfmod" => ("--vpp-unsharp", L.T("unsharp mask / edge level on the GPU"), false),
        "awarpsharp" => ("--vpp-warpsharp", L.T("warp sharpening on the GPU"), false),
        "dehalo" => ("--vpp-dehalo mode=alpha", L.T("halo removal (DeHalo_alpha style) on the GPU"), false),
        "crop" => ("--crop", L.T("the encoder crops while reading"), true),
        "resize" => ("--output-res --vpp-resize", L.T("the same resize kernels on the GPU"), true),
        "depth" => ("--output-depth", L.T("the encoder writes the bit depth itself"), true),
        "levels" or "tweak" => ("--vpp-tweak", L.T("brightness / contrast / gamma / saturation on the GPU"), false),
        _ => null,
    };

    private static readonly Dictionary<string, string> ResizeKernels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Spline36"] = "spline36", ["Spline64"] = "spline64", ["Lanczos"] = "lanczos3", ["Bicubic"] = "bicubic", ["Bilinear"] = "bilinear",
    };

    /// <summary>
    /// A catalogue chain made only of "same algorithm" filters, as GPU options (null when a filter needs the script).
    /// The script is still written and shown; it just isn't needed to run.
    /// </summary>
    public static List<(string Name, List<string> Args)>? ChainOnGpu(FrameserverSettings fs, EncoderBackend backend)
    {
        if (fs.Kind == FrameserverKind.None || fs.Chain is not { Count: > 0 } chain || fs.AudioFromScript) return null;
        var list = new List<(string, List<string>)>();
        foreach (var item in chain)
        {
            var cat = FilterCatalog.Get(item.Id);
            if (cat is null || Alternative(item.Id) is not { Same: true }) return null;
            string Val(string name) => FilterCatalog.Format(cat.Kind, cat.Params.First(p => p.Name == name), item.Values?.GetValueOrDefault(name));
            string kind = item.Id.Split('.').Last();
            switch (kind)
            {
                case "bwdif":
                    // VapourSynth field 0/1 = same rate (bottom/top), 2/3 = double rate; AviSynth BWDIF field -1..3.
                    var field = cat.Params.Any(p => p.Name == "field") ? Val("field") : "1";
                    bool bob = field is "2" or "3";
                    string order = field is "0" or "2" ? "bff" : field is "1" or "3" ? "tff" : "auto";
                    list.Add((cat.Name, ["--vpp-bwdif", $"mode={(bob ? "bob" : "frame")},order={order}"]));
                    break;
                case "cas":
                    list.Add((cat.Name, ["--vpp-cas", $"sharpness={Val("sharpness")}"]));
                    break;
                case "crop":
                    var names = cat.Params.Select(p => p.Name).ToList();
                    if (!names.Contains("left") || !names.Contains("right") || !names.Contains("top") || !names.Contains("bottom")) return null;
                    list.Add((cat.Name, ["--crop", $"{Val("left")},{Val("top")},{Val("right")},{Val("bottom")}"]));
                    break;
                case "resize":
                    if (!cat.Params.Any(p => p.Name == "width") || !cat.Params.Any(p => p.Name == "height")) return null;
                    var kernel = cat.Params.FirstOrDefault(p => p.Name == "kernel") is { } kp ? FilterCatalog.Format(cat.Kind, kp, item.Values?.GetValueOrDefault("kernel")) : "Spline36";
                    if (!ResizeKernels.TryGetValue(kernel, out var algo)) return null;
                    list.Add((cat.Name, ["--output-res", $"{Val("width")}x{Val("height")}", "--vpp-resize", algo]));
                    break;
                case "depth":
                    // The encoder works at high precision and writes the depth chosen in the Video tab.
                    list.Add((cat.Name, []));
                    break;
                default:
                    return null;
            }
        }
        return list;
    }

    /// <summary>The steps of a script that stays on the CPU, each with the GPU filter that could replace it.</summary>
    public static IEnumerable<PipelineStep> ScriptSteps(FrameserverSettings fs)
    {
        if (fs.Chain is not { Count: > 0 } chain)
        {
            yield return new PipelineStep(L.T("Script written by hand"), StepPlace.CpuScript);
            yield break;
        }
        for (int k = 0; k < chain.Count; k++)
        {
            var item = chain[k];
            var cat = FilterCatalog.Get(item.Id);
            var alt = Alternative(item.Id);
            yield return new PipelineStep(cat?.Name ?? item.Id, StepPlace.CpuScript, null,
                alt is null ? L.T("No GPU filter of the encoder does this.") : null,
                alt is { } x ? $"{x.Option} — {x.Why}" : null) { ChainIndex = alt is null ? null : k };
        }
    }

    private static readonly Dictionary<string, string> RtgmcPresets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Placebo"] = "slower", ["Very Slow"] = "slower", ["Slower"] = "slower", ["Slow"] = "slow", ["Medium"] = "medium", ["Fast"] = "fast",
        ["Faster"] = "faster", ["Very Fast"] = "veryfast", ["Super Fast"] = "superfast", ["Ultra Fast"] = "ultrafast", ["Draft"] = "draft",
    };

    /// <summary>
    /// "Use the GPU filter instead": the catalogue filter at <paramref name="index"/> leaves the script and its GPU
    /// alternative is added to the encoder's GPU filters (QTGMC's preset carried over to --vpp-rtgmc). With nothing
    /// left in the chain the script is switched off.
    /// </summary>
    public static JobSettings UseGpuInstead(JobSettings s, int index)
    {
        var fs = s.Frameserver;
        if (fs.Chain is not { } chain || index < 0 || index >= chain.Count || Alternative(chain[index].Id) is not { } alt) return s;
        var item = chain[index];
        string option = alt.Option;
        if (item.Id.EndsWith(".qtgmc", StringComparison.Ordinal) && RtgmcPresets.TryGetValue(item.Values?.GetValueOrDefault("preset") ?? "Slower", out var pr))
            option = $"--vpp-rtgmc preset={pr}";
        var rest = chain.Where((_, k) => k != index).ToList();
        var gpu = ToolArguments.Combine(s.Encoder.GpuFilters, option);
        return s with
        {
            Frameserver = rest.Count == 0 ? new FrameserverSettings() : fs with { Chain = rest },
            Encoder = s.Encoder with { GpuFilters = gpu },
        };
    }
}
