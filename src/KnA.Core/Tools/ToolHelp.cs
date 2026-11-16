using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using KnA.Core.Processing;

namespace KnA.Core.Tools;

/// <summary>One option of a program, read from its own help text.</summary>
public sealed record ToolOption(string Name, string? Argument, string Description, IReadOnlyList<string> Values)
{
    /// <summary>A switch without value ("--aq", "-ssim" boolean flags still take 0/1 in FFmpeg).</summary>
    public bool IsFlag => Argument is null;
}

/// <summary>
/// Every parameter of every program KnA runs, without a hand-written list that ages: the options are read from the
/// installed program's own help (FFmpeg's encoder and codec AVOptions, NVEncC/QSVEncC/VCEEncC, fdkaac, qaac…), so a
/// newer build brings its new options by itself. Results are cached per program file.
/// </summary>
public static partial class ToolHelp
{
    private static readonly ConcurrentDictionary<string, IReadOnlyList<ToolOption>> Cache = new();

    /// <summary>Options of an FFmpeg encoder: its private ones, then the generic codec options that apply to its kind.</summary>
    public static async Task<IReadOnlyList<ToolOption>> FfmpegEncoderAsync(string ffmpeg, string encoder, char kind, CancellationToken ct = default)
    {
        var key = Key(ffmpeg, "enc:" + encoder + kind);
        if (Cache.TryGetValue(key, out var hit)) return hit;
        var own = await RunAsync(ffmpeg, ["-hide_banner", "-h", "encoder=" + encoder], ct);
        var full = await GenericCodecOptionsAsync(ffmpeg, ct);
        // The encoder's own options first (what users look for), the codec-wide ones (-b, -g, -bf, -refs…) after.
        var list = ParseFfmpeg(own, kind).Concat(ParseFfmpeg(full, kind)).DistinctBy(o => o.Name).ToList();
        return Cache[key] = list;
    }

    /// <summary>Options of a muxer (container), e.g. "matroska", "mp4".</summary>
    public static async Task<IReadOnlyList<ToolOption>> FfmpegMuxerAsync(string ffmpeg, string muxer, CancellationToken ct = default)
    {
        var key = Key(ffmpeg, "mux:" + muxer);
        if (Cache.TryGetValue(key, out var hit)) return hit;
        return Cache[key] = ParseFfmpeg(await RunAsync(ffmpeg, ["-hide_banner", "-h", "muxer=" + muxer], ct), null);
    }

    /// <summary>Options of a standalone program (NVEncC, QSVEncC, VCEEncC, fdkaac, qaac…) from its --help.</summary>
    public static async Task<IReadOnlyList<ToolOption>> ProgramAsync(string exe, CancellationToken ct = default)
    {
        var key = Key(exe, "help");
        if (Cache.TryGetValue(key, out var hit)) return hit;
        var text = await RunAsync(exe, ["--help"], ct);
        if (string.IsNullOrWhiteSpace(text)) text = await RunAsync(exe, ["-h"], ct);
        return Cache[key] = ParseGnu(text);
    }

    private static async Task<string> GenericCodecOptionsAsync(string ffmpeg, CancellationToken ct)
    {
        var text = await RunAsync(ffmpeg, ["-hide_banner", "-h", "full"], ct);
        int i = text.IndexOf("AVCodecContext AVOptions:", StringComparison.Ordinal);
        if (i < 0) return "";
        int end = text.IndexOf("\n\n", i, StringComparison.Ordinal);
        return end < 0 ? text[i..] : text[i..end];
    }

    private static string Key(string exe, string what)
    {
        long stamp = 0;
        try { stamp = File.GetLastWriteTimeUtc(exe).Ticks; } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return $"{exe}|{stamp}|{what}";
    }

    private static async Task<string> RunAsync(string exe, IReadOnlyList<string> args, CancellationToken ct)
    {
        try
        {
            var r = await ProcessRunner.RunAsync(exe, args, timeout: TimeSpan.FromSeconds(20), ct: ct);
            return r.StdOut + "\n" + r.StdErr;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return ""; }
    }

    [GeneratedRegex(@"^  (-[^\s<]+)\s+<([^>]+)>\s+([ED.][D.][F.]?[V.][A.][S.][\w.]*)\s?(.*)$")]
    private static partial Regex FfmpegOption();

    [GeneratedRegex(@"^ {4,}(\S+)(?:\s+(-?\d+|\S+))?\s+([ED.][D.][F.]?[V.][A.][S.][\w.]*)\s?(.*)$")]
    private static partial Regex FfmpegConstant();

