using System.Globalization;
using KnA.Core.Localization;
using KnA.Core.Media;

namespace KnA.Core.Encoding;

public enum HdrAction { None, ToneMap, Preserve }

/// <summary>How a piece of dynamic HDR metadata (Dolby Vision RPU, HDR10+ JSON) reaches the output.</summary>
public enum HdrCarry
{
    /// <summary>Not carried (static HDR10/HLG only, or SDR).</summary>
    None,
    /// <summary>Extracted from the source (edited for trim/crop) and given to the encoder as a file.</summary>
    Encoder,
    /// <summary>Copied by NVEncC/QSVEncC/VCEEncC while they read the source themselves (--avhw/--avsw only).</summary>
    Copy,
    /// <summary>Extracted, edited and injected into the finished HEVC stream (dovi_tool / hdr10plus_tool), then remuxed.</summary>
    Inject,
}

public sealed record HdrPlan
{
    public HdrAction Action { get; init; } = HdrAction.None;
    /// <summary>Filters inserted early in the video chain (tone mapping).</summary>
    public string? Filter { get; init; }
    /// <summary>Colour tags for the output (-color_primaries …).</summary>
    public List<string> ColorArgs { get; init; } = [];
    /// <summary>Extra ":key=value" items for -x265-params.</summary>
    public List<string> X265Params { get; init; } = [];
    public HdrCarry DolbyVision { get; init; }
    public HdrCarry Hdr10Plus { get; init; }
    /// <summary>Dolby Vision profile of the output ("8.1", "8.4", "5.0", "10.1"…), when it is carried.</summary>
    public string? DolbyVisionProfile { get; init; }
    /// <summary>The picture is cropped: the RPU's active area (letterbox offsets) is reset.</summary>
    public bool RpuCrop { get; init; }
    /// <summary>
    /// Parts of the video's own timeline (seconds) that reach the output, when a trim removes frames: the RPU and the
    /// HDR10+ JSON keep only these frames. Null = every frame.
    /// </summary>
    public IReadOnlyList<(double Start, double End)>? KeptSeconds { get; init; }
    /// <summary>Frame rate used to turn <see cref="KeptSeconds"/> into frame numbers.</summary>
    public double FrameRate { get; init; }
    /// <summary>Static HDR10 metadata of the output (written again in the container when the video is remuxed).</summary>
    public MasteringDisplay? Mastering { get; init; }
    public int? MaxCll { get; init; }
    public int? MaxFall { get; init; }
    /// <summary>The RPU is extracted with dovi_tool (passed to the encoder or injected).</summary>
    public bool UseDolbyVisionRpu => DolbyVision is HdrCarry.Encoder or HdrCarry.Inject;
    /// <summary>The HDR10+ JSON is extracted with hdr10plus_tool (passed to the encoder or injected).</summary>
    public bool UseHdr10PlusJson => Hdr10Plus is HdrCarry.Encoder or HdrCarry.Inject;
    /// <summary>The encoder itself reads a side file (a resumed or split encode would lose the frame alignment).</summary>
    public bool EncoderReadsSideFiles => DolbyVision == HdrCarry.Encoder || Hdr10Plus == HdrCarry.Encoder;
    /// <summary>Metadata is injected into the finished HEVC.</summary>
    public bool NeedsInjection => DolbyVision == HdrCarry.Inject || Hdr10Plus == HdrCarry.Inject;
    public List<ValidationIssue> Issues { get; init; } = [];
    public string Summary { get; init; } = "";
    /// <summary>What is kept and how, one line each (log and editor pipeline).</summary>
    public List<string> Details { get; init; } = [];
}

public sealed record HdrEnvironment(bool HasZscale, bool HasLibplacebo, bool HasDoviTool, bool HasHdr10PlusTool)
{
    /// <summary>mkvmerge is there: injected metadata can be remuxed (FFmpeg can't time a raw HEVC stream).</summary>
    public bool HasMkvmerge { get; init; } = true;
    public static readonly HdrEnvironment Unknown = new(true, false, false, false);
}

