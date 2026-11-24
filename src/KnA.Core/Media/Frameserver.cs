using System.Globalization;
using KnA.Core.Encoding;
using KnA.Core.Localization;
using KnA.Core.Processing;

namespace KnA.Core.Media;

public enum FrameserverKind { None, VapourSynth, AviSynth }

/// <summary>
/// VapourSynth / AviSynth scripts.
/// VapourSynth runs through vspipe: video = output 0 as Y4M on a pipe, audio = output 1 (if the script sets one)
/// pre-rendered to W64. Seeking (previews, segments) uses vspipe's frame range plus -itsoffset so timestamps stay
/// on the original timeline. AviSynth (.avs) is read by FFmpeg's avisynth demuxer (audio included via AudioDub).
/// </summary>
public static class Frameserver
{
    public static readonly string[] ScriptExtensions = [".vpy", ".avs"];

    public static FrameserverKind KindOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".vpy" => FrameserverKind.VapourSynth,
        ".avs" => FrameserverKind.AviSynth,
        _ => FrameserverKind.None,
    };

    public static bool IsVapourSynth(string path) => KindOf(path) == FrameserverKind.VapourSynth;

    /// <summary>Reads "vspipe --info" for every output and builds the media description.</summary>
    public static async Task<MediaInfo> ProbeVapourSynthAsync(string vspipe, string script, CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync(vspipe, ["--info", script, "-"], timeout: TimeSpan.FromMinutes(5), ct: ct);
        if (r.ExitCode != 0)
            throw new InvalidOperationException(L.T("The VapourSynth script failed: {0}", (r.StdErr + r.StdOut).Trim()));
        return ParseVsInfo(script, r.StdOut);
    }

    internal static MediaInfo ParseVsInfo(string script, string info)
    {
        var video = new List<VideoStream>();
        var audio = new List<AudioStream>();
        foreach (var block in info.Split("Output Index:", StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = block.Split('\n').Select(l => l.Split(':', 2)).Where(p => p.Length == 2)
                .ToDictionary(p => p[0].Trim(), p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);
            int index = int.TryParse(block.Split('\n')[0].Trim(), out var ix) ? ix : 0;
            var type = kv.GetValueOrDefault("Type") ?? "";
            if (type.StartsWith("Video", StringComparison.OrdinalIgnoreCase))
            {
                double fps = 0;
                string? rational = null;
                if (kv.GetValueOrDefault("FPS") is { } f)
                {
                    rational = f.Split(' ')[0];
                    fps = FfprobeParser.ParseRational(rational);
                }
                int.TryParse(kv.GetValueOrDefault("Bits"), out var bits);
                long.TryParse(kv.GetValueOrDefault("Frames"), out var frames);
                video.Add(new VideoStream
                {
                    Kind = "video", Index = index, CodecName = "vapoursynth",
                    Width = int.Parse(kv.GetValueOrDefault("Width") ?? "0", CultureInfo.InvariantCulture),
                    Height = int.Parse(kv.GetValueOrDefault("Height") ?? "0", CultureInfo.InvariantCulture),
                    FrameRate = fps, FrameRateRational = rational, FrameCount = frames, BitDepth = bits > 0 ? bits : 8,
                    PixelFormat = kv.GetValueOrDefault("Format Name"), IsDefault = true,
                });
            }
            else if (type.StartsWith("Audio", StringComparison.OrdinalIgnoreCase))
            {
                int.TryParse(kv.GetValueOrDefault("Channels"), out var ch);
                int.TryParse(kv.GetValueOrDefault("Sample Rate"), out var sr);
                audio.Add(new AudioStream
                {
                    Kind = "audio", Index = index, CodecName = "pcm_s" + (kv.GetValueOrDefault("Bits") ?? "16") + "le",
                    Channels = ch, SampleRate = sr, Title = L.T("Script audio"), IsDefault = true,
                });
            }
        }
        var v0 = video.FirstOrDefault() ?? throw new InvalidOperationException(L.T("The script has no video output (output 0)."));
        var duration = v0.FrameRate > 0 ? TimeSpan.FromSeconds(v0.FrameCount / v0.FrameRate) : TimeSpan.Zero;
        return new MediaInfo { Path = script, ContainerFormat = "vapoursynth", Duration = duration, Video = video, Audio = audio };
    }

    /// <summary>vspipe arguments for the video, optionally limited to a frame range.</summary>
    public static List<string> VsPipeVideoArgs(string script, long? startFrame, long? endFrame)
    {
        var a = new List<string> { "-c", "y4m" };
        if (startFrame is > 0) a.AddRange(["-s", startFrame.Value.ToString(CultureInfo.InvariantCulture)]);
        if (endFrame is { } e) a.AddRange(["-e", e.ToString(CultureInfo.InvariantCulture)]);
        a.AddRange([script, "-"]);
        return a;
    }

    // ---------------------------------------------------------------- script templates

    public sealed record Template(string Name, FrameserverKind Kind, string Body);

    /// <summary>Starting points; "{source}" is replaced with the source file path, "{fps}" with its frame rate.</summary>
    public static IReadOnlyList<Template> Templates { get; } =
    [
        new("L-SMASH Works", FrameserverKind.VapourSynth, """
            import vapoursynth as vs
            core = vs.core
            clip = core.lsmas.LWLibavSource(source=r"{source}")
            # --- filters ---
            # clip = core.std.Crop(clip, left=0, right=0, top=0, bottom=0)
            clip.set_output(0)
            # Audio from the script (optional):
            # audio = core.bs.AudioSource(source=r"{source}")
            # audio.set_output(1)
            """),
        new("BestSource", FrameserverKind.VapourSynth, """
            import vapoursynth as vs
            core = vs.core
            clip = core.bs.VideoSource(source=r"{source}")
            # --- filters ---
            clip.set_output(0)
            # audio = core.bs.AudioSource(source=r"{source}")
            # audio.set_output(1)
            """),
        new("FFMS2", FrameserverKind.VapourSynth, """
            import vapoursynth as vs
            core = vs.core
            clip = core.ffms2.Source(source=r"{source}")
            # --- filters ---
            clip.set_output(0)
            """),
        new("L-SMASH Works", FrameserverKind.AviSynth, """
            LWLibavVideoSource("{source}")
            # AudioDub(last, LWLibavAudioSource("{source}"))   # audio from the script (optional)
            # --- filters ---
            """),
        new("FFMS2", FrameserverKind.AviSynth, """
            FFVideoSource("{source}")
            # AudioDub(last, FFAudioSource("{source}"))
            # --- filters ---
            """),
    ];

    /// <summary>Writes the job's script next to the other job files and returns its path.</summary>
    public static string WriteScript(string folder, Guid jobId, FrameserverSettings fs, string source)
    {
        Directory.CreateDirectory(folder);
        var ext = fs.Kind == FrameserverKind.AviSynth ? ".avs" : ".vpy";
        var path = Path.Combine(folder, jobId.ToString("N") + ext);
        // L-SMASH's index goes to KnA's folder, not next to the video (read-only shares, no clutter).
        var cache = Path.Combine(folder, "cache");
        Directory.CreateDirectory(cache);
        var text = (fs.EffectiveScript ?? "").Replace("{source}", fs.Kind == FrameserverKind.AviSynth ? source.Replace("\"", "") : source)
            .Replace("{cachedir}", cache);
        if (!File.Exists(path) || File.ReadAllText(path) != text) File.WriteAllText(path, text);
        return path;
    }
}

