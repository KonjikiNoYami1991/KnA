using System.Text.Json.Serialization;
using KnA.Core.Localization;
using KnA.Core.Media;

namespace KnA.Core.Encoding;

public enum DeinterlaceMode { Auto, Off, Force }
public enum FilterStrength { Off, Light, Medium, Strong }
public enum HdrMode
{
    /// <summary>Keep HDR when the output can carry it (10-bit HEVC/AV1), otherwise tone-map to SDR.</summary>
    Auto,
    /// <summary>Keep HDR10/HLG (+ HDR10+/Dolby Vision when the tools and encoder allow it).</summary>
    Preserve,
    /// <summary>Always convert to SDR BT.709.</summary>
    ToneMapSdr,
}
public enum ImageFormat { Png, Jpeg, Tiff, Webp, Bmp }
public enum ImageMode { Interval, Count, SceneChanges, EveryFrame }

public sealed record CropSettings(int Left = 0, int Top = 0, int Right = 0, int Bottom = 0)
{
    [JsonIgnore] public bool IsEmpty => Left <= 0 && Top <= 0 && Right <= 0 && Bottom <= 0;
}

public sealed record VideoFilterSettings
{
    public DeinterlaceMode Deinterlace { get; init; } = DeinterlaceMode.Auto;
    public CropSettings? Crop { get; init; }
    public FilterStrength Denoise { get; init; }
    public FilterStrength Deband { get; init; }
    public FilterStrength Sharpen { get; init; }
    /// <summary>
    /// Final orientation, clockwise 0/90/180/270; null = follow the file's rotation metadata.
    /// Applied through the display matrix: lossless on remux, baked into the picture when encoding.
    /// </summary>
    public int? Orientation { get; init; }
    /// <summary>Mirror the picture (display flip).</summary>
    public bool FlipHorizontal { get; init; }
    public bool FlipVertical { get; init; }

    [JsonIgnore]
    public bool OverridesOrientation => Orientation is not null || FlipHorizontal || FlipVertical;

    /// <summary>Clockwise rotation that will be applied to a stream with the given metadata rotation.</summary>
    public int EffectiveRotation(int metadataRotation) => (((Orientation ?? metadataRotation) % 360) + 360) % 360;
    /// <summary>Output frame rate, null = keep.</summary>
    public double? Fps { get; init; }
    /// <summary>Raw FFmpeg filters appended at the end of the chain (advanced).</summary>
    public string? Custom { get; init; }

    [JsonIgnore]
    public bool ChangesFrameCount => Fps is > 0;
}

public sealed record AudioFilterSettings
{
    public double GainDb { get; init; }
    /// <summary>EBU R128 loudness normalization (loudnorm, single pass).</summary>
    public bool Normalize { get; init; }
    public bool DownmixStereo { get; init; }
    public int? SampleRate { get; init; }
    public string? Custom { get; init; }

    [JsonIgnore]
    public bool IsActive => Math.Abs(GainDb) > 0.01 || Normalize || DownmixStereo || SampleRate is > 0 || !string.IsNullOrWhiteSpace(Custom);
}

public sealed record ImageSettings
{
    public ImageFormat Format { get; init; } = ImageFormat.Png;
    public ImageMode Mode { get; init; } = ImageMode.Interval;
    public double IntervalSeconds { get; init; } = 10;
    public int Count { get; init; } = 24;
    /// <summary>JPEG/WebP quality 1..100.</summary>
    public int Quality { get; init; } = 92;
    /// <summary>16 bits per channel (PNG/TIFF only).</summary>
    public bool SixteenBit { get; init; }
}

public sealed record DemuxSettings
{
    /// <summary>Elementary streams (.h264, .aac, .ass…) when true; Matroska wrappers (.mkv/.mka/.mks) otherwise.</summary>
    public bool Raw { get; init; } = true;
    public bool Attachments { get; init; } = true;
    public bool Chapters { get; init; } = true;
    /// <summary>Absolute stream indexes to extract; null = all.</summary>
    public List<int>? Streams { get; init; }
}

/// <summary>AI / GPU upscaling used when the target resolution is above the source.</summary>
public sealed record UpscaleSettings
{
    public UpscaleMethod Method { get; init; } = UpscaleMethod.Standard;
    /// <summary>Anime4K mode ("A", "B", "C", "A+A", "B+B", "C+A") or the neural model id (see <see cref="Ai.UpscaleModels"/>).</summary>
    public string? Model { get; init; }
    /// <summary>Also run the model when the size doesn't change (restoration / clean-up only).</summary>
    public bool AlwaysApply { get; init; }
}

