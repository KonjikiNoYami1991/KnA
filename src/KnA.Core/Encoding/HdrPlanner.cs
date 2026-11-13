using System.Globalization;
using KnA.Core.Localization;
using KnA.Core.Media;

namespace KnA.Core.Encoding;

public enum HdrAction { None, ToneMap, Preserve }

public sealed record HdrPlan
{
    public HdrAction Action { get; init; } = HdrAction.None;
    /// <summary>Filters inserted early in the video chain (tone mapping).</summary>
    public string? Filter { get; init; }
    /// <summary>Colour tags for the output (-color_primaries …).</summary>
    public List<string> ColorArgs { get; init; } = [];
    /// <summary>Extra ":key=value" items for -x265-params.</summary>
    public List<string> X265Params { get; init; } = [];
    /// <summary>A Dolby Vision RPU must be extracted with dovi_tool and passed to x265.</summary>
    public bool UseDolbyVisionRpu { get; init; }
    /// <summary>HDR10+ metadata must be extracted with hdr10plus_tool and passed to x265.</summary>
    public bool UseHdr10PlusJson { get; init; }
    public List<ValidationIssue> Issues { get; init; } = [];
    public string Summary { get; init; } = "";
}

public sealed record HdrEnvironment(bool HasZscale, bool HasLibplacebo, bool HasDoviTool, bool HasHdr10PlusTool)
{
    public static readonly HdrEnvironment Unknown = new(true, false, false, false);
}

/// <summary>
/// Decides what happens to HDR sources:
///  • SDR output (H.264/8-bit/XviD, images, or HdrMode.ToneMapSdr) → tone mapping to BT.709
///    (libplacebo when available — the only correct path for Dolby Vision profile 5 — otherwise zscale+hable);
///  • 10-bit HEVC/AV1 output → HDR10 / HLG kept with full colour tags and mastering metadata;
///    HDR10+ and Dolby Vision dynamic metadata are carried only with libx265 + hdr10plus_tool / dovi_tool,
///    and only when the frame count is unchanged (no trim, no fps change).
/// Copied video (remux) always keeps everything untouched.
/// </summary>
public static class HdrPlanner
{
    public static HdrPlan Plan(VideoStream v, JobSettings s, bool videoIsCopied, bool trimmed, HdrEnvironment env)
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

        if (!preserve) return ToneMap(v, s, env, issues);

        // ---------------------------------------------------------------- preserve
        bool hlg = hdr.IsHlg;
        bool dvOnly = hdr.DolbyVisionWithoutFallback;
        if (dvOnly)
        {
            issues.Add(Containers.Warning("Dolby Vision profile 5 has no HDR10 fallback: it is converted to SDR."));
            return ToneMap(v, s, env, issues);
        }

        string trc = hlg ? "arib-std-b67" : "smpte2084";
        var color = new List<string> { "-color_primaries", "bt2020", "-color_trc", trc, "-colorspace", "bt2020nc", "-color_range", "tv" };
        var x265 = new List<string>();
        bool rpu = false, hdr10Plus = false;
        bool frameExact = !trimmed && !s.VideoFilters.ChangesFrameCount;
        bool cropped = s.VideoFilters.Crop is { IsEmpty: false };

        if (codec == VideoCodec.HEVC && backend == EncoderBackend.Software)
        {
            x265.AddRange(["hdr10-opt=1", "repeat-headers=1", "colorprim=bt2020", $"transfer={trc}", "colormatrix=bt2020nc"]);
            if (!hlg)
            {
                x265.Add("hdr10=1");
                if (hdr.Mastering is { } md) x265.Add("master-display=" + md.ToX265());
                if (hdr.MaxCll is > 0) x265.Add($"max-cll={hdr.MaxCll.Value.ToString(CultureInfo.InvariantCulture)},{(hdr.MaxFall ?? 0).ToString(CultureInfo.InvariantCulture)}");
            }

            if (hdr.IsDolbyVision)
            {
                if (env.HasDoviTool && frameExact && !cropped) rpu = true;
                else issues.Add(Containers.Warning(
                    "The Dolby Vision layer is dropped (needs dovi_tool, no trim, no crop and no frame-rate change); the HDR10 base is kept."));
            }
            if (hdr.HasHdr10Plus)
            {
                if (env.HasHdr10PlusTool && frameExact) hdr10Plus = true;
                else issues.Add(Containers.Warning(
                    "HDR10+ dynamic metadata is dropped (needs hdr10plus_tool, no trim and no frame-rate change); static HDR10 is kept."));
            }
        }
        else if (hdr.IsDolbyVision || hdr.HasHdr10Plus)
        {
            issues.Add(Containers.Warning(
                "Dolby Vision / HDR10+ dynamic metadata can only be kept with HEVC on the CPU (x265): {0} keeps static HDR only.",
                VideoEncoders.EncoderName(codec, backend)));
        }

        var kept = hlg ? "HLG" : "HDR10";
        if (rpu) kept = $"Dolby Vision 8.1 + {kept}";
        if (hdr10Plus) kept += " + HDR10+";
        return new HdrPlan
        {
            Action = HdrAction.Preserve, ColorArgs = color, X265Params = x265,
            UseDolbyVisionRpu = rpu, UseHdr10PlusJson = hdr10Plus, Issues = issues,
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

    private static HdrPlan ToneMap(VideoStream v, JobSettings s, HdrEnvironment env, List<ValidationIssue> issues)
    {
        var hdr = v.Hdr;
        string filter;
        if (env.HasLibplacebo)
        {
            // Handles PQ, HLG and Dolby Vision (including profile 5 reshaping) correctly. The output is BT.709
            // (primaries, transfer, matrix, limited range). Without peak_detect=0 libplacebo measures every scene and
            // stretches dim ones towards white (the "too bright" SDR), and contrast_recovery lifts flat areas further.
            filter = $"libplacebo=tonemapping={LibplaceboCurve(s.ToneMap)}" +
                     (s.ToneMapDynamic ? "" : ":peak_detect=0:contrast_recovery=0") +
                     ":colorspace=bt709:color_primaries=bt709:color_trc=bt709:range=tv:format=yuv420p";
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
                     $"zscale=p=bt709,tonemap=tonemap={ZscaleCurve(s.ToneMap)}:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p";
        }
        // The frames still carry the source's HDR side data (mastering display, MaxCLL/MaxFALL, HDR10+): recent FFmpeg
        // passes it to the encoder, which writes it into the SDR file, and players/TVs then treat it as HDR.
        filter += "," + StripHdrSideData;
        return new HdrPlan
        {
            Action = HdrAction.ToneMap, Filter = filter,
            ColorArgs = ["-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709", "-color_range", "tv"],
            Issues = issues, Summary = L.T("{0} → SDR (tone mapping)", hdr.Description),
        };
    }
}
