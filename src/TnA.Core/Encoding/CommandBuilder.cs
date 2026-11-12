using System.Globalization;
using TnA.Core.Media;

namespace TnA.Core.Encoding;

public sealed record EncodePlan(IReadOnlyList<string> Arguments, string OutputPath)
{
    /// <summary>Human readable command line (for logs and the "show command" button).</summary>
    public string CommandLine(string ffmpeg = "ffmpeg") =>
        string.Join(' ', new[] { ffmpeg }.Concat(Arguments).Select(Quote));

    private static string Quote(string a) =>
        a.Length > 0 && a.All(c => !char.IsWhiteSpace(c) && c is not '"' and not '\'' and not ';' and not '|' and not '&' and not '[' and not ']')
            ? a : "\"" + a.Replace("\"", "\\\"") + "\"";
}

public sealed record BuildOptions
{
    /// <summary>Destination folder; null = next to the source file.</summary>
    public string? OutputDirectory { get; init; }
    public string? VaapiDevice { get; init; }
    /// <summary>Extra arguments appended before the output (power users).</summary>
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];
    public string AppVersion { get; init; } = "2.0";
}

/// <summary>Builds the FFmpeg command for a job. Pure function: easy to unit test.</summary>
public static class CommandBuilder
{
    public static readonly IReadOnlyList<string> ProgressArgs = ["-progress", "pipe:1", "-nostats", "-stats_period", "0.5"];

    public static EncodePlan Build(MediaInfo media, JobSettings s, PreparedSubtitles? subs, BuildOptions o, string? forcedOutputPath = null)
    {
        var profile = s.ProfileDefinition;
        if (profile.IsKeyframes) throw new InvalidOperationException("Keyframe jobs don't use CommandBuilder.");

        var mode = s.EffectiveSubtitles;
        string container = profile.Container;
        if (mode == SubtitleMode.Softsub && container != "mkv") container = "mkv";

        string output = forcedOutputPath ?? OutputNaming.Build(media.Path, s.Label, container, o.OutputDirectory);
        var a = new List<string> { "-hide_banner", "-nostdin", "-n" };

        return profile.Kind switch
        {
            ProfileKind.RemuxMkv => Finish(a, RemuxMkv(media), o, output, container),
            ProfileKind.RemuxMp4 => Finish(a, RemuxMp4(media, s, o), o, output, container),
            _ => Finish(a, Encode(media, s, subs, o, container, mode), o, output, container),
        };
    }

    private static List<string> RemuxMkv(MediaInfo m) =>
        ["-i", m.Path, "-map", "0", "-map", "-0:d?", "-c", "copy", "-map_metadata", "0", "-map_chapters", "0"];

    private static List<string> RemuxMp4(MediaInfo m, JobSettings s, BuildOptions o)
    {
        var v = m.PrimaryVideo!;
        bool copyVideo = v.CodecName is "h264" or "hevc" or "av1" or "mpeg4" or "mpeg2video";
        var a = new List<string>();
        if (!copyVideo) a.AddRange(VideoEncoders.GlobalArgs(s.EffectiveBackend, o.VaapiDevice));
        a.AddRange(["-i", m.Path, "-map", $"0:{v.Index}"]);
        if (copyVideo)
        {
            a.AddRange(["-c:v", "copy"]);
            if (v.CodecName == "hevc") a.AddRange(["-tag:v", "hvc1"]);
        }
        else
        {
            var filters = BaseFilters(v, s.Resolution, null, s);
            if (filters.Count > 0) a.AddRange(["-vf", string.Join(',', filters)]);
            a.AddRange(VideoEncoders.BuildArgs(new VideoEncodeRequest(VideoCodec.H264, s.EffectiveBackend, s.Quality, false,
                ProfileKind.RemuxMp4, v.FrameRate, true)));
        }

        int n = 0;
        foreach (var audio in m.Audio)
        {
            a.AddRange(["-map", $"0:{audio.Index}"]);
            a.AddRange(AudioPlanner.BuildArgs(audio, n++, ProfileKind.RemuxMp4, s.Quality));
        }
        foreach (var sub in m.Subtitles.Where(x => x.IsText))
            a.AddRange(["-map", $"0:{sub.Index}"]);
        if (m.Subtitles.Any(x => x.IsText)) a.AddRange(["-c:s", "mov_text"]);
        a.AddRange(["-map_metadata", "0", "-map_chapters", "0"]);
        return a;
    }

