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
}
