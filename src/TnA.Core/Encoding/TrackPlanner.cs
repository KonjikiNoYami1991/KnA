using TnA.Core.Media;

namespace TnA.Core.Encoding;

/// <summary>Builds the automatic track plan and looks up stream details for explicit plans.</summary>
public static class TrackPlanner
{
    public static readonly string[] FontExtensions = [".ttf", ".otf", ".ttc", ".otc", ".woff", ".woff2"];

    public static bool IsFontFile(string path) => FontExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>
    /// The plan TnA uses when the user didn't edit the tracks. It reproduces the classic behaviour:
    /// hardsub → one audio track + the best subtitle burned; softsub → everything; none → no subtitles.
    /// </summary>
    public static List<TrackPlan> Default(MediaInfo source, JobSettings s, IReadOnlyDictionary<string, MediaInfo>? externals = null)
    {
        var plan = new List<TrackPlan>();
        var p = s.ProfileDefinition;
        var mode = s.EffectiveSubtitles;

        if (source.PrimaryVideo is { } v)
            plan.Add(FromStream(source.Path, v, include: true));

        if (p.UsesAudio)
        {
            var audio = p.IsRemux ? source.Audio : AudioPlanner.SelectTracks(source, mode, s.Profile);
            foreach (var a in source.Audio)
                plan.Add(FromStream(source.Path, a, include: audio.Contains(a)));
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

    /// <summary>Extra options placed right before "-i" of an input (e.g. display rotation).</summary>
    public void AddOptions(int input, IEnumerable<string> args)
    {
        if (!_options.TryGetValue(input, out var list)) _options[input] = list = [];
        list.AddRange(args);
    }

    public InputTable(string source) => _inputs.Add((source, 0));

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
            a.AddRange(MediaProber.RobustInputArgs(file));
            if (_options.TryGetValue(i, out var opts)) a.AddRange(opts);
            if (seek?.Invoke(delay) is > 0 and var ss)
                a.AddRange(["-ss", ss.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)]);
            if (Math.Abs(delay) >= 0.0005)
                a.AddRange(["-itsoffset", delay.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)]);
            a.AddRange(["-i", file]);
        }
        return a;
    }
}