    private static List<string> Encode(MediaInfo m, JobSettings s, PreparedSubtitles? subs, BuildOptions o,
        string container, SubtitleMode mode)
    {
        var v = m.PrimaryVideo!;
        var codec = s.EffectiveCodec;
        var backend = s.EffectiveBackend;
        bool tenBit = s.EffectiveTenBit;
        var a = new List<string>();
        a.AddRange(VideoEncoders.GlobalArgs(backend, o.VaapiDevice));
        a.AddRange(["-i", m.Path]);

        bool burn = mode == SubtitleMode.Hardsub && subs is not null;
        var tail = VideoEncoders.FilterTail(backend, codec, tenBit);

        if (burn && subs!.BitmapStreamIndex is { } bitmapIndex)
        {
            // Bitmap subs (PGS/VobSub) are overlaid at source resolution, then everything is scaled.
            var pre = v.IsInterlaced ? $"[0:{v.Index}]{Deinterlace}[base];[base]" : $"[0:{v.Index}]";
            var post = BaseFilters(v, s.Resolution, null, s, includeDeinterlace: false);
            if (tail is not null) post.Add(tail);
            var graph = $"{pre}[0:{bitmapIndex}]overlay=eof_action=pass:repeatlast=0" +
                        (post.Count > 0 ? "," + string.Join(',', post) : "") + "[vout]";
            a.AddRange(["-filter_complex", graph, "-map", "[vout]"]);
        }
        else
        {
            var filters = BaseFilters(v, s.Resolution, burn ? subs : null, s);
            if (tail is not null) filters.Add(tail);
            a.AddRange(["-map", $"0:{v.Index}"]);
            if (filters.Count > 0) a.AddRange(["-vf", string.Join(',', filters)]);
        }

        a.AddRange(VideoEncoders.BuildArgs(new VideoEncodeRequest(codec, backend, s.Quality, tenBit, s.Profile,
            v.FrameRate, container == "mp4")));

        var tracks = AudioPlanner.SelectTracks(m, mode, s.Profile);
        for (int i = 0; i < tracks.Count; i++)
        {
            a.AddRange(["-map", $"0:{tracks[i].Index}"]);
            a.AddRange(AudioPlanner.BuildArgs(tracks[i], i, s.Profile, s.Quality));
        }

        if (mode == SubtitleMode.Softsub)
            a.AddRange(["-map", "0:s?", "-map", "0:t?", "-c:s", "copy", "-c:t", "copy"]);

        if (container is "mkv" or "mp4") a.AddRange(["-map_metadata", "0", "-map_chapters", "0"]);
        a.AddRange(["-metadata", $"comment=Encoded with TnA - Tanoshimi no Autohardsubber {o.AppVersion}"]);
        return a;
    }

    private const string Deinterlace = "bwdif=mode=send_frame:parity=auto:deint=interlaced";

    /// <summary>deinterlace → scale → burn text subtitles (after scaling, so libass renders crisp at output size).</summary>
    private static List<string> BaseFilters(VideoStream v, int targetHeight, PreparedSubtitles? textSubs, JobSettings s,
        bool includeDeinterlace = true)
    {
        var f = new List<string>();
        if (includeDeinterlace && v.IsInterlaced) f.Add(Deinterlace);
        if (ScaleCalculator.Compute(v, s.ProfileDefinition.UsesResolution ? targetHeight : 0) is { } scale)
            f.Add(ScaleCalculator.ToFilter(scale));
        if (textSubs?.RelativeSubtitleFile is { } file)
        {
            // Relative names + working directory = no filtergraph escaping nightmares with Windows paths.
            var filter = $"subtitles=filename={file}";
            if (textSubs.RelativeFontsDir is { } fonts) filter += $":fontsdir={fonts}";
            f.Add(filter);
        }
        return f;
    }

    private static EncodePlan Finish(List<string> head, List<string> body, BuildOptions o, string output, string container)
    {
        var a = new List<string>(head);
        a.AddRange(body);
        switch (container)
        {
            case "mp4": a.AddRange(["-movflags", "+faststart"]); break;
            case "avi": a.AddRange(["-f", "avi"]); break;
        }
        a.AddRange(o.ExtraArguments);
        a.AddRange(ProgressArgs);
        a.Add(output);
        return new EncodePlan(a, output);
    }
}

public static class OutputNaming
{
    /// <summary>"Name [Label].ext" next to the source (or in a chosen folder), never overwriting an existing file.</summary>
    public static string Build(string source, string label, string extension, string? outputDirectory)
    {
        var dir = string.IsNullOrWhiteSpace(outputDirectory) ? Path.GetDirectoryName(source)! : outputDirectory;
        var name = Path.GetFileNameWithoutExtension(source);
        var safeLabel = string.Concat(label.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' or ':' ? '-' : c));
        var candidate = Path.Combine(dir, $"{name} [{safeLabel}].{extension}");
        for (int i = 2; File.Exists(candidate); i++)
            candidate = Path.Combine(dir, $"{name} [{safeLabel}] ({i.ToString(CultureInfo.InvariantCulture)}).{extension}");
        return candidate;
    }
}
