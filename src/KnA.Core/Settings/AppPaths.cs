namespace KnA.Core.Settings;

/// <summary>
/// Where the programs KnA manages live. A portable copy keeps everything in its own folder, each kind of
/// dependency in a subfolder next to KnA.exe: tools\ffmpeg, tools\fdkaac, tools\dovi_tool, tools\shaders…,
/// ai\ (portable Python, PyTorch environment, models) and data\ (settings, presets, queue, logs, temporary files).
/// An installed copy uses the per-user data folder (data\ffmpeg, data\tools\…, data\ai\…) as before.
/// </summary>
public static class AppPaths
{
    /// <summary>The program folder when <paramref name="dataDirectory"/> is the "data" folder of a portable copy, else null.</summary>
    public static string? PortableRoot(string dataDirectory)
    {
        try
        {
            var full = Path.GetFullPath(dataDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.Equals(Path.GetFileName(full), "data", StringComparison.OrdinalIgnoreCase)) return null;
            var root = Path.GetDirectoryName(full);
            return root is not null && File.Exists(Path.Combine(root, AppEnv.PortableMarker)) ? root : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>Managed programs (fdkaac, dovi_tool, shaders…; FFmpeg too in a portable copy).</summary>
    public static string Tools(string dataDirectory) =>
        PortableRoot(dataDirectory) is { } root ? Path.Combine(root, "tools") : Path.Combine(dataDirectory, "tools");

    /// <summary>The AI runtime: portable Python, the PyTorch environment (venv) and the models.</summary>
    public static string Ai(string dataDirectory)
    {
        var legacy = Path.Combine(dataDirectory, "ai");
        if (PortableRoot(dataDirectory) is not { } root) return legacy;
        var ai = Path.Combine(root, "ai");
        // Portable copies made by KnA 3.1.1 and older kept the AI runtime in data\ai: keep using it (a venv can't be moved).
        bool legacyRuntime = Directory.Exists(Path.Combine(legacy, "venv")) || Directory.Exists(Path.Combine(legacy, "python"));
        bool newRuntime = Directory.Exists(Path.Combine(ai, "venv")) || Directory.Exists(Path.Combine(ai, "python"));
        return legacyRuntime && !newRuntime ? legacy : ai;
    }

    /// <summary>FFmpeg + FFprobe managed by KnA.</summary>
    public static string Ffmpeg(string dataDirectory) =>
        PortableRoot(dataDirectory) is { } root ? Path.Combine(root, "tools", "ffmpeg") : Path.Combine(dataDirectory, "ffmpeg");
}
