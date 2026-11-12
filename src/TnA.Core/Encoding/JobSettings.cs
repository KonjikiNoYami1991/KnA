using System.Text.Json.Serialization;

namespace TnA.Core.Encoding;

/// <summary>What the user chose for one file.</summary>
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
    /// <summary>Prefer a "same name.ass/.srt" file next to the video for hardsub.</summary>
    public bool UseSidecarSubtitles { get; init; } = true;

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

    [JsonIgnore]
    public bool EffectiveTenBit => TenBit && VideoEncoders.Supports10Bit(EffectiveCodec, EffectiveBackend)
                                   && Profile is ProfileKind.UniversalMkv or ProfileKind.Streaming;

    [JsonIgnore]
    public SubtitleMode EffectiveSubtitles => ProfileDefinition.UsesSubtitleMode ? Subtitles : SubtitleMode.None;

    [JsonIgnore]
    public string Label
    {
        get
        {
            var p = ProfileDefinition;
            if (!p.EncodesVideo) return p.Name;
            var parts = new List<string> { p.Name };
            if (p.AllowsCodecChoice) parts.Add(EffectiveCodec.Short());
            if (EffectiveBackend != EncoderBackend.Software) parts.Add(EffectiveBackend.Short());
            if (EffectiveTenBit) parts.Add("10bit");
            parts.Add(Resolutions.Display(Resolution));
            parts.Add(Quality.Display().Replace(" (test only)", ""));
            return string.Join(" ", parts);
        }
    }
}
