using System.Globalization;
using System.Text.RegularExpressions;
using KnA.Core.Media;
using KnA.Core.Processing;

namespace KnA.Core.Encoding;

/// <summary>Which program encodes the video of a GPU job.</summary>
public enum EncoderProgram
{
    /// <summary>FFmpeg's own encoders (h264_nvenc, hevc_qsv, av1_amf…).</summary>
    FFmpeg,
    /// <summary>rigaya's standalone encoders: NVEncC (NVIDIA), QSVEncC (Intel), VCEEncC (AMD).</summary>
    Rigaya,
}

/// <summary>What the runner needs to encode the video with a rigaya encoder.</summary>
public sealed record RigayaPlan(EncoderBackend Backend, IReadOnlyList<string> EncoderArgs)
{
    /// <summary>
    /// Complete command when the encoder can read the source by itself (GPU decoding with --avhw, no FFmpeg filter):
    /// the fastest path, also for Dolby Vision / HDR10+ copy. Null = FFmpeg decodes and filters, y4m on a pipe.
    /// </summary>
    public IReadOnlyList<string>? NativeArgs { get; init; }
    /// <summary>Why the native path is not used (shown in the log).</summary>
    public string? PipeReason { get; init; }
}

/// <summary>
/// NVEncC / QSVEncC / VCEEncC (github.com/rigaya): the same hardware encoders FFmpeg uses, driven by their
/// vendors' SDKs directly. They read the source themselves with GPU decoding (--avhw: decode → encode never leaves
/// the GPU), copy Dolby Vision RPU and HDR10+ metadata, and split a file into parallel parts (--parallel), which is
/// why StaxRip is often faster with them. KnA uses them for the video only; audio, subtitles, chapters and the
/// final container are made by FFmpeg exactly as in the other paths.
/// Options checked against NVEncC_Options.en.md, QSVEncC_Options.en.md, VCEEncC_Options.en.md.
/// </summary>
public static class RigayaEncoders
{
    /// <summary>Component id (tools/&lt;id&gt;) of the encoder for a backend, or null.</summary>
    public static string? ToolId(EncoderBackend b) => b switch
    {
        EncoderBackend.Nvenc => "nvencc",
        EncoderBackend.Qsv => "qsvencc",
        EncoderBackend.Amf => "vceencc",
        _ => null,
    };

    public static string DisplayName(EncoderBackend b) => b switch
    {
        EncoderBackend.Nvenc => "NVEncC",
        EncoderBackend.Qsv => "QSVEncC",
        EncoderBackend.Amf => "VCEEncC",
        _ => b.Display(),
    };

    /// <summary>Codecs the encoder of a backend makes.</summary>
    public static bool Supports(VideoCodec codec, EncoderBackend b) =>
        ToolId(b) is not null && codec is VideoCodec.H264 or VideoCodec.HEVC or VideoCodec.AV1;

    /// <summary>The job asked for a rigaya encoder and it can make this codec.</summary>
    public static bool Wanted(JobSettings s) =>
        s.Encoder.Program == EncoderProgram.Rigaya && s.ProfileDefinition.EncodesVideo && !s.ProfileDefinition.IsImages
        && s.ProfileDefinition.AllowsHardware && Supports(s.EffectiveCodec, s.Backend);

    /// <summary>Pseudo encoder name used by the advanced settings ("nvencc"…).</summary>
    public static bool IsName(string? encoder) => encoder is "nvencc" or "qsvencc" or "vceencc";

    private static string Codec(VideoCodec c) => c switch { VideoCodec.H264 => "h264", VideoCodec.HEVC => "hevc", _ => "av1" };

    /// <summary>Constant-quality option of each encoder and its range (for the UI); lower = better except VCEEncC QVBR.</summary>
    public static (string Option, double Min, double Max, string Label)? QualityOption(string encoder, VideoCodec codec) => encoder switch
    {
        "nvencc" => ("--qvbr", 0, codec == VideoCodec.AV1 ? 63 : 51, "QVBR (CQ)"),
        "qsvencc" => ("--icq", 1, 51, "ICQ"),
        "vceencc" when codec == VideoCodec.HEVC => ("--cqp", 0, 51, "QP"),
        "vceencc" => ("--qvbr", 1, 51, "QVBR (51 = best)"),
        _ => null,
    };

    public static IReadOnlyList<string> SpeedValues(string encoder) => encoder switch
    {
        "nvencc" => ["P1", "P2", "P3", "P4", "P5", "P6", "P7"],
        "qsvencc" => ["fastest", "faster", "fast", "balanced", "high", "higher", "best"],
        "vceencc" => ["fast", "balanced", "slow", "slower"],
        _ => [],
    };

    public static IReadOnlyList<string> TuneValues(string encoder, VideoCodec codec) => encoder switch
    {
        "nvencc" => codec == VideoCodec.H264 ? ["hq"] : ["hq", "uhq"],
        _ => [],
    };