/// <summary>av1an-style segmented encoding: split at keyframes, encode segments in parallel, resume after a stop.</summary>
public sealed record ChunkSettings
{
    public bool Enabled { get; init; }
    /// <summary>Segments encoded at the same time.</summary>
    public int Workers { get; init; } = Math.Clamp(Environment.ProcessorCount / 8, 1, 4);
    /// <summary>Approximate segment length (cut at the nearest following keyframe).</summary>
    public double TargetSeconds { get; init; } = 60;
}

public enum RateControl
{
    /// <summary>From the quality level (KnA's tuned values per encoder).</summary>
    Auto,
    /// <summary>Constant quality: CRF (x264/x265/SVT-AV1/VP9), CQ (NVENC), global_quality (QSV), QP (AMF/VA-API).</summary>
    Quality,
    /// <summary>Average bitrate (VBR) with an optional maximum.</summary>
    Bitrate,
}

/// <summary>Encoder parameters set by hand; empty fields keep KnA's choice for the quality level.</summary>
public sealed record EncoderOverrides
{
    /// <summary>FFmpeg's encoders, or rigaya's NVEncC / QSVEncC / VCEEncC for the GPU backends.</summary>
    public EncoderProgram Program { get; init; }
    public RateControl RateControl { get; init; }
    /// <summary>CRF / CQ / QP value (lower = better).</summary>
    public double? QualityValue { get; init; }
    public int? BitrateKbps { get; init; }
    public int? MaxBitrateKbps { get; init; }
    /// <summary>Encoder speed preset ("slow", "p5", "6"…), null = from the quality level.</summary>
    public string? Speed { get; init; }
    public string? Tune { get; init; }
    /// <summary>Maximum distance between keyframes, seconds.</summary>
    public double? KeyframeSeconds { get; init; }
    public int? BFrames { get; init; }
    /// <summary>Rate-control lookahead, frames.</summary>
    public int? Lookahead { get; init; }
    /// <summary>Extra encoder options, FFmpeg syntax ("-x265-params …", "-spatial-aq 1"…).</summary>
    public string? Extra { get; init; }

    [JsonIgnore]
    public bool IsEmpty => Program == EncoderProgram.FFmpeg && RateControl == RateControl.Auto && Speed is null && Tune is null && KeyframeSeconds is null
                           && BFrames is null && Lookahead is null && string.IsNullOrWhiteSpace(Extra);
}

/// <summary>
/// Which tracks of the source the automatic plan keeps, so a preset can drop unwanted streams of every file
/// (languages, commentaries, attachments). Empty = keep as the profile decides.
/// </summary>
public sealed record TrackRules
{
    /// <summary>Audio languages to keep, in order of preference ("ita,jpn"); "und" matches tracks without a language.</summary>
    public string? AudioLanguages { get; init; }
    public string? SubtitleLanguages { get; init; }
    /// <summary>Leave out tracks whose title says commentary ("commento", "commentary"…).</summary>
    public bool DropCommentary { get; init; }
    /// <summary>Leave out the attachments of the file (fonts are still used to burn subtitles).</summary>
    public bool DropAttachments { get; init; }

    [JsonIgnore] public bool IsEmpty => string.IsNullOrWhiteSpace(AudioLanguages) && string.IsNullOrWhiteSpace(SubtitleLanguages) && !DropCommentary && !DropAttachments;

    public static IReadOnlyList<string> Parse(string? list) =>
        (list ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.ToLowerInvariant()).ToList();

    /// <summary>Whether a track passes the language list (no list = every track).</summary>
    public static bool LanguageMatches(string? list, string? language)
    {
        var langs = Parse(list);
        if (langs.Count == 0) return true;
        var lang = string.IsNullOrWhiteSpace(language) ? "und" : language.ToLowerInvariant();
        return langs.Any(l => l == lang || Media.LanguageNames.Same(l, lang));
    }

