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
    /// Injects the edited RPU / HDR10+ JSON into the video of the finished file (HEVC) and remuxes it in place, for the
    /// encoders that can't take them (FFmpeg's GPU encoders, chunked or multi-part encodes, HDR10+ with rigaya's
    /// encoders). When the frame counts don't match, nothing is injected and the log says so (static HDR stays).
    /// </summary>
    public static async Task<List<string>> InjectAsync(QueueJob job, CompiledJob final, HdrSideFiles files, string workDir, RunnerContext ctx, CancellationToken ct)
    {
        var warnings = new List<string>();
        var hdr = final.Hdr;
        string output = final.Plan.OutputPath;
        string? rpu = hdr.DolbyVision == HdrCarry.Inject && ctx.Tools.DoviTool is not null ? files.DolbyVisionRpu : null;
        string? json = hdr.Hdr10Plus == HdrCarry.Inject && ctx.Tools.Hdr10PlusTool is not null ? files.Hdr10PlusJson : null;
        if (rpu is null && json is null) return warnings;

        job.BeginStage(L.T("Adding HDR metadata"));
        ctx.Changed(job);
        var info = await ProbeVideoAsync(ctx, output, ct);
        if (info.Frames <= 0)
        {
            warnings.Add(L.T("The encoded video could not be counted: Dolby Vision / HDR10+ were not added, static HDR is kept."));
            return warnings;
        }
        // Match the metadata to the encoded frames: a frame or two of rounding at a trim is fixed, more is a real mismatch.
        if (rpu is not null)
        {
            long n = await RpuCountAsync(ctx, ctx.Tools.DoviTool!, rpu, ct);
            rpu = await FitAsync(ctx, ctx.Tools.DoviTool!, rpu, n, info.Frames, workDir, "rpu", true, ct);
            if (rpu is null) warnings.Add(L.T("Dolby Vision: the RPU has {0} frames, the video {1}: not added, static HDR is kept.", n, info.Frames));
        }
        if (json is not null)
        {
            long n = JsonCount(json);
            json = await FitAsync(ctx, ctx.Tools.Hdr10PlusTool!, json, n, info.Frames, workDir, "hdr10plus", false, ct);
            if (json is null) warnings.Add(L.T("HDR10+: the metadata has {0} frames, the video {1}: not added, static HDR10 is kept.", n, info.Frames));
        }
        if (rpu is null && json is null) return warnings;

        var stream = Path.Combine(workDir, "inject.hevc");
        var ffArgs = new List<string> { "-hide_banner", "-nostdin", "-v", "error", "-i", output, "-map", "0:v:0", "-c:v", "copy", "-bsf:v", "hevc_mp4toannexb", "-f", "hevc", "-y", stream };
        await RunOrThrowAsync(ctx, ctx.Tools.Ffmpeg, ffArgs, ct);
        if (json is not null)
        {
            var next = Path.Combine(workDir, "inject.h10.hevc");
            await RunOrThrowAsync(ctx, ctx.Tools.Hdr10PlusTool!, ["inject", "-i", stream, "-j", json, "-o", next], ct);
            TryDelete(stream);
            stream = next;
            job.AppendLog("HDR10+ metadata injected into the video.");
        }
        if (rpu is not null)
        {
            var next = Path.Combine(workDir, "inject.dv.hevc");
            await RunOrThrowAsync(ctx, ctx.Tools.DoviTool!, ["inject-rpu", "-i", stream, "--rpu-in", rpu, "-o", next], ct);
            TryDelete(stream);
            stream = next;
            job.AppendLog($"Dolby Vision RPU injected into the video (profile {hdr.DolbyVisionProfile}).");
        }

        // The raw stream has no timestamps: the frame rate and the video's start come from the encoded file.
        var tmp = Path.Combine(Path.GetDirectoryName(output)!, "." + Path.GetFileNameWithoutExtension(output) + ".kna-hdr" + Path.GetExtension(output));
        var mux = new List<string> { "-hide_banner", "-nostdin", "-y", "-fflags", "+genpts", "-r", info.Rate };
        if (Math.Abs(info.Start) >= 0.0005) mux.AddRange(["-itsoffset", CommandBuilder.Sec(info.Start)]);
        mux.AddRange(["-f", "hevc", "-i", stream, "-i", output, "-map", "0:v:0", "-map", "1", "-map", "-1:v:0", "-c", "copy",
            "-map_metadata", "1", "-map_chapters", "1"]);
        mux.AddRange(hdr.ColorArgs);
        switch (final.Container)
        {
            case "mp4" or "mov": mux.AddRange(["-tag:v", "hvc1", "-movflags", "+faststart", "-strict", "unofficial"]); break;
            case "ts": mux.AddRange(["-f", "mpegts"]); break;
            case "mkv": mux.AddRange(["-f", "matroska"]); break;
        }
        mux.Add(tmp);
        job.AppendLog("Remux with the HDR metadata: " + new EncodePlan(mux, tmp).CommandLine(ctx.Tools.Ffmpeg));
        try
        {
            await RunOrThrowAsync(ctx, ctx.Tools.Ffmpeg, mux, ct);
            File.Move(tmp, output, overwrite: true);
        }
        finally
        {
            TryDelete(tmp);
            TryDelete(stream);
        }
        return warnings;
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

    private sealed record VideoCount(long Frames, string Rate, double Start);

    private static async Task<VideoCount> ProbeVideoAsync(RunnerContext ctx, string file, CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync(ctx.Tools.Ffprobe, ["-v", "error", "-select_streams", "v:0", "-count_packets",
            "-show_entries", "stream=nb_read_packets,r_frame_rate,avg_frame_rate,start_time", "-of", "json", file], timeout: TimeSpan.FromHours(1), ct: ct);
        try
        {
            using var doc = JsonDocument.Parse(r.StdOut);
            var st = doc.RootElement.GetProperty("streams")[0];
            string S(string n) => st.TryGetProperty(n, out var e) ? e.GetString() ?? "" : "";
            long.TryParse(S("nb_read_packets"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var frames);
            double.TryParse(S("start_time"), NumberStyles.Float, CultureInfo.InvariantCulture, out var start);
            var rate = S("avg_frame_rate") is { Length: > 0 } a && a != "0/0" ? a : S("r_frame_rate");
            return new VideoCount(frames, rate is { Length: > 0 } && rate != "0/0" ? rate : "25", start);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
        {
            return new VideoCount(0, "25", 0);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
