using TnA.Core.Settings;

namespace TnA.Server;

/// <summary>
/// Lets the browser pick files that live on the server (Docker volumes, NAS mounts…).
/// Access is limited to the configured roots ($TNA_MEDIA_ROOTS, separated by ';').
/// </summary>
public sealed class FileBrowser
{
    public IReadOnlyList<string> Roots { get; }

    public FileBrowser()
    {
        var env = Environment.GetEnvironmentVariable("TNA_MEDIA_ROOTS");
        IEnumerable<string> roots = !string.IsNullOrWhiteSpace(env)
            ? env.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : DefaultRoots();
        Roots = roots.Select(Normalize).Where(Directory.Exists).Distinct().ToList();
    }

    private static IEnumerable<string> DefaultRoots()
    {
        if (OperatingSystem.IsWindows())
            return DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.RootDirectory.FullName);
        var list = new List<string> { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
        list.AddRange(new[] { "/media", "/mnt", "/data", "/Volumes" }.Where(Directory.Exists));
        return list;
    }

    private static string Normalize(string p)
    {
        var full = Path.GetFullPath(p);
        // Keep "/" and "C:\" intact, strip the trailing separator from everything else.
        return full == Path.GetPathRoot(full) ? full : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static readonly string[] ExternalExtensions =
        [".mka", ".mkv", ".mks", ".mp4", ".m4a", ".aac", ".ac3", ".eac3", ".dts", ".thd", ".flac", ".opus", ".ogg", ".mp3", ".wav", ".wv",
         ".ass", ".ssa", ".srt", ".vtt", ".sup", ".ttf", ".otf", ".ttc", ".otc", ".webm", ".ts", ".m2ts", ".mov"];

    public bool IsAllowed(string path)
    {
        var full = Normalize(path);
        if (!IsInsideRoots(full)) return false;
        // A symlink anywhere along the path (file or any parent folder) may point outside the roots.
        try
        {
            for (var current = full; current is not null && IsInsideRoots(current); current = Path.GetDirectoryName(current))
            {
                FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                if (info.Exists && info.LinkTarget is not null
                    && info.ResolveLinkTarget(returnFinalTarget: true) is { } target && !IsInsideRoots(Normalize(target.FullName)))
                    return false;
                if (Roots.Contains(current)) break;
            }
        }
        catch (IOException) { return false; }
        return true;
    }

    private bool IsInsideRoots(string full)
    {
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return Roots.Any(r => full.Equals(r, cmp)
                              || full.StartsWith(r.EndsWith(Path.DirectorySeparatorChar) ? r : r + Path.DirectorySeparatorChar, cmp));
    }

    public BrowseResult Browse(string? path, bool external = false)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new BrowseResult(null, null, Roots.Select(r => new BrowseEntry(r, r, true, 0)).ToList(), Roots);

        var full = Normalize(path);
        if (!IsAllowed(full) || !Directory.Exists(full))
            throw new UnauthorizedAccessException("Folder outside the allowed media roots.");

        var entries = new List<BrowseEntry>();
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
        foreach (var d in Directory.EnumerateDirectories(full, "*", options).Order(StringComparer.OrdinalIgnoreCase))
            entries.Add(new BrowseEntry(Path.GetFileName(d), d, true, 0));
        Func<string, bool> filter = external
            ? f => ExternalExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())
            : MediaExtensions.IsVideo;
        foreach (var f in Directory.EnumerateFiles(full, "*", options).Where(filter).Order(StringComparer.OrdinalIgnoreCase))
        {
            long size = 0;
            try { size = new FileInfo(f).Length; } catch (IOException) { }
            entries.Add(new BrowseEntry(Path.GetFileName(f), f, false, size));
        }

        var parent = Path.GetDirectoryName(full);
        if (parent is null || !IsAllowed(parent)) parent = Roots.Contains(full) ? "" : null;
        return new BrowseResult(full, parent, entries, Roots);
    }
}
