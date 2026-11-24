using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using KnA.Core.Encoding;
using KnA.Core.Localization;
using KnA.Core.Media;
using KnA.Core.Processing;

namespace KnA.Core.Queue;

/// <summary>
/// Dynamic HDR metadata of a job, done by itself (StaxRip's logic, none of its manual steps): the Dolby Vision RPU and
/// the HDR10+ JSON are extracted from the source (dovi_tool / hdr10plus_tool), edited so they describe exactly the
/// frames of the output (a trim keeps only the kept frames, a crop resets the RPU's active area), then given to the
/// encoder or injected into the finished HEVC stream and remuxed.
/// </summary>
public static partial class HdrMetadata
{
    /// <summary>Extracts and edits the side files the plan needs.</summary>
    public static async Task<HdrSideFiles> PrepareAsync(QueueJob job, HdrPlan hdr, string workDir, RunnerContext ctx, CancellationToken ct)
    {
        var v = job.Media!.PrimaryVideo!;
        string? rpu = null, rpuUncut = null, json = null;
        if (hdr.UseDolbyVisionRpu && ctx.Tools.DoviTool is { } dovi)
        {
            rpuUncut = Path.Combine(workDir, "RPU.bin");
            // Mode 2 converts profile 7 (dual layer) and keeps 8.x as 8.1; mode 4 makes 8.4 for an HLG base; profile 5
            // stays as it is. -c (crop) sets the active area offsets to 0: the bars were cut away.
            var args = new List<string>();
            switch (hdr.DolbyVisionProfile)
            {
                case "5.0" or "10.0": break;
                case "8.4" or "10.4": args.AddRange(["-m", "4"]); break;
                default: args.AddRange(["-m", "2"]); break;
            }
            if (hdr.RpuCrop) args.Add("-c");
            args.AddRange(["extract-rpu", "-", "-o", rpuUncut]);
            await JobRunner.PipeVideoAsync(ctx, job.SourcePath, v.Index, dovi, args, ct);
            job.AppendLog("Dolby Vision RPU extracted" + (hdr.RpuCrop ? " (active area reset for the crop)." : "."));
            rpu = rpuUncut;
            if (hdr.KeptSeconds is { } kept && await RpuCountAsync(ctx, dovi, rpuUncut, ct) is > 0 and var total
                && RemoveRanges(kept, hdr.FrameRate, total) is { Count: > 0 } remove)
            {
                rpu = Path.Combine(workDir, "RPU.kept.bin");
                await EditAsync(ctx, dovi, ["editor", "-i", rpuUncut, "-j", WriteEdits(workDir, "rpu-edit.json", remove, null), "-o", rpu], ct);
                job.AppendLog($"Dolby Vision RPU: only the kept frames ({string.Join(", ", remove.Select(r => "-" + r))} removed of {total}).");
            }
        }
        if (hdr.UseHdr10PlusJson && ctx.Tools.Hdr10PlusTool is { } h10)
        {
            var full = Path.Combine(workDir, "hdr10plus.json");
            await JobRunner.PipeVideoAsync(ctx, job.SourcePath, v.Index, h10, ["extract", "-", "-o", full], ct);
            job.AppendLog("HDR10+ metadata extracted.");
            json = full;
            if (hdr.KeptSeconds is { } kept && JsonCount(full) is > 0 and var total
                && RemoveRanges(kept, hdr.FrameRate, total) is { Count: > 0 } remove)
            {
                json = Path.Combine(workDir, "hdr10plus.kept.json");
                await EditAsync(ctx, h10, ["editor", full, "-j", WriteEdits(workDir, "hdr10plus-edit.json", remove, null), "-o", json], ct);
                job.AppendLog("HDR10+ metadata: only the kept frames.");
            }
        }
        return new HdrSideFiles(rpu, json, rpuUncut);
    }

