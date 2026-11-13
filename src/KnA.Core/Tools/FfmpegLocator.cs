namespace KnA.Core.Tools;

public sealed record FfmpegTools(string Ffmpeg, string Ffprobe, string? Scxvid, string? DoviTool = null, string? Hdr10PlusTool = null, string? VsPipe = null)
{
    /// <summary>Standalone Fraunhofer AAC encoder (shipped with KnA, or installed by the component manager).</summary>
    public string? Fdkaac { get; init; }
}

/// <summary>Tool ids accepted in <see cref="Settings.AppSettings.ToolPaths"/>.</summary>
public static class ToolIds
{
    public const string Ffprobe = "ffprobe", Fdkaac = "fdkaac", DoviTool = "dovi_tool", Hdr10PlusTool = "hdr10plus_tool",
        VsPipe = "vspipe", Scxvid = "scxvid", Python = "python";
    public static readonly string[] All = [Ffprobe, Fdkaac, DoviTool, Hdr10PlusTool, VsPipe, Scxvid, Python];
}

/// <summary>
/// Finds the external programs. Order: configured path → KnA-managed copies in the data folder
/// (installed by the component manager) → next to the app → PATH → Homebrew / jellyfin-ffmpeg.
/// </summary>
public static class FfmpegLocator
{
    public static string ExeName(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    public static string ManagedDir(string dataDirectory, string component) =>
        component == "ffmpeg" ? Path.Combine(dataDirectory, "ffmpeg") : Path.Combine(dataDirectory, "tools", component);

    public static FfmpegTools? Locate(string? configuredFfmpeg, string dataDirectory) =>
        Locate(configuredFfmpeg, dataDirectory, null);

    /// <summary>
    /// <paramref name="toolPaths"/>: user-chosen executables (tool id → path); a missing file is ignored
    /// and the normal search applies.
    /// </summary>
    public static FfmpegTools? Locate(string? configuredFfmpeg, string dataDirectory, IReadOnlyDictionary<string, string>? toolPaths)
    {
        string? Custom(string id) => toolPaths?.GetValueOrDefault(id) is { Length: > 0 } p && File.Exists(p) ? p : null;
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredFfmpeg))
            candidates.Add(Directory.Exists(configuredFfmpeg) ? Path.Combine(configuredFfmpeg, ExeName("ffmpeg")) : configuredFfmpeg);
        if (KnA.Core.Settings.AppEnv.Get("FFMPEG") is { } env) candidates.Add(env);
        candidates.Add(Path.Combine(ManagedDir(dataDirectory, "ffmpeg"), ExeName("ffmpeg")));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg", ExeName("ffmpeg")));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, ExeName("ffmpeg")));
        candidates.AddRange(SearchPath("ffmpeg"));
        if (OperatingSystem.IsMacOS()) candidates.AddRange(["/opt/homebrew/bin/ffmpeg", "/usr/local/bin/ffmpeg"]);
        if (OperatingSystem.IsLinux()) candidates.Add("/usr/lib/jellyfin-ffmpeg/ffmpeg");

        foreach (var ff in candidates.Where(File.Exists))
        {
            var dir = Path.GetDirectoryName(ff)!;
            var probe = Custom(ToolIds.Ffprobe) ?? Path.Combine(dir, ExeName("ffprobe"));
            if (!File.Exists(probe)) probe = SearchPath("ffprobe").FirstOrDefault() ?? "";
            if (File.Exists(probe))
                return new FfmpegTools(ff, probe,
                    Custom(ToolIds.Scxvid) ?? LocateTool(["scxvid", "SCXvid"], dataDirectory, dir),
                    Custom(ToolIds.DoviTool) ?? LocateTool(["dovi_tool"], dataDirectory, dir),
                    Custom(ToolIds.Hdr10PlusTool) ?? LocateTool(["hdr10plus_tool"], dataDirectory, dir),
                    Custom(ToolIds.VsPipe) ?? LocateVsPipe())
                {
                    Fdkaac = Custom(ToolIds.Fdkaac) ?? LocateFdkaac(dataDirectory, dir),
                };
        }
        return null;
    }

    /// <summary>fdkaac: $KNA_FDKAAC, component folder, "tools" next to the app (release bundles), next to FFmpeg, PATH.</summary>
    public static string? LocateFdkaac(string dataDirectory, string? ffmpegDir = null)
    {
        if (Settings.AppEnv.Get("FDKAAC") is { } env && File.Exists(env)) return env;
        var bundled = Path.Combine(AppContext.BaseDirectory, "tools", ExeName("fdkaac"));
        if (File.Exists(bundled)) return bundled;
        return LocateTool(["fdkaac"], dataDirectory, ffmpegDir);
    }

    /// <summary>vspipe: PATH, $KNA_VSPIPE, the VapourSynth installer folders on Windows.</summary>
    public static string? LocateVsPipe()
    {
        if (KnA.Core.Settings.AppEnv.Get("VSPIPE") is { } env && File.Exists(env)) return env;
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
