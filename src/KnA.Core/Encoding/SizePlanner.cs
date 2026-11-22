using KnA.Core.Localization;
using KnA.Core.Media;

namespace KnA.Core.Encoding;

/// <summary>What a target file size leaves for the video.</summary>
public sealed record SizeBudget(double Seconds, double TargetKbits, double AudioKbps, double SubtitleKbps, double OverheadKbps, int VideoKbps)
{
    /// <summary>Less than this is not a usable picture at any resolution: the size is too small for the duration.</summary>
    public bool TooSmall => VideoKbps < SizePlanner.MinimumVideoKbps;
    /// <summary>
    /// Above this the picture doesn't improve visibly (≈0.35 bit per pixel per frame of the output): a larger size is a
    /// limit, not a goal, so the video stops here and the file stays smaller. 0 = unknown picture size.
    /// </summary>
    public int CeilingKbps { get; init; }
    public int EffectiveKbps => Math.Max(SizePlanner.MinimumVideoKbps, CeilingKbps > 0 ? Math.Min(VideoKbps, CeilingKbps) : VideoKbps);
    public bool Capped => CeilingKbps > 0 && VideoKbps > CeilingKbps;
}

/// <summary>
/// Turns "the file must not exceed N MB" into bitrates. Every stream counts: audio tracks (copied ones at their real
/// bitrate, encoded ones at the planned one), subtitles (their size in the source, scaled to the kept duration), the
/// container overhead, and a small safety margin because one-pass encoders land within a few percent of the target.
/// </summary>
public static class SizePlanner
{
    public const int MinimumVideoKbps = 100;
    /// <summary>One-pass VBR/ABR lands within ±3 % on a film: aim 3 % lower so the result stays below the limit.</summary>
    public const double SafetyMargin = 0.03;

    public static double MiB(double mb) => mb * 1024 * 1024;

    /// <summary>Kept duration of the output (trim and segments), seconds.</summary>
    public static double OutputSeconds(JobEdit edit, TimeSpan source)
    {
        double total = source.TotalSeconds;
        if (edit.IsMultiSegment) return edit.KeptParts(total).Sum(p => p.End - p.Start);
        double start = Math.Max(0, edit.TrimStart ?? 0);
        double end = edit.TrimEnd is > 0 and var e ? Math.Min(e, total) : total;
        return Math.Max(0, end - start);
    }

    /// <summary>Planned bitrate of each included audio track, kb/s.</summary>
    public static IEnumerable<double> AudioKbps(IEnumerable<TrackPlan> tracks, IReadOnlyDictionary<string, MediaInfo> media,
        JobSettings s, string container, AudioEncoderSet encoders)
    {
        foreach (var t in tracks.Where(t => t.Include && t.Kind == "audio"))
        {
            if (TrackPlanner.Lookup(t, media) is not AudioStream a) continue;
            var plan = AudioPlanner.PlanTrack(t, a, s, container, encoders);
            if (plan.IsCopy) yield return a.BitRate > 0 ? a.BitRate / 1000.0 : Guess(a);
            else if (plan.Kbps is { } k) yield return k;
            else yield return a.IsLossless || plan.Encoder is "flac" or "alac" or "wavpack" or "truehd" ? 600.0 * Math.Max(2, plan.Channels) / 2
                : plan.Encoder!.StartsWith("pcm_", StringComparison.Ordinal) ? 48 * 24 * Math.Max(2, plan.Channels)
                : 160.0 * Math.Max(2, plan.Channels) / 2;   // VBR quality modes: a typical value
        }
    }

    private static double Guess(AudioStream a) => a.CodecName switch
    {
        "truehd" or "mlp" => 3500,
        "dts" when a.IsLossless => 4000,
        "dts" => 1509,
        "eac3" => 768,
        "ac3" => 640,
        "flac" => 900,
        _ when a.CodecName.StartsWith("pcm_", StringComparison.Ordinal) => a.SampleRate / 1000.0 * 24 * Math.Max(2, a.Channels),
        _ => 192.0 * Math.Max(2, a.Channels) / 2,
    };

