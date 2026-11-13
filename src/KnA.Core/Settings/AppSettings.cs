using System.Text.Json;
using System.Text.Json.Serialization;
using KnA.Core.Encoding;
using KnA.Core.Localization;
using KnA.Core.Tools;

namespace KnA.Core.Settings;

public enum ProcessPriority { Normal, BelowNormal, Idle }

public sealed class AppSettings
{
    // ---- general
    /// <summary>"Auto" (system language), "it" or "en".</summary>
    public string Language { get; set; } = "Auto";
    public bool RestoreQueue { get; set; } = true;
    public bool ConfirmOnClose { get; set; } = true;
    // ---- output
    /// <summary>Output file name: {name} = source name, {label} = profile/codec/quality summary, {profile}, {codec}, {res}, {date}.</summary>
    public string OutputNameTemplate { get; set; } = OutputNaming.DefaultTemplate;
    // ---- processing
    public ProcessPriority Priority { get; set; } = ProcessPriority.BelowNormal;
    /// <summary>Folder for temporary files (subtitles, fonts, HDR metadata, previews); null = data folder.</summary>
    public string? TempDirectory { get; set; }
    public int StallTimeoutMinutes { get; set; } = 10;
    public int KeepLogs { get; set; } = 300;

    public string? FfmpegPath { get; set; }
    /// <summary>
    /// Extra FFmpeg builds (e.g. one compiled with libfdk_aac, or a newer one with Vulkan encoders). They are
    /// tested at detection time and each job uses the build that has its encoders (custom builds first).
    /// </summary>
    public List<string> CustomFfmpegBuilds { get; set; } = [];
    /// <summary>FFmpeg downloaded by the component manager: "release" (newest release branch) or "master" (nightly).</summary>
    public string FfmpegChannel { get; set; } = "release";
    /// <summary>User-chosen companion programs: ffprobe, fdkaac, dovi_tool, hdr10plus_tool, vspipe, scxvid, python.</summary>
    public Dictionary<string, string> ToolPaths { get; set; } = [];
    public string? VaapiDevice { get; set; }
    /// <summary>null = write next to the source file.</summary>
    public string? OutputDirectory { get; set; }
    public JobSettings Defaults { get; set; } = new();
    public PostQueueAction PostAction { get; set; } = PostQueueAction.Nothing;
    /// <summary>"System", "Light" or "Dark".</summary>
    public string Theme { get; set; } = "System";
    public List<string> FolderExtensions { get; set; } = [.. MediaExtensions.Video];
    /// <summary>Blu-ray / DVD: queue every title longer than <see cref="DiscMinMinutes"/> (episodes) instead of the main one only.</summary>
    public bool DiscAllTitles { get; set; } = true;
    public int DiscMinMinutes { get; set; } = 3;
    /// <summary>Extra FFmpeg arguments, space separated (advanced).</summary>
    public string ExtraArguments { get; set; } = "";
    public FfmpegCapabilities? Capabilities { get; set; }
    public bool CheckUpdatesOnStart { get; set; } = true;
    public DateTimeOffset? LastUpdateCheck { get; set; }
    /// <summary>Preset names offered in the Windows "Send to" menu.</summary>
    public List<string> SendToPresets { get; set; } = [];
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 800;
}

public static class MediaExtensions
{
    public static readonly string[] Video =
        [".mkv", ".mp4", ".m2ts", ".ts", ".avi", ".mov", ".webm", ".rmvb", ".ogm", ".flv", ".vob", ".mpg", ".mpeg", ".3gp", ".m4v", ".wmv", ".vpy", ".avs",
         ".mxf", ".mts", ".m2t", ".mpv", ".y4m", ".nut", ".dv", ".wtv", ".dvr-ms", ".asf", ".f4v", ".ogv", ".divx", ".evo", ".iso"];

    public static bool IsVideo(string path) => Video.Contains(Path.GetExtension(path).ToLowerInvariant());
}

/// <summary>Loads/saves settings as JSON in the data directory (AppData/KnA, or $KNA_DATA_DIR in Docker).</summary>
public sealed class SettingsStore
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public string DataDirectory { get; }
    public string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public string LogsDirectory => Path.Combine(DataDirectory, "logs");
    private string? _tempOverride;
    public string TempDirectory => _tempOverride ?? Path.Combine(DataDirectory, "temp");

    /// <summary>Applies the settings that change paths or global behaviour.</summary>
    public int KeepLogs { get; private set; } = 300;

    public void Apply(AppSettings s)
    {
        KeepLogs = Math.Clamp(s.KeepLogs, 10, 100_000);
        _tempOverride = string.IsNullOrWhiteSpace(s.TempDirectory) ? null : Path.Combine(s.TempDirectory, "KnA-temp");
        Directory.CreateDirectory(TempDirectory);
        L.Override = s.Language is "it" or "en" ? s.Language : null;
        Media.Discs.DiscSource.MinTitleLength = TimeSpan.FromMinutes(Math.Clamp(s.DiscMinMinutes, 0, 600));
        Media.Discs.DiscSource.WorkDirectory = Path.Combine(TempDirectory, "discs");
    }

    public SettingsStore(string? dataDirectory = null)
    {
        DataDirectory = dataDirectory
                        ?? AppEnv.Get("DATA_DIR") ?? AppEnv.DefaultDataDirectory();
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(TempDirectory);
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Json) ?? new AppSettings();
                Apply(loaded);
                return loaded;
            }
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException)
        {
            // Keep the broken file for inspection instead of silently losing it.
            try { File.Copy(SettingsPath, SettingsPath + $".broken-{DateTime.Now:yyyyMMddHHmmss}", overwrite: true); } catch (IOException) { }
        }
        return new AppSettings();
    }

    /// <summary>Keeps the newest <paramref name="keep"/> job logs.</summary>
    public void RotateLogs(int keep = 300)
    {
        try
        {
            foreach (var old in new DirectoryInfo(LogsDirectory).GetFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc).Skip(keep))
                try { old.Delete(); } catch (IOException) { }
        }
        catch (IOException) { }
    }

    /// <summary>Removes temporary folders left by a crash or a killed process.</summary>
    public void CleanTemp()
    {
        try
        {
            foreach (var d in Directory.GetDirectories(TempDirectory))
                try { Directory.Delete(d, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        catch (IOException) { }
    }

    /// <summary>Writes an unhandled exception to logs/crash-*.log.</summary>
    public string WriteCrashLog(Exception ex, string where)
    {
        var path = Path.Combine(LogsDirectory, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        try { File.WriteAllText(path, $"KnA {Queue.AppInfo.Version} ({where})\n{RuntimeInformationText()}\n\n{ex}"); } catch (IOException) { }
        return path;
    }

    private static string RuntimeInformationText() =>
        $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} · {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture} · .NET {Environment.Version}";

    public void Save(AppSettings settings)
    {
        Apply(settings);
        var tmp = SettingsPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
        File.Move(tmp, SettingsPath, overwrite: true);
    }
}
