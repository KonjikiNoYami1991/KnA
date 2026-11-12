using System.Text.Json;
using System.Text.Json.Serialization;
using TnA.Core.Encoding;
using TnA.Core.Tools;

namespace TnA.Core.Settings;

public sealed class AppSettings
{
    public string? FfmpegPath { get; set; }
    public string? VaapiDevice { get; set; }
    /// <summary>null = write next to the source file.</summary>
    public string? OutputDirectory { get; set; }
    public JobSettings Defaults { get; set; } = new();
    public PostQueueAction PostAction { get; set; } = PostQueueAction.Nothing;
    /// <summary>"System", "Light" or "Dark".</summary>
    public string Theme { get; set; } = "System";
    public List<string> FolderExtensions { get; set; } = [.. MediaExtensions.Video];
    /// <summary>Extra FFmpeg arguments, space separated (advanced).</summary>
    public string ExtraArguments { get; set; } = "";
    public FfmpegCapabilities? Capabilities { get; set; }
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 800;
}

public static class MediaExtensions
{
    public static readonly string[] Video =
        [".mkv", ".mp4", ".m2ts", ".ts", ".avi", ".mov", ".webm", ".rmvb", ".ogm", ".flv", ".vob", ".mpg", ".mpeg", ".3gp", ".m4v", ".wmv"];

    public static bool IsVideo(string path) => Video.Contains(Path.GetExtension(path).ToLowerInvariant());
}

/// <summary>Loads/saves settings as JSON in the data directory (AppData/TnA, or $TNA_DATA_DIR in Docker).</summary>
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
    public string TempDirectory => Path.Combine(DataDirectory, "temp");

    public SettingsStore(string? dataDirectory = null)
    {
        DataDirectory = dataDirectory
                        ?? (Environment.GetEnvironmentVariable("TNA_DATA_DIR") is { Length: > 0 } env
                            ? env
                            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData,
                                Environment.SpecialFolderOption.Create), "TnA"));
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(TempDirectory);
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Json) ?? new AppSettings();
        }
        catch (JsonException) { /* corrupted file → defaults */ }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        var tmp = SettingsPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
        File.Move(tmp, SettingsPath, overwrite: true);
    }
}