    /// <summary>
    /// Frame ranges to remove ("a-b", inclusive) so that only the frames inside <paramref name="kept"/> (seconds of the
    /// video's own timeline) remain, of <paramref name="total"/> frames at <paramref name="fps"/>. Empty = keep all.
    /// A frame stays when its time is at or after a part's start and before its end (FFmpeg's -ss / -t).
    /// </summary>
    public static List<string> RemoveRanges(IReadOnlyList<(double Start, double End)> kept, double fps, long total)
    {
        var result = new List<string>();
        if (fps <= 0 || total <= 0 || kept.Count == 0) return result;
        long next = 0;   // first frame not yet decided
        foreach (var (a, b) in kept.OrderBy(k => k.Start))
        {
            long first = Math.Clamp((long)Math.Ceiling(a * fps - 1e-3), 0, total);
            long end = Math.Clamp((long)Math.Ceiling(b * fps - 1e-3), 0, total);   // exclusive
            if (end <= first) continue;
            if (first > next) result.Add($"{next.ToString(CultureInfo.InvariantCulture)}-{(first - 1).ToString(CultureInfo.InvariantCulture)}");
            next = Math.Max(next, end);
        }
        if (next < total) result.Add($"{next.ToString(CultureInfo.InvariantCulture)}-{(total - 1).ToString(CultureInfo.InvariantCulture)}");
        return result;
    }

    /// <summary>Number of frames kept by <see cref="RemoveRanges"/>.</summary>
    public static long KeptCount(IReadOnlyList<string> remove, long total) =>
        total - remove.Sum(r => { var p = r.Split('-'); return long.Parse(p[1], CultureInfo.InvariantCulture) - long.Parse(p[0], CultureInfo.InvariantCulture) + 1; });

    /// <summary>
    /// Finishes the HDR of an encoded file (HEVC): injects the edited RPU / HDR10+ JSON the encoder couldn't take
    /// (FFmpeg's GPU encoders, chunked or multi-part encodes, HDR10+ with rigaya's encoders) and gives a Dolby Vision
    /// video the container's configuration record FFmpeg never writes. The video is remuxed by mkvmerge with the
    /// timestamps of every frame read from the encoded file (FFmpeg can't time a raw HEVC stream), then MP4/MOV/TS get it
    /// back from FFmpeg (-strict unofficial keeps the record). Frame counts are checked first: on a mismatch nothing is
    /// injected and the log says so (static HDR stays).
    /// </summary>
    public static async Task<List<string>> FinishAsync(QueueJob job, CompiledJob final, HdrSideFiles files, string workDir, RunnerContext ctx, CancellationToken ct)
    {
        var warnings = new List<string>();
        var hdr = final.Hdr;
        string output = final.Plan.OutputPath;
        string container = final.Container;
        string? rpu = hdr.DolbyVision == HdrCarry.Inject && ctx.Tools.DoviTool is not null ? files.DolbyVisionRpu : null;
        string? json = hdr.Hdr10Plus == HdrCarry.Inject && ctx.Tools.Hdr10PlusTool is not null ? files.Hdr10PlusJson : null;
        bool dv = hdr.DolbyVision != HdrCarry.None && container is "mkv" or "mp4" or "mov";
        if (rpu is null && json is null && !dv) return warnings;

        var info = await ProbeVideoAsync(ctx, output, ct);
        // Already there (NVEncC/QSVEncC/VCEEncC write it themselves): nothing to do for Dolby Vision.
        if (rpu is null && json is null && info.HasDoviRecord) return warnings;
        if (ctx.Tools.Mkvmerge is not { } mkvmerge)
        {
            warnings.Add(rpu is not null || json is not null
                ? L.T("mkvmerge is missing: Dolby Vision / HDR10+ could not be added after encoding, static HDR is kept.")
                : L.T("mkvmerge is missing: the file carries the Dolby Vision RPU without the configuration record (some TVs and players won't see Dolby Vision)."));
            return warnings;
        }

        job.BeginStage(L.T("Adding HDR metadata"));
        ctx.Changed(job);
        if (info.Timestamps.Count == 0)
        {
            warnings.Add(L.T("The encoded video could not be counted: Dolby Vision / HDR10+ were not added, static HDR is kept."));
            return warnings;
        }
        long frames = info.Timestamps.Count;
        // Match the metadata to the encoded frames: a frame or two of rounding at a trim is fixed, more is a real mismatch.
        if (rpu is not null)
        {
            long n = await RpuCountAsync(ctx, ctx.Tools.DoviTool!, rpu, ct);
            rpu = await FitAsync(ctx, ctx.Tools.DoviTool!, rpu, n, frames, workDir, "rpu", true, ct);
            if (rpu is null) warnings.Add(L.T("Dolby Vision: the RPU has {0} frames, the video {1}: not added, static HDR is kept.", n, frames));
        }
        if (json is not null)
        {
            long n = JsonCount(json);
            json = await FitAsync(ctx, ctx.Tools.Hdr10PlusTool!, json, n, frames, workDir, "hdr10plus", false, ct);
            if (json is null) warnings.Add(L.T("HDR10+: the metadata has {0} frames, the video {1}: not added, static HDR10 is kept.", n, frames));
        }
        // The record only when the stream really carries an RPU (injected now, or given to the encoder).
        bool record = dv && !info.HasDoviRecord && (rpu is not null || (hdr.DolbyVision == HdrCarry.Encoder && files.DolbyVisionRpu is not null));
        if (rpu is null && json is null && !record) return warnings;

        var stream = Path.Combine(workDir, "finish.hevc");
        await RunOrThrowAsync(ctx, ctx.Tools.Ffmpeg, ["-hide_banner", "-nostdin", "-v", "error", "-i", output, "-map", "0:v:0", "-c:v", "copy",
            "-bsf:v", "hevc_mp4toannexb", "-f", "hevc", "-y", stream], ct);
        try
        {
            if (json is not null)
            {
                var next = Path.Combine(workDir, "finish.h10.hevc");
                await RunOrThrowAsync(ctx, ctx.Tools.Hdr10PlusTool!, ["inject", "-i", stream, "-j", json, "-o", next], ct);
                TryDelete(stream);
                stream = next;
                job.AppendLog("HDR10+ metadata injected into the video.");
            }
            if (rpu is not null)
            {
                var next = Path.Combine(workDir, "finish.dv.hevc");
                await RunOrThrowAsync(ctx, ctx.Tools.DoviTool!, ["inject-rpu", "-i", stream, "--rpu-in", rpu, "-o", next], ct);
                TryDelete(stream);
                stream = next;
                job.AppendLog($"Dolby Vision RPU injected into the video (profile {hdr.DolbyVisionProfile}).");
            }
            var ts = Path.Combine(workDir, "finish.timestamps.txt");
            await File.WriteAllTextAsync(ts, TimestampsV2(info.Timestamps), ct);
            await RemuxAsync(job, ctx, mkvmerge, output, stream, ts, container, hdr, info, workDir, ct);
            if (dv) job.AppendLog("Dolby Vision configuration record written (mkvmerge).");
        }
        finally { TryDelete(stream); }
        return warnings;
    }