    public static SizeBudget Plan(double targetMB, double seconds, IEnumerable<double> audioKbps, double subtitleBytes, string container)
    {
        seconds = Math.Max(seconds, 0.1);
        double targetKbits = MiB(targetMB) * 8 / 1000 * (1 - SafetyMargin);
        double audio = audioKbps.Sum();
        double subs = subtitleBytes * 8 / 1000 / seconds;
        // Matroska ≈ 0.5 %, MP4/MOV ≈ 1 %, MPEG-TS ≈ 5 % of the stream (packet headers).
        double overheadShare = container switch { "ts" => 0.05, "mp4" or "mov" => 0.01, _ => 0.005 };
        double perSecond = targetKbits / seconds;
        double overhead = perSecond * overheadShare;
        int video = (int)Math.Floor(perSecond - audio - subs - overhead);
        return new SizeBudget(seconds, targetKbits, audio, subs, overhead, Math.Max(video, 0));
    }

    /// <summary>Budget of a job, or null when it doesn't use a target size.</summary>
    public static SizeBudget? For(CompileContext ctx)
    {
        var s = ctx.Settings;
        if (s.Encoder.RateControl != RateControl.TargetSize || s.Encoder.TargetSizeMB is not > 0 || !s.ProfileDefinition.EncodesVideo) return null;
        var media = new Dictionary<string, MediaInfo>(ctx.Media) { [ctx.Source.Path] = ctx.Source };
        var tracks = ctx.Edit.Tracks ?? TrackPlanner.Default(ctx.Source, s);
        double seconds = OutputSeconds(ctx.Edit, ctx.Source.Duration);
        double sourceSeconds = Math.Max(ctx.Source.Duration.TotalSeconds, 0.1);
        double subtitleBytes = tracks.Where(t => t.Include && t.Kind == "subtitle")
            .Select(t => TrackPlanner.Lookup(t, media))
            .Sum(st => st is null ? 0 : st.SizeBytes > 0 ? st.SizeBytes * seconds / sourceSeconds : st.BitRate / 8.0 * seconds);
        var container = s.EffectiveContainer;
        var budget = Plan(s.Encoder.TargetSizeMB.Value, seconds, AudioKbps(tracks, media, s, container, ctx.Options.AudioEncoders), subtitleBytes, container);
        if (ctx.Source.PrimaryVideo is { Width: > 0, Height: > 0 } v)
        {
            double h = s.ProfileDefinition.UsesResolution && s.Resolution > 0 && s.Resolution < v.Height ? s.Resolution : v.Height;
            double w = v.Width * h / v.Height;
            double fps = s.VideoFilters.Fps ?? (v.FrameRate > 0 ? v.FrameRate : 25);
            budget = budget with { CeilingKbps = (int)(w * h * fps * 0.35 / 1000) };
        }
        return budget;
    }

    /// <summary>
    /// The context with the target size turned into an average bitrate (idempotent). Resolved once for the whole job,
    /// so chunks and cut segments all use the same bitrate.
    /// </summary>
    public static CompileContext Resolve(CompileContext ctx, List<ValidationIssue>? issues = null)
    {
        if (For(ctx) is not { } b) return ctx;
        var o = ctx.Settings.Encoder;
        int kbps = b.EffectiveKbps;
        if (b.TooSmall)
            issues?.Add(Containers.Warning("{0} MB is too small for {1}: the video gets the minimum ({2} kb/s) and the file will be bigger.",
                o.TargetSizeMB!.Value.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture), Format(b.Seconds), MinimumVideoKbps));
        return ctx with
        {
            Settings = ctx.Settings with
            {
                // A ceiling keeps peaks reasonable for hardware decoders; the average decides the size.
                Encoder = o with { RateControl = RateControl.Bitrate, BitrateKbps = kbps, MaxBitrateKbps = o.MaxBitrateKbps ?? kbps * 2 },
            },
        };
    }

    public static string Describe(SizeBudget b) =>
        L.T("Video {0} kb/s · audio {1} kb/s · subtitles {2} kb/s · container {3} kb/s ({4})",
            b.EffectiveKbps, Math.Round(b.AudioKbps), Math.Round(b.SubtitleKbps, 1), Math.Round(b.OverheadKbps), Format(b.Seconds))
        + (b.Capped ? " · " + L.T("more than this video needs: the file will be smaller") : "");

    private static string Format(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }
}
