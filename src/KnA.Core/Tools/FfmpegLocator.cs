namespace KnA.Core.Tools;

public sealed record FfmpegTools(string Ffmpeg, string Ffprobe, string? Scxvid, string? DoviTool = null, string? Hdr10PlusTool = null, string? VsPipe = null)
{
    /// <summary>Standalone Fraunhofer AAC encoder (shipped with KnA, or installed by the component manager).</summary>
    public string? Fdkaac { get; init; }
    /// <summary>qaac64 (Windows), only when Apple's CoreAudioToolbox can be loaded by it.</summary>
    public string? Qaac { get; init; }
    /// <summary>rigaya's hardware encoders: NVEncC (NVIDIA), QSVEncC (Intel), VCEEncC (AMD).</summary>
    public string? NvencC { get; init; }
    public string? QsvencC { get; init; }
    public string? VceencC { get; init; }
}

/// <summary>Tool ids accepted in <see cref="Settings.AppSettings.ToolPaths"/>.</summary>
public static class ToolIds
{
    public const string Ffprobe = "ffprobe", Fdkaac = "fdkaac", DoviTool = "dovi_tool", Hdr10PlusTool = "hdr10plus_tool",
        VsPipe = "vspipe", Scxvid = "scxvid", Python = "python", Qaac = "qaac", NvencC = "nvencc", QsvencC = "qsvencc", VceencC = "vceencc";
    public static readonly string[] All = [Ffprobe, Fdkaac, DoviTool, Hdr10PlusTool, VsPipe, Scxvid, Python, Qaac, NvencC, QsvencC, VceencC];
}

/// <summary>
/// Finds the external programs. Order: configured path → KnA-managed copies in the data folder
/// (installed by the component manager) → next to the app → PATH → Homebrew / jellyfin-ffmpeg.
/// </summary>
public static class FfmpegLocator
{
    public static string ExeName(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    public static string ManagedDir(string dataDirectory, string component) =>
        component == "ffmpeg" ? KnA.Core.Settings.AppPaths.Ffmpeg(dataDirectory) : Path.Combine(KnA.Core.Settings.AppPaths.Tools(dataDirectory), component);

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
        candidates.Add(Path.Combine(dataDirectory, "ffmpeg", ExeName("ffmpeg")));   // portable copies of KnA ≤ 3.1.1
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", ExeName("ffmpeg")));   // bundled (portable packages)
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
                    Qaac = Custom(ToolIds.Qaac) ?? LocateQaac(dataDirectory),
                    NvencC = Custom(ToolIds.NvencC) ?? LocateTool(["NVEncC64", "nvencc"], dataDirectory, Path.Combine(KnA.Core.Settings.AppPaths.Tools(dataDirectory), "nvencc")),
                    QsvencC = Custom(ToolIds.QsvencC) ?? LocateTool(["QSVEncC64", "qsvencc"], dataDirectory, Path.Combine(KnA.Core.Settings.AppPaths.Tools(dataDirectory), "qsvencc")),
                    VceencC = Custom(ToolIds.VceencC) ?? LocateTool(["VCEEncC64", "vceencc"], dataDirectory, Path.Combine(KnA.Core.Settings.AppPaths.Tools(dataDirectory), "vceencc")),
                };
        }
        return null;
    }

    /// <summary>fdkaac: $KNA_FDKAAC, component folder, "tools" next to the app (release bundles), next to FFmpeg, PATH.</summary>
    public static string? LocateFdkaac(string dataDirectory, string? ffmpegDir = null)
    {
        if (Settings.AppEnv.Get("FDKAAC") is { } env && File.Exists(env)) return env;
        foreach (var bundled in new[] { Path.Combine(AppContext.BaseDirectory, "tools", "fdkaac", ExeName("fdkaac")), Path.Combine(AppContext.BaseDirectory, "tools", ExeName("fdkaac")) })
            if (File.Exists(bundled)) return bundled;
        return LocateTool(["fdkaac"], dataDirectory, ffmpegDir);
    }

    /// <summary>
    /// qaac64.exe (Windows only) when Apple's CoreAudioToolbox is reachable: QTfiles64 next to it (extracted from the
    /// iTunes installer by the component manager), or an installed iTunes / Apple Application Support.
    /// </summary>
    public static string? LocateQaac(string dataDirectory)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var exe = LocateTool(["qaac64"], dataDirectory, Path.Combine(KnA.Core.Settings.AppPaths.Tools(dataDirectory), "qaac"));
        if (exe is null) return null;
        var dir = Path.GetDirectoryName(exe)!;
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
        var program = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        bool coreAudio = new[]
        {
            Path.Combine(dir, "QTfiles64"), dir, Path.Combine(common, "Apple", "Apple Application Support"), Path.Combine(program, "iTunes"),
        }.Any(d => File.Exists(Path.Combine(d, "CoreAudioToolbox.dll")));
        return coreAudio ? exe : null;
    }

    /// <summary>vspipe: PATH, $KNA_VSPIPE, the VapourSynth installer folders on Windows.</summary>
    public static string? LocateVsPipe()
    {
        if (KnA.Core.Settings.AppEnv.Get("VSPIPE") is { } env && File.Exists(env)) return env;
        // VapourSynth portable bundled with KnA or installed by the component manager.
        foreach (var root in new[] { Path.Combine(AppContext.BaseDirectory, "tools", "vapoursynth") })
            if (File.Exists(Path.Combine(root, ExeName("vspipe")))) return Path.Combine(root, ExeName("vspipe"));
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
        // Programs shipped with an installed package or a Docker image: <program folder>/tools/<name>.
        foreach (var n in names) dirs.Add(Path.Combine(AppContext.BaseDirectory, "tools", n.ToLowerInvariant()));
        dirs.Add(AppContext.BaseDirectory);
        dirs.Add(Path.Combine(dataDirectory, "ffmpeg"));
        foreach (var n in names) dirs.Add(Path.Combine(dataDirectory, "tools", n.ToLowerInvariant()));   // portable copies of KnA ≤ 3.1.1
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
