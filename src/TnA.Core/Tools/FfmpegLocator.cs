namespace TnA.Core.Tools;

public sealed record FfmpegTools(string Ffmpeg, string Ffprobe, string? Scxvid, string? DoviTool = null, string? Hdr10PlusTool = null, string? VsPipe = null);

/// <summary>
/// Finds the external programs. Order: configured path → TnA-managed copies in the data folder
/// (installed by the component manager) → next to the app → PATH → Homebrew / jellyfin-ffmpeg.
/// </summary>
public static class FfmpegLocator
{
    public static string ExeName(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    public static string ManagedDir(string dataDirectory, string component) =>
        component == "ffmpeg" ? Path.Combine(dataDirectory, "ffmpeg") : Path.Combine(dataDirectory, "tools", component);

    public static FfmpegTools? Locate(string? configuredFfmpeg, string dataDirectory)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredFfmpeg))
            candidates.Add(Directory.Exists(configuredFfmpeg) ? Path.Combine(configuredFfmpeg, ExeName("ffmpeg")) : configuredFfmpeg);
        if (Environment.GetEnvironmentVariable("TNA_FFMPEG") is { Length: > 0 } env) candidates.Add(env);
        candidates.Add(Path.Combine(ManagedDir(dataDirectory, "ffmpeg"), ExeName("ffmpeg")));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg", ExeName("ffmpeg")));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, ExeName("ffmpeg")));
        candidates.AddRange(SearchPath("ffmpeg"));
        if (OperatingSystem.IsMacOS()) candidates.AddRange(["/opt/homebrew/bin/ffmpeg", "/usr/local/bin/ffmpeg"]);
        if (OperatingSystem.IsLinux()) candidates.Add("/usr/lib/jellyfin-ffmpeg/ffmpeg");

        foreach (var ff in candidates.Where(File.Exists))
        {
            var dir = Path.GetDirectoryName(ff)!;
            var probe = Path.Combine(dir, ExeName("ffprobe"));
            if (!File.Exists(probe)) probe = SearchPath("ffprobe").FirstOrDefault() ?? "";
            if (File.Exists(probe))
                return new FfmpegTools(ff, probe,
                    LocateTool(["scxvid", "SCXvid"], dataDirectory, dir),
                    LocateTool(["dovi_tool"], dataDirectory, dir),
                    LocateTool(["hdr10plus_tool"], dataDirectory, dir),
                    LocateVsPipe());
        }
        return null;
    }

    /// <summary>vspipe: PATH, $TNA_VSPIPE, the VapourSynth installer folders on Windows.</summary>
    public static string? LocateVsPipe()
    {
        if (Environment.GetEnvironmentVariable("TNA_VSPIPE") is { Length: > 0 } env && File.Exists(env)) return env;
        var found = SearchPath("vspipe").FirstOrDefault() ?? SearchPath("VSPipe").FirstOrDefault();
        if (found is not null) return found;
        if (OperatingSystem.IsWindows())
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\Programs" })
                foreach (var rel in new[] { "VapourSynth\\core\\vspipe.exe", "VapourSynth\\vspipe.exe" })
                    if (File.Exists(Path.Combine(root, rel))) return Path.Combine(root, rel);
        if (OperatingSystem.IsMacOS())
            foreach (var p in new[] { "/opt/homebrew/bin/vspipe", "/usr/local/bin/vspipe" })
                if (File.Exists(p)) return p;
        return null;
    }

    public static string? LocateTool(string[] names, string dataDirectory, string? extraDir = null)
    {
        var dirs = new List<string>();
        foreach (var n in names) dirs.Add(ManagedDir(dataDirectory, n.ToLowerInvariant()));
        if (extraDir is not null) dirs.Add(extraDir);
        dirs.Add(AppContext.BaseDirectory);
        dirs.Add(Path.Combine(dataDirectory, "ffmpeg"));
        if (OperatingSystem.IsMacOS()) dirs.AddRange(["/opt/homebrew/bin", "/usr/local/bin"]);
        foreach (var dir in dirs)
            foreach (var n in names)
                if (File.Exists(Path.Combine(dir, ExeName(n)))) return Path.Combine(dir, ExeName(n));
        return names.SelectMany(SearchPath).FirstOrDefault();
    }

    private static IEnumerable<string> SearchPath(string tool) => SearchPathExact(ExeName(tool));

    private static IEnumerable<string> SearchPathExact(string file)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string full;
            try { full = Path.Combine(dir.Trim('"'), file); }
            catch (ArgumentException) { continue; }
            if (File.Exists(full)) yield return full;
        }
    }
}
