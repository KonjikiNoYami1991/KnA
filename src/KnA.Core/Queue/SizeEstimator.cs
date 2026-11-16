using KnA.Core.Encoding;
using KnA.Core.Localization;
using KnA.Core.Processing;

namespace KnA.Core.Queue;

/// <summary>Predicted size of a job, measured on short encoded samples.</summary>
public sealed record SizeEstimate(long Bytes, double VideoKbps, double AudioKbps, int Samples, double SampleSeconds, double Seconds)
{
    public string Describe() => L.T("≈ {0} (video ≈ {1} kb/s, audio {2} kb/s; measured on {3} samples of {4} s)",
        Format(Bytes), Math.Round(VideoKbps), Math.Round(AudioKbps), Samples, Math.Round(SampleSeconds, 1));

    public static string Format(long bytes)
    {
        string[] u = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {u[i]}";
    }
}

/// <summary>
/// "Compressibility check" without the wait: a few short pieces spread over the kept part are encoded exactly as the
/// job will (same filters, encoder, quality), and their size predicts the whole file before it is encoded. Useful
/// to choose a quality level, or to see whether a maximum file size costs quality.
/// </summary>
public static class SizeEstimator
{
    public static async Task<SizeEstimate> EstimateAsync(QueueService queue, QueueJob job, JobSettings settings, JobEdit edit,
        int samples = 6, double sampleSeconds = 4, CancellationToken ct = default)
    {
        var tools = queue.Tools ?? throw new InvalidOperationException(L.T("FFmpeg was not found. Configure it in Settings."));
        if (job.Media is null) throw new InvalidOperationException(L.T("The file has not been analysed yet."));
        var baseCtx = JobRunner.BuildContext(job, queue.Settings, tools, queue.Store.DataDirectory) with { Settings = settings, Edit = edit };
        baseCtx = SizePlanner.Resolve(baseCtx);
        var parts = edit.KeptParts(job.Media.Duration.TotalSeconds);
        if (parts.Count == 0) parts = [(0, job.Media.Duration.TotalSeconds)];
        double total = parts.Sum(p => p.End - p.Start);
        if (total <= 0) throw new InvalidOperationException(L.T("The trim end must be after the start."));
        samples = Math.Clamp((int)(total / sampleSeconds / 3), 1, samples);
        var dir = Path.Combine(queue.Store.TempDirectory, "estimate-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        long bytes = 0;
        double seconds = 0;
        try
        {
            var group = new ProcessGroup();
            for (int i = 0; i < samples; i++)
            {
                // Evenly spread over the kept timeline (skips the very start: logos and black frames compress too well).
                double at = total * (i + 0.5) / samples;
                double start = At(parts, at), end = Math.Min(start + sampleSeconds, At(parts, Math.Min(total, at + sampleSeconds)) + 0.0001);
                if (end - start < 0.5) continue;
                var output = Path.Combine(dir, $"s{i}.mkv");
                var c = CommandBuilder.Compile(baseCtx with { Chunk = new ChunkRequest(start, end), ForcedOutputPath = output });
                if (c.HasErrors) throw new InvalidOperationException(c.Issues.First(x => x.IsError).Message);
                var err = new System.Text.StringBuilder();
                int exit = await JobRunner.RunStreamingAsync(tools.Ffmpeg, c.Plan.Arguments, dir, group, queue.Settings.Priority, null,
                    line => { if (err.Length < 4000) err.AppendLine(line); }, ct);
                if (exit != 0 || !File.Exists(output)) throw new InvalidOperationException(JobRunner.LastError(err.ToString()) ?? $"FFmpeg exit {exit}");
                bytes += new FileInfo(output).Length;
                seconds += end - start;
            }
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
        if (seconds <= 0) throw new InvalidOperationException(L.T("The kept part is too short to estimate."));
        double videoKbps = bytes * 8 / 1000.0 / seconds;
        var media = new Dictionary<string, Media.MediaInfo>(baseCtx.Media) { [job.Media.Path] = job.Media };
        double audioKbps = SizePlanner.AudioKbps(baseCtx.Edit.Tracks ?? TrackPlanner.Default(job.Media, settings), media, settings,
            settings.EffectiveContainer, baseCtx.Options.AudioEncoders).Sum();
        long estimate = (long)((videoKbps + audioKbps) * 1000 / 8 * total * 1.005);
        return new SizeEstimate(estimate, videoKbps, audioKbps, samples, sampleSeconds, total);
    }

    /// <summary>Source time of a position on the kept timeline (segments joined).</summary>
    private static double At(IReadOnlyList<(double Start, double End)> parts, double t)
    {
        foreach (var (s, e) in parts)
        {
            if (t <= e - s) return s + t;
            t -= e - s;
        }
        return parts[^1].End;
    }
}
