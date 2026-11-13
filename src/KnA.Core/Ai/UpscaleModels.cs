using System.Globalization;
using KnA.Core.Encoding;
using KnA.Core.Localization;
using KnA.Core.Media;
using KnA.Core.Tools;

namespace KnA.Core.Ai;

public sealed record UpscaleModel(string Id, string Name, string Description, int Scale, string File, string Url, string License);

/// <summary>Neural models for the Python sidecar (official Real-ESRGAN releases, BSD-3-Clause), plus any local file.</summary>
public static class UpscaleModels
{
    private const string Rel = "https://github.com/xinntao/Real-ESRGAN/releases/download/";

    public static IReadOnlyList<UpscaleModel> All { get; } =
    [
        new("realesr-animevideov3", "AnimeVideo v3 (×4)", "Fast compact model for anime video; temporally stable.", 4,
            "realesr-animevideov3.pth", Rel + "v0.2.5.0/realesr-animevideov3.pth", "BSD-3-Clause"),
        new("RealESRGAN_x4plus_anime_6B", "Real-ESRGAN anime 6B (×4)", "Sharper anime line art, slower than AnimeVideo v3.", 4,
            "RealESRGAN_x4plus_anime_6B.pth", Rel + "v0.2.2.4/RealESRGAN_x4plus_anime_6B.pth", "BSD-3-Clause"),
        new("realesr-general-x4v3", "General v3 (×4)", "Compact model for live action, light and fast.", 4,
            "realesr-general-x4v3.pth", Rel + "v0.2.5.0/realesr-general-x4v3.pth", "BSD-3-Clause"),
        new("RealESRGAN_x4plus", "Real-ESRGAN x4plus (×4)", "Classic heavy model for photos and live action.", 4,
            "RealESRGAN_x4plus.pth", Rel + "v0.1.0/RealESRGAN_x4plus.pth", "BSD-3-Clause"),
        new("RealESRGAN_x2plus", "Real-ESRGAN x2plus (×2)", "Heavy ×2 model: 1080p → 4K with less overshoot.", 2,
            "RealESRGAN_x2plus.pth", Rel + "v0.2.1/RealESRGAN_x2plus.pth", "BSD-3-Clause"),
    ];

    public static string ModelDir(string dataDirectory) => Path.Combine(dataDirectory, "ai", "models");

    /// <summary>Model id from the list, or a path to a local .pth/.safetensors/.onnx file (scale read from its name: "x2", "2x"…).</summary>
    public static (string Path, int Scale, UpscaleModel? Known) Resolve(string? id, string dataDirectory)
    {
        var known = All.FirstOrDefault(m => m.Id == (id ?? All[0].Id));
        if (known is not null) return (Path.Combine(ModelDir(dataDirectory), known.File), known.Scale, known);
        var file = id!;
        var m = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(file), @"(?:^|[^a-z0-9])(?:x([1-8])|([1-8])x)(?:[^a-z0-9]|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        int scale = m.Success ? int.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, CultureInfo.InvariantCulture) : 4;
        return (file, scale, null);
    }

    /// <summary>Downloads a known model if it is missing.</summary>
    public static async Task<string> EnsureAsync(string? id, string dataDirectory, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var (path, _, known) = Resolve(id, dataDirectory);
        if (File.Exists(path)) return path;
        if (known is null) throw new FileNotFoundException(L.T("Model file not found: {0}", path), path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".download";
        await GitHubReleases.DownloadAsync(known.Url, tmp, progress, ct);
        File.Move(tmp, path, overwrite: true);
        return path;
    }
}

/// <summary>
/// The neural upscaler: ai/kna_upscale.py decodes the source with FFmpeg (with KnA's pre-filters: deinterlace,
/// crop, tone mapping, denoise…), runs the model with PyTorch (spandrel loads Real-ESRGAN, Real-CUGAN, SwinIR,
/// SPAN… checkpoints) and writes raw RGB48 frames to stdout, which the encoding FFmpeg reads as its video input.
/// </summary>
public static class UpscaleSidecar
{
    public const string ScriptName = "kna_upscale.py";

    /// <summary>The script shipped next to the app ("ai" folder), or $KNA_AI_SCRIPT.</summary>
    public static string? Script()
    {
        if (Settings.AppEnv.Get("AI_SCRIPT") is { } env && File.Exists(env)) return env;
        foreach (var dir in new[] { Path.Combine(AppContext.BaseDirectory, "ai"), AppContext.BaseDirectory })
            if (File.Exists(Path.Combine(dir, ScriptName))) return Path.Combine(dir, ScriptName);
        return null;
    }

    /// <summary>Python of the AI component (venv in the data folder), the configured one, or python3/python on the PATH.</summary>
    public static string? Python(AiSettings s, string dataDirectory)
    {
        if (!string.IsNullOrWhiteSpace(s.Python) && File.Exists(s.Python)) return s.Python;
        var venv = Path.Combine(dataDirectory, "ai", "venv", OperatingSystem.IsWindows() ? "Scripts" : "bin", FfmpegLocator.ExeName("python"));
        if (File.Exists(venv)) return venv;
        return FfmpegLocator.LocateTool(["python3", "python"], dataDirectory);
    }

    public static string Matrix(VideoStream v, HdrAction hdr) =>
        hdr == HdrAction.Preserve || (v.ColorSpace?.StartsWith("bt2020", StringComparison.Ordinal) ?? false) && hdr != HdrAction.ToneMap ? "bt2020"
        : v.ColorSpace is "bt470bg" or "smpte170m" || (v.ColorSpace is null && v.Height < 720) ? "bt601" : "bt709";

    /// <summary>Command line of the sidecar for one run (start/end in seconds on the source timeline).</summary>
    public static List<string> Arguments(string script, string ffmpeg, string source, IReadOnlyList<string> inputArgs, string? preFilter,
        int width, int height, string fps, string matrix, string model, int scale, AiSettings ai, double start, double? end)
    {
        var a = new List<string> { "-u", script, "--ffmpeg", ffmpeg, "--input", source,
            "--width", width.ToString(CultureInfo.InvariantCulture), "--height", height.ToString(CultureInfo.InvariantCulture),
            "--fps", fps, "--matrix", matrix, "--model", model, "--scale", scale.ToString(CultureInfo.InvariantCulture),
            "--device", ai.Device, "--tile", ai.Tile.ToString(CultureInfo.InvariantCulture) };
        if (ai.Fp16) a.Add("--fp16");
        if (inputArgs.Count > 0) a.AddRange(["--input-args", System.Text.Json.JsonSerializer.Serialize(inputArgs)]);
        if (!string.IsNullOrWhiteSpace(preFilter)) a.AddRange(["--vf", preFilter]);
        if (start > 0) a.AddRange(["--start", start.ToString("0.###", CultureInfo.InvariantCulture)]);
        if (end is { } e) a.AddRange(["--end", e.ToString("0.###", CultureInfo.InvariantCulture)]);
        return a;
    }
}