/// <summary>
/// VapourSynth as the standard frameserver of encodes (like StaxRip): the video of a CPU encode is read by a script
/// KnA writes (L-SMASH source) instead of FFmpeg's own reader, and KnA's filters follow. Sources whose timing or
/// metadata only FFmpeg's reader keeps, and GPU encoders (GPU decoding first), are read by FFmpeg as before.
/// </summary>
public static class StandardFrameserver
{
    public static FrameserverSettings Settings { get; } = new() { Kind = FrameserverKind.VapourSynth, Chain = [], Source = "lsmas" };

    /// <summary>Why the standard script can't read this job's video (null = it can).</summary>
    public static string? Blocker(JobSettings s, MediaInfo? media, string sourcePath, JobEdit edit)
    {
        var p = s.ProfileDefinition;
        if (!p.EncodesVideo || p.IsImages || !p.UsesVideoFilters) return Localization.L.T("the video is not encoded");
        if (Frameserver.KindOf(sourcePath) != FrameserverKind.None) return Localization.L.T("the source is already a script");
        if (Discs.DiscSource.IsTitlePath(sourcePath)) return Localization.L.T("disc source");
        if (RigayaEncoders.Wanted(s) || s.EffectiveBackend != EncoderBackend.Software) return Localization.L.T("GPU encoder: the GPU decodes the file (GPU first)");
        if (media?.PrimaryVideo is not { } v) return Localization.L.T("no video");
        // HDR travels with the script (KnA gives the encoder the source's colours, RPU and HDR10+ explicitly), except Dolby
        // Vision profile 5, whose reshaping only FFmpeg's reader applies when converting.
        if (v.Hdr.DolbyVisionWithoutFallback) return Localization.L.T("Dolby Vision profile 5 (its reshaping only travels with FFmpeg's reader)");
        if (v.VariableFrameRate) return Localization.L.T("variable frame rate");
        if (v.IsInterlaced) return Localization.L.T("interlaced source (deinterlaced by FFmpeg as it reads)");
        if (v.Rotation != 0 || s.VideoFilters.OverridesOrientation) return Localization.L.T("rotation");
        if (v.SampleAspectRatio > 0 && Math.Abs(v.SampleAspectRatio - 1) > 0.01) return Localization.L.T("non-square pixels (anamorphic)");
        if (s.Upscale.Method == UpscaleMethod.Neural) return Localization.L.T("AI upscaler (it reads the file itself)");
        if (Path.GetExtension(sourcePath).ToLowerInvariant() is ".ts" or ".m2ts" or ".mts" or ".vob" or ".mpg" or ".mpeg")
            return Localization.L.T("MPEG stream (timestamps that don't start at 0)");
        if (edit.Tracks?.Any(t => t.Kind == "video" && t.Include && t.CopyVideo) == true) return Localization.L.T("the video is copied");
        return null;
    }
}

/// <summary>Frameserver applied to a normal video (stored in presets: the script uses {source}).</summary>
public sealed record FrameserverSettings
{
    public FrameserverKind Kind { get; init; } = FrameserverKind.None;
    public string? Script { get; init; }
    /// <summary>Use the audio produced by the script (VapourSynth output 1 / AviSynth AudioDub) instead of the file's tracks.</summary>
    public bool AudioFromScript { get; init; }
    /// <summary>
    /// Filters picked from the catalogue (<see cref="FilterCatalog"/>), in order; the script is written from them.
    /// Null = the script is written by hand (<see cref="Script"/>).
    /// </summary>
    public List<FrameserverFilter>? Chain { get; init; }
    /// <summary>Source filter of a catalogue script: "lsmas" (L-SMASH Works), "bs" (BestSource) or "ffms2".</summary>
    public string? Source { get; init; }

    /// <summary>The script that runs: generated from <see cref="Chain"/>, or the one written by hand.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? EffectiveScript => Chain is not null && Kind != FrameserverKind.None ? FilterCatalog.Build(this) : Script;
}

/// <summary>One filter of a catalogue chain: its id and the values given to its parameters (missing = default).</summary>
public sealed record FrameserverFilter(string Id, Dictionary<string, string>? Values = null);