/// <summary>Where the video goes: what decides whether dynamic HDR metadata can follow it frame by frame.</summary>
public sealed record HdrRoute
{
    /// <summary>A single trim (start / end).</summary>
    public bool Trimmed { get; init; }
    /// <summary>Several kept parts, encoded one by one and joined.</summary>
    public bool MultiSegment { get; init; }
    /// <summary>Chunked encoding (segments encoded separately).</summary>
    public bool Chunked { get; init; }
    /// <summary>NVEncC / QSVEncC / VCEEncC encode the video.</summary>
    public bool Rigaya { get; init; }
    /// <summary>Frames are added or removed (frame-rate change, a script that changes the count…).</summary>
    public bool FrameCountChanged { get; init; }
    /// <summary>Why the frame count changes (shown to the user).</summary>
    public string? FrameCountReason { get; init; }
    /// <summary>The picture is cropped (KnA's crop or the script's).</summary>
    public bool Cropped { get; init; }
    /// <summary>The video comes from a script that reads the source: its frames carry no colour data.</summary>
    public bool FromScript { get; init; }
    /// <summary>The video has a variable frame rate: a raw HEVC stream can't be remuxed with its timing.</summary>
    public bool VariableFrameRate { get; init; }
    /// <summary>Kept parts of the video's timeline (seconds) when trimmed, null otherwise.</summary>
    public IReadOnlyList<(double Start, double End)>? KeptSeconds { get; init; }
}

/// <summary>
/// Decides what happens to HDR sources, by itself (it is an autoencoder: no option, no extraction step for the user):
///  • SDR output (H.264/8-bit/XviD, images, or HdrMode.ToneMapSdr) → tone mapping to BT.709, and no HDR parameter
///    reaches the encoder (libplacebo when available — the only correct path for Dolby Vision profile 5 — otherwise
///    zscale+hable). Script video is tagged with the source's colours first (vspipe's y4m carries none);
///  • 10-bit HEVC/AV1 output → HDR10 / HLG kept with explicit colour tags and mastering metadata;
///  • Dolby Vision and HDR10+ are extracted from the source (dovi_tool / hdr10plus_tool), edited for a trim (only the
///    kept frames) or a crop (RPU active area), then given to the encoder (x265, NVEncC/QSVEncC/VCEEncC) or injected
///    into the finished HEVC when the encoder can't take them (FFmpeg's GPU encoders, chunked or multi-part encodes);
///  • when frames are added or removed (frame-rate change, a script that changes the count) dynamic metadata can no
///    longer match frame by frame: static HDR10 only, said clearly.
/// Copied video (remux) always keeps everything untouched.
/// </summary>
public static class HdrPlanner
{
    /// <summary>Older entry point: a trimmed job.</summary>
    public static HdrPlan Plan(VideoStream v, JobSettings s, bool videoIsCopied, bool trimmed, HdrEnvironment env) =>
        Plan(v, s, videoIsCopied, new HdrRoute { Trimmed = trimmed }, env);

