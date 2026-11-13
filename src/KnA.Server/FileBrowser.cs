using KnA.Core.Media.Discs;
using KnA.Core.Queue;
using KnA.Core.Settings;

namespace KnA.Server;

public sealed record BrowseEntry(string Name, string Path, bool IsDirectory, long Size, string Kind = "other", DateTime? Modified = null);
public sealed record BrowseResult(string? Path, string? Parent, IReadOnlyList<BrowseEntry> Entries, IReadOnlyList<string> Roots, bool Writable = false);

/// <summary>
/// Lets the browser read the server's folders (OS folders, NAS mounts, Docker bind mounts), download from them
/// and write uploads / outputs into them.
///   KNA_MEDIA_ROOTS     read/write roots, ';' separated. Unset = the whole file system (every drive on Windows, "/" elsewhere),
///                       except inside a container where /media is the default.
///   KNA_READONLY_ROOTS  extra roots that can be browsed, downloaded and encoded from, but never written to.
/// Symlinks that leave the roots are refused.
/// </summary>
public sealed class FileBrowser
{
    public IReadOnlyList<string> Roots { get; }
    public IReadOnlyList<string> WritableRoots { get; }

    public FileBrowser() : this(AppEnv.Get("MEDIA_ROOTS"), AppEnv.Get("READONLY_ROOTS")) { }

    /// <param name="extraWritable">Folders KnA itself writes to (the upload folder), added when not already inside a root.</param>
    public FileBrowser(string? readWrite, string? readOnly, IEnumerable<string>? extraWritable = null)
    {
        static IEnumerable<string> Split(string? v) =>
            (v ?? "").Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rw = (string.IsNullOrWhiteSpace(readWrite) ? DefaultRoots() : Split(readWrite)).Select(Normalize).Where(Directory.Exists).Distinct().ToList();
        foreach (var extra in (extraWritable ?? []).Where(Directory.Exists).Select(Normalize))
            if (!IsInside(extra, rw)) rw.Add(extra);
        var ro = Split(readOnly).Select(Normalize).Where(Directory.Exists).Where(r => !rw.Contains(r)).Distinct().ToList();
        WritableRoots = rw;
        Roots = [.. rw, .. ro];
    }

    private static IEnumerable<string> DefaultRoots()
    {
        if (OperatingSystem.IsWindows())
            return DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.RootDirectory.FullName);
        if (File.Exists("/.dockerenv") || Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true")
            return new[] { "/media" }.Where(Directory.Exists);
        return ["/"];
    }

    private static string Normalize(string p)
    {
        var full = Path.GetFullPath(p);
        // Keep "/" and "C:\" intact, strip the trailing separator from everything else.
        return full == Path.GetPathRoot(full) ? full : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static readonly string[] ExternalExtensions =
        [.. DropClassifier.SubtitleExtensions, .. DropClassifier.AudioExtensions, ".mks", ".mkv", ".mp4", ".ttf", ".otf", ".ttc", ".otc", ".webm", ".ts", ".m2ts", ".mov"];

    /// <summary>Readable: inside any root (disc title paths are checked on their disc).</summary>
    public bool IsAllowed(string path) => Check(path, Roots);

    /// <summary>Writable: inside a read/write root.</summary>
    public bool IsWritable(string path) => Check(path, WritableRoots);

    private bool Check(string path, IReadOnlyList<string> roots)
    {
        if (DiscSource.ParseTitlePath(path) is { } t) path = t.Disc.Root;
        string full;
        try { full = Normalize(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        if (!IsInside(full, roots)) return false;
        // A symlink anywhere along the path (file or any parent folder) may point outside the roots.
        try
        {
            for (var current = full; current is not null && IsInside(current, roots); current = Path.GetDirectoryName(current))
            {
                FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                if (info.Exists && info.LinkTarget is not null
                    && info.ResolveLinkTarget(returnFinalTarget: true) is { } target && !IsInside(Normalize(target.FullName), roots))
                    return false;
                if (roots.Contains(current)) break;
            }
        }
        catch (IOException) { return false; }
        return true;
    }

    private static bool IsInside(string full, IReadOnlyList<string> roots)
    {
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return roots.Any(r => full.Equals(r, cmp)
                              || full.StartsWith(r.EndsWith(Path.DirectorySeparatorChar) ? r : r + Path.DirectorySeparatorChar, cmp));
    }

    public static string KindOf(string path, bool isDirectory)
    {
        if (isDirectory) return DiscSource.Detect(path) is not null ? "disc" : "dir";
        var e = Path.GetExtension(path).ToLowerInvariant();
        if (e == ".iso") return "disc";
        if (MediaExtensions.IsVideo(path)) return "video";
        if (DropClassifier.SubtitleExtensions.Contains(e) || e == ".mks") return "subtitle";
        if (DropClassifier.AudioExtensions.Contains(e)) return "audio";
        if (KnA.Core.Encoding.TrackPlanner.IsFontFile(path)) return "font";
        if (e is ".png" or ".jpg" or ".jpeg" or ".webp" or ".tif" or ".tiff" or ".bmp") return "image";
        if (e is ".txt" or ".log" or ".json" or ".vpy" or ".avs") return "text";
        return "other";
    }

    /// <summary>mode: "video" (sources), "external" (audio/subtitles/fonts), "all" (file manager), "folders" (destinations).</summary>
    public BrowseResult Browse(string? path, string mode = "video", bool showHidden = false)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new BrowseResult(null, null, Roots.Select(r => new BrowseEntry(r, r, true, 0, "root")).ToList(), Roots);

        var full = Normalize(path);
        if (!IsAllowed(full) || !Directory.Exists(full))
            throw new UnauthorizedAccessException("Folder outside the allowed media roots.");

        var entries = new List<BrowseEntry>();
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = showHidden ? FileAttributes.System : FileAttributes.Hidden | FileAttributes.System,
        };
        foreach (var d in Directory.EnumerateDirectories(full, "*", options).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!showHidden && Path.GetFileName(d).StartsWith('.')) continue;
            entries.Add(new BrowseEntry(Path.GetFileName(d), d, true, 0, KindOf(d, true)));
        }
        if (mode != "folders")
        {
            Func<string, bool> filter = mode switch
            {
                "external" => f => ExternalExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()),
                "all" => _ => true,
                _ => MediaExtensions.IsVideo,
            };
            foreach (var f in Directory.EnumerateFiles(full, "*", options).Where(filter).Order(StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(f);
                if (!showHidden && (name.StartsWith('.') || name.Contains(".kna-upload", StringComparison.Ordinal))) continue;
                long size = 0;
                DateTime? modified = null;
                try { var fi = new FileInfo(f); size = fi.Length; modified = fi.LastWriteTimeUtc; } catch (IOException) { }
                entries.Add(new BrowseEntry(name, f, false, size, KindOf(f, false), modified));
            }
        }

        var parent = Path.GetDirectoryName(full);
        if (parent is null || !IsAllowed(parent)) parent = Roots.Contains(full) ? "" : null;
        return new BrowseResult(full, parent, entries, Roots, IsWritable(full));
    }
}
