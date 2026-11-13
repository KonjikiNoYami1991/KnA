using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TextEncoding = System.Text.Encoding;
using KnA.Core.Localization;
using KnA.Core.Processing;

namespace KnA.Core.Media.Discs;

public enum DiscKind { BluRay, Dvd }

/// <summary>A Blu-ray or DVD: a BDMV / VIDEO_TS folder (or its parent) or an .iso image.</summary>
public sealed record Disc(string Root, DiscKind Kind, bool IsIso)
{
    /// <summary>Name shown to the user and used for output files: the image or folder name.</summary>
    public string Name
    {
        get
        {
            if (IsIso) return Path.GetFileNameWithoutExtension(Root);
            var n = Path.GetFileName(Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.IsNullOrEmpty(n) ? (Kind == DiscKind.BluRay ? "BLURAY" : "DVD") : n;
        }
    }

    /// <summary>Folder the outputs go to by default (next to the image / disc folder).</summary>
    public string ParentFolder => Path.GetDirectoryName(Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? Root;
}

public sealed record DiscTitle(int Number, TimeSpan Duration, int Chapters, string Description);

/// <summary>What the FFmpeg builds can open natively.</summary>
public sealed record DiscSupport(bool Bluray, bool DvdVideo)
{
    public static DiscSupport None { get; } = new(false, false);
}

/// <summary>
/// Blu-ray / DVD sources. A title is addressed with a virtual path "&lt;disc&gt;::title=N" (N = playlist
/// number for Blu-ray, title number for DVD; 0 = the main title chosen by FFmpeg), so every queue job is one title.
///
/// Reading, best first:
///  • Blu-ray: FFmpeg's bluray: protocol (libbluray, folders and ISO images) with -playlist N;
///    otherwise (folders only) KnA parses the playlist and feeds the clips through an ffconcat list.
///  • DVD: FFmpeg's dvdvideo demuxer (7.0+, libdvdnav, folders and ISO images) with -title N;
///    otherwise (folders only) the title set's VOB files are joined with the concat: protocol.
/// </summary>
public static class DiscSource
{
    private const string Marker = "::title=";

    /// <summary>Set at startup / after detection (from the main FFmpeg build).</summary>
    public static DiscSupport Support { get; set; } = DiscSupport.None;
    /// <summary>Where generated ffconcat lists go.</summary>
    public static string WorkDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "kna-discs");
    /// <summary>Titles shorter than this are ignored when a disc is added (menus, trailers, warnings).</summary>
    public static TimeSpan MinTitleLength { get; set; } = TimeSpan.FromMinutes(3);

    // ---------------------------------------------------------------- virtual paths

    public static string TitlePath(Disc disc, int title) => disc.Root + Marker + title.ToString(CultureInfo.InvariantCulture);

    public static bool IsTitlePath(string path) => path.Contains(Marker, StringComparison.Ordinal);

    public static (Disc Disc, int Title)? ParseTitlePath(string path)
    {
        int i = path.LastIndexOf(Marker, StringComparison.Ordinal);
        if (i < 0 || !int.TryParse(path[(i + Marker.Length)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var t)) return null;
        return Detect(path[..i]) is { } d ? (d, t) : null;
    }

    /// <summary>File.Exists that understands title paths.</summary>
    public static bool Exists(string path) =>
        IsTitlePath(path) ? ParseTitlePath(path) is not null : File.Exists(path);

    /// <summary>"Disc name" or "Disc name - title 3" for output names.</summary>
    public static string DisplayName(string path)
    {
        if (ParseTitlePath(path) is not { } t) return Path.GetFileNameWithoutExtension(path);
        return t.Title == 0 ? t.Disc.Name : $"{t.Disc.Name} - {(t.Disc.Kind == DiscKind.BluRay ? "playlist" : "title")} {t.Title:00}";
    }

    /// <summary>"[BD] Disc name · playlist 00002" for lists.</summary>
    public static string Label(string path) =>
        ParseTitlePath(path) is { } t ? $"[{(t.Disc.Kind == DiscKind.BluRay ? "BD" : "DVD")}{(t.Disc.IsIso ? " ISO" : "")}] {DisplayName(path)}" : Path.GetFileName(path);

    /// <summary>Folder that contains the source (for title paths: the folder that contains the disc).</summary>
    public static string? ContainingFolder(string path) =>
        ParseTitlePath(path) is { } t ? t.Disc.ParentFolder : Path.GetDirectoryName(path);

    // ---------------------------------------------------------------- detection

    /// <summary>Recognises a disc folder (the BDMV / VIDEO_TS folder or its parent) or an .iso image.</summary>
    public static Disc? Detect(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                if (!path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) return null;
                using var iso = Iso9660.Open(path);
                bool dvd = iso?.List("VIDEO_TS").Any(e => e.Name.Equals("VIDEO_TS.IFO", StringComparison.OrdinalIgnoreCase)) ?? false;
                return new Disc(path, dvd ? DiscKind.Dvd : DiscKind.BluRay, true);
            }
            if (!Directory.Exists(path)) return null;
            var dir = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(dir);
            if (name.Equals("BDMV", StringComparison.OrdinalIgnoreCase) && FileCi(dir, "index.bdmv") is not null)
                return new Disc(Path.GetDirectoryName(dir)!, DiscKind.BluRay, false);
            if (name.Equals("VIDEO_TS", StringComparison.OrdinalIgnoreCase) && FileCi(dir, "VIDEO_TS.IFO") is not null)
                return new Disc(Path.GetDirectoryName(dir)!, DiscKind.Dvd, false);
            if (DirCi(dir, "BDMV") is { } bd && FileCi(bd, "index.bdmv") is not null) return new Disc(dir, DiscKind.BluRay, false);
            if (DirCi(dir, "VIDEO_TS") is { } vt && FileCi(vt, "VIDEO_TS.IFO") is not null) return new Disc(dir, DiscKind.Dvd, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>True for files that belong to a disc structure (they are imported through the disc, not one by one).</summary>
    public static bool IsInsideDiscStructure(string file)
    {
        var parts = file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(p => p.Equals("BDMV", StringComparison.OrdinalIgnoreCase) || p.Equals("VIDEO_TS", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Disc folders and .iso images inside a folder tree (not descending into a disc).</summary>
    public static List<Disc> FindDiscs(string folder, int maxDepth = 8)
    {
        var found = new List<Disc>();
        void Walk(string dir, int depth)
        {
            if (Detect(dir) is { IsIso: false } d)
            {
                found.Add(d);
                return;
            }
            if (depth >= maxDepth) return;
            var opt = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
            try
            {
                foreach (var iso in Directory.EnumerateFiles(dir, "*", opt).Where(f => f.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)).Order())
                    if (Detect(iso) is { } di) found.Add(di);
                foreach (var sub in Directory.EnumerateDirectories(dir, "*", opt).Order())
                    Walk(sub, depth + 1);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        Walk(folder, 0);
        return found;
    }

    // ---------------------------------------------------------------- titles

    public static async Task<List<DiscTitle>> ListTitlesAsync(Disc disc, string? ffprobe, CancellationToken ct = default)
    {
        try
        {
            if (disc.Kind == DiscKind.BluRay)
            {
                if (!disc.IsIso) return UniquePlaylists(ReadPlaylists(disc))
                    .Select(p => new DiscTitle(p.Number, p.Duration, p.Chapters.Count, $"{p.Number:00000}.mpls · {p.Items.Count} clip(s)")).ToList();
                if (ffprobe is not null && Support.Bluray) return await BlurayTitlesFromLibblurayAsync(disc, ffprobe, ct);
                return [];
            }
            return DvdTitles(disc).Select(t => new DiscTitle(t.Number, t.Duration, t.Chapters,
                $"VTS {t.TitleSet:00} · {t.Chapters} chapter(s){(t.Angles > 1 ? $" · {t.Angles} angles" : "")}")).ToList();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>The titles to queue when a disc is added: every title above the minimum length, longest first if only the main one is wanted.</summary>
    public static async Task<List<string>> TitlePathsAsync(Disc disc, string? ffprobe, bool allTitles, CancellationToken ct = default)
    {
        var titles = (await ListTitlesAsync(disc, ffprobe, ct)).Where(t => t.Duration >= MinTitleLength).ToList();
        if (titles.Count == 0) return [TitlePath(disc, 0)];
        if (!allTitles) return [TitlePath(disc, titles.MaxBy(t => t.Duration)!.Number)];
        return titles.OrderBy(t => t.Number).Select(t => TitlePath(disc, t.Number)).ToList();
    }

    public static List<BdPlaylist> ReadPlaylists(Disc disc)
    {
        var dir = DirCi(DirCi(disc.Root, "BDMV")!, "PLAYLIST");
        if (dir is null) return [];
        var list = new List<BdPlaylist>();
        foreach (var f in Directory.EnumerateFiles(dir).Where(f => f.EndsWith(".mpls", StringComparison.OrdinalIgnoreCase)))
        {
            if (!int.TryParse(Path.GetFileNameWithoutExtension(f), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) continue;
            try { list.Add(MplsParser.Parse(n, File.ReadAllBytes(f))); }
            catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException) { }
        }
        return list;
    }

    /// <summary>Drops duplicate playlists (same clips) and keeps the lowest number.</summary>
    private static IEnumerable<BdPlaylist> UniquePlaylists(IEnumerable<BdPlaylist> all) =>
        all.GroupBy(p => p.Signature).Select(g => g.OrderBy(p => p.Number).First()).OrderBy(p => p.Number);

    /// <summary>libbluray (through ffprobe's bluray: protocol) prints the usable playlists: works for ISO images.</summary>
    private static async Task<List<DiscTitle>> BlurayTitlesFromLibblurayAsync(Disc disc, string ffprobe, CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync(ffprobe, ["-v", "info", "-hide_banner", "bluray:" + disc.Root], timeout: TimeSpan.FromMinutes(2), ct: ct);
        var titles = new List<DiscTitle>();
        foreach (var line in r.StdErr.Split('\n'))
        {
            // "playlist 00001.mpls (0:23:40)"
            var m = System.Text.RegularExpressions.Regex.Match(line, @"playlist (\d{5})\.mpls \((\d+):(\d\d):(\d\d)\)");
            if (!m.Success) continue;
            int n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var d = new TimeSpan(int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture));
            if (titles.All(t => t.Number != n)) titles.Add(new DiscTitle(n, d, 0, $"{n:00000}.mpls"));
        }
        return titles;
    }

    public static List<DvdTitle> DvdTitles(Disc disc)
    {
        if (disc.IsIso)
        {
            using var iso = Iso9660.Open(disc.Root) ?? throw new InvalidDataException("Not a DVD image.");
            var entries = iso.List("VIDEO_TS");
            byte[]? Read(string name) => entries.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } e ? iso.ReadFile(e) : null;
            var vmg = Read("VIDEO_TS.IFO") ?? throw new InvalidDataException("VIDEO_TS.IFO not found.");
            return IfoParser.Titles(vmg, vts => Read($"VTS_{vts:00}_0.IFO"));
        }
        var dir = DirCi(disc.Root, "VIDEO_TS")!;
        byte[]? ReadFolder(string name) => FileCi(dir, name) is { } f ? File.ReadAllBytes(f) : null;
        return IfoParser.Titles(ReadFolder("VIDEO_TS.IFO")!, vts => ReadFolder($"VTS_{vts:00}_0.IFO"));
    }

    // ---------------------------------------------------------------- FFmpeg input

    /// <summary>
    /// Options + "-i url" to open <paramref name="path"/>; for normal files just ["-i", path].
    /// Throws with an explanation when the disc cannot be read with this FFmpeg.
    /// </summary>
    public static List<string> InputArgs(string path)
    {
        if (ParseTitlePath(path) is not { } t) return ["-i", path];
        var (disc, title) = t;
        string n = title.ToString(CultureInfo.InvariantCulture);
        var robust = new List<string> { "-analyzeduration", "100M", "-probesize", "100M", "-fflags", "+genpts+discardcorrupt" };
        if (disc.Kind == DiscKind.BluRay)
        {
            if (Support.Bluray)
                return [.. robust, .. title > 0 ? new[] { "-playlist", n } : [], "-i", "bluray:" + disc.Root];
            if (disc.IsIso) throw new InvalidOperationException(L.T("Blu-ray ISO images need an FFmpeg built with libbluray (or mount the image and add its folder)."));
            return [.. robust, "-f", "concat", "-safe", "0", "-i", BlurayConcatList(disc, title)];
        }
        if (Support.DvdVideo)
            return [.. robust, "-f", "dvdvideo", "-title", title > 0 ? n : "1", "-i", disc.Root];
        if (disc.IsIso) throw new InvalidOperationException(L.T("DVD ISO images need FFmpeg 7 or newer with libdvdnav (or mount the image and add its folder)."));
        return [.. robust, "-i", DvdConcatUrl(disc, title)];
    }

    /// <summary>Same as <see cref="InputArgs"/> without "-i" (ffprobe takes the url as a plain argument).</summary>
    public static List<string> ProbeArgs(string path)
    {
        var a = InputArgs(path);
        int i = a.LastIndexOf("-i");
        a.RemoveAt(i);
        return a;
    }

    private static string BlurayConcatList(Disc disc, int title)
    {
        var lists = ReadPlaylists(disc);
        var pl = title > 0 ? lists.FirstOrDefault(p => p.Number == title) : lists.MaxBy(p => p.Duration);
        if (pl is null) throw new InvalidOperationException(L.T("Playlist {0} not found on the disc.", title));
        var bdmv = DirCi(disc.Root, "BDMV")!;
        var stream = DirCi(bdmv, "STREAM") ?? throw new InvalidDataException("BDMV/STREAM not found.");
        var clipinf = DirCi(bdmv, "CLIPINF");
        var sb = new StringBuilder("ffconcat version 1.0\n");
        foreach (var item in pl.Items)
        {
            var file = FileCi(stream, item.Clip + ".m2ts") ?? throw new FileNotFoundException(L.T("Clip {0}.m2ts not found.", item.Clip));
            sb.Append("file '").Append(file.Replace("'", "'\\''")).Append("'\n");
            // Only cut when the play item really uses part of the clip (seamless branching, shared openings).
            var clip = clipinf is not null && FileCi(clipinf, item.Clip + ".clpi") is { } ci ? MplsParser.ClipTimes(File.ReadAllBytes(ci)) : null;
            if (clip is { } c)
            {
                if (item.In - c.Start > 0.5) sb.Append("inpoint ").Append(item.In.ToString("0.###", CultureInfo.InvariantCulture)).Append('\n');
                if (c.End - item.Out > 0.5) sb.Append("outpoint ").Append(item.Out.ToString("0.###", CultureInfo.InvariantCulture)).Append('\n');
            }
        }
        Directory.CreateDirectory(WorkDirectory);
        var hash = Convert.ToHexString(SHA1.HashData(TextEncoding.UTF8.GetBytes(disc.Root)))[..12];
        var path = Path.Combine(WorkDirectory, $"bd-{hash}-{pl.Number:00000}.ffconcat");
        var text = sb.ToString();
        if (!File.Exists(path) || File.ReadAllText(path) != text) File.WriteAllText(path, text);
        return path;
    }

    private static string DvdConcatUrl(Disc disc, int title)
    {
        var titles = DvdTitles(disc);
        var t = title > 0 ? titles.FirstOrDefault(x => x.Number == title) : titles.MaxBy(x => x.Duration);
        int vts = t?.TitleSet ?? 1;
        var dir = DirCi(disc.Root, "VIDEO_TS")!;
        var vobs = Directory.EnumerateFiles(dir)
            .Where(f => Path.GetFileName(f).StartsWith($"VTS_{vts:00}_", StringComparison.OrdinalIgnoreCase)
                        && f.EndsWith(".VOB", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(f).EndsWith("_0.VOB", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (vobs.Count == 0) throw new FileNotFoundException(L.T("No VOB files for title set {0}.", vts));
        return "concat:" + string.Join('|', vobs);
    }

    /// <summary>First .m2ts of a Blu-ray folder title read through the concat fallback (null otherwise).</summary>
    public static string? FirstClip(string path)
    {
        if (Support.Bluray || ParseTitlePath(path) is not { Disc: { Kind: DiscKind.BluRay, IsIso: false } d } t) return null;
        var pl = ReadPlaylists(d);
        var p = t.Title > 0 ? pl.FirstOrDefault(x => x.Number == t.Title) : pl.MaxBy(x => x.Duration);
        var stream = DirCi(DirCi(d.Root, "BDMV")!, "STREAM");
        return p is null || stream is null ? null : FileCi(stream, p.Items[0].Clip + ".m2ts");
    }

    /// <summary>Language of a Blu-ray stream (by PID) from the playlist, or null.</summary>
    public static string? BlurayLanguage(string path, int pid)
    {
        if (ParseTitlePath(path) is not { Disc: { Kind: DiscKind.BluRay, IsIso: false } d } t) return null;
        try
        {
            var pl = ReadPlaylists(d);
            var p = t.Title > 0 ? pl.FirstOrDefault(x => x.Number == t.Title) : pl.MaxBy(x => x.Duration);
            return p?.Languages.GetValueOrDefault(pid);
        }
        catch (IOException) { return null; }
    }

    // ---------------------------------------------------------------- helpers (case-insensitive lookups for Linux)

    private static string? DirCi(string parent, string name)
    {
        var direct = Path.Combine(parent, name);
        if (Directory.Exists(direct)) return direct;
        try { return Directory.EnumerateDirectories(parent).FirstOrDefault(d => Path.GetFileName(d).Equals(name, StringComparison.OrdinalIgnoreCase)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string? FileCi(string parent, string name)
    {
        var direct = Path.Combine(parent, name);
        if (File.Exists(direct)) return direct;
        try { return Directory.EnumerateFiles(parent).FirstOrDefault(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
