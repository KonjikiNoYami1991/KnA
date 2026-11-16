using System.Globalization;
using KnA.Core.Encoding;
using KnA.Core.Localization;
using KnA.Core.Settings;

namespace KnA.Core.Queue;

/// <summary>
/// Headless encoding for scripts, scheduled tasks and other programs (StaxRip's command line, simpler):
///   KnA-server --encode &lt;files or folders…&gt; [--preset "name"] [--output &lt;folder&gt;] [--size &lt;MB&gt;] [--codec hevc|av1|h264]
///              [--quality 0-7] [--resolution 1080] [--gpu nvenc|qsv|amf|vaapi|software] [--trim start-end]
///   KnA-server --list-presets
/// The same engine, settings and components as the app: whatever is not given comes from the default settings.
/// Exit code 0 = every file done, 1 = some failed, 2 = bad arguments.
/// </summary>
public static class CommandLine
{
    public static bool Handles(string[] args) => args.Length > 0 && args[0] is "--encode" or "--list-presets" or "--help-cli";

    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken ct = default)
    {
        var store = new SettingsStore();
        if (args[0] == "--help-cli") { output.WriteLine(Usage); return 0; }
        using var queue = new QueueService(store, restoreQueue: false);
        if (args[0] == "--list-presets")
        {
            foreach (var p in queue.Presets.All) output.WriteLine((p.BuiltIn ? "  " : "★ ") + p.Name + "  —  " + p.Settings.Label);
            return 0;
        }

        var files = new List<string>();
        var s = queue.Settings.Defaults;
        var edit = new JobEdit();
        try
        {
            for (int i = 1; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException(L.T("{0} needs a value.", args[i]));
                switch (args[i])
                {
                    case "--preset":
                        var name = Next();
                        s = (queue.Presets.Get(name) ?? throw new ArgumentException(L.T("No preset named \"{0}\" (see --list-presets).", name))).Settings with { PresetName = name };
                        break;
                    case "--output": edit = edit with { OutputFolder = Path.GetFullPath(Next()) }; break;
                    case "--size":
                        s = s with { Encoder = s.Encoder with { RateControl = RateControl.TargetSize, TargetSizeMB = double.Parse(Next(), CultureInfo.InvariantCulture) } };
                        break;
                    case "--codec":
                        s = s with { Codec = Next().ToLowerInvariant() switch { "h264" or "avc" => VideoCodec.H264, "hevc" or "h265" => VideoCodec.HEVC, "av1" => VideoCodec.AV1, "vp9" => VideoCodec.VP9, var c => throw new ArgumentException(c) } };
                        break;
                    case "--quality": s = s with { Quality = (QualityLevel)Math.Clamp(int.Parse(Next(), CultureInfo.InvariantCulture), 0, 7), Encoder = s.Encoder with { RateControl = RateControl.Auto } }; break;
                    case "--resolution": s = s with { Resolution = int.Parse(Next(), CultureInfo.InvariantCulture) }; break;
                    case "--gpu":
                        s = s with { Backend = Next().ToLowerInvariant() switch { "nvenc" or "nvidia" => EncoderBackend.Nvenc, "qsv" or "intel" => EncoderBackend.Qsv, "amf" or "amd" => EncoderBackend.Amf, "vaapi" => EncoderBackend.Vaapi, "software" or "cpu" => EncoderBackend.Software, var g => throw new ArgumentException(g) } };
                        break;
                    case "--trim":
                        var parts = Next().Split('-', 2);
                        edit = edit with { TrimStart = Seconds(parts[0]), TrimEnd = parts.Length > 1 && parts[1].Length > 0 ? Seconds(parts[1]) : null };
                        break;
                    default:
                        if (args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException(L.T("Unknown option {0}.", args[i]));
                        files.Add(Path.GetFullPath(args[i]));
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            output.WriteLine(ex.Message);
            output.WriteLine(Usage);
            return 2;
        }
        if (files.Count == 0) { output.WriteLine(Usage); return 2; }
        if (queue.Tools is null) { output.WriteLine(L.T("FFmpeg was not found. Configure it in Settings.")); return 2; }

        var jobs = queue.AddPaths(files, s, null, edit);
        output.WriteLine(L.T("{0} file(s) in the queue: {1}", jobs.Count, s.Label));
        var done = new TaskCompletionSource();
        var last = DateTime.MinValue;
        queue.JobChanged += j =>
        {
            if (j.Status == JobStatus.Running && (DateTime.UtcNow - last).TotalSeconds < 5) return;
            last = DateTime.UtcNow;
            lock (output) output.WriteLine($"[{j.Status}] {j.FileName} {j.Percent:0.0}%{(j.Stage is { } st ? " " + st : "")}{(j.Message is { Length: > 0 } m ? " — " + m : "")}");
        };
        queue.QueueChanged += () => { if (!queue.IsRunning) done.TrySetResult(); };
        using var reg = ct.Register(() => _ = queue.StopAsync());
        queue.Start();
        await done.Task;
        int failed = jobs.Count(j => j.Status is JobStatus.Error or JobStatus.Stopped);
        foreach (var j in jobs) output.WriteLine($"{j.Status,-8} {j.FileName} → {j.OutputPath}{(j.Message is { Length: > 0 } m ? "  (" + m + ")" : "")}");
        return failed > 0 ? 1 : 0;
    }

    private static double Seconds(string text) =>
        TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var t) && text.Contains(':') ? t.TotalSeconds : double.Parse(text, CultureInfo.InvariantCulture);

    public const string Usage = """
        KnA command line
          --encode <files or folders…>   encode and exit (exit code 0 = all done, 1 = some failed)
            --preset "name"              a preset (see --list-presets); otherwise the default settings
            --output <folder>            destination folder
            --size <MB>                  maximum size of each file (bitrates computed for every stream)
            --codec h264|hevc|av1|vp9    --quality 0-7 (0 = best)    --resolution 1080 (0 = original)
            --gpu nvenc|qsv|amf|vaapi|software
            --trim 00:01:30-00:22:10     keep only this part (seconds or h:mm:ss)
          --list-presets
        """;
}