    public static HdrPlan Plan(VideoStream v, JobSettings s, bool videoIsCopied, HdrRoute route, HdrEnvironment env)
    {
        var hdr = v.Hdr;
        if (!hdr.IsHdr) return new HdrPlan { Summary = "SDR" };
        if (videoIsCopied) return new HdrPlan { Summary = L.T("{0} kept (stream copy)", hdr.Description) };

        var codec = s.EffectiveCodec;
        var backend = s.EffectiveBackend;
        bool canCarry = !s.ProfileDefinition.IsImages
                        && codec is VideoCodec.HEVC or VideoCodec.AV1 or VideoCodec.VP9 or VideoCodec.VVC
                            or VideoCodec.ProRes or VideoCodec.FFV1 or VideoCodec.CineForm or VideoCodec.Uncompressed
                        && s.EffectiveTenBit
                        && s.EffectiveContainer is "mkv" or "mp4" or "mov" or "ts" or "webm" or "mxf";

        var issues = new List<ValidationIssue>();
        bool preserve = s.Hdr switch
        {
            HdrMode.ToneMapSdr => false,
            HdrMode.Preserve when !canCarry => Fallback(issues),
            _ => canCarry,
        };
        if (s.ProfileDefinition.IsImages && s.Images.SixteenBit && s.Hdr == HdrMode.Preserve)
            issues.Add(Containers.Warning("Images are always converted to SDR."));

        if (!preserve) return ToneMap(v, s, env, issues, route.FromScript);

        // ---------------------------------------------------------------- preserve
        bool hlg = hdr.IsHlg;
        bool rigaya = route.Rigaya;
        bool x265Single = codec == VideoCodec.HEVC && backend == EncoderBackend.Software && !rigaya && !route.Chunked && !route.MultiSegment;
        bool rigayaOne = rigaya && codec is VideoCodec.HEVC or VideoCodec.AV1 && !route.MultiSegment;
        var details = new List<string>();
        string encoderName = (rigaya ? RigayaEncoders.DisplayName(s.Backend) : VideoEncoders.EncoderName(codec, backend)) ?? codec.Display();
        string why = route.FrameCountReason ?? L.T("the frame count changes");

        // Dolby Vision: from the source's RPU (dovi_tool), the profile follows the base layer.
        var dv = HdrCarry.None;
        string? dvProfile = null;
        if (hdr.IsDolbyVision)
        {
            bool p5 = hdr.DolbyVisionWithoutFallback;
            if (!env.HasDoviTool) issues.Add(Containers.Warning("dovi_tool is missing: the Dolby Vision layer is dropped, the HDR10 base is kept."));
            else if (route.FrameCountChanged)
                issues.Add(Containers.Warning("Dolby Vision is dropped because {0} (its metadata no longer matches frame by frame): static HDR is kept.", why));
            else if (x265Single || rigayaOne) dv = HdrCarry.Encoder;
            else if (codec == VideoCodec.HEVC && !p5 && !route.VariableFrameRate && env.HasMkvmerge) dv = HdrCarry.Inject;
            else if (codec == VideoCodec.HEVC && !p5 && !env.HasMkvmerge)
                issues.Add(Containers.Warning("mkvmerge is missing: Dolby Vision can't be added to {0} after encoding, static HDR is kept.", encoderName));
            else issues.Add(Containers.Warning("Dolby Vision can't be added to {0} after encoding (dovi_tool works on HEVC): static HDR is kept.",
                codec == VideoCodec.HEVC ? encoderName : codec.Display()));
            if (dv != HdrCarry.None)
                dvProfile = codec == VideoCodec.AV1
                    ? (p5 ? "10.0" : hlg ? "10.4" : "10.1")
                    : (p5 ? "5.0" : hlg ? "8.4" : "8.1");
            if (p5 && dv == HdrCarry.None)
            {
                issues.Add(Containers.Warning("Dolby Vision profile 5 has no HDR10 fallback: it is converted to SDR."));
                return ToneMap(v, s, env, issues, route.FromScript);
            }
        }

        // HDR10+: from the source's SEI (hdr10plus_tool).
        var h10 = HdrCarry.None;
        if (hdr.HasHdr10Plus)
        {
            if (route.FrameCountChanged)
                issues.Add(Containers.Warning("HDR10+ is dropped because {0} (its metadata no longer matches frame by frame): static HDR10 is kept.", why));
            else if (rigaya && codec == VideoCodec.AV1)
            {
                // The encoders read a JSON file only through rigaya's extra hdr10plus_gen module: copy it while they read the source.
                if (route.Trimmed || route.FromScript || route.MultiSegment)
                    issues.Add(Containers.Warning("HDR10+ in AV1 is copied by {0} only from the untrimmed source file: static HDR10 is kept.", encoderName));
                else h10 = HdrCarry.Copy;
            }
            else if (!env.HasHdr10PlusTool) issues.Add(Containers.Warning("hdr10plus_tool is missing: HDR10+ dynamic metadata is dropped, static HDR10 is kept."));
            else if (x265Single) h10 = HdrCarry.Encoder;
            else if (codec == VideoCodec.HEVC && !route.VariableFrameRate && env.HasMkvmerge) h10 = HdrCarry.Inject;
            else if (codec == VideoCodec.HEVC && !env.HasMkvmerge)
                issues.Add(Containers.Warning("mkvmerge is missing: HDR10+ can't be added to {0} after encoding, static HDR10 is kept.", encoderName));
            else issues.Add(Containers.Warning("HDR10+ can only be added to HEVC (hdr10plus_tool): {0} keeps static HDR10.", encoderName));
        }

        bool p5Out = dvProfile is "5.0" or "10.0";
        string trc = hlg ? "arib-std-b67" : "smpte2084";
        // Profile 5 frames are IPTPQc2, not BT.2020: the encoder writes Dolby Vision's own signalling.
        var color = p5Out ? new List<string>() : ["-color_primaries", "bt2020", "-color_trc", trc, "-colorspace", "bt2020nc", "-color_range", "tv"];
        if (!p5Out && v.ChromaLocation is { Length: > 0 } cl && cl != "unspecified") color.AddRange(["-chroma_sample_location", cl]);
        var x265 = new List<string>();
        if (codec == VideoCodec.HEVC && backend == EncoderBackend.Software && !rigaya)
        {
            x265.AddRange(["hdr10-opt=1", "repeat-headers=1"]);
            if (!p5Out) x265.AddRange(["colorprim=bt2020", $"transfer={trc}", "colormatrix=bt2020nc"]);
            if (v.ChromaLocationIndex is { } ci && !p5Out) x265.Add("chromaloc=" + ci.ToString(CultureInfo.InvariantCulture));
            if (!hlg && !p5Out)
            {
                x265.Add("hdr10=1");
                if (hdr.Mastering is { } md) x265.Add("master-display=" + md.ToX265());
                if (hdr.MaxCll is > 0) x265.Add($"max-cll={hdr.MaxCll.Value.ToString(CultureInfo.InvariantCulture)},{(hdr.MaxFall ?? 0).ToString(CultureInfo.InvariantCulture)}");
            }
        }

        // ---------------------------------------------------------------- what is kept, and how (log / editor)
        string edits = string.Join(", ", new[]
        {
            route.KeptSeconds is not null && (dv != HdrCarry.None || h10 != HdrCarry.None) ? L.T("only the kept frames") : null,
        }.Where(x => x is not null));
        if (!p5Out)
            details.Add(hlg ? L.T("HLG: colour tags written explicitly.")
                : L.T("HDR10: colour tags, mastering display and MaxCLL/MaxFALL written explicitly."));
        if (dv != HdrCarry.None)
            details.Add(L.T("Dolby Vision {0}: RPU extracted with dovi_tool{1}{2}, {3}.", dvProfile!,
                edits.Length > 0 ? " (" + edits + ")" : "",
                route.Cropped ? " " + L.T("with the active area of the crop") : "",
                dv == HdrCarry.Encoder ? L.T("given to {0}", encoderName) : L.T("injected into the finished HEVC")));
        if (dv != HdrCarry.None)
            details.Add(env.HasMkvmerge
                ? L.T("Dolby Vision configuration record written with mkvmerge when the encoder leaves it out (TVs and players need it).")
                : L.T("mkvmerge is missing: the file will carry the Dolby Vision RPU without the configuration record (some TVs and players won't see Dolby Vision)."));
        if (h10 == HdrCarry.Copy) details.Add(L.T("HDR10+: copied by {0} from the source.", encoderName));
        else if (h10 != HdrCarry.None)
            details.Add(L.T("HDR10+: JSON extracted with hdr10plus_tool{0}, {1}.", edits.Length > 0 ? " (" + edits + ")" : "",
                h10 == HdrCarry.Encoder ? L.T("given to {0}", encoderName) : L.T("injected into the finished HEVC")));
        if (route.FromScript) details.Add(L.T("The script's video takes the HDR data of its source file."));

        var kept = p5Out ? "" : hlg ? "HLG" : "HDR10";
        if (dv != HdrCarry.None) kept = $"Dolby Vision {dvProfile}" + (kept.Length > 0 ? " + " + kept : "");
        if (h10 != HdrCarry.None) kept += " + HDR10+";
        return new HdrPlan
        {
            Action = HdrAction.Preserve, ColorArgs = color, X265Params = x265,
            DolbyVision = dv, Hdr10Plus = h10, DolbyVisionProfile = dvProfile, RpuCrop = route.Cropped,
            KeptSeconds = route.KeptSeconds, FrameRate = v.FrameRate,
            Mastering = hlg || p5Out ? null : hdr.Mastering, MaxCll = hlg || p5Out ? null : hdr.MaxCll, MaxFall = hlg || p5Out ? null : hdr.MaxFall,
            Issues = issues, Details = details,
            Summary = L.T("{0} → {1} kept", hdr.Description, kept),
        };
    }

