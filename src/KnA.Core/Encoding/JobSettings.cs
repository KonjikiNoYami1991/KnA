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
    public VideoFilterSettings VideoFilters { get; init; } = new();
    public AudioFilterSettings AudioFilters { get; init; } = new();
    public ImageSettings Images { get; init; } = new();
    public DemuxSettings Demux { get; init; } = new();
    public ChunkSettings Chunks { get; init; } = new();
    public FrameserverSettings Frameserver { get; init; } = new();
    /// <summary>Name of the preset these settings came from (informative).</summary>
    public string? PresetName { get; init; }

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

    [JsonIgnore] public double DelaySeconds => DelayMs / 1000.0;
}

/// <summary>Per-file edits: trim, external files, explicit track plan.</summary>
public sealed record JobEdit
{
    /// <summary>Applied to the final timeline, after filters, imports and delays.</summary>
    public double? TrimStart { get; init; }
    public double? TrimEnd { get; init; }
    /// <summary>External audio/subtitle/font/container files available to the track plan.</summary>
    public List<string> ExternalFiles { get; init; } = [];
    /// <summary>Explicit output tracks in order; null = automatic (based on the subtitle mode).</summary>
    public List<TrackPlan>? Tracks { get; init; }
    public bool KeepChapters { get; init; } = true;
    /// <summary>Destination folder for demux / image export, null = next to the source.</summary>
    public string? OutputFolder { get; init; }

    [JsonIgnore] public bool IsTrimmed => TrimStart is > 0 || TrimEnd is > 0;
}

public sealed record ValidationIssue(bool IsError, string Message);
