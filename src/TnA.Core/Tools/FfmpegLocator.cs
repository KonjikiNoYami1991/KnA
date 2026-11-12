namespace TnA.Core.Tools;

public sealed record FfmpegTools(string Ffmpeg, string Ffprobe, string? Scxvid);

/// <summary>Finds ffmpeg/ffprobe: configured path → app folder → TnA data folder → PATH → Homebrew.</summary>
public static class FfmpegLocator
{
    public static string ExeName(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    public static FfmpegTools? Locate(string? configuredFfmpeg, string dataDirectory)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredFfmpeg))
            candidates.Add(Directory.Exists(configuredFfmpeg) ? Path.Combine(configuredFfmpeg, ExeName("ffmpeg")) : configuredFfmpeg);
        if (Environment.GetEnvironmentVariable("TNA_FFMPEG") is { Length: > 0 } env) candidates.Add(env);
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg", ExeName("ffmpeg")));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, ExeName("ffmpeg")));
        candidates.Add(Path.Combine(dataDirectory, "ffmpeg", ExeName("ffmpeg")));
        candidates.AddRange(SearchPath("ffmpeg"));
        if (OperatingSystem.IsMacOS())
            candidates.AddRange(["/opt/homebrew/bin/ffmpeg", "/usr/local/bin/ffmpeg"]);
        // jellyfin-ffmpeg (great VA-API/QSV support on Linux)
        if (OperatingSystem.IsLinux()) candidates.Add("/usr/lib/jellyfin-ffmpeg/ffmpeg");

        foreach (var ff in candidates.Where(File.Exists))
        {
            var dir = Path.GetDirectoryName(ff)!;
            var probe = Path.Combine(dir, ExeName("ffprobe"));
            if (!File.Exists(probe)) probe = SearchPath("ffprobe").FirstOrDefault() ?? "";
            if (File.Exists(probe)) return new FfmpegTools(ff, probe, LocateScxvid(dir, dataDirectory));
        }
        return null;
    }

    private static string? LocateScxvid(string ffmpegDir, string dataDirectory)
    {
        string[] names = [ExeName("scxvid"), ExeName("SCXvid")];
        foreach (var dir in new[] { ffmpegDir, AppContext.BaseDirectory, Path.Combine(dataDirectory, "ffmpeg") })
            foreach (var n in names)
                if (File.Exists(Path.Combine(dir, n))) return Path.Combine(dir, n);
        return names.SelectMany(SearchPathExact).FirstOrDefault();
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
