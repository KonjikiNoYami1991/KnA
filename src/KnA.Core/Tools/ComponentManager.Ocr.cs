using KnA.Core.Localization;
using KnA.Core.Ocr;

namespace KnA.Core.Tools;

/// <summary>
/// Subtitle OCR as components, like Subtitle Edit: "tesseract" (Windows: the Tesseract 5.5.0 package Subtitle Edit
/// downloads; Linux/macOS: the system package) and "ocr:&lt;language&gt;" (official tessdata_best models).
/// </summary>
public sealed partial class ComponentManager
{
    /// <summary>Models offered in the list (any other language is downloaded by the first job that needs it).</summary>
    public static readonly (string Code, string Name)[] OcrLanguages =
    [
        ("ita", "Italiano"), ("eng", "English"), ("fra", "Français"), ("deu", "Deutsch"), ("spa", "Español"), ("por", "Português"),
        ("nld", "Nederlands"), ("pol", "Polski"), ("rus", "Русский"), ("jpn", "日本語"), ("chi_sim", "中文 (简体)"), ("kor", "한국어"),
    ];

    private async Task<List<ComponentStatus>> CheckOcrAsync(CancellationToken ct)
    {
        var list = new List<ComponentStatus>();
        var exe = SubtitleOcr.LocateTesseract(dataDirectory);
        string? version = exe is null ? null : await ReadVersionAsync(exe, ["--version"], ct);
        bool managed = exe?.StartsWith(SubtitleOcr.Folder(dataDirectory), StringComparison.OrdinalIgnoreCase) ?? false;
        string? note = exe is not null ? null
            : OperatingSystem.IsWindows() ? null
            : OperatingSystem.IsMacOS() ? L.T("Install it with Homebrew: brew install tesseract")
            : L.T("Install it with your package manager: apt install tesseract-ocr (dnf / pacman: tesseract)");
        list.Add(new ComponentStatus("tesseract", "Tesseract OCR",
            L.T("Reads picture subtitles (Blu-ray PGS) and turns them into text (SRT), like Subtitle Edit."),
            version?.Split(' ', 2).ElementAtOrDefault(1) ?? version, exe, managed, OperatingSystem.IsWindows() ? "5.5.0" : null,
            OperatingSystem.IsWindows() && exe is null, OperatingSystem.IsWindows(), note));
        // archive engine (7-Zip) and PaddleOCR, the second OCR engine of Subtitle Edit
        {
            var py = ArchiveEngine.Python(dataDirectory);
            bool have = ArchiveEngine.IsInstalled(dataDirectory);
            list.Add(new ComponentStatus("archive", L.T("Archive engine (7-Zip)"),
                L.T("Opens 7-Zip archives (also split into .001/.002), needed by PaddleOCR. A standalone Python with py7zr: nothing is installed on the system."),
                have ? "py7zr" : null, have ? ArchiveEngine.Folder(dataDirectory) : null, have, null, false, true,
                have ? null : py is null ? L.T("A portable Python is downloaded with it (about 30 MB).") : null));
        }
        bool paddleOs = (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) && Arch == "x64";
        {
            var paddle = PaddleOcr.Locate(dataDirectory);
            var state = LoadState();
            list.Add(new ComponentStatus("paddleocr", "PaddleOCR (CPU)",
                L.T("Second OCR engine (PP-OCRv5, the standalone build Subtitle Edit uses): often better than Tesseract on unusual fonts and Asian scripts. About 1 GB."),
                paddle is null ? null : state.TryGetValue("paddleocr", out var pv) ? pv.Version : PaddleOcr.Version, paddle, paddle is not null, PaddleOcr.Version,
                false, paddleOs, paddleOs ? null : L.T("Available for Windows and Linux x64.")));
            list.Add(new ComponentStatus("paddleocr-gpu", "PaddleOCR (NVIDIA GPU)",
                L.T("The same engine on an NVIDIA GPU (CUDA 12.9): much faster on long tracks. Several GB; replaces the CPU build."),
                null, null, false, PaddleOcr.Version, false, paddleOs, null));
            bool models = PaddleOcr.ModelsInstalled(dataDirectory);
            list.Add(new ComponentStatus("paddleocr-models", L.T("PaddleOCR models"),
                L.T("Detection and recognition models (PP-OCRv5) for every language PaddleOCR reads."),
                models ? PaddleOcr.Version : null, models ? PaddleOcr.ModelFolder(dataDirectory) : null, models, PaddleOcr.Version, false, paddleOs, null));
        }
        foreach (var (code, name) in OcrLanguages)
        {
            var folder = SubtitleOcr.FindModel(dataDirectory, exe, code);
            var path = folder is null ? null : Path.Combine(folder, code + ".traineddata");
            list.Add(new ComponentStatus("ocr:" + code, L.T("OCR model: {0}", name),
                L.T("Tesseract language model (tessdata_best) for the subtitle OCR."),
                path is null ? null : $"{new FileInfo(path).Length / 1048576.0:0.#} MB", path,
                path?.StartsWith(dataDirectory, StringComparison.OrdinalIgnoreCase) ?? false, null, false, true,
                path is null ? L.T("Downloaded automatically by the first job that uses it, or now.") : null));
        }
        return list;
    }

