using System.Globalization;
using System.Text.Json;
using KnA.Core.Encoding;
using KnA.Core.Localization;
using KnA.Core.Media;
using KnA.Core.Processing;

namespace KnA.Core.Queue;

/// <summary>
/// Single-pass encode that survives a stop, a crash or a restart, at no extra cost while it runs:
///  • one FFmpeg encodes the whole video (same command as a segment of <see cref="ChunkedEncoder"/>: exact seek,
///    -copyts, trim=) but writes it as pieces of <see cref="PieceSeconds"/> (segment muxer, an IDR forced at every cut);
///  • the segment muxer lists every finished piece in a CSV: after a stop only the unfinished piece is lost and the
///    next run starts exactly at the end of the last finished one (frame-exact thanks to trim=);
///  • at the end the pieces are joined without re-encoding and muxed with audio, subtitles and chapters.
/// Used automatically for video encodes when the encoder supports it; never for dynamic HDR metadata
/// (Dolby Vision RPU / HDR10+ files are per-frame and would lose their alignment).
/// </summary>
public static class ResumableEncoder
{
    /// <summary>Length of a piece (seconds); settable for tests.</summary>
    public static double PieceSeconds { get; set; } = 60;

    private sealed record Piece(string File, double Seconds);
    private sealed record State(string Key, double Start, double End, List<Piece> Pieces);

    public static bool CanUse(QueueJob job, CompiledJob final, Settings.AppSettings settings) =>
        settings.ResumableEncoding
        && job.Settings.ProfileDefinition.EncodesVideo
        // Disc titles are read through playlists (no reliable seeking) and need a known duration.
        && !KnA.Core.Media.Discs.DiscSource.IsTitlePath(job.SourcePath)
        && job.Media is { Duration.TotalSeconds: > 2 }
        && !job.Settings.Chunks.Enabled
        && !job.Edit.IsMultiSegment
        && !final.Hdr.UseDolbyVisionRpu && !final.Hdr.UseHdr10PlusJson
        && final.Tracks.FirstOrDefault(t => t.Include && t.Kind == "video") is { CopyVideo: false }
        && CommandBuilder.ResumableEncoder(CommandBuilder.ResolvedEncoder(final.Plan.Arguments));

    /// <summary>The folder of a job's pieces (data folder, kept until the job succeeds or the job changes).</summary>
    public static string Folder(QueueJob job, Settings.SettingsStore store) =>
        Path.Combine(WorkFolders.Chunks(job, store), "r-" + ChunkedEncoder.Key(job)[..16]);

