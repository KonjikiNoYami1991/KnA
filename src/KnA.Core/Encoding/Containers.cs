using KnA.Core.Localization;
using KnA.Core.Media;

namespace KnA.Core.Encoding;

/// <summary>What each output container can carry, and how to adapt streams that don't fit.</summary>
public static class Containers
{
    public static IReadOnlyList<string> All { get; } = ["mkv", "mp4", "mov", "webm", "avi", "ts"];

    public static string Display(string c) => c switch
    {
        "mkv" => "Matroska (.mkv)",
        "mp4" => "MPEG-4 (.mp4)",
        "mov" => "QuickTime (.mov)",
        "webm" => "WebM (.webm)",
        "avi" => "AVI (.avi)",
        "ts" => "MPEG-TS (.ts)",
        _ => c,
    };

    private static readonly Dictionary<string, string[]> VideoCodecs = new()
    {
        ["mkv"] = ["*"],
        ["mp4"] = ["h264", "hevc", "av1", "mpeg4", "mpeg2video", "vp9"],
        ["mov"] = ["h264", "hevc", "av1", "mpeg4", "mpeg2video", "prores", "mjpeg"],
        ["webm"] = ["av1", "vp9", "vp8"],
        ["avi"] = ["mpeg4", "msmpeg4v3", "mjpeg", "mpeg2video", "h264"],
        ["ts"] = ["h264", "hevc", "mpeg2video", "av1"],
    };

    private static readonly Dictionary<string, string[]> AudioCodecs = new()
    {
        ["mkv"] = ["*"],
        ["mp4"] = ["aac", "ac3", "eac3", "mp3", "alac", "opus", "flac", "mp2"],
        ["mov"] = ["aac", "ac3", "eac3", "alac", "mp3", "pcm_s16le", "pcm_s24le", "pcm_s16be", "pcm_s24be", "flac"],
        ["webm"] = ["opus", "vorbis"],
        ["avi"] = ["mp3", "ac3", "mp2", "pcm_s16le"],
        ["ts"] = ["aac", "ac3", "eac3", "mp3", "mp2", "dts", "opus"],
    };

    /// <summary>Default audio encoder used when a track must be (re-)encoded for this container.</summary>
    public static string FallbackAudioEncoder(string container, bool lossless) => container switch
    {
        "webm" => "libopus",
        "avi" => "libmp3lame",
        "mkv" when lossless => "flac",
        "mov" when lossless => "alac",
        "mp4" when lossless => "alac",
        _ => "aac",
    };

    public static bool AcceptsVideo(string container, string codec) =>
        VideoCodecs.TryGetValue(container, out var list) && (list.Contains("*") || list.Contains(codec));

    public static bool AcceptsAudio(string container, string codec) =>
        AudioCodecs.TryGetValue(container, out var list) && (list.Contains("*") || list.Contains(codec));

    public static bool AcceptsAttachments(string container) => container == "mkv";

    /// <summary>Codec to use for a subtitle track in this container: "copy", a conversion, or null when impossible.</summary>
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

    public static ValidationIssue Error(string english, params object?[] args) => new(true, L.T(english, args));
    public static ValidationIssue Warning(string english, params object?[] args) => new(false, L.T(english, args));
}
