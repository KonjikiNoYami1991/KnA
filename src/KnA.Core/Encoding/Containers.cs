using KnA.Core.Localization;
using KnA.Core.Media;

namespace KnA.Core.Encoding;

/// <summary>What each output container can carry, and how to adapt streams that don't fit.</summary>
public static class Containers
{
    public static IReadOnlyList<string> All { get; } = ["mkv", "mp4", "mov", "webm", "avi", "ts", "mxf"];

    public static string Display(string c) => c switch
    {
        "mkv" => "Matroska (.mkv)",
        "mp4" => "MPEG-4 (.mp4)",
        "mov" => "QuickTime (.mov)",
        "webm" => "WebM (.webm)",
        "avi" => "AVI (.avi)",
        "ts" => "MPEG-TS (.ts)",
        "mxf" => "MXF OP1a (.mxf)",
        _ => c,
    };

    private static readonly Dictionary<string, string[]> VideoCodecs = new()
    {
        ["mkv"] = ["*"],
        ["mp4"] = ["h264", "hevc", "av1", "mpeg4", "mpeg2video", "vp9", "vvc"],
        ["mov"] = ["h264", "hevc", "av1", "mpeg4", "mpeg2video", "prores", "mjpeg", "dnxhd", "cfhd", "v210", "jpeg2000", "vp9"],
        ["webm"] = ["av1", "vp9", "vp8"],
        ["avi"] = ["mpeg4", "msmpeg4v3", "mjpeg", "mpeg2video", "h264", "ffv1", "utvideo", "ffvhuff", "cfhd", "v210", "dnxhd"],
        ["ts"] = ["h264", "hevc", "mpeg2video", "av1", "vvc"],
        ["mxf"] = ["dnxhd", "prores", "mpeg2video", "h264", "jpeg2000", "ffv1"],
    };

    private static readonly Dictionary<string, string[]> AudioCodecs = new()
    {
        ["mkv"] = ["*"],
        ["mp4"] = ["aac", "ac3", "eac3", "mp3", "alac", "opus", "flac", "mp2"],
        ["mov"] = ["aac", "ac3", "eac3", "alac", "mp3", "pcm_s16le", "pcm_s24le", "pcm_s16be", "pcm_s24be", "pcm_f32le", "flac", "opus"],
        ["webm"] = ["opus", "vorbis"],
        ["avi"] = ["mp3", "ac3", "mp2", "pcm_s16le", "pcm_s24le"],
        ["ts"] = ["aac", "ac3", "eac3", "mp3", "mp2", "dts", "opus", "truehd"],
        ["mxf"] = ["pcm_s16le", "pcm_s24le"],
    };

    /// <summary>Default audio encoder used when a track must be (re-)encoded for this container.</summary>
    public static string FallbackAudioEncoder(string container, bool lossless) => container switch
    {
        "webm" => "libopus",
        "avi" => "libmp3lame",
        "mkv" when lossless => "flac",
        "mov" when lossless => "alac",
        "mp4" when lossless => "alac",
        "mxf" => "pcm_s24le",
        _ => "aac",
    };

    public static bool AcceptsVideo(string container, string codec) =>
        VideoCodecs.TryGetValue(container, out var list) && (list.Contains("*") || list.Contains(codec));

    public static bool AcceptsAudio(string container, string codec) =>
        AudioCodecs.TryGetValue(container, out var list) && (list.Contains("*") || list.Contains(codec));

    public static bool AcceptsAttachments(string container) => container == "mkv";

    /// <summary>Codec to use for a subtitle track in this container: "copy", a conversion, or null when impossible.</summary>
    /// <summary>Subtitle formats offered in the track list: value → label.</summary>
    public static IReadOnlyList<(string Value, string Label)> SubtitleOutputs { get; } =
        [("copy", "Copy"), ("srt", "SRT"), ("ass", "ASS"), ("webvtt", "WebVTT"), ("mov_text", "mov_text (MP4)")];

    /// <summary>FFmpeg subtitle encoder for an explicit choice, or null when the container/source can't take it.</summary>
    public static string? ChosenSubtitleCodec(string container, SubtitleStream? source, string? sourceCodec, string wanted)
    {
        var codec = source?.CodecName ?? sourceCodec ?? "";
        bool text = source?.IsText ?? codec is "ass" or "ssa" or "subrip" or "srt" or "webvtt" or "mov_text" or "text";
        wanted = wanted.ToLowerInvariant();
        if (wanted == "copy")
        {
            bool ok = container switch
            {
                "mkv" => codec != "mov_text",
                "mp4" or "mov" => codec == "mov_text",
                "webm" => codec == "webvtt",
                "ts" => codec == "dvb_subtitle",
                _ => false,
            };
            return ok ? "copy" : null;
        }
        if (!text) return null; // bitmap subtitles can't become text without OCR
        return (container, wanted) switch
        {
            ("mkv", "srt" or "ass" or "webvtt") => wanted,
            ("mp4" or "mov", "mov_text") => "mov_text",
            ("webm", "webvtt") => "webvtt",
            _ => null,
        };
    }

    public static string? SubtitleCodec(string container, SubtitleStream? source, string? sourceCodec = null)
    {
        var codec = source?.CodecName ?? sourceCodec ?? "";
        bool text = source?.IsText ?? codec is "ass" or "ssa" or "subrip" or "srt" or "webvtt" or "mov_text" or "text";
        return container switch
        {
            "mkv" => codec is "mov_text" ? "ass" : "copy",
            "mp4" or "mov" => text ? (codec == "mov_text" ? "copy" : "mov_text") : null,
            "webm" => text ? (codec == "webvtt" ? "copy" : "webvtt") : null,
            "ts" => codec is "dvb_subtitle" ? "copy" : null,
            _ => null,
        };
    }

