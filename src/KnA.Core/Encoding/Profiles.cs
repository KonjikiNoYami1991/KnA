using KnA.Core.Localization;

namespace KnA.Core.Encoding;

public sealed record ProfileDefinition(
    ProfileKind Kind,
    string EnglishName,
    string EnglishDescription,
    IReadOnlyList<VideoCodec> Codecs,
    bool AllowsHardware,
    bool UsesQuality,
    bool UsesResolution,
    bool UsesSubtitleMode,
    string Container,
    IReadOnlyList<string> Containers)
{
    public string Name => L.T(EnglishName);
    public string Description => L.T(EnglishDescription);
    public bool IsRemux => Kind is ProfileKind.RemuxMkv or ProfileKind.RemuxMp4;
    public bool IsKeyframes => Kind == ProfileKind.Keyframes;
    public bool IsImages => Kind == ProfileKind.Images;
    public bool IsDemux => Kind == ProfileKind.Demux;
    /// <summary>Produces a video file by encoding the picture.</summary>
    public bool EncodesVideo => !IsRemux && !IsKeyframes && !IsImages && !IsDemux;
    /// <summary>Uses the picture pipeline (filters, burned subtitles, HDR conversion).</summary>
    public bool UsesVideoFilters => EncodesVideo || IsImages;
    public bool UsesAudio => EncodesVideo || IsRemux;
    public bool UsesTrackPlan => EncodesVideo || IsRemux || IsImages;
    public bool AllowsCodecChoice => Codecs.Count > 1;

    public override string ToString() => Name;
}

public static class Profiles
{
    public static readonly ProfileDefinition UniversalMkv = new(ProfileKind.UniversalMkv, "Universal",
        "Modern H.264, HEVC or AV1 (10-bit capable). Audio is copied untouched when the container allows it.",
        [VideoCodec.HEVC, VideoCodec.AV1, VideoCodec.H264, VideoCodec.VP9, VideoCodec.VVC, VideoCodec.MPEG2], true, true, true, true, "mkv", ["mkv", "mp4", "mov", "webm", "ts"]);

    public static readonly ProfileDefinition Streaming = new(ProfileKind.Streaming, "Streaming / Web",
        "Fast start and a fixed 2 s GOP for HTML5 players. Stereo AAC (Opus in WebM).",
        [VideoCodec.H264, VideoCodec.HEVC, VideoCodec.AV1, VideoCodec.VP9], true, true, true, true, "mp4", ["mp4", "webm", "mkv"]);

    public static readonly ProfileDefinition Professional = new(ProfileKind.Professional, "Professional / Lossless",
        "Editing and mastering codecs (ProRes, DNxHR, CineForm) and lossless archives (FFV1, Ut Video…). PCM or FLAC audio.",
        [VideoCodec.ProRes, VideoCodec.DNxHR, VideoCodec.FFV1, VideoCodec.CineForm, VideoCodec.UtVideo, VideoCodec.FFVHuff, VideoCodec.JPEG2000, VideoCodec.Uncompressed],
        true, true, true, true, "mov", ["mov", "mkv", "mxf", "avi"]);

    public static readonly ProfileDefinition BlurayAac = new(ProfileKind.BlurayAac, "Blu-ray AAC",
        "H.264 High@4.1 8-bit, Blu-ray player friendly, AAC audio (copied when already AAC).",
        [VideoCodec.H264], true, true, true, true, "mp4", ["mp4", "mkv", "ts"]);

    public static readonly ProfileDefinition BlurayAc3 = new(ProfileKind.BlurayAc3, "Blu-ray AC-3",
        "H.264 High@4.1 8-bit, Blu-ray player friendly, AC-3 audio (copied when already AC-3).",
        [VideoCodec.H264], true, true, true, true, "mp4", ["mp4", "mkv", "ts"]);

    public static readonly ProfileDefinition Workraw = new(ProfileKind.Workraw, "Workraw",
        "Light, fast-decoding H.264 + mono Vorbis for typesetting/timing. No subtitles.",
        [VideoCodec.H264], true, true, true, false, "mkv", ["mkv"]);

    public static readonly ProfileDefinition XvidMp3 = new(ProfileKind.XvidMp3, "XviD + MP3",
        "AVI for old DivX/XviD players. CPU only.",
        [VideoCodec.Xvid], false, true, true, true, "avi", ["avi", "mkv"]);

    public static readonly ProfileDefinition RemuxMp4 = new(ProfileKind.RemuxMp4, "Remux → MP4",
        "Copies compatible streams into MP4; only incompatible ones are re-encoded. Text subs become mov_text.",
        [VideoCodec.H264], true, true, true, false, "mp4", ["mp4"]);

    public static readonly ProfileDefinition RemuxMkv = new(ProfileKind.RemuxMkv, "Remux → MKV",
        "Copies the chosen streams, attachments and chapters into MKV without re-encoding.",
        [], false, false, false, false, "mkv", ["mkv"]);

    public static readonly ProfileDefinition Images = new(ProfileKind.Images, "Images",
        "Exports frames as PNG, JPEG, TIFF, WebP or BMP (filters, HDR conversion and burned subtitles included).",
        [], false, false, true, true, "png", []);

    public static readonly ProfileDefinition Demux = new(ProfileKind.Demux, "Demux",
        "Extracts the chosen streams, attachments and chapters to separate files, without re-encoding.",
        [], false, false, false, false, "", []);

    public static readonly ProfileDefinition Keyframes = new(ProfileKind.Keyframes, "Keyframes (Aegisub)",
        "Writes an Aegisub keyframe file (SCXviD if available, otherwise FFmpeg scene detection).",
        [], false, false, false, false, "txt", []);

    public static IReadOnlyList<ProfileDefinition> All { get; } =
        [UniversalMkv, Streaming, Professional, BlurayAac, BlurayAc3, Workraw, XvidMp3, RemuxMp4, RemuxMkv, Images, Demux, Keyframes];

    public static ProfileDefinition Get(ProfileKind kind) => All.First(p => p.Kind == kind);
}

public static class Resolutions
{
    /// <summary>Target heights. 0 means "keep original".</summary>
    public static IReadOnlyList<int> All { get; } = [0, 2160, 1440, 1080, 900, 720, 576, 480, 396, 360, 288];

    public static string Display(int height) => height == 0 ? L.T("Original") : $"{height}p";
}
