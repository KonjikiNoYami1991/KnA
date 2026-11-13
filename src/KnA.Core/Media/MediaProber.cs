using KnA.Core.Processing;

namespace KnA.Core.Media;

public sealed class MediaProber(string ffprobePath, string? vspipePath = null)
{
    public static MediaProber For(Tools.FfmpegTools tools) => new(tools.Ffprobe, tools.VsPipe);

    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    /// <summary>Extra demuxer options for transport streams (late streams, broken timestamps, unknown PIDs).</summary>
    public static IReadOnlyList<string> RobustInputArgs(string path) =>
        IsTransportLike(path) ? ["-analyzeduration", "100M", "-probesize", "100M", "-fflags", "+genpts+discardcorrupt", "-err_detect", "ignore_err"] : [];

    public static bool IsTransportLike(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".ts" or ".m2ts" or ".mts" or ".tp" or ".trp" or ".vob" or ".mpg" or ".mpeg" or ".m2t";

    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken ct = default, bool requireVideo = true)
    {
        if (Frameserver.IsVapourSynth(path))
        {
            if (vspipePath is null) throw new InvalidOperationException(Localization.L.T("vspipe was not found: install VapourSynth (or set KNA_VSPIPE)."));
            return await Frameserver.ProbeVapourSynthAsync(vspipePath, path, ct);
        }
        var args = new List<string> { "-v", "error" };
        if (IsTransportLike(path)) args.AddRange(["-analyzeduration", "100M", "-probesize", "100M"]);
        var input = Discs.DiscSource.ProbeArgs(path);
        args.AddRange(input.Take(input.Count - 1));
        args.AddRange(["-print_format", "json", "-show_format", "-show_streams", "-show_chapters", input[^1]]);
        var result = await ProcessRunner.RunAsync(ffprobePath, args, timeout: Timeout, ct: ct);
        if (result.ExitCode != 0)
        {
            if (Frameserver.KindOf(path) == FrameserverKind.AviSynth)
                throw new InvalidOperationException(Localization.L.T("This FFmpeg cannot read AviSynth scripts (it needs AviSynth+ installed and an FFmpeg built with --enable-avisynth): {0}", result.StdErr.Trim()));
            throw new InvalidOperationException($"ffprobe failed ({result.ExitCode}): {result.StdErr.Trim()}");
        }
        // Through the concat fallback the stream ids (PIDs) are lost: read them from the first clip.
        Dictionary<int, int>? pids = null;
        if (Discs.DiscSource.FirstClip(path) is { } clip)
        {
            var ids = await ProcessRunner.RunAsync(ffprobePath, ["-v", "error", "-show_entries", "stream=index,id", "-of", "csv=p=0", clip], timeout: Timeout, ct: ct);
            pids = ids.StdOut.Split('\n').Select(l => l.Trim().Split(',')).Where(p => p.Length == 2 && p[1].StartsWith("0x", StringComparison.Ordinal))
                .DistinctBy(p => p[0])
                .ToDictionary(p => int.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture),
                              p => int.Parse(p[1][2..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture));
        }
        var info = FfprobeParser.Parse(path, result.StdOut, pids);
        if (requireVideo && info.PrimaryVideo is null)
            throw new InvalidOperationException(Localization.L.T("No video stream found."));

        // HDR10 mastering metadata and HDR10+ live in frame side data: read the first video frame only.
        if (info.PrimaryVideo is { } v && (v.ColorTransfer is "smpte2084" or "arib-std-b67" || v.Hdr.IsDolbyVision))
        {
            try
            {
                var frames = await ProcessRunner.RunAsync(ffprobePath,
                    ["-v", "error", "-select_streams", v.Index.ToString(System.Globalization.CultureInfo.InvariantCulture),"-read_intervals", "%+#1",
                     "-show_frames", "-show_entries", "frame=side_data_list", "-print_format", "json", .. Discs.DiscSource.ProbeArgs(path)],
                    timeout: TimeSpan.FromSeconds(60), ct: ct);
                if (frames.ExitCode == 0) FfprobeParser.ApplyFrameSideData(info, frames.StdOut);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* best effort */ }
        }
        return info;
    }
}
