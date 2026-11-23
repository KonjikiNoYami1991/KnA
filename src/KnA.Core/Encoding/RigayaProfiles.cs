using KnA.Core.Localization;
using KnA.Core.Tools;

namespace KnA.Core.Encoding;

/// <summary>A speed / quality profile of a rigaya encoder: the values it writes into the editor's fields.</summary>
public sealed record RigayaProfile(string Id, string EnglishName, string EnglishDescription, string Speed, string? Tune, int? Lookahead, string? Extra)
{
    public string Name => L.T(EnglishName);
    public string Description => L.T(EnglishDescription);
}

/// <summary>
/// "Best quality", "Balanced" and "Fast" for NVEncC, QSVEncC and VCEEncC. Built on StaxRip's defaults (NVEnc: P4,
/// HQ, 3 B-frames; QSVEnc: balanced; VCEEnc: balanced) and moved towards KnA's GPU-first goal: the profiles only use
/// options that cost GPU time (lookahead, temporal AQ, UHQ tune, split-frame encoding), never CPU work. They fill the
/// fields; every value stays editable. Options checked against the encoders' option documents (NVEncC 9.41,
/// QSVEncC 8.38, VCEEncC 9.23).
/// </summary>
public static class RigayaProfiles
{
    /// <summary>Options the profiles add to the encoder parameters (removed again when another profile is chosen).</summary>
    public static readonly IReadOnlyList<string> Owned = ["--aq-temporal", "--split-enc", "--b-pyramid", "--vbaq", "--pe"];

    public static IReadOnlyList<RigayaProfile> For(EncoderBackend backend, VideoCodec codec) => backend switch
    {
        EncoderBackend.Nvenc =>
        [
            new("best", "Best quality",
                "P7 with the UHQ tune for HEVC/AV1 (Turing or newer), lookahead 32 and temporal AQ: the slowest, most careful NVENC settings, about half the speed of Balanced. For archiving.",
                "P7", codec == VideoCodec.H264 ? "hq" : "uhq", 32, "--aq-temporal"),
            new("balanced", "Balanced",
                "P5 HQ with lookahead 16 and temporal AQ: StaxRip's P4 defaults one step more careful. The best value for films and series.",
                "P5", "hq", 16, "--aq-temporal"),
            new("fast", "Fast",
                "P3 without lookahead; HEVC/AV1 frames are split between the GPU's NVENC engines when it has more than one (--split-enc). For quick encodes.",
                "P3", "hq", 0, codec == VideoCodec.H264 ? null : "--split-enc auto_forced"),
        ],
        EncoderBackend.Qsv =>
        [
            new("best", "Best quality", "The \"best\" preset with pyramid B-frames: the most careful Quick Sync settings.", "best", null, null, "--b-pyramid"),
            new("balanced", "Balanced", "The \"balanced\" preset (StaxRip's default): the best value on most Intel GPUs.", "balanced", null, null, null),
            new("fast", "Fast", "The \"faster\" preset: for quick encodes and older integrated GPUs.", "faster", null, null, null),
        ],
        EncoderBackend.Amf =>
        [
            new("best", "Best quality",
                "The \"slower\" preset with adaptive quantization (H.264/HEVC) and pre-encode rate control (H.264/AV1): the most careful AMF settings.",
                "slower", null, null, codec switch { VideoCodec.H264 => "--vbaq --pe", VideoCodec.HEVC => "--vbaq", _ => "--pe" }),
            new("balanced", "Balanced", "The \"balanced\" preset (StaxRip's default) with adaptive quantization for H.264/HEVC.",
                "balanced", null, null, codec == VideoCodec.AV1 ? null : "--vbaq"),
            new("fast", "Fast", "The \"fast\" preset: for quick encodes.", "fast", null, null, null),
        ],
        _ => [],
    };

    /// <summary>The encoder values with a profile written into them (its own options replace the previous profile's).</summary>
    public static EncoderOverrides Apply(EncoderOverrides o, RigayaProfile p)
    {
        var kept = VideoEncoders.ParseExtra(o.Extra ?? "").Where(x => !Owned.Contains(x.Option))
            .Select(x => x.Value is null ? x.Option : $"{x.Option} {x.Value}");
        var extra = ToolArguments.Combine(string.Join(' ', kept), p.Extra);
        return o with { Speed = p.Speed, Tune = p.Tune, Lookahead = p.Lookahead, Extra = string.IsNullOrWhiteSpace(extra) ? null : extra };
    }

    /// <summary>The profile the current values match, or null (values set by hand).</summary>
    public static RigayaProfile? Current(EncoderOverrides o, EncoderBackend backend, VideoCodec codec)
    {
        var own = VideoEncoders.ParseExtra(o.Extra ?? "").Where(x => Owned.Contains(x.Option)).Select(x => x.Value is null ? x.Option : $"{x.Option} {x.Value}").ToList();
        return For(backend, codec).FirstOrDefault(p =>
            string.Equals(p.Speed, o.Speed, StringComparison.OrdinalIgnoreCase) && p.Tune == o.Tune && p.Lookahead == o.Lookahead
            && own.SequenceEqual(VideoEncoders.ParseExtra(p.Extra ?? "").Select(x => x.Value is null ? x.Option : $"{x.Option} {x.Value}")));
    }

    /// <summary>
    /// The starting profile for the GPU found by the hardware test and the quality level: a generation with hardware
    /// AV1 encoding (NVIDIA Ada / Blackwell, Intel Arc / Core Ultra, AMD RDNA 3+) affords Best quality for the top
    /// level and Balanced below; an older one starts one step faster (Balanced at the top, Fast from "below normal").
    /// </summary>
    public static RigayaProfile Recommended(EncoderBackend backend, VideoCodec codec, QualityLevel quality, FfmpegCapabilities? caps)
    {
        var list = For(backend, codec);
        bool newGeneration = caps?.IsUsable(VideoCodec.AV1, backend, false) == true;
        string id = quality <= QualityLevel.VeryHigh ? (newGeneration ? "best" : "balanced")
            : quality >= QualityLevel.Low || (!newGeneration && quality >= QualityLevel.BelowNormal) ? "fast"
            : "balanced";
        return list.FirstOrDefault(p => p.Id == id) ?? list[1];
    }

    /// <summary>AV1 with a rigaya encoder only where the GPU encodes AV1 in hardware (FFmpeg's test of that GPU passed).</summary>
    public static bool Av1InHardware(EncoderBackend backend, FfmpegCapabilities? caps) => caps?.IsUsable(VideoCodec.AV1, backend, false) == true;
}