    private static bool Fallback(List<ValidationIssue> issues)
    {
        issues.Add(Containers.Warning("HDR can only be kept in 10-bit HEVC or AV1: the video is converted to SDR."));
        return false;
    }

    /// <summary>Removes HDR metadata from the frames once they are SDR.</summary>
    public const string StripHdrSideData =
        "sidedata=mode=delete:type=MASTERING_DISPLAY_METADATA,sidedata=mode=delete:type=CONTENT_LIGHT_LEVEL,sidedata=mode=delete:type=DYNAMIC_HDR_PLUS";

    /// <summary>libplacebo name of a curve.</summary>
    public static string LibplaceboCurve(ToneMapCurve c) => c switch
    {
        ToneMapCurve.Bt2390 => "bt.2390",
        ToneMapCurve.Bt2446a => "bt.2446a",
        ToneMapCurve.Hable => "hable",
        ToneMapCurve.Mobius => "mobius",
        _ => "spline",
    };

    /// <summary>FFmpeg "tonemap" filter curve (zscale path): it only has hable, mobius, reinhard, clip, gamma, linear.</summary>
    public static string ZscaleCurve(ToneMapCurve c) => c == ToneMapCurve.Mobius ? "mobius" : "hable";

    /// <summary>
    /// Input colours of HDR frames that carry none (y4m from a script): the tone mapping must know they are PQ/HLG
    /// BT.2020, never assume SDR.
    /// </summary>
    public static string SourceColours(HdrInfo hdr) =>
        $"setparams=color_primaries=bt2020:color_trc={(hdr.IsHlg ? "arib-std-b67" : "smpte2084")}:colorspace=bt2020nc:range=tv";

