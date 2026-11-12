using TnA.Core.Processing;

namespace TnA.Core.Media;

public sealed class MediaProber(string ffprobePath)
{
    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken ct = default)
    {
        var result = await ProcessRunner.RunAsync(ffprobePath,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path], ct: ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"ffprobe failed ({result.ExitCode}): {result.StdErr.Trim()}");
        var info = FfprobeParser.Parse(path, result.StdOut);
        if (info.PrimaryVideo is null)
            throw new InvalidOperationException("No video stream found.");
        return info;
    }
}