    public static bool IsCommentary(string? title) =>
        title is not null && new[] { "comment", "kommentar", "commento", "commentaire", "comentario", "director" }
            .Any(w => title.Contains(w, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Parts of the settings a preset can be applied to, leaving the others as they are.</summary>
[Flags]
public enum PresetParts
{
    None = 0,
    /// <summary>Profile, codec, encoder, quality, resolution, 10-bit, container, HDR, upscaling, advanced encoder, segments.</summary>
    Video = 1,
    /// <summary>Audio codec, bitrate and audio filters.</summary>
    Audio = 2,
    /// <summary>Subtitle mode and external subtitles.</summary>
    Subtitles = 4,
    /// <summary>Picture filters and frameserver.</summary>
    Filters = 8,
    /// <summary>Track selection rules.</summary>
    Tracks = 16,
    All = Video | Audio | Subtitles | Filters | Tracks,
}

/// <summary>Everything that can be stored in a preset (not tied to a specific file).</summary>
public sealed record JobSettings
{
    public ProfileKind Profile { get; init; } = ProfileKind.UniversalMkv;
    public VideoCodec Codec { get; init; } = VideoCodec.HEVC;
    public EncoderBackend Backend { get; init; } = EncoderBackend.Software;
    public QualityLevel Quality { get; init; } = QualityLevel.Normal;
    /// <summary>Target height, 0 = original.</summary>
    public int Resolution { get; init; } = 1080;
    public SubtitleMode Subtitles { get; init; } = SubtitleMode.Hardsub;
    public bool TenBit { get; init; } = true;
    /// <summary>Audio codec of the output tracks (Auto = profile default, copy when possible).</summary>
    public AudioCodec AudioCodec { get; init; } = AudioCodec.Auto;
    /// <summary>Audio bitrate in kb/s per stereo pair (scaled for multichannel); null = from the quality level.</summary>
    public int? AudioBitrate { get; init; }
    /// <summary>ProRes / DNxHR profile, chroma and bit depth for the professional codecs.</summary>
    public ProSettings Pro { get; init; } = new();
    /// <summary>How the picture is enlarged when the target resolution is above the source (AI upscaling).</summary>
    public UpscaleSettings Upscale { get; init; } = new();
    /// <summary>Prefer a "same name.ass/.srt" file next to the video for hardsub.</summary>
    public bool UseSidecarSubtitles { get; init; } = true;
    /// <summary>Output container ("mkv", "mp4"…), null = profile default.</summary>
    public string? Container { get; init; }
    public HdrMode Hdr { get; init; } = HdrMode.Auto;
    /// <summary>Curve used when HDR is converted to SDR.</summary>
    public ToneMapCurve ToneMap { get; init; } = ToneMapCurve.Spline;
    /// <summary>
    /// Scene-by-scene peak detection (libplacebo): dim scenes are stretched towards SDR white. Off = the
    /// mastering metadata (MaxCLL / mastering peak) decides, so the brightness is stable across the film.
    /// </summary>
    public bool ToneMapDynamic { get; init; }
    public VideoFilterSettings VideoFilters { get; init; } = new();
    public AudioFilterSettings AudioFilters { get; init; } = new();
    public ImageSettings Images { get; init; } = new();
    public DemuxSettings Demux { get; init; } = new();
    public ChunkSettings Chunks { get; init; } = new();
    public FrameserverSettings Frameserver { get; init; } = new();
    /// <summary>Encoder parameters set by hand (advanced).</summary>
    public EncoderOverrides Encoder { get; init; } = new();
    /// <summary>Which source tracks the automatic plan keeps.</summary>
    public TrackRules TrackRules { get; init; } = new();
    /// <summary>Name of the preset these settings came from (informative).</summary>
    public string? PresetName { get; init; }

    /// <summary>These settings with the chosen <paramref name="parts"/> taken from <paramref name="preset"/>.</summary>
    public JobSettings Merge(JobSettings preset, PresetParts parts)
    {
        if (parts == PresetParts.All) return preset;
        var r = this;
        if (parts.HasFlag(PresetParts.Video))
            r = r with
            {
                Profile = preset.Profile, Codec = preset.Codec, Backend = preset.Backend, Quality = preset.Quality, Resolution = preset.Resolution,
                TenBit = preset.TenBit, Container = preset.Container, Hdr = preset.Hdr, ToneMap = preset.ToneMap, ToneMapDynamic = preset.ToneMapDynamic,
                Pro = preset.Pro, Upscale = preset.Upscale, Encoder = preset.Encoder, Chunks = preset.Chunks, Images = preset.Images, Demux = preset.Demux,
            };
        if (parts.HasFlag(PresetParts.Audio))
            r = r with { AudioCodec = preset.AudioCodec, AudioBitrate = preset.AudioBitrate, AudioFilters = preset.AudioFilters };
        if (parts.HasFlag(PresetParts.Subtitles))
            r = r with { Subtitles = preset.Subtitles, UseSidecarSubtitles = preset.UseSidecarSubtitles };
        if (parts.HasFlag(PresetParts.Filters))
            r = r with { VideoFilters = preset.VideoFilters, Frameserver = preset.Frameserver };
        if (parts.HasFlag(PresetParts.Tracks))
            r = r with { TrackRules = preset.TrackRules };
        return r with { PresetName = preset.PresetName };
    }

    [JsonIgnore]
    public ProfileDefinition ProfileDefinition => Profiles.Get(Profile);

    /// <summary>Codec actually used (profiles with a fixed codec override the selection).</summary>
    [JsonIgnore]
    public VideoCodec EffectiveCodec =>
        ProfileDefinition.Codecs.Contains(Codec) ? Codec : ProfileDefinition.Codecs.FirstOrDefault(VideoCodec.H264);

    [JsonIgnore]
    public EncoderBackend EffectiveBackend =>
        ProfileDefinition.AllowsHardware && VideoEncoders.EncoderName(EffectiveCodec, Backend) is not null
            ? Backend : EncoderBackend.Software;

    /// <summary>A GPU backend was chosen that has no encoder for the codec (validation reports it as an error).</summary>
    [JsonIgnore]
    public bool BackendUnavailable =>
        ProfileDefinition.AllowsHardware && Backend != EncoderBackend.Software && VideoEncoders.EncoderName(EffectiveCodec, Backend) is null;

    [JsonIgnore]
    public bool EffectiveTenBit => VideoEncoders.Always10Bit(EffectiveCodec)
                                   || (TenBit && VideoEncoders.Supports10Bit(EffectiveCodec, EffectiveBackend)
                                       && Profile is ProfileKind.UniversalMkv or ProfileKind.Streaming or ProfileKind.Professional);

    [JsonIgnore]
    public SubtitleMode EffectiveSubtitles => ProfileDefinition.UsesSubtitleMode ? Subtitles : SubtitleMode.None;

    [JsonIgnore]
    public string EffectiveContainer => Containers.Resolve(this);

    [JsonIgnore]
    public string Label
    {
        get
        {
            var p = ProfileDefinition;
            if (p.Kind == ProfileKind.Images) return $"{p.Name} {Images.Format.ToString().ToUpperInvariant()}";
            if (!p.EncodesVideo) return p.Name;
            var parts = new List<string> { p.Name };
            if (p.AllowsCodecChoice) parts.Add(EffectiveCodec.Short());
            if (EffectiveBackend != EncoderBackend.Software) parts.Add(EffectiveBackend.Short());
            if (EffectiveTenBit && !EffectiveCodec.IsProfessional()) parts.Add("10bit");
            if (EffectiveCodec == VideoCodec.ProRes) parts.Add(VideoEncoders.ProResFor(new VideoEncodeRequest(EffectiveCodec, EffectiveBackend, Quality, true, Profile, 25, false) { Pro = Pro }).Display());
            if (Upscale.Method != UpscaleMethod.Standard) parts.Add(Upscale.Method.ToString());
            parts.Add(Resolutions.Display(Resolution));
            parts.Add(Quality == QualityLevel.Draft ? L.T("Draft") : Quality.Display());
            return string.Join(" ", parts);
        }
    }
}

/// <summary>One output track (or a standalone file such as a font) and how to treat it.</summary>
public sealed record TrackPlan
{
    /// <summary>File the stream comes from (the source or an external file).</summary>
    public required string File { get; init; }
    /// <summary>Absolute stream index in <see cref="File"/>; -1 for a standalone attachment (font file).</summary>
    public int Stream { get; init; }
    /// <summary>"video", "audio", "subtitle" or "attachment".</summary>
    public required string Kind { get; init; }
    public bool Include { get; init; } = true;
    /// <summary>Subtitle to burn into the picture (at most one).</summary>
    public bool Burn { get; init; }
    /// <summary>Positive = later, negative = earlier (milliseconds).</summary>
    public double DelayMs { get; init; }
    public string? Language { get; init; }
    public string? Title { get; init; }
    public bool Default { get; init; }
    public bool Forced { get; init; }
    /// <summary>Informative (codec name) for the UIs.</summary>
    public string? Codec { get; init; }
    /// <summary>
    /// What to do with this track, chosen in the track list; null = follow the profile.
    /// Video: "copy". Audio: an <see cref="AudioCodec"/> name ("Copy", "Aac", "Opus"…).
    /// Subtitles: "copy", "srt", "ass", "webvtt" or "mov_text".
    /// </summary>
    public string? Output { get; init; }
    /// <summary>Audio bitrate of this track in kb/s per stereo pair (null = the job's setting).</summary>
    public int? Bitrate { get; init; }

    [JsonIgnore] public bool CopyVideo => Kind == "video" && string.Equals(Output, "copy", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore] public double DelaySeconds => DelayMs / 1000.0;
}

/// <summary>One kept part of the final timeline (seconds); End null = until the end.</summary>
public sealed record TrimSegment(double Start, double? End);

/// <summary>A chapter mark on the source timeline (seconds).</summary>
public sealed record ChapterMark(double Start, string? Title);

/// <summary>Per-file edits: trim, external files, explicit track plan.</summary>
public sealed record JobEdit
{
    /// <summary>Applied to the final timeline, after filters, imports and delays (single segment).</summary>
    public double? TrimStart { get; init; }
    public double? TrimEnd { get; init; }
    /// <summary>
    /// Two or more kept parts, joined into one output (a single cut uses <see cref="TrimStart"/>/<see cref="TrimEnd"/>).
    /// Sorted, non overlapping, on the final timeline.
    /// </summary>
    public List<TrimSegment>? Segments { get; init; }
    /// <summary>External audio/subtitle/font/container files available to the track plan.</summary>
    public List<string> ExternalFiles { get; init; } = [];
    /// <summary>Explicit output tracks in order; null = automatic (based on the subtitle mode).</summary>
    public List<TrackPlan>? Tracks { get; init; }
    public bool KeepChapters { get; init; } = true;
    /// <summary>Edited chapters (source timeline); null = the chapters of the file as they are.</summary>
    public List<ChapterMark>? Chapters { get; init; }
    /// <summary>Destination folder of this job (watch folders, editor), null = the global output folder or next to the source.</summary>
    public string? OutputFolder { get; init; }
    /// <summary>Added to the output name ("part 2") when a multi-segment job was split into several jobs.</summary>
    public string? PartLabel { get; init; }

    [JsonIgnore] public bool IsMultiSegment => Segments is { Count: > 1 };
    [JsonIgnore] public bool IsTrimmed => TrimStart is > 0 || TrimEnd is > 0 || IsMultiSegment;

    /// <summary>The kept parts (start, end) clamped to the duration; empty = not trimmed.</summary>
    public List<(double Start, double End)> KeptParts(double duration)
    {
        var list = new List<(double, double)>();
        double max = duration > 0 ? duration : double.MaxValue;
        if (IsMultiSegment)
        {
            foreach (var s in Segments!.OrderBy(x => x.Start))
            {
                double a = Math.Max(0, s.Start), b = Math.Min(s.End ?? max, max);
                if (b - a >= 0.05) list.Add((a, b));
            }
        }
        else if (TrimStart is > 0 || TrimEnd is > 0)
        {
            double a = Math.Max(0, TrimStart ?? 0), b = Math.Min(TrimEnd is > 0 and var e ? e : max, max);
            if (b - a >= 0.05) list.Add((a, b));
        }
        return list;
    }

    /// <summary>Stores the parts the canonical way: one → TrimStart/TrimEnd, several → Segments, none → no trim.</summary>
    public JobEdit WithParts(IReadOnlyList<TrimSegment> parts, double duration)
    {
        var sorted = parts.Where(p => (p.End ?? double.MaxValue) - p.Start >= 0.05).OrderBy(p => p.Start).ToList();
        if (sorted.Count > 1) return this with { Segments = sorted, TrimStart = null, TrimEnd = null };
        if (sorted.Count == 0) return this with { Segments = null, TrimStart = null, TrimEnd = null };
        var only = sorted[0];
        return this with
        {
            Segments = null,
            TrimStart = only.Start > 0.0005 ? only.Start : null,
            TrimEnd = only.End is { } e && (duration <= 0 || e < duration - 0.0005) ? e : null,
        };
    }

    /// <summary>The edit with no trim at all (previews, thumbnails).</summary>
    [JsonIgnore] public JobEdit Untrimmed => this with { TrimStart = null, TrimEnd = null, Segments = null };
}

public sealed record ValidationIssue(bool IsError, string Message);