    /// <summary>Encoding options (codec, rate control, speed, depth, GOP, colour and static HDR).</summary>
    public static List<string> EncoderArgs(EncoderBackend backend, VideoCodec codec, QualityLevel quality, bool tenBit,
        EncoderOverrides o, double frameRate, HdrPlan hdr, VideoStream video, bool native)
    {
        var enc = ToolId(backend) ?? throw new NotSupportedException(backend.Display());
        int q = (int)quality;
        var a = new List<string> { "-c", Codec(codec) };

        // ---------------------------------------------------------------- rate control
        if (o.RateControl == RateControl.Bitrate && o.BitrateKbps is > 0 and var kb)
        {
            a.AddRange(["--vbr", kb.ToString(CultureInfo.InvariantCulture)]);
            if (o.MaxBitrateKbps is > 0 and var mx) a.AddRange(["--max-bitrate", Math.Max(mx, kb).ToString(CultureInfo.InvariantCulture)]);
        }
        else
        {
            var qo = QualityOption(enc, codec)!.Value;
            double value = o.RateControl == RateControl.Quality && o.QualityValue is { } qv
                ? Math.Clamp(qv, qo.Min, qo.Max)
                : enc switch
                {
                    "nvencc" => Pick(q, NvencQ[codec]),
                    "qsvencc" => Pick(q, QsvIcq[codec]),
                    _ when codec == VideoCodec.HEVC => Pick(q, 18, 20, 22, 24, 26, 29, 32, 38),
                    _ => Pick(q, 36, 33, 31, 29, 27, 24, 21, 14),
                };
            string v = VideoEncoders.Num(enc == "nvencc" ? value : Math.Round(value));
            if (qo.Option == "--cqp")
            {
                // I < P < B, as the documentation recommends.
                int i = (int)Math.Round(value);
                a.AddRange(["--cqp", $"{i}:{Math.Min(51, i + 2)}:{Math.Min(51, i + 4)}"]);
            }
            else a.AddRange([qo.Option, v]);
            if (o.MaxBitrateKbps is > 0 and var mx && enc == "nvencc") a.AddRange(["--max-bitrate", mx.ToString(CultureInfo.InvariantCulture)]);
        }

        // ---------------------------------------------------------------- speed / tune
        var speeds = SpeedValues(enc);
        string speed = o.Speed is { Length: > 0 } sp && speeds.Contains(sp) ? sp
            : speeds[Math.Clamp(speeds.Count - 1 - (int)Math.Round(q * (speeds.Count - 1) / 7.0), 0, speeds.Count - 1)];
        a.AddRange(["-u", speed]);
        if (enc == "nvencc")
        {
            a.AddRange(["--tune", o.Tune is { Length: > 0 } t && TuneValues(enc, codec).Contains(t) ? t : "hq"]);
            a.AddRange(["--aq", "--lookahead", (o.Lookahead is >= 0 and var la ? la : 32).ToString(CultureInfo.InvariantCulture)]);
        }
        else if (enc == "qsvencc" && o.Lookahead is > 0 and var qla) a.AddRange(["--la-depth", qla.ToString(CultureInfo.InvariantCulture)]);

        // ---------------------------------------------------------------- structure
        a.AddRange(["--output-depth", tenBit ? "10" : "8"]);
        if (tenBit && codec == VideoCodec.HEVC) a.AddRange(["--profile", "main10"]);
        if (o.KeyframeSeconds is > 0 and var ks)
            a.AddRange(["--gop-len", Math.Max(1, (int)Math.Round(ks * (frameRate > 0 ? frameRate : 25))).ToString(CultureInfo.InvariantCulture)]);
        if (o.BFrames is >= 0 and var bf) a.AddRange(["-b", bf.ToString(CultureInfo.InvariantCulture)]);

        // ---------------------------------------------------------------- colour / HDR
        if (hdr.Action == HdrAction.Preserve)
        {
            var h = video.Hdr;
            string trc = h.IsHlg ? "arib-std-b67" : "smpte2084";
            a.AddRange(["--colorprim", "bt2020", "--transfer", trc, "--colormatrix", "bt2020nc", "--colorrange", "limited"]);
            if (codec != VideoCodec.H264 && !h.IsHlg)
            {
                if (native) a.AddRange(["--master-display", "copy", "--max-cll", "copy"]);
                else
                {
                    if (h.Mastering is { } md) a.AddRange(["--master-display", md.ToX265()]);
                    if (h.MaxCll is > 0) a.AddRange(["--max-cll", $"{h.MaxCll.Value.ToString(CultureInfo.InvariantCulture)},{(h.MaxFall ?? 0).ToString(CultureInfo.InvariantCulture)}"]);
                }
            }
            if (h.IsHlg && codec == VideoCodec.HEVC) a.AddRange(["--atc-sei", "arib-std-b67"]);
        }
        else if (hdr.Action == HdrAction.ToneMap || video.Height >= 600 || video.Height == 0)
            a.AddRange(["--colorprim", "bt709", "--transfer", "bt709", "--colormatrix", "bt709", "--colorrange", "limited"]);
        else
            a.AddRange(["--colorprim", "smpte170m", "--transfer", "smpte170m", "--colormatrix", "smpte170m", "--colorrange", "limited"]);

        if (!string.IsNullOrWhiteSpace(o.Extra))
            foreach (var (opt, value) in VideoEncoders.ParseExtra(o.Extra))
            {
                int i = a.IndexOf(opt);
                if (value is null) { if (i < 0) a.Add(opt); }
                else if (i >= 0 && i + 1 < a.Count && !a[i + 1].StartsWith('-')) a[i + 1] = value;
                else a.AddRange([opt, value]);
            }
        return a;
    }

