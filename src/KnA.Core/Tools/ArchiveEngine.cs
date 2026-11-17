using KnA.Core.Localization;
using KnA.Core.Processing;

namespace KnA.Core.Tools;

/// <summary>
/// Archives .NET cannot open (7-Zip, also split into .001/.002 volumes): a standalone Python (KnA's own portable
/// copy from python-build-standalone, never the system one where a portable build exists) with py7zr, kept in
/// tools/archive. Installed on first need (or from Settings → Components, "archive"); zip/tar.gz/tar.xz keep using
/// the built-in extractors.
/// </summary>
public static class ArchiveEngine
{
    public static string Folder(string dataDirectory) => Path.Combine(Settings.AppPaths.Tools(dataDirectory), "archive");
    private static string Libs(string dataDirectory) => Path.Combine(Folder(dataDirectory), "lib");
    private static string Script(string dataDirectory) => Path.Combine(Folder(dataDirectory), "kna_archive.py");

    public static bool IsArchive(string path) =>
        path.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".7z.001", StringComparison.OrdinalIgnoreCase);

    public static bool IsInstalled(string dataDirectory) =>
        File.Exists(Script(dataDirectory)) && Directory.Exists(Path.Combine(Libs(dataDirectory), "py7zr")) && Python(dataDirectory) is not null;

    /// <summary>The standalone Python: KnA's portable copy; a system Python only where no portable build exists (Docker).</summary>
    public static string? Python(string dataDirectory)
    {
        if (Settings.AppEnv.Get("PYTHON") is { } forced && File.Exists(forced)) return forced;
        var portable = ComponentManager.PortablePython(dataDirectory);
        if (File.Exists(portable)) return portable;
        if (ComponentManager.PortablePythonTriple() is not null && !ComponentManager.IsContainer()) return null;
        return FfmpegLocator.LocateTool(["python3", "python"], dataDirectory);
    }

    private const string ScriptText = """
        # KnA archive helper: extracts 7-Zip archives (single or split .001/.002…) with py7zr.
        import os, sys
        sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "lib"))
        import py7zr
        src, dst = sys.argv[1], sys.argv[2]
        os.makedirs(dst, exist_ok=True)
        if src.endswith(".001"):
            import multivolumefile
            with multivolumefile.open(src[:-4], mode="rb") as vol:
                with py7zr.SevenZipFile(vol, "r") as z:
                    z.extractall(dst)
        else:
            with py7zr.SevenZipFile(src, "r") as z:
                z.extractall(dst)
        print("ok")
        """;

    /// <summary>Installs the portable Python when needed, then py7zr into tools/archive/lib.</summary>
    public static async Task InstallAsync(ComponentManager cm, string dataDirectory, string temp, IProgress<double>? progress, IProgress<string>? log, CancellationToken ct)
    {
        var py = Python(dataDirectory);
        if (py is null)
        {
            log?.Report(L.T("Downloading a portable Python (no installation, stays in KnA's data folder)…"));
            py = await cm.EnsurePortablePythonAsync(temp, progress, log, ct);
        }
        Directory.CreateDirectory(Libs(dataDirectory));
        log?.Report(L.T("Installing {0}…", "py7zr"));
        var r = await ProcessRunner.RunAsync(py, ["-m", "pip", "install", "--disable-pip-version-check", "--no-warn-script-location", "--upgrade",
            "--target", Libs(dataDirectory), "py7zr", "multivolumefile"], timeout: TimeSpan.FromMinutes(15), ct: ct);
        if (r.ExitCode != 0) throw new InvalidOperationException("pip: " + (r.StdErr + r.StdOut).Trim().Split('\n').LastOrDefault());
        await File.WriteAllTextAsync(Script(dataDirectory), ScriptText.Replace("\r", ""), ct);
    }

    /// <summary>Extracts a .7z (or the first volume .7z.001) into <paramref name="destination"/>.</summary>
    public static async Task ExtractAsync(string dataDirectory, string archive, string destination, CancellationToken ct)
    {
        var py = Python(dataDirectory);
        if (py is null || !IsInstalled(dataDirectory))
            throw new InvalidOperationException(L.T("7-Zip archives need the archive engine: install \"Archive engine\" in Settings → Components."));
        var r = await ProcessRunner.RunAsync(py, [Script(dataDirectory), archive, destination], timeout: TimeSpan.FromHours(2), ct: ct);
        if (r.ExitCode != 0) throw new InvalidOperationException("7z: " + (r.StdErr.Trim() is { Length: > 0 } e ? e.Split('\n').Last() : $"exit {r.ExitCode}"));
    }
}
