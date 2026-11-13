using KnA.Core.Media;

namespace KnA.Core.Encoding;

/// <summary>Builds the automatic track plan and looks up stream details for explicit plans.</summary>
public static class TrackPlanner
{
    public static readonly string[] FontExtensions = [".ttf", ".otf", ".ttc", ".otc", ".woff", ".woff2"];

    public static bool IsFontFile(string path) => FontExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>
    /// The plan KnA uses when the user didn't edit the tracks. It reproduces the classic behaviour:
    /// hardsub → one audio track + the best subtitle burned; softsub → everything; none → no subtitles.
    /// </summary>
    public static List<TrackPlan> Default(MediaInfo source, JobSettings s, IReadOnlyDictionary<string, MediaInfo>? externals = null,
        MediaInfo? frameserver = null)
    {
        var plan = new List<TrackPlan>();
        var p = s.ProfileDefinition;
        var mode = s.EffectiveSubtitles;

        // Script applied to the file: the picture comes from the script (and the audio too, when asked).
        if (frameserver?.PrimaryVideo is { } fv)
        {
            plan.Add(FromStream(frameserver.Path, fv, include: true) with { Title = "VapourSynth/AviSynth" });
            if (source.PrimaryVideo is { } orig) plan.Add(FromStream(source.Path, orig, include: false));
        }
        else if (source.PrimaryVideo is { } v)
            plan.Add(FromStream(source.Path, v, include: true));

        bool scriptAudio = frameserver is not null && s.Frameserver.AudioFromScript && frameserver.Audio.Count > 0;
        if (scriptAudio)
            foreach (var a in frameserver!.Audio) plan.Add(FromStream(frameserver.Path, a, include: true));

        if (p.UsesAudio)
        {
            var audio = p.IsRemux ? source.Audio : AudioPlanner.SelectTracks(source, mode, s.Profile);
            foreach (var a in source.Audio)
                plan.Add(FromStream(source.Path, a, include: !scriptAudio && audio.Contains(a)));
        }

        var burnChoice = mode == SubtitleMode.Hardsub ? SubtitleSelector.Choose(source, s.UseSidecarSubtitles) : null;
        bool keepSubs = mode == SubtitleMode.Softsub || p.IsRemux;
        foreach (var sub in source.Subtitles)
        {
            bool burn = burnChoice?.Stream == sub;
            plan.Add(FromStream(source.Path, sub, include: keepSubs && !burn) with { Burn = burn });
        }
        if (burnChoice?.ExternalFile is { } sidecar)
        {
            plan.Add(new TrackPlan { File = sidecar, Stream = 0, Kind = "subtitle", Include = false, Burn = true, Codec = Path.GetExtension(sidecar).TrimStart('.') });
        }

        bool keepAttachments = (mode == SubtitleMode.Softsub && p.EncodesVideo) || p.Kind == ProfileKind.RemuxMkv;
        foreach (var att in source.Attachments)
            plan.Add(FromStream(source.Path, att, include: keepAttachments));

        if (externals is not null)
            foreach (var (path, media) in externals)
                plan.AddRange(ForExternal(path, media, include: false));
        return plan;
    }

    /// <summary>Tracks offered by an external file (all streams, or the file itself for fonts).</summary>
    public static IEnumerable<TrackPlan> ForExternal(string path, MediaInfo? media, bool include)
    {
        if (IsFontFile(path))
        {
            yield return new TrackPlan { File = path, Stream = -1, Kind = "attachment", Include = include, Title = Path.GetFileName(path), Codec = "font" };
            yield break;
        }
        if (media is null) yield break;
        foreach (var st in media.AllStreams)
            yield return FromStream(path, st, include) with { Default = false };
    }

    public static TrackPlan FromStream(string file, StreamBase st, bool include) => new()
    {
        File = file,
        Stream = st.Index,
        Kind = st.Kind,
        Include = include,
        Language = st.Language,
        Title = st is AttachmentStream at ? at.FileName : st.Title,
        Default = st.IsDefault,
        Forced = st.IsForced,
        Codec = st.CodecName,
    };

    /// <summary>Finds the probed stream a plan entry refers to.</summary>
    public static StreamBase? Lookup(TrackPlan t, IReadOnlyDictionary<string, MediaInfo> media) =>
        t.Stream >= 0 && media.TryGetValue(t.File, out var m) ? m.AllStreams.FirstOrDefault(s => s.Index == t.Stream) : null;
}

/// <summary>
/// FFmpeg inputs. Each (file, delay) pair becomes its own input with "-itsoffset", which is the
/// only reliable way to shift a single stream of a file without touching the others.
/// Input 0 is always the source without delay (metadata/chapters reference).
/// </summary>
public sealed class InputTable
{
    private readonly List<(string File, double Delay)> _inputs = [];
    private readonly Dictionary<int, List<string>> _options = [];

