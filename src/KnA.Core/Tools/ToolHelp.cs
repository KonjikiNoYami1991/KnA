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
    /// <summary>Heading of the help section the option is listed under ("libx265", "Encode Mode Options"…), if any.</summary>
    public string? Section { get; init; }
    /// <summary>Default value written in the help ("(default 23)"), if any.</summary>
    public string? Default { get; init; }
    /// <summary>Group of the parameter pages (one sub-tab each), see <see cref="ToolHelp.Categorize"/>.</summary>
    public string Category => ToolHelp.Categorize(this);
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

    /// <summary>One FFmpeg filter (for the GPU filter list).</summary>
    public sealed record FilterInfo(string Name, string Description);

    [GeneratedRegex(@"^\s[T.][S.][C.]?\s+(\S+)\s+(\S+->\S+)\s+(.*)$")]
    private static partial Regex FilterLine();

    /// <summary>Every filter of an FFmpeg build ("ffmpeg -filters").</summary>
    public static async Task<IReadOnlyList<FilterInfo>> FfmpegFiltersAsync(string ffmpeg, CancellationToken ct = default)
    {
        var key = Key(ffmpeg, "filters");
        if (Cache.TryGetValue(key, out var hit)) return hit.Select(o => new FilterInfo(o.Name, o.Description)).ToList();
        var text = await RunAsync(ffmpeg, ["-hide_banner", "-filters"], ct);
        var list = text.Split('\n').Select(l => FilterLine().Match(l.TrimEnd('\r'))).Where(m => m.Success)
            .Select(m => new ToolOption(m.Groups[1].Value, null, m.Groups[3].Value.Trim(), [])).ToList();
        Cache[key] = list;
        return list.Select(o => new FilterInfo(o.Name, o.Description)).ToList();
    }

    /// <summary>Options of one FFmpeg filter ("ffmpeg -h filter=scale_cuda").</summary>
    public static async Task<IReadOnlyList<ToolOption>> FfmpegFilterAsync(string ffmpeg, string filter, CancellationToken ct = default)
    {
        var key = Key(ffmpeg, "filter:" + filter);
        if (Cache.TryGetValue(key, out var hit)) return hit;
        return Cache[key] = ParseFfmpeg(await RunAsync(ffmpeg, ["-hide_banner", "-h", "filter=" + filter], ct), null, filter: true)
            .Select(o => o with { Name = o.Name.TrimStart('-') }).DistinctBy(o => o.Name).ToList();
    }

    /// <summary>
    /// The sub-tab of the parameter pages an option belongs to, from its name, then its help section. Same groups for
    /// every program, so FFmpeg's encoders and rigaya's NVEncC/QSVEncC/VCEEncC look alike.
    /// </summary>
    public static string Categorize(ToolOption o)
    {
        var n = o.Name.TrimStart('-').ToLowerInvariant();
        var sec = (o.Section ?? "").ToLowerInvariant();
        static bool Any(string text, params string[] words) => words.Any(w => text.Contains(w, StringComparison.Ordinal));
        if (n.StartsWith("vpp", StringComparison.Ordinal)) return "Filters";
        if (Any(n, "color", "colour", "transfer", "matrix", "prim", "range", "master", "cll", "hdr", "dolby", "chromaloc", "atc-sei", "videoformat", "sar", "aspect"))
            return "Colour / HDR";
        if (Any(n, "crf", "cqp", "qvbr", "icq", "vbr", "cbr", "abr", "bitrate", "maxrate", "minrate", "bufsize", "vbv", "qmin", "qmax", "qcomp", "qdiff",
                "qblur", "qsquish", "init-q", "qp-", "qpi", "qpp", "qpb", "aq", "lookahead", "la-", "rc-", "rc_", "mbbrc", "extbrc", "quality", "zones", "pass",
                "i-qfactor", "b-qfactor", "i_qfactor", "b_qfactor", "chroma-qp", "dynamic-rc", "hrd", "max-frame", "global_quality") || n is "cq" or "qp" or "rc" or "b" or "q" or "la")
            return "Rate control";
        if (Any(n, "gop", "keyint", "bframe", "b-frame", "b-pyramid", "bref", "b_ref", "b_strategy", "ref", "open-gop", "scenecut", "sc_threshold", "slice", "idr",
                "weightp", "weightb", "b-adapt", "strict-gop", "intra-refresh", "forced-idr", "nonref") || n is "g" or "bf" or "refs")
            return "GOP / frames";
        if (n is "aud" || Any(n, "profile", "level", "tier", "bluray", "compat", "repeat-headers", "pic-struct", "nal", "sei", "annexb", "bitstream", "x264", "x265",
                "svtav1", "aom-params", "vvenc", "params", "tile", "cabac", "8x8dct", "fullrange"))
            return "Profile / bitstream";
        if (Any(n, "input", "output", "avhw", "avsw", "audio", "sub-", "subtitle", "chapter", "metadata", "attachment", "seek", "trim", "mux", "log",
                "caption", "timecode", "thumbnail", "dump", "check", "help", "version") || n is "i" or "o")
            return "Input / output";
        if (Any(n, "subme", "merange", "motion", "trellis", "psy", "partition", "direct", "deblock", "rdo", "tu-", "ctu", "sao", "cu-", "transform",
                "intra", "inter", "mv", "adaptive", "tskip", "cdef", "filter", "temporal", "spatial", "smart", "split", "multipass", "dct",
                "noise", "denoise", "film-grain", "sharp", "enable-", "cmp", "mbd", "precmp", "dia")
            || n is "me" or "rd" or "me_method" || n.StartsWith("me-", StringComparison.Ordinal) || n.StartsWith("me_", StringComparison.Ordinal) || n.StartsWith("rd-", StringComparison.Ordinal))
            return "Analysis / tools";
        if (Any(n, "preset", "tune", "device", "thread", "parallel", "gpu", "lowlatency", "low_power", "low-power", "async", "frames", "pool", "memory", "buffer",
                "cuda", "opencl", "d3d", "vulkan", "output-buf", "input-buf", "priority", "affinity", "perf", "speed", "cpu", "fast", "lowdelay", "delay")
            || n is "u")
            return "Performance / device";
        if (Any(n, "format", "interlace", "tff", "bff", "fps", "crop", "data"))
            return "Input / output";
        if (Any(sec, "rate", "encode mode", "quality")) return "Rate control";
        if (Any(sec, "frame", "gop")) return "GOP / frames";
        if (Any(sec, "vpp", "filter")) return "Filters";
        if (Any(sec, "input", "output", "audio", "subtitle", "other", "log")) return "Input / output";
        return "Other";
    }

    /// <summary>The order of the parameter pages.</summary>
    public static IReadOnlyList<string> Categories { get; } =
        ["Rate control", "GOP / frames", "Analysis / tools", "Colour / HDR", "Profile / bitstream", "Performance / device", "Input / output", "Filters", "Other"];

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

    /// <summary>Filter options: "   w   &lt;string&gt;   ..FV....... text" (three spaces, no dash).</summary>
    [GeneratedRegex(@"^   ([A-Za-z_][\w\-]*)\s+<([^>]+)>\s+([ED.][D.][F.]?[V.][A.][S.][\w.]*)\s?(.*)$")]
    private static partial Regex FilterOption();

    /// <summary>
    /// FFmpeg AVOptions ("  -crf  &lt;float&gt;  E..V....... text (from 0 to 51) (default 23)" followed by indented named
    /// constants). <paramref name="kind"/> V/A/S keeps the options that apply to video, audio or subtitles; null = all.
    /// </summary>
    public static List<ToolOption> ParseFfmpeg(string text, char? kind, bool filter = false)
    {
        var list = new List<ToolOption>();
        ToolOption? cur = null;
        List<string>? values = null;
        string? section = null;
        void Flush() { if (cur is not null) list.Add(cur with { Values = values ?? [] }); cur = null; values = null; }
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if ((filter ? FilterOption() : FfmpegOption()).Match(line) is { Success: true } m)
            {
                Flush();
                var flags = m.Groups[3].Value;
                if (filter ? !(flags.Length > 2 && flags[2] == 'F') : !flags.StartsWith('E')) continue;   // decoding-only option
                if (kind is { } k && !Applies(flags, k)) continue;
                string type = m.Groups[2].Value;
                var desc = m.Groups[4].Value.Trim();
                var def = DefaultValue().Match(desc) is { Success: true } dm ? dm.Groups[1].Value.Trim('"') : null;
                cur = new ToolOption(m.Groups[1].Value, type, desc, []) { Section = section, Default = def };
                values = type == "boolean" ? ["0", "1"] : null;
            }
            else if (cur is not null && FfmpegConstant().Match(line) is { Success: true } c && line.StartsWith("     ", StringComparison.Ordinal))
            {
                (values ??= []).Add(c.Groups[1].Value);
            }
            else if (line.Length > 0 && !line.StartsWith(' '))
            {
                Flush();   // next section
                int i = line.IndexOf(" AVOptions", StringComparison.Ordinal);
                section = i > 0 ? line[..i].Trim() : section;
            }
        }
        Flush();
        return list;
    }

    [GeneratedRegex(@"\(default ([^)]*)\)")]
    private static partial Regex DefaultValue();

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
        string? name = null, arg = null, sectionOf = null;
        var desc = new System.Text.StringBuilder();
        List<string> values = [];
        void Flush()
        {
            if (name is not null)
            {
                var d = desc.ToString().Trim();
                var def = Regex.Match(d, @"(?:default|def\.)\s*[:=]?\s*([\w.\-:]+)", RegexOptions.IgnoreCase) is { Success: true } dm ? dm.Groups[1].Value : null;
                list.Add(new ToolOption(name, arg, d, values) { Section = sectionOf, Default = def });
            }
            name = null; arg = null; desc.Clear(); values = [];
        }
        string? section = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0) { Flush(); continue; }
            // "Encode Mode Options:" / "[Common Options]" / "--- Frame buffer ---": the heading of the next options.
            var head = line.Trim();
            if (!line.StartsWith(' ') && !head.StartsWith('-') && head.Length < 70
                && (head.EndsWith(':') || (head.StartsWith('[') && head.EndsWith(']'))))
            {
                Flush();
                section = head.Trim(':', '[', ']', ' ');
                continue;
            }
            if (GnuOption().Match(line) is { Success: true } m && m.Groups[2].Value.Length > 2)
            {
                Flush();
                sectionOf = section;
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