    /// <summary>
    /// mkvmerge's timestamp file (format v2): one line per frame in display order, milliseconds — the encoded file's own
    /// times, so the start offset, a variable frame rate and the sync with audio stay exactly as they were.
    /// </summary>
    public static string TimestampsV2(IReadOnlyList<double> seconds) =>
        "# timestamp format v2\n" + string.Concat(seconds.Order().Select(t => (t * 1000).ToString("0.######", CultureInfo.InvariantCulture) + "\n"));

    /// <summary>
    /// The new video (raw HEVC + timestamps) in place of the old one: MKV in one mkvmerge pass that keeps every other
    /// track, tag, chapter and attachment of the file; MP4/MOV/TS through a Matroska video then FFmpeg (stream copy).
    /// </summary>
    private static async Task RemuxAsync(QueueJob job, RunnerContext ctx, string mkvmerge, string output, string stream, string timestamps,
        string container, HdrPlan hdr, VideoCount info, string workDir, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetDirectoryName(output)!, "." + Path.GetFileNameWithoutExtension(output) + ".kna-hdr" + Path.GetExtension(output));
        var video = MkvmergeVideoArgs(hdr, info, timestamps);
        try
        {
            if (container == "mkv")
            {
                var args = new List<string> { "-q", "-o", tmp };
                args.AddRange(video);
                args.AddRange([stream, "--no-video", output]);
                job.AppendLog("Remux (mkvmerge): " + new EncodePlan(args, tmp).CommandLine(mkvmerge));
                await RunMkvmergeAsync(mkvmerge, args, ct);
            }
            else
            {
                var mkv = Path.Combine(workDir, "finish.video.mkv");
                var args = new List<string> { "-q", "-o", mkv };
                args.AddRange(video);
                args.Add(stream);
                job.AppendLog("Video (mkvmerge): " + new EncodePlan(args, mkv).CommandLine(mkvmerge));
                await RunMkvmergeAsync(mkvmerge, args, ct);
                // -copyts: the encoded file's own times (a start offset, the audio sync) stay exactly as they were.
                var mux = new List<string> { "-hide_banner", "-nostdin", "-v", "error", "-y", "-copyts", "-i", mkv, "-i", output,
                    "-map", "0:v:0", "-map", "1", "-map", "-1:v:0", "-c", "copy", "-map_metadata", "1", "-map_chapters", "1" };
                switch (container)
                {
                    case "mp4" or "mov": mux.AddRange(["-tag:v", "hvc1", "-movflags", "+faststart", "-strict", "unofficial"]); break;
                    case "ts": mux.AddRange(["-f", "mpegts"]); break;
                }
                mux.Add(tmp);
                job.AppendLog("Remux (FFmpeg): " + new EncodePlan(mux, tmp).CommandLine(ctx.Tools.Ffmpeg));
                try { await RunOrThrowAsync(ctx, ctx.Tools.Ffmpeg, mux, ct); }
                finally { TryDelete(mkv); }
            }
            File.Move(tmp, output, overwrite: true);
        }
        finally { TryDelete(tmp); }
    }

    /// <summary>mkvmerge options of the video track: its times, language, name and the static HDR10 metadata of the container.</summary>
    internal static List<string> MkvmergeVideoArgs(HdrPlan hdr, VideoCount info, string timestamps)
    {
        static string N(double v) => v.ToString("0.#####", CultureInfo.InvariantCulture);
        var a = new List<string> { "--timestamps", "0:" + timestamps };
        if (info.Language is { Length: > 0 } lang && lang != "und") a.AddRange(["--language", "0:" + lang]);
        if (info.Title is { Length: > 0 } title) a.AddRange(["--track-name", "0:" + title]);
        if (hdr.MaxCll is > 0 and var cll) a.AddRange(["--max-content-light", "0:" + cll.ToString(CultureInfo.InvariantCulture)]);
        if (hdr.MaxFall is > 0 and var fall) a.AddRange(["--max-frame-light", "0:" + fall.ToString(CultureInfo.InvariantCulture)]);
        if (hdr.Mastering is { } md)
        {
            a.AddRange(["--chromaticity-coordinates", $"0:{N(md.RedX)},{N(md.RedY)},{N(md.GreenX)},{N(md.GreenY)},{N(md.BlueX)},{N(md.BlueY)}"]);
            a.AddRange(["--white-colour-coordinates", $"0:{N(md.WhiteX)},{N(md.WhiteY)}"]);
            a.AddRange(["--max-luminance", "0:" + N(md.MaxLuminance), "--min-luminance", "0:" + N(md.MinLuminance)]);
        }
        return a;
    }

    /// <summary>mkvmerge exits with 1 for warnings (the file is fine), 2 for errors.</summary>
    private static async Task RunMkvmergeAsync(string mkvmerge, IReadOnlyList<string> args, CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync(mkvmerge, args, timeout: TimeSpan.FromHours(4), ct: ct);
        if (r.ExitCode is not (0 or 1))
            throw new InvalidOperationException(L.T("{0} failed: {1}", "mkvmerge", (r.StdOut + r.StdErr).Trim()));
    }

    /// <summary>
    /// The metadata file with exactly <paramref name="frames"/> entries: extra ones at the end removed, one to three
    /// missing ones repeated from the last; null when the difference is larger (wrong alignment).
    /// </summary>
    private static async Task<string?> FitAsync(RunnerContext ctx, string tool, string file, long count, long frames, string workDir, string name,
        bool dovi, CancellationToken ct)
    {
        if (count == frames) return file;
        if (count <= 0 || Math.Abs(count - frames) > 3) return null;
        var edits = count > frames
            ? WriteEdits(workDir, name + "-fit.json", [$"{frames.ToString(CultureInfo.InvariantCulture)}-{(count - 1).ToString(CultureInfo.InvariantCulture)}"], null)
            : WriteEdits(workDir, name + "-fit.json", null, (count - 1, count, frames - count));
        var outFile = Path.Combine(workDir, name + ".fit" + Path.GetExtension(file));
        await EditAsync(ctx, tool, dovi ? ["editor", "-i", file, "-j", edits, "-o", outFile] : ["editor", file, "-j", edits, "-o", outFile], ct);
        return outFile;
    }

    private static string WriteEdits(string workDir, string name, IReadOnlyList<string>? remove, (long Source, long Offset, long Length)? duplicate)
    {
        var path = Path.Combine(workDir, name);
        var o = new Dictionary<string, object>();
        if (remove is { Count: > 0 }) o["remove"] = remove;
        if (duplicate is { } d) o["duplicate"] = new[] { new { source = d.Source, offset = d.Offset, length = d.Length } };
        File.WriteAllText(path, JsonSerializer.Serialize(o));
        return path;
    }

    private static Task EditAsync(RunnerContext ctx, string tool, IReadOnlyList<string> args, CancellationToken ct) => RunOrThrowAsync(ctx, tool, args, ct);

    private static async Task RunOrThrowAsync(RunnerContext ctx, string exe, IReadOnlyList<string> args, CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync(exe, args, timeout: TimeSpan.FromHours(4), ct: ct);
        if (r.ExitCode != 0)
            throw new InvalidOperationException(L.T("{0} failed: {1}", Path.GetFileNameWithoutExtension(exe), (r.StdErr + r.StdOut).Trim()));
    }

    [GeneratedRegex(@"Frames:\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FramesLine();

    /// <summary>Frames in an RPU file ("dovi_tool info --summary"), 0 when unknown.</summary>
    private static async Task<long> RpuCountAsync(RunnerContext ctx, string dovi, string rpu, CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync(dovi, ["info", "-i", rpu, "--summary"], timeout: TimeSpan.FromMinutes(10), ct: ct);
        return ParseRpuCount(r.StdOut + "\n" + r.StdErr);
    }

    internal static long ParseRpuCount(string summary) =>
        FramesLine().Match(summary) is { Success: true } m ? long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;

    /// <summary>Frames in an HDR10+ JSON (one SceneInfo entry each), 0 when unknown.</summary>
    internal static long JsonCount(string path)
    {
        try
        {
            using var s = File.OpenRead(path);
            using var doc = JsonDocument.Parse(s);
            return doc.RootElement.TryGetProperty("SceneInfo", out var si) && si.ValueKind == JsonValueKind.Array ? si.GetArrayLength() : 0;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary>The encoded video: every frame's time, its language and name, and whether it has a Dolby Vision record.</summary>
    internal sealed record VideoCount(IReadOnlyList<double> Timestamps, string? Language, string? Title, bool HasDoviRecord);

    private static async Task<VideoCount> ProbeVideoAsync(RunnerContext ctx, string file, CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync(ctx.Tools.Ffprobe, ["-v", "error", "-select_streams", "v:0",
            "-show_entries", "stream_tags=language,title:stream_side_data=side_data_type", "-of", "json", file], timeout: TimeSpan.FromMinutes(2), ct: ct);
        string? lang = null, title = null;
        bool record = false;
        try
        {
            using var doc = JsonDocument.Parse(r.StdOut);
            if (doc.RootElement.TryGetProperty("streams", out var ss) && ss.GetArrayLength() > 0)
            {
                var st = ss[0];
                if (st.TryGetProperty("tags", out var tags))
                {
                    if (tags.TryGetProperty("language", out var l)) lang = l.GetString();
                    if (tags.TryGetProperty("title", out var t)) title = t.GetString();
                }
                if (st.TryGetProperty("side_data_list", out var sd))
                    record = sd.EnumerateArray().Any(x => x.TryGetProperty("side_data_type", out var ty) && ty.GetString()?.Contains("DOVI configuration", StringComparison.OrdinalIgnoreCase) == true);
            }
        }
        catch (JsonException) { }
        if (!record) record = r.StdOut.Contains("DOVI configuration record", StringComparison.OrdinalIgnoreCase);
        // Every frame's presentation time (demuxing only: fast even for a whole film).
        var p = await ProcessRunner.RunAsync(ctx.Tools.Ffprobe, ["-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time",
            "-of", "csv=p=0", file], timeout: TimeSpan.FromHours(1), ct: ct);
        var times = new List<double>();
        foreach (var line in p.StdOut.Split('\n'))
            if (double.TryParse(line.Trim().TrimEnd(','), NumberStyles.Float, CultureInfo.InvariantCulture, out var t)) times.Add(t);
        return new VideoCount(times, lang, title, record);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