    /// <summary>
    /// FFmpeg AVOptions ("  -crf  &lt;float&gt;  E..V....... text (from 0 to 51) (default 23)" followed by indented named
    /// constants). <paramref name="kind"/> V/A/S keeps the options that apply to video, audio or subtitles; null = all.
    /// </summary>
    public static List<ToolOption> ParseFfmpeg(string text, char? kind)
    {
        var list = new List<ToolOption>();
        ToolOption? cur = null;
        List<string>? values = null;
        void Flush() { if (cur is not null) list.Add(cur with { Values = values ?? [] }); cur = null; values = null; }
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (FfmpegOption().Match(line) is { Success: true } m)
            {
                Flush();
                var flags = m.Groups[3].Value;
                if (!flags.StartsWith('E')) continue;                      // decoding-only option
                if (kind is { } k && !Applies(flags, k)) continue;
                string type = m.Groups[2].Value;
                var desc = m.Groups[4].Value.Trim();
                cur = new ToolOption(m.Groups[1].Value, type, desc, []);
                values = type == "boolean" ? ["0", "1"] : null;
            }
            else if (cur is not null && FfmpegConstant().Match(line) is { Success: true } c && line.StartsWith("     ", StringComparison.Ordinal))
            {
                (values ??= []).Add(c.Groups[1].Value);
            }
            else if (line.Length > 0 && !line.StartsWith(' ')) Flush();   // next section
        }
        Flush();
        return list;
    }

    private static bool Applies(string flags, char kind)
    {
        // E D F? V A S …: with the filter column present the letters move by one.
        bool hasF = flags.Length > 2 && flags[2] is 'F' or '.' && flags.Length >= 11;
        int v = hasF ? 3 : 2;
        if (flags.Length <= v + 2) return true;
        return kind switch
        {
            'V' => flags[v] == 'V',
            'A' => flags[v + 1] == 'A',
            'S' => flags[v + 2] == 'S',
            _ => true,
        };
    }

    [GeneratedRegex(@"^\s{0,8}(?:(-[A-Za-z0-9])\s*,\s*)?(--?[A-Za-z0-9][\w\-:.]*)(?:[ =](<[^>]*>|\[[^\]]*\]|[A-Za-z_]+\b(?=\s{2,}|$)))?(?:\s{2,}(.*))?$")]
    private static partial Regex GnuOption();

    /// <summary>
    /// "--help" text of GNU-style programs: "  -p, --profile &lt;n&gt;   description" with continuation lines indented
    /// further. Value lists written as "- a, b(default), c" become choices.
    /// </summary>
    public static List<ToolOption> ParseGnu(string text)
    {
        var list = new List<ToolOption>();
        string? name = null, arg = null;
        var desc = new System.Text.StringBuilder();
        List<string> values = [];
        void Flush()
        {
            if (name is not null) list.Add(new ToolOption(name, arg, desc.ToString().Trim(), values));
            name = null; arg = null; desc.Clear(); values = [];
        }
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0) { Flush(); continue; }
            if (GnuOption().Match(line) is { Success: true } m && m.Groups[2].Value.Length > 2)
            {
                Flush();
                name = m.Groups[2].Value;
                arg = m.Groups[3].Success && m.Groups[3].Value.Length > 0 ? m.Groups[3].Value.Trim('<', '>', '[', ']') : null;
                desc.Append(m.Groups[4].Value);
                continue;
            }
            if (name is null) continue;
            var t = line.Trim();
            if (t.StartsWith("- ", StringComparison.Ordinal) && t.Contains(','))
                values.AddRange(t[2..].Split(',').Select(v => Regex.Replace(v.Trim(), @"\(.*?\)", "").Trim()).Where(v => v.Length > 0 && !v.Contains(' ')));
            else if (Regex.Match(t, @"^(\S{1,24})\s*[:=]\s") is { Success: true } vm && line.Length - line.TrimStart().Length > 8)
                values.Add(vm.Groups[1].Value);
            if (desc.Length < 600) desc.Append(' ').Append(t);
        }
        Flush();
        return list.DistinctBy(o => o.Name).ToList();
    }

    /// <summary>"-opt value -flag" (FFmpeg) or "--opt value --flag" (others) → pairs, as stored in the settings.</summary>
    public static List<(string Option, string? Value)> Parse(string? text) =>
        string.IsNullOrWhiteSpace(text) ? [] : KnA.Core.Encoding.VideoEncoders.ParseExtra(text);

    /// <summary>Pairs back to text (values with spaces quoted).</summary>
    public static string Format(IEnumerable<(string Option, string? Value)> pairs) =>
        string.Join(' ', pairs.Select(p => p.Value is null ? p.Option : p.Option + " " + (p.Value.Contains(' ') ? "\"" + p.Value + "\"" : p.Value)));

    internal static string Invariant(double d) => d.ToString(CultureInfo.InvariantCulture);
}