    /// <summary>
    /// Command for the native path: the encoder opens the source, decodes on the GPU and writes a video-only MKV.
    /// Dolby Vision RPU and HDR10+ are copied when kept (HEVC/AV1); --parallel splits the file between the GPU's
    /// encoder engines when nothing forbids it.
    /// </summary>
    public static List<string> NativeArgs(string source, IReadOnlyList<string> encoderArgs, VideoStream video, HdrPlan hdr,
        VideoCodec codec, VideoFilterSettings filters, string output, bool parallel)
    {
        var a = new List<string> { "--avhw", "-i", source };
        if (filters.Crop is { IsEmpty: false } c)
            a.AddRange(["--crop", $"{c.Left},{c.Top},{c.Right},{c.Bottom}"]);
        a.AddRange(encoderArgs);
        if (hdr.Action == HdrAction.Preserve && codec != VideoCodec.H264)
        {
            if (video.Hdr.IsDolbyVision && !video.Hdr.DolbyVisionWithoutFallback && filters.Crop is not { IsEmpty: false })
                a.AddRange(["--dolby-vision-rpu", "copy", "--dolby-vision-profile", "8.1"]);
            if (video.Hdr.HasHdr10Plus) a.AddRange(["--dhdr10-info", "copy"]);
        }
        if (parallel) a.AddRange(["--parallel", "auto"]);
        a.AddRange(["-o", output]);
        return a;
    }

    /// <summary>Command for the pipe path: y4m frames from FFmpeg (decoded, filtered, trimmed) on stdin.</summary>
    public static List<string> PipeArgs(IReadOnlyList<string> encoderArgs, string output) =>
        ["--y4m", "-i", "-", .. encoderArgs, "-o", output];

    /// <summary>Source formats the GPU decoders of --avhw read (NVEncC table; QSV/VCE read the same set or less).</summary>
    public static bool HwDecodable(string? codecName) =>
        codecName is "mpeg1video" or "mpeg2video" or "h264" or "hevc" or "vp9" or "av1" or "vc1";

    private static readonly Regex Progress = new(@"^\s*(?:\[(?<pct>[\d.]+)%\]\s*)?(?<frames>\d+)\s+frames:\s*(?<fps>[\d.]+)\s*fps(?:,\s*(?<kbps>[\d.]+)\s*kb/s)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Status line of the encoders ("[42.1%] 12345 frames: 245.67 fps, 5432 kb/s, remain 0:01:23, …"), or null.
    /// </summary>
    public static EncodeProgress? ParseProgress(string? line, TimeSpan duration, double frameRate)
    {
        if (line is null || Progress.Match(line) is not { Success: true } m) return null;
        long frames = long.Parse(m.Groups["frames"].Value, CultureInfo.InvariantCulture);
        double fps = double.Parse(m.Groups["fps"].Value, CultureInfo.InvariantCulture);
        TimeSpan pos = m.Groups["pct"].Success && duration > TimeSpan.Zero
            ? TimeSpan.FromSeconds(duration.TotalSeconds * double.Parse(m.Groups["pct"].Value, CultureInfo.InvariantCulture) / 100)
            : TimeSpan.FromSeconds(frameRate > 0 ? frames / frameRate : 0);
        double kbps = m.Groups["kbps"].Success ? double.Parse(m.Groups["kbps"].Value, CultureInfo.InvariantCulture) : 0;
        return new EncodeProgress(pos, duration, fps, frameRate > 0 ? fps / frameRate : 0,
            (long)(kbps * 125 * pos.TotalSeconds), frames, false);
    }

    private static readonly Dictionary<VideoCodec, double[]> NvencQ = new()
    {
        [VideoCodec.H264] = [16, 19, 21, 23, 25, 28, 31, 38],
        [VideoCodec.HEVC] = [18, 21, 23, 25, 27, 30, 33, 40],
        [VideoCodec.AV1] = [20, 24, 27, 30, 33, 37, 41, 48],
    };

    private static readonly Dictionary<VideoCodec, double[]> QsvIcq = new()
    {
        [VideoCodec.H264] = [18, 20, 22, 24, 26, 29, 32, 38],
        [VideoCodec.HEVC] = [19, 21, 23, 25, 27, 30, 33, 40],
        [VideoCodec.AV1] = [19, 21, 23, 25, 27, 30, 33, 40],
    };

    private static T Pick<T>(int index, params T[] values) => values[Math.Clamp(index, 0, values.Length - 1)];
}
