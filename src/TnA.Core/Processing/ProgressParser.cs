using System.Globalization;

namespace TnA.Core.Processing;

public sealed record EncodeProgress(
    TimeSpan Position,
    TimeSpan Duration,
    double Fps,
    double Speed,
    long SizeBytes,
    long Frame,
    bool Finished)
{
    public double Percent => Duration > TimeSpan.Zero
        ? Math.Clamp(Position.TotalSeconds / Duration.TotalSeconds * 100.0, 0, 100) : 0;

    public TimeSpan? Remaining => Speed > 0.01 && Duration > Position
        ? TimeSpan.FromSeconds((Duration - Position).TotalSeconds / Speed) : null;

    public long? EstimatedFinalSize => Position.TotalSeconds > 2 && SizeBytes > 0
        ? (long)(SizeBytes / Position.TotalSeconds * Duration.TotalSeconds) : null;
}

/// <summary>
/// Parses the machine readable output of <c>ffmpeg -progress pipe:1</c>
/// (blocks of key=value lines terminated by <c>progress=continue|end</c>).
/// Replaces the fragile parsing of the human readable "frame=... fps=..." status line.
/// </summary>
public sealed class ProgressParser(TimeSpan duration)
{
    private readonly Dictionary<string, string> _block = new(StringComparer.Ordinal);

    public TimeSpan Duration { get; set; } = duration;

    /// <summary>Feeds one line; returns a progress snapshot when a block completes.</summary>
    public EncodeProgress? Feed(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        int eq = line.IndexOf('=');
        if (eq <= 0) return null;
        var key = line[..eq].Trim();
        var value = line[(eq + 1)..].Trim();
        _block[key] = value;
        if (key != "progress") return null;

        var snapshot = new EncodeProgress(
            Position: ReadPosition(),
            Duration: Duration,
            Fps: ReadDouble("fps"),
            Speed: ReadDouble("speed", trimSuffix: 'x'),
            SizeBytes: ReadLong("total_size"),
            Frame: ReadLong("frame"),
            Finished: value == "end");
        _block.Clear();
        return snapshot;
    }

    /// <summary>True when the line belongs to the -progress protocol (so it shouldn't be shown as log).</summary>
    public static bool IsProgressLine(string? line)
    {
        if (string.IsNullOrEmpty(line)) return false;
        int eq = line.IndexOf('=');
        if (eq <= 0) return false;
        return line.AsSpan(0, eq) switch
        {
            "frame" or "fps" or "bitrate" or "total_size" or "out_time_us" or "out_time_ms" or "out_time"
                or "dup_frames" or "drop_frames" or "speed" or "progress" => true,
            var k when k.StartsWith("stream_") => true,
            _ => false,
        };
    }

    private TimeSpan ReadPosition()
    {
        // out_time_ms is (historically) microseconds too; prefer out_time_us.
        if (_block.TryGetValue("out_time_us", out var us) && long.TryParse(us, out var micro) && micro >= 0)
            return TimeSpan.FromTicks(micro * 10);
        if (_block.TryGetValue("out_time", out var t) && TimeSpan.TryParse(t, CultureInfo.InvariantCulture, out var ts))
            return ts;
        return TimeSpan.Zero;
    }

    private double ReadDouble(string key, char? trimSuffix = null)
    {
        if (!_block.TryGetValue(key, out var v)) return 0;
        if (trimSuffix is { } c) v = v.TrimEnd(c);
        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    private long ReadLong(string key) =>
        _block.TryGetValue(key, out var v) && long.TryParse(v, out var l) ? l : 0;
}