    /// <summary>Seconds already encoded by an earlier, interrupted run (0 = none).</summary>
    public static double ResumablePart(QueueJob job, Settings.SettingsStore store)
    {
        var path = Path.Combine(Folder(job, store), "state.json");
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<State>(File.ReadAllText(path)) is { } st && st.Key == ChunkedEncoder.Key(job))
                return st.Pieces.Sum(p => p.Seconds);
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return 0;
    }

    public static async Task<int> RunAsync(QueueJob job, RunnerContext ctx, CompileContext baseCtx, CompiledJob final, string workDir, CancellationToken ct)
    {
        var media = job.Media!;
        var videoTrack = final.Tracks.First(t => t.Include && t.Kind == "video");
        double delay = videoTrack.DelaySeconds;
        double videoDuration = (job.ExternalMedia.GetValueOrDefault(videoTrack.File) ?? media).Duration.TotalSeconds;
        double start = Math.Max(job.Edit.TrimStart ?? 0, Math.Max(0, delay));
        double end = Math.Min(job.Edit.TrimEnd is > 0 and var te ? te : double.MaxValue, videoDuration + delay);

        var key = ChunkedEncoder.Key(job);
        var dir = Folder(job, ctx.Store);
        Directory.CreateDirectory(dir);
        var statePath = Path.Combine(dir, "state.json");
        State state;
        try
        {
            state = File.Exists(statePath) && JsonSerializer.Deserialize<State>(File.ReadAllText(statePath)) is { } st && st.Key == key
                ? st with { Pieces = st.Pieces.TakeWhile(p => File.Exists(p.File)).ToList() }
                : Fresh();
        }
        catch (JsonException) { state = Fresh(); }
        State Fresh()
        {
            foreach (var f in Directory.GetFiles(dir)) TryDelete(f);
            return new State(key, start, end, []);
        }

        double done = state.Pieces.Sum(p => p.Seconds);
        double resumeAt = state.Start + done;
        if (done > 0)
            job.AppendLog($"Resuming: {state.Pieces.Count} piece(s), {done:0.###} s already encoded, continuing at {resumeAt:0.###} s.");
        Save(statePath, state);

        // Audio, subtitles and chapters are made meanwhile (they read the source once more, but alongside the
        // slower video encode instead of after it), and kept across resumes.
        var restTask = ChunkedEncoder.BuildRestAsync(job, ctx, baseCtx, dir, workDir, ct);
        if (state.End - resumeAt > 0.05)
        {
            int run = Directory.GetFiles(dir, "pieces-*.csv").Length + 1;
            var list = Path.Combine(dir, $"pieces-{run:000}.csv");
            var compiled = CommandBuilder.Compile(baseCtx with
            {
                Chunk = new ChunkRequest(resumeAt, state.End)
                {
                    SegmentSeconds = PieceSeconds, SegmentStartNumber = state.Pieces.Count, SegmentList = list,
                },
                ForcedOutputPath = Path.Combine(dir, "piece_%05d.mkv"),
            });
            if (compiled.HasErrors) throw new InvalidOperationException(compiled.Issues.First(x => x.IsError).Message);
            job.AppendLog("Command (resumable): " + compiled.Plan.CommandLine(ctx.Tools.Ffmpeg));
            job.BeginStage(done > 0 ? L.T("Encoding (resumed at {0})", TimeSpan.FromSeconds(done).ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)) : L.T("Encoding"));
            ctx.Changed(job);

            // Progress over the whole video, not only this run.
            double total = state.End - state.Start;
            var parser = new ProgressParser(compiled.OutputDuration);
            var errors = new System.Text.StringBuilder();
            int exit = -1;
            try
            {
                exit = await JobRunner.RunStreamingAsync(ctx.Tools.Ffmpeg, compiled.Plan.Arguments, workDir, ctx.Processes, ctx.Settings.Priority,
                    compiled.Pipe, ctx.Tools.VsPipe,
                    line =>
                    {
                        if (parser.Feed(line) is not { } pr) return;
                        job.Progress = pr with { Position = pr.Position + TimeSpan.FromSeconds(done), Duration = TimeSpan.FromSeconds(total) };
                        ctx.Changed(job);
                    },
                    line => { lock (errors) { if (errors.Length < 40000) errors.AppendLine(line); } }, ct);
            }
            finally
            {
                // Stopped, crashed or finished: keep every piece the segment muxer reported as complete. After an
                // interruption the next run starts where they end, so their length must be exact: the list's times
                // are shifted by the encoder's B-frame delay, the packets are not.
                var finished = ReadList(list, dir);
                if (exit != 0)
                    for (int i = 0; i < finished.Count; i++)
                        if (await ExactSecondsAsync(ctx.Tools.Ffprobe, finished[i].File) is { } exact) finished[i] = finished[i] with { Seconds = exact };
                Save(statePath, state = state with { Pieces = [.. state.Pieces, .. finished] });
            }
            if (exit != 0)
            {
                job.AppendLog(errors.ToString());
                try { await restTask; } catch (Exception) { /* the encode error is the one to report */ }
                return exit;
            }
        }

        var pieces = state.Pieces.Select(p => p.File).ToList();
        if (pieces.Count == 0) throw new InvalidOperationException(L.T("FFmpeg finished but the output file is empty."));
        int code = await ChunkedEncoder.AssembleAsync(job, ctx, baseCtx, final, dir, pieces, state.Start, workDir, ct, restTask);
        if (code == 0)
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
        return code;
    }

    /// <summary>Complete pieces of one run, from the segment muxer's CSV ("file,start,end").</summary>
    private static List<Piece> ReadList(string list, string dir)
    {
        var result = new List<Piece>();
        if (!File.Exists(list)) return result;
        foreach (var line in File.ReadAllLines(list))
        {
            var c = line.Split(',');
            if (c.Length < 3) continue;
            if (!double.TryParse(c[^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
                || !double.TryParse(c[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b) || b <= a) continue;
            var name = string.Join(',', c[..^2]).Trim('"');
            var file = Path.IsPathRooted(name) ? name : Path.Combine(dir, name);
            if (File.Exists(file)) result.Add(new Piece(file, b - a));
        }
        return result;
    }

    /// <summary>Time span covered by a piece's video frames: last pts + its duration − first pts.</summary>
    private static async Task<double?> ExactSecondsAsync(string ffprobe, string file)
    {
        try
        {
            var r = await ProcessRunner.RunAsync(ffprobe, ["-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time,duration_time",
                "-of", "csv=p=0", file], timeout: TimeSpan.FromSeconds(60));
            var rows = r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim().TrimEnd(',').Split(','))
                .Select(c => (Pts: Num(c.ElementAtOrDefault(0)), Dur: Num(c.ElementAtOrDefault(1))))
                .Where(x => x.Pts is not null).ToList();
            if (rows.Count == 0) return null;
            var sorted = rows.Select(x => x.Pts!.Value).Order().ToList();
            double frame = rows.Select(x => x.Dur).FirstOrDefault(d => d is > 0)
                           ?? (sorted.Count > 1 ? sorted.Zip(sorted.Skip(1), (a, b) => b - a).Where(d => d > 0).DefaultIfEmpty(0).Min() : 0);
            return sorted[^1] + frame - sorted[0];
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }

        static double? Num(string? v) => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    private static void Save(string path, State st)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(st));
        File.Move(tmp, path, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