    private static HdrPlan ToneMap(VideoStream v, JobSettings s, HdrEnvironment env, List<ValidationIssue> issues, bool fromScript = false)
    {
        var hdr = v.Hdr;
        if (fromScript && hdr.DolbyVisionWithoutFallback)
            issues.Add(Containers.Warning("Dolby Vision profile 5 read through a script loses its reshaping: colours will be wrong. Remove the script filters to convert it correctly."));
        // 10-bit SDR when the encoder takes it (HEVC/AV1/VP9…): tone mapping makes smooth gradients that 8 bits band.
        string fmt = s.EffectiveTenBit ? "yuv420p10le" : "yuv420p";
        string filter;
        if (env.HasLibplacebo)
        {
            // Handles PQ, HLG and Dolby Vision (including profile 5 reshaping) correctly. The output is BT.709
            // (primaries, transfer, matrix, limited range). Without peak_detect=0 libplacebo measures every scene and
            // stretches dim ones towards white (the "too bright" SDR), and contrast_recovery lifts flat areas further.
            filter = $"libplacebo=tonemapping={LibplaceboCurve(s.ToneMap)}" +
                     (s.ToneMapDynamic ? "" : ":peak_detect=0:contrast_recovery=0") +
                     $":colorspace=bt709:color_primaries=bt709:color_trc=bt709:range=tv:format={fmt}";
        }
        else
        {
            if (hdr.DolbyVisionWithoutFallback)
                issues.Add(Containers.Warning(
                    "Dolby Vision profile 5 needs libplacebo (Vulkan) for correct colours; without it the result will have a green/purple tint."));
            if (!env.HasZscale)
            {
                issues.Add(Containers.Error("This FFmpeg build has no zscale filter: HDR to SDR conversion is impossible."));
                return new HdrPlan { Issues = issues, Summary = hdr.Description };
            }
            string tin = hdr.IsHlg ? "arib-std-b67" : "smpte2084";
            filter = $"zscale=tin={tin}:min=bt2020nc:pin=bt2020:rin=tv:t=linear:npl=100,format=gbrpf32le," +
                     $"zscale=p=bt709,tonemap=tonemap={ZscaleCurve(s.ToneMap)}:desat=0,zscale=t=bt709:m=bt709:r=tv,format={fmt}";
        }
        // The frames still carry the source's HDR side data (mastering display, MaxCLL/MaxFALL, HDR10+): recent FFmpeg
        // passes it to the encoder, which writes it into the SDR file, and players/TVs then treat it as HDR.
        filter += "," + StripHdrSideData;
        if (fromScript) filter = SourceColours(hdr) + "," + filter;
        return new HdrPlan
        {
            Action = HdrAction.ToneMap, Filter = filter,
            Details = [L.T("Converted to SDR BT.709: no HDR parameter reaches the encoder."),
                .. fromScript ? new[] { L.T("The script's frames are tone-mapped as {0} BT.2020 (the source's colours).", hdr.IsHlg ? "HLG" : "PQ") } : []],
            ColorArgs = ["-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709", "-color_range", "tv"],
            Issues = issues, Summary = L.T("{0} → SDR (tone mapping)", hdr.Description),
        };
    }
}