    private async Task InstallTesseractAsync(string temp, IProgress<double>? progress, IProgress<string>? log, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(OperatingSystem.IsMacOS()
                ? L.T("Install it with Homebrew: brew install tesseract")
                : L.T("Install it with your package manager: apt install tesseract-ocr (dnf / pacman: tesseract)"));
        var zip = Path.Combine(temp, "Tesseract550.zip");
        log?.Report(L.T("Downloading {0}…", "Tesseract 5.5.0"));
        await GitHubReleases.DownloadAsync(SubtitleOcr.WindowsTesseractUrl, zip, progress, ct);
        var target = SubtitleOcr.Folder(dataDirectory);
        var x = Path.Combine(temp, "x");
        await ExtractAsync(zip, x, ct);
        var exe = Directory.EnumerateFiles(x, "tesseract.exe", SearchOption.AllDirectories).FirstOrDefault()
                  ?? throw new InvalidOperationException(L.T("The package does not contain {0}.", "tesseract.exe"));
        // the folder of tesseract.exe (with its DLLs) becomes tools/tesseract; models already there are kept
        var root = Path.GetDirectoryName(exe)!;
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var dst = Path.Combine(target, Path.GetRelativePath(root, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            ReplaceFile(f, dst);
        }
        await VerifyAsync(Path.Combine(target, "tesseract.exe"), ["--version"], ct);
        SaveState("tesseract", "5.5.0");
        log?.Report(L.T("Installed {0}.", "Tesseract 5.5.0"));
    }

    private async Task InstallOcrModelAsync(string id, IProgress<double>? progress, IProgress<string>? log, CancellationToken ct)
    {
        var code = id["ocr:".Length..];
        log?.Report(L.T("Downloading {0}…", code + ".traineddata"));
        await SubtitleOcr.DownloadModelAsync(dataDirectory, code, best: true, progress, ct);
        SaveState(id, "tessdata_best");
    }

    private async Task InstallPaddleOcrAsync(string id, string temp, IProgress<double>? progress, IProgress<string>? log, CancellationToken ct)
    {
        if (!((OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) && Arch == "x64"))
            throw new PlatformNotSupportedException(L.T("Available for Windows and Linux x64."));
        if (!ArchiveEngine.IsInstalled(dataDirectory))
        {
            log?.Report(L.T("Installing the archive engine first (PaddleOCR comes as 7-Zip archives)…"));
            await ArchiveEngine.InstallAsync(this, dataDirectory, Path.Combine(temp, "archive"), progress, log, ct);
        }
        var urls = id switch
        {
            "paddleocr-models" => PaddleOcr.Models,
            "paddleocr-gpu" => OperatingSystem.IsWindows() ? PaddleOcr.WindowsGpu : PaddleOcr.LinuxGpu,
            _ => OperatingSystem.IsWindows() ? PaddleOcr.WindowsCpu : PaddleOcr.LinuxCpu,
        };
        var files = new List<string>();
        foreach (var url in urls)
        {
            var file = Path.Combine(temp, url[(url.LastIndexOf('/') + 1)..]);
            log?.Report(L.T("Downloading {0}…", Path.GetFileName(file)));
            await GitHubReleases.DownloadAsync(url, file, progress, ct);
            files.Add(file);
        }
        var x = Path.Combine(temp, "x");
        log?.Report(L.T("Extracting {0}…", Path.GetFileName(files[0])));
        await ArchiveEngine.ExtractAsync(dataDirectory, files[0], x, ct);
        if (id == "paddleocr-models")
        {
            // the folder holding det/ and rec/ becomes tools/paddleocr/models
            var root = Directory.EnumerateDirectories(x, "det", SearchOption.AllDirectories).Select(Path.GetDirectoryName).FirstOrDefault(d => d is not null && Directory.Exists(Path.Combine(d, "rec")))
                       ?? throw new InvalidOperationException(L.T("The package does not contain {0}.", "det/rec"));
            var target = PaddleOcr.ModelFolder(dataDirectory);
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.Move(root!, target);
        }
        else
        {
            var exeName = OperatingSystem.IsWindows() ? "paddleocr.exe" : "paddleocr.bin";
            var exe = Directory.EnumerateFiles(x, exeName, SearchOption.AllDirectories).FirstOrDefault()
                      ?? throw new InvalidOperationException(L.T("The package does not contain {0}.", exeName));
            var target = Path.Combine(PaddleOcr.Folder(dataDirectory), "engine");
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.Move(Path.GetDirectoryName(exe)!, target);
            if (!OperatingSystem.IsWindows())
            {
                var bin = Path.Combine(target, exeName);
                File.SetUnixFileMode(bin, File.GetUnixFileMode(bin) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }
        }
        SaveState(id == "paddleocr-gpu" ? "paddleocr" : id, PaddleOcr.Version + (id == "paddleocr-gpu" ? " GPU" : ""));
        log?.Report(L.T("Installed {0}.", id));
    }
}
