using System.Globalization;
using KnA.Core.Encoding;
using KnA.Core.Localization;
using KnA.Core.Media;

namespace KnA.Core.Ai;

public sealed record Anime4KMode(string Id, string Label, string Description, string[] Shaders);

/// <summary>
/// GPU upscaling inside FFmpeg through libplacebo (Vulkan, any vendor): EWA Lanczos-sharp, Anime4K CNN shaders
/// (bloc97/Anime4K, MIT) and the FSRCNNX neural shader (igv/FSRCNN-TensorFlow). Shaders are downloaded by the
/// component manager into "&lt;data&gt;/tools/shaders".
/// </summary>
public static class Upscalers
{
    /// <summary>The recommended Anime4K presets (from the project's mpv instructions), as one concatenated shader each.</summary>
    public static IReadOnlyList<Anime4KMode> Anime4KModes { get; } =
    [
        new("A", "Mode A", "Best for most anime: restores blur and compression, then doubles the size.",
            ["Anime4K_Clamp_Highlights.glsl", "Anime4K_Restore_CNN_VL.glsl", "Anime4K_Upscale_CNN_x2_VL.glsl",
             "Anime4K_AutoDownscalePre_x2.glsl", "Anime4K_AutoDownscalePre_x4.glsl", "Anime4K_Upscale_CNN_x2_M.glsl"]),
        new("B", "Mode B", "Softer restore for sources with ringing or aliasing.",
            ["Anime4K_Clamp_Highlights.glsl", "Anime4K_Restore_CNN_Soft_VL.glsl", "Anime4K_Upscale_CNN_x2_VL.glsl",
             "Anime4K_AutoDownscalePre_x2.glsl", "Anime4K_AutoDownscalePre_x4.glsl", "Anime4K_Upscale_CNN_x2_M.glsl"]),
        new("C", "Mode C", "Denoising upscale for already sharp, noisy sources.",
            ["Anime4K_Clamp_Highlights.glsl", "Anime4K_Upscale_Denoise_CNN_x2_VL.glsl",
             "Anime4K_AutoDownscalePre_x2.glsl", "Anime4K_AutoDownscalePre_x4.glsl", "Anime4K_Upscale_CNN_x2_M.glsl"]),
        new("A+A", "Mode A+A", "Mode A twice: strongest restoration (slow, may oversharpen).",
            ["Anime4K_Clamp_Highlights.glsl", "Anime4K_Restore_CNN_VL.glsl", "Anime4K_Upscale_CNN_x2_VL.glsl", "Anime4K_Restore_CNN_M.glsl",
             "Anime4K_AutoDownscalePre_x2.glsl", "Anime4K_AutoDownscalePre_x4.glsl", "Anime4K_Upscale_CNN_x2_M.glsl"]),
    ];

    public const string FsrcnnxShader = "FSRCNNX_x2_16-0-4-1.glsl";

    /// <summary>
    /// Explicit Vulkan device for the libplacebo filter (more reliable than its own device search, which also
    /// skips software devices). $KNA_VULKAN_DEVICE picks one: an index ("1") or a name part ("NVIDIA", "llvmpipe").
    /// </summary>
    public static List<string> VulkanDeviceArgs() =>
        ["-init_hw_device", "vulkan=vk" + (Settings.AppEnv.Get("VULKAN_DEVICE") is { } d ? ":" + d : ""), "-filter_hw_device", "vk"];

    public static string ShaderDir(string dataDirectory) => Path.Combine(dataDirectory, "tools", "shaders");

    /// <summary>Methods that run in FFmpeg's libplacebo filter.</summary>
    public static bool UsesLibplacebo(UpscaleMethod m) => m is UpscaleMethod.Placebo or UpscaleMethod.Anime4K or UpscaleMethod.Fsrcnnx;

    /// <summary>Target size when enlarging (KnA never enlarges with the standard scaler).</summary>
    public static ScaleResult? Enlarge(VideoStream v, int targetHeight)
    {
        if (targetHeight <= 0 || v.Height <= 0) return null;
        double dar = v.DisplayAspectRatio > 0 ? v.DisplayAspectRatio : (double)v.Width / v.Height;
        int boxW = Even(targetHeight * 16.0 / 9.0);
        int h = Even(targetHeight), w = Even(h * dar);
        if (w > boxW) { w = boxW; h = Even(w / dar); }
        return h > v.Height || w > v.DisplayWidth + 1 ? new ScaleResult(w, h, true) : null;
    }

