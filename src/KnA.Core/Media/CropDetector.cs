using System.Globalization;
using System.Text.RegularExpressions;
using KnA.Core.Encoding;
using KnA.Core.Processing;

namespace KnA.Core.Media;

/// <summary>
/// Finds letterbox / pillarbox bars by itself: FFmpeg's cropdetect on keyframes taken at many points spread over the
/// film (only keyframes are decoded, so even a UHD film takes a few seconds). Only bars present in every sample are
/// cut — the smallest bar of each side — so variable or partial bars (a scope film with a 16:9 scene, a black scene)
/// never make the crop cut into the picture; tiny bars are ignored and the values are even.
/// </summary>
public static partial class CropDetector
{
    /// <summary>Samples taken over the film.</summary>
    public const int Points = 16;
    /// <summary>Bars thinner than this (pixels) are left alone (encoders' padding, soft edges).</summary>
    public const int MinBar = 6;

    public static bool Worth(MediaInfo media) =>
        media.PrimaryVideo is { IsAttachedPicture: false, Width: > 0, Height: > 0 } && media.Duration.TotalSeconds >= 2
        && Frameserver.KindOf(media.Path) == FrameserverKind.None;

    /// <summary>Detects the bars of the primary video and stores them in it (nothing when not worth it or none).</summary>
    public static async Task ApplyAsync(string ffmpeg, MediaInfo media, CancellationToken ct = default)
    {
        if (media.PrimaryVideo is not { } v || v.DetectedCrop is not null || !Worth(media)) return;
        try { v.DetectedCrop = await DetectAsync(ffmpeg, media.Path, v, media.Duration, ct) ?? new CropSettings(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
    }

    public static async Task<CropSettings?> DetectAsync(string ffmpeg, string path, VideoStream v, TimeSpan duration, CancellationToken ct)
    {
        double len = duration.TotalSeconds;
        var times = Enumerable.Range(0, Points).Select(i => len * (0.05 + 0.9 * i / (Points - 1))).ToList();
        var inArgs = MediaProber.RobustInputArgs(path).ToList();
        inArgs.AddRange(Discs.DiscSource.InputArgs(path));
        using var gate = new SemaphoreSlim(4);
        var found = await Task.WhenAll(times.Select(async t =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var args = new List<string> { "-hide_banner", "-nostdin", "-nostats", "-skip_frame", "nokey", "-ss", t.ToString("0.###", CultureInfo.InvariantCulture) };
                args.AddRange(inArgs);
                args.AddRange(["-i", path, "-map", "0:" + v.Index.ToString(CultureInfo.InvariantCulture), "-frames:v", "3",
                    "-vf", "cropdetect=limit=0.094:round=2:reset=0", "-an", "-sn", "-dn", "-f", "null", "-"]);
                var r = await ProcessRunner.RunAsync(ffmpeg, args, timeout: TimeSpan.FromMinutes(1), ct: ct);
                return Parse(r.StdErr, v.Width, v.Height);
            }
            finally { gate.Release(); }
        }));
        return Combine(found, v.Width, v.Height);
    }

    [GeneratedRegex(@"crop=(-?\d+):(-?\d+):(-?\d+):(-?\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex CropText();

    /// <summary>The last crop cropdetect printed, as bars (left, top, right, bottom); null for a black or unreadable frame.</summary>
    public static CropSettings? Parse(string stderr, int width, int height)
    {
        if (CropText().Matches(stderr).LastOrDefault() is not { } m) return null;
        int w = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), h = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        int x = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), y = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
        // A (nearly) black frame: cropdetect finds no picture at all.
        if (w <= width / 4 || h <= height / 4 || x < 0 || y < 0 || x + w > width || y + h > height) return null;
        return new CropSettings(x, y, width - w - x, height - h - y);
    }

    /// <summary>
    /// The bars present in every sample: the smallest of each side, when at least half the samples could be read.
    /// Thin bars are ignored, values are even. Null when nothing is to be cut.
    /// </summary>
    public static CropSettings? Combine(IReadOnlyList<CropSettings?> samples, int width, int height)
    {
        var ok = samples.Where(c => c is not null).Select(c => c!).ToList();
        if (ok.Count == 0 || ok.Count * 2 < samples.Count) return null;
        static int Bar(int v) => v < MinBar ? 0 : v & ~1;
        var c = new CropSettings(Bar(ok.Min(x => x.Left)), Bar(ok.Min(x => x.Top)), Bar(ok.Min(x => x.Right)), Bar(ok.Min(x => x.Bottom)));
        // Never more than a third of the picture away on one axis (that is not a bar).
        if (c.Left + c.Right > width / 3 || c.Top + c.Bottom > height / 3) return null;
        return c.IsEmpty ? null : c;
    }
}