    /// <summary>Output codec name of the encoded video for a profile/codec choice.</summary>
    public static string OutputVideoCodec(VideoCodec c) => c switch
    {
        VideoCodec.H264 => "h264",
        VideoCodec.HEVC => "hevc",
        VideoCodec.AV1 => "av1",
        VideoCodec.Xvid => "mpeg4",
        VideoCodec.VP9 => "vp9",
        VideoCodec.VVC => "vvc",
        VideoCodec.MPEG2 => "mpeg2video",
        VideoCodec.ProRes => "prores",
        VideoCodec.DNxHR => "dnxhd",
        VideoCodec.CineForm => "cfhd",
        VideoCodec.FFV1 => "ffv1",
        VideoCodec.UtVideo => "utvideo",
        VideoCodec.FFVHuff => "ffvhuff",
        VideoCodec.JPEG2000 => "jpeg2000",
        VideoCodec.Uncompressed => "v210",
        _ => "h264",
    };

    /// <summary>The container the job will use (profile default unless overridden and allowed).</summary>
    public static string Resolve(JobSettings s)
    {
        var p = s.ProfileDefinition;
        if (p.IsImages) return s.Images.Format switch
        {
            ImageFormat.Jpeg => "jpg",
            ImageFormat.Tiff => "tif",
            ImageFormat.Webp => "webp",
            ImageFormat.Bmp => "bmp",
            _ => "png",
        };
        var c = s.Container is { } wanted && p.Containers.Contains(wanted) ? wanted : p.Container;
        // No explicit choice: the profile default, or the first container that can hold the chosen codec.
        if (s.Container is null && p.EncodesVideo && !AcceptsVideo(c, OutputVideoCodec(s.EffectiveCodec)))
            c = p.Containers.FirstOrDefault(x => AcceptsVideo(x, OutputVideoCodec(s.EffectiveCodec))) ?? c;
        if (s.EffectiveSubtitles == SubtitleMode.Softsub && p.EncodesVideo && c is "avi" or "ts") c = "mkv";
        return c;
    }

    /// <summary>Containers that can hold the encoded video of these settings (for the UI).</summary>
    public static IReadOnlyList<string> Allowed(JobSettings s)
    {
        var p = s.ProfileDefinition;
        if (!p.EncodesVideo) return p.Containers;
        var codec = OutputVideoCodec(s.EffectiveCodec);
        return p.Containers.Where(c => AcceptsVideo(c, codec)).ToList();
    }

    /// <summary>
    /// Containers that can hold every final stream of this file: the video (encoded or copied), each kept audio
    /// track as it will be written, the subtitle formats chosen and the attachments. Falls back to
    /// <see cref="Allowed(JobSettings)"/> when no container fits them all (validation then says what is wrong).
    /// </summary>
    public static IReadOnlyList<string> AllowedFor(JobSettings s, IReadOnlyList<TrackPlan> tracks, IReadOnlyDictionary<string, MediaInfo> media,
        AudioEncoderSet encoders)
    {
        var p = s.ProfileDefinition;
        var basic = Allowed(s);
        if (!p.UsesTrackPlan || p.IsImages || basic.Count <= 1) return basic;
        var fit = basic.Where(c => Holds(c, s, tracks, media, encoders)).ToList();
        return fit.Count > 0 ? fit : basic;
    }

    private static bool Holds(string c, JobSettings s, IReadOnlyList<TrackPlan> tracks, IReadOnlyDictionary<string, MediaInfo> media, AudioEncoderSet encoders)
    {
        var p = s.ProfileDefinition;
        foreach (var t in tracks.Where(t => t.Include))
        {
            var st = Encoding.TrackPlanner.Lookup(t, media);
            switch (t.Kind)
            {
                case "video" when (t.CopyVideo || p.IsRemux) && st is VideoStream v && p.Kind != ProfileKind.RemuxMp4:
                    if (!AcceptsVideo(c, v.CodecName)) return false;
                    break;
                case "audio" when st is AudioStream a && t.Output is { Length: > 0 } o && !o.Equals(nameof(AudioCodec.Auto), StringComparison.OrdinalIgnoreCase):
                    if (o.Equals(nameof(AudioCodec.Copy), StringComparison.OrdinalIgnoreCase)) { if (!AcceptsAudio(c, a.CodecName)) return false; }
                    else if (Enum.TryParse<AudioCodec>(o, true, out var codec))
                    {
                        var enc = t.Encoder is { Length: > 0 } te && encoders.Has(te) ? te : encoders.Best(codec);
                        if (enc is not null && !AcceptsAudio(c, AudioEncoderSet.OutputCodec(enc))) return false;
                    }
                    break;
                case "subtitle" when !t.Burn:
                    var sst = st as SubtitleStream;
                    if (t.Output is { Length: > 0 } so && !so.Equals(Ocr.SubtitleOcr.OutputValue, StringComparison.OrdinalIgnoreCase))
                    {
                        if (ChosenSubtitleCodec(c, sst, t.Codec, so) is null) return false;
                    }
                    else if (t.Output is { Length: > 0 }) { if (c is not ("mkv" or "mp4" or "mov" or "webm")) return false; }
                    else if (SubtitleCodec(c, sst, t.Codec) is null) return false;
                    break;
                case "attachment":
                    if (!AcceptsAttachments(c)) return false;
                    break;
            }
        }
        return true;
    }

    public static ValidationIssue Error(string english, params object?[] args) => new(true, L.T(english, args));
    public static ValidationIssue Warning(string english, params object?[] args) => new(false, L.T(english, args));
}