    /// <summary>
    /// The libplacebo filter that replaces "scale=" (null when the method doesn't apply), with the shader file
    /// it needs. <paramref name="size"/> = output size; with AlwaysApply and no size change the source size.
    /// </summary>
    public static string? PlaceboFilter(UpscaleSettings u, ScaleResult size, string? shaderFile, bool tenBit)
    {
        if (!UsesLibplacebo(u.Method)) return null;
        var f = $"libplacebo=w={size.Width}:h={size.Height}:format={(tenBit ? "yuv420p10le" : "yuv420p")}";
        f += u.Method switch
        {
            UpscaleMethod.Placebo => ":upscaler=ewa_lanczossharp:downscaler=mitchell",
            _ => ":upscaler=ewa_lanczossharp:custom_shader_path=" + EscapeFilterPath(shaderFile ?? "missing.glsl"),
        };
        return f + ",setsar=1";
    }

    /// <summary>Shader file a method needs (null for EWA Lanczos), in the shader folder.</summary>
    public static string? ShaderFile(UpscaleSettings u, string dataDirectory) => u.Method switch
    {
        UpscaleMethod.Anime4K => Path.Combine(ShaderDir(dataDirectory), $"kna-anime4k-{(u.Model ?? "A").Replace("+", "plus")}.glsl"),
        UpscaleMethod.Fsrcnnx => Path.Combine(ShaderDir(dataDirectory), FsrcnnxShader),
        _ => null,
    };

    /// <summary>Concatenates the Anime4K shaders of every mode (libplacebo takes one file).</summary>
    public static void BuildAnime4KChains(string shaderDir)
    {
        foreach (var mode in Anime4KModes)
        {
            var parts = mode.Shaders.Select(s => Directory.EnumerateFiles(shaderDir, s, SearchOption.AllDirectories).FirstOrDefault()).ToList();
            if (parts.Any(p => p is null)) continue;
            var text = string.Join("\n\n", parts.Select(p => File.ReadAllText(p!)));
            File.WriteAllText(Path.Combine(shaderDir, $"kna-anime4k-{mode.Id.Replace("+", "plus")}.glsl"), text);
        }
    }

    /// <summary>Filter-graph escaping for a path inside an option value (Windows drive letters, quotes).</summary>
    public static string EscapeFilterPath(string path) =>
        "'" + path.Replace("\\", "/").Replace("'", "'\\''").Replace(":", "\\:") + "'";

    private static int Even(double value) => Math.Max(2, (int)Math.Round(value / 2.0, MidpointRounding.AwayFromZero) * 2);
}

public static class UpscaleText
{
    public static string Display(this UpscaleMethod m) => m switch
    {
        UpscaleMethod.Standard => L.T("Standard (Lanczos, never enlarges)"),
        UpscaleMethod.Placebo => L.T("GPU EWA Lanczos (libplacebo)"),
        UpscaleMethod.Anime4K => L.T("Anime4K CNN (GPU, animation)"),
        UpscaleMethod.Fsrcnnx => L.T("FSRCNNX neural shader (GPU)"),
        UpscaleMethod.Neural => L.T("AI model: Real-ESRGAN & co. (Python)"),
        _ => m.ToString(),
    };

    public static string Description(this UpscaleMethod m) => m switch
    {
        UpscaleMethod.Standard => L.T("Resizes down only; a lower resolution is kept as it is."),
        UpscaleMethod.Placebo => L.T("High-quality resampling on any Vulkan GPU. Needs FFmpeg with libplacebo."),
        UpscaleMethod.Anime4K => L.T("Real-time CNN shaders made for anime line art. Needs libplacebo and the Anime4K component."),
        UpscaleMethod.Fsrcnnx => L.T("Small neural network as a GPU shader, good on live action too. Needs libplacebo and the shader component."),
        UpscaleMethod.Neural => L.T("Full neural models (Real-ESRGAN, AnimeVideo v3, any spandrel model) on CUDA, ROCm, Apple MPS or CPU. Best quality, slowest."),
        _ => "",
    };
}
