using System.Globalization;
using System.Text.RegularExpressions;
using KnA.Core.Processing;

namespace KnA.Core.Media;

/// <summary>
/// Finds out how the frames of a source are made, the StaxRip / Hybrid way: FFmpeg's idet filter looks at a few
/// seconds at three points of the film. The field-order flag alone is not enough: telecined film (NTSC DVDs, anime,
/// 29.97 hard telecine) is flagged interlaced but needs inverse telecine back to 23.976, not a deinterlacer (judder,
/// blended frames), and many progressive files carry a wrong interlaced flag.
/// </summary>
public static partial class ScanDetector
{
    /// <summary>Sources worth analysing: the formats of DVD, broadcast and camcorders, at TV frame rates.</summary>
    public static bool Worth(VideoStream v) =>
        v.CodecName is "mpeg2video" or "mpeg1video" or "h264" or "vc1" or "mpeg4" or "msmpeg4v3" or "wmv3" or "dvvideo"
        && !v.Hdr.IsHdr && !v.IsAttachedPicture
        && (v.IsInterlaced || Near(v.FrameRate, 29.97) || Near(v.FrameRate, 30) || Near(v.FrameRate, 25) || Near(v.FrameRate, 59.94));

    private static bool Near(double a, double b) => Math.Abs(a - b) < 0.05;

    /// <summary>Analyses the primary video of <paramref name="media"/> and stores the result in it (nothing when not worth it).</summary>
    public static async Task ApplyAsync(string ffmpeg, MediaInfo media, CancellationToken ct = default)
    {
        if (media.PrimaryVideo is not { } v || v.Scan is not null || !Worth(v)) return;
        if (Discs.DiscSource.IsTitlePath(media.Path) || Frameserver.KindOf(media.Path) != FrameserverKind.None) return;
        try { v.Scan = await AnalyzeAsync(ffmpeg, media.Path, v, media.Duration, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* the flag stays the guide */ }
    }

    public static async Task<ScanType?> AnalyzeAsync(string ffmpeg, string path, VideoStream v, TimeSpan duration, CancellationToken ct)
    {
        double len = duration.TotalSeconds;
        double[] points = len > 60 ? [len * 0.2, len * 0.5, len * 0.8] : [0];
        var runs = await Task.WhenAll(points.Select(async t =>
        {
            var args = new List<string> { "-hide_banner", "-nostdin", "-nostats" };
            if (t > 0) args.AddRange(["-ss", t.ToString("0.###", CultureInfo.InvariantCulture)]);
            args.AddRange(["-i", path, "-map", "0:" + v.Index.ToString(CultureInfo.InvariantCulture), "-t", "8", "-vf", "idet", "-an", "-sn", "-dn", "-f", "null", "-"]);
            var r = await ProcessRunner.RunAsync(ffmpeg, args, timeout: TimeSpan.FromMinutes(2), ct: ct);
            return Parse(r.StdErr);
        }));
        var sum = runs.Aggregate(new IdetCounts(), (a, b) => a + b);
        return Classify(sum, v.FrameRate);
    }

    public sealed record IdetCounts(long Tff = 0, long Bff = 0, long Progressive = 0, long Undetermined = 0, long Neither = 0, long Top = 0, long Bottom = 0)
    {
        public static IdetCounts operator +(IdetCounts a, IdetCounts b) => new(a.Tff + b.Tff, a.Bff + b.Bff, a.Progressive + b.Progressive,
            a.Undetermined + b.Undetermined, a.Neither + b.Neither, a.Top + b.Top, a.Bottom + b.Bottom);
    }

    [GeneratedRegex(@"Multi frame detection:\s*TFF:\s*(\d+)\s*BFF:\s*(\d+)\s*Progressive:\s*(\d+)\s*Undetermined:\s*(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex MultiLine();
    [GeneratedRegex(@"Repeated Fields:\s*Neither:\s*(\d+)\s*Top:\s*(\d+)\s*Bottom:\s*(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex RepeatLine();

    /// <summary>The idet summary FFmpeg prints at the end (multi-frame detection and repeated fields).</summary>
    public static IdetCounts Parse(string stderr)
    {
        long L(Group g) => long.Parse(g.Value, CultureInfo.InvariantCulture);
        var c = new IdetCounts();
        if (MultiLine().Matches(stderr).LastOrDefault() is { } m)
            c = c with { Tff = L(m.Groups[1]), Bff = L(m.Groups[2]), Progressive = L(m.Groups[3]), Undetermined = L(m.Groups[4]) };
        if (RepeatLine().Matches(stderr).LastOrDefault() is { } r)
            c = c with { Neither = L(r.Groups[1]), Top = L(r.Groups[2]), Bottom = L(r.Groups[3]) };
        return c;
    }

    /// <summary>
    /// At ~30 fps, fields repeated in about two frames of five (the 3:2 pulldown's signature: idet compares each field
    /// with the previous frame's), or a share of combed frames between a fifth and three fifths, mean hard telecine.
    /// Otherwise many combed frames → interlaced; almost none → progressive whatever the flag says (also soft telecine,
    /// which FFmpeg decodes as progressive film frames); in between the flag decides (null).
    /// </summary>
    public static ScanType? Classify(IdetCounts c, double fps)
    {
        long frames = c.Tff + c.Bff + c.Progressive;
        if (frames < 30) return null;
        long fields = c.Neither + c.Top + c.Bottom;
        double repeated = fields > 0 ? (double)(c.Top + c.Bottom) / fields : 0;
        double combed = (double)(c.Tff + c.Bff) / frames;
        bool ntsc = Near(fps, 29.97) || Near(fps, 30);
        if (ntsc && (repeated is >= 0.2 and <= 0.6 || combed is >= 0.2 and <= 0.6)) return ScanType.Telecine;
        if (combed > 0.6 || (!ntsc && combed >= 0.15)) return ScanType.Interlaced;
        if (combed < 0.05) return ScanType.Progressive;
        return null;
    }
}
