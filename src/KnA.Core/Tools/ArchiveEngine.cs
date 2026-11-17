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

    /// <summary>py7zr's folder: the data folder's, else the one shipped with the program (packages, Docker, KnA.app).</summary>
    private static string Libs(string dataDirectory)
    {
        var own = Path.Combine(Folder(dataDirectory), "lib");
        var bundled = Settings.AppPaths.Bundled("tools", "archive", "lib");
        return !Directory.Exists(Path.Combine(own, "py7zr")) && Directory.Exists(Path.Combine(bundled, "py7zr")) ? bundled : own;
    }

    public static bool IsArchive(string path) =>
        path.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".7z.001", StringComparison.OrdinalIgnoreCase);

    public static bool IsInstalled(string dataDirectory) =>
        Directory.Exists(Path.Combine(Libs(dataDirectory), "py7zr")) && Python(dataDirectory) is not null;

    /// <summary>The standalone Python: KnA's portable copy; a system Python only where no portable build exists (Docker).</summary>
    public static string? Python(string dataDirectory)
    {
        if (Settings.AppEnv.Get("PYTHON") is { } forced && File.Exists(forced)) return forced;
        var portable = ComponentManager.PortablePython(dataDirectory);
        if (File.Exists(portable)) return portable;
        var bundled = OperatingSystem.IsWindows() ? Settings.AppPaths.Bundled("ai", "python", "python.exe") : Settings.AppPaths.Bundled("ai", "python", "bin", "python3");
        if (File.Exists(bundled)) return bundled;
        if (ComponentManager.PortablePythonTriple() is not null && !ComponentManager.IsContainer()) return null;
        return FfmpegLocator.LocateTool(["python3", "python"], dataDirectory);
    }

    private const string ScriptText = """
        # KnA archive helper: extracts 7-Zip archives (single or split .001/.002…) with py7zr.
        import os, sys
        src, dst, lib = sys.argv[1], sys.argv[2], sys.argv[3]
        sys.path.insert(0, lib)
        import py7zr
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
    }

    /// <summary>Extracts a .7z (or the first volume .7z.001) into <paramref name="destination"/>.</summary>
    public static async Task ExtractAsync(string dataDirectory, string archive, string destination, CancellationToken ct)
    {
        var py = Python(dataDirectory);
        if (py is null || !IsInstalled(dataDirectory))
            throw new InvalidOperationException(L.T("7-Zip archives need the archive engine: install \"Archive engine\" in Settings → Components."));
        // the helper script is written for each run (the program folder may be read-only: KnA.app, /opt/kna, Docker)
        var script = Path.Combine(Path.GetTempPath(), "kna_archive_" + Guid.NewGuid().ToString("N")[..8] + ".py");
        await File.WriteAllTextAsync(script, ScriptText.Replace("\r", ""), ct);
        try
        {
            var r = await ProcessRunner.RunAsync(py, [script, archive, destination, Libs(dataDirectory)], timeout: TimeSpan.FromHours(2), ct: ct);
            if (r.ExitCode != 0) throw new InvalidOperationException("7z: " + (r.StdErr.Trim() is { Length: > 0 } e ? e.Split('\n').Last() : $"exit {r.ExitCode}"));
        }
        finally { try { File.Delete(script); } catch (IOException) { } }
    }
}