    /// <summary>Frame rate of VapourSynth inputs (fed through vspipe on stdin).</summary>
    public Dictionary<string, double> PipeFps { get; } = new(StringComparer.Ordinal);
    /// <summary>Frame count of VapourSynth inputs (vspipe refuses ranges past the end).</summary>
    public Dictionary<string, long> PipeFrames { get; } = new(StringComparer.Ordinal);
    /// <summary>Filled by ToArgs: the vspipe frame range to start for the (single) piped input.</summary>
    public PipeSource? Pipe { get; private set; }
    /// <summary>Last frame to request from vspipe, computed from the seek position (previews/segments).</summary>
    public Func<double, long?>? PipeEnd { get; set; }

    /// <summary>Pseudo input name of the AI upscaler's video (raw RGB48 on stdin).</summary>
    public const string AiInput = "\u0001kna-ai";
    /// <summary>Builds the upscaler command for a start / end (seconds on the source timeline).</summary>
    public Func<double, double?, IReadOnlyList<string>>? AiCommand { get; set; }
    public (int Width, int Height, string Fps)? AiFormat { get; set; }
    /// <summary>End (seconds) of what the upscaler must render, from the seek position (previews/segments).</summary>
    public Func<double, double?>? AiEnd { get; set; }

    /// <summary>Extra options placed right before "-i" of an input (e.g. display rotation).</summary>
    public void AddOptions(int input, IEnumerable<string> args)
    {
        if (!_options.TryGetValue(input, out var list)) _options[input] = list = [];
        list.AddRange(args);
    }

    /// <summary>Input 0 is the source (chapters/metadata), except for VapourSynth scripts, which are only opened when used.</summary>
    public InputTable(string source)
    {
        if (!Frameserver.IsVapourSynth(source)) _inputs.Add((source, 0));
    }

    public int Get(string file, double delaySeconds)
    {
        for (int i = 0; i < _inputs.Count; i++)
            if (_inputs[i].File == file && Math.Abs(_inputs[i].Delay - delaySeconds) < 0.0005) return i;
        _inputs.Add((file, delaySeconds));
        return _inputs.Count - 1;
    }

    public int Count => _inputs.Count;

    /// <param name="seek">Optional input seek (seconds) computed from each input's delay (previews only).</param>
    public List<string> ToArgs(Func<double, double>? seek = null)
    {
        var a = new List<string>();
        for (int i = 0; i < _inputs.Count; i++)
        {
            var (file, delay) = _inputs[i];
            if (file == AiInput && AiCommand is not null && AiFormat is { } fmt)
            {
                if (Pipe is not null) throw new InvalidOperationException(Localization.L.T("Only one VapourSynth video can be used per job."));
                double start = Math.Max(0, seek?.Invoke(delay) ?? 0);
                Pipe = new PipeSource("", 0, null) { Command = AiCommand(start, AiEnd?.Invoke(delay)) };
                double offset = delay + start;
                if (Math.Abs(offset) >= 0.0005) a.AddRange(["-itsoffset", offset.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)]);
                a.AddRange(["-f", "rawvideo", "-pixel_format", "rgb48le", "-video_size", $"{fmt.Width}x{fmt.Height}", "-framerate", fmt.Fps, "-i", "pipe:0"]);
                continue;
            }
            if (Frameserver.IsVapourSynth(file))
            {
                if (Pipe is not null) throw new InvalidOperationException(Localization.L.T("Only one VapourSynth video can be used per job."));
                double fps = PipeFps.GetValueOrDefault(file, 25);
                double want = seek?.Invoke(delay) ?? 0;
                long last = PipeFrames.TryGetValue(file, out var n) && n > 0 ? n - 1 : long.MaxValue;
                long start = want > 0 ? Math.Min((long)Math.Floor(want * fps), last) : 0;
                long? end = PipeEnd?.Invoke(delay) is { } e ? Math.Max(start, Math.Min(e, last)) : null;
                Pipe = new PipeSource(file, start, end);
                double offset = delay + start / fps;   // restores the original timeline
                if (_options.TryGetValue(i, out var o2)) a.AddRange(o2.Where(x => !x.StartsWith("-display_", StringComparison.Ordinal)));
                if (Math.Abs(offset) >= 0.0005) a.AddRange(["-itsoffset", offset.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)]);
                a.AddRange(["-f", "yuv4mpegpipe", "-i", "pipe:0"]);
                continue;
            }
            a.AddRange(MediaProber.RobustInputArgs(file));
            if (_options.TryGetValue(i, out var opts)) a.AddRange(opts);
            if (seek?.Invoke(delay) is > 0 and var ss)
                a.AddRange(["-ss", ss.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)]);
            if (Math.Abs(delay) >= 0.0005)
                a.AddRange(["-itsoffset", delay.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)]);
            a.AddRange(KnA.Core.Media.Discs.DiscSource.InputArgs(file));
        }
        return a;
    }
}

/// <summary>vspipe invocation (or, with <see cref="Command"/>, any program) feeding FFmpeg's stdin.</summary>
public sealed record PipeSource(string Script, long StartFrame, long? EndFrame)
{
    /// <summary>Executable followed by its arguments (AI upscaler); null = vspipe on <see cref="Script"/>.</summary>
    public IReadOnlyList<string>? Command { get; init; }
}
