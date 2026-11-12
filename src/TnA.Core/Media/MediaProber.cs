using TnA.Core.Processing;

namespace TnA.Core.Media;

public sealed class MediaProber(string ffprobePath)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    /// <summary>Extra demuxer options for transport streams (late streams, broken timestamps, unknown PIDs).</summary>
    public static IReadOnlyList<string> RobustInputArgs(string path) =>
        IsTransportLike(path) ? ["-analyzeduration", "100M", "-probesize", "100M", "-fflags", "+genpts+discardcorrupt", "-err_detect", "ignore_err"] : [];

    public static bool IsTransportLike(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".ts" or ".m2ts" or ".mts" or ".tp" or ".trp" or ".vob" or ".mpg" or ".mpeg" or ".m2t";

    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken ct = default, bool requireVideo = true)
    {
        var args = new List<string> { "-v", "error" };
        if (IsTransportLike(path)) args.AddRange(["-analyzeduration", "100M", "-probesize", "100M"]);
        args.AddRange(["-print_format", "json", "-show_format", "-show_streams", "-show_chapters", path]);
        var result = await ProcessRunner.RunAsync(ffprobePath, args, timeout: Timeout, ct: ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"ffprobe failed ({result.ExitCode}): {result.StdErr.Trim()}");
        var info = FfprobeParser.Parse(path, result.StdOut);
        if (requireVideo && info.PrimaryVideo is null)
            throw new InvalidOperationException(Localization.L.T("No video stream found."));

        // HDR10 mastering metadata and HDR10+ live in frame side data: read the first video frame only.
        if (info.PrimaryVideo is { } v && (v.ColorTransfer is "smpte2084" or "arib-std-b67" || v.Hdr.IsDolbyVision))
        {
            try
            {
                var frames = await ProcessRunner.RunAsync(ffprobePath,
                    ["-v", "error", "-select_streams", v.Index.ToString(System.Globalization.CultureInfo.InvariantCulture),"-read_intervals", "%+#1",
                     "-show_frames", "-show_entries", "frame=side_data_list", "-print_format", "json", path],
                    timeout: TimeSpan.FromSeconds(60), ct: ct);
                if (frames.ExitCode == 0) FfprobeParser.ApplyFrameSideData(info, frames.StdOut);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* best effort */ }
        }
        return info;
    }
}
