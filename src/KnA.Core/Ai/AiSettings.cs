using System.Text.Json.Serialization;

namespace KnA.Core.Ai;

/// <summary>A local (or remote) LLM server speaking the OpenAI chat-completions API.</summary>
public sealed record LlmProvider(string Id, string Name, string DefaultUrl, string Notes);

public sealed record AiSettings
{
    /// <summary>ollama, lmstudio, unsloth, llamacpp, vllm, localai, jan, openai, custom.</summary>
    public string Provider { get; init; } = "ollama";
    /// <summary>Base URL ending in /v1 (OpenAI-compatible); empty = the provider's default.</summary>
    public string? BaseUrl { get; init; }
    /// <summary>Text model (settings assistant).</summary>
    public string? Model { get; init; }
    /// <summary>Vision model for the content analysis (llava, qwen2.5-vl, gemma3…); empty = <see cref="Model"/>.</summary>
    public string? VisionModel { get; init; }
    /// <summary>Only for servers that need one (Unsloth Studio, OpenAI, protected vLLM…). Stored in settings.json.</summary>
    public string? ApiKey { get; init; }
    public int TimeoutSeconds { get; init; } = 180;

    // ---- neural upscaling sidecar (Python)
    /// <summary>Python interpreter with torch + spandrel (or onnxruntime); empty = "python3"/"python" on the PATH.</summary>
    public string? Python { get; init; }
    /// <summary>auto, cuda, rocm, mps, xpu, cpu.</summary>
    public string Device { get; init; } = "auto";
    /// <summary>Half precision on GPUs (twice as fast, same look).</summary>
    public bool Fp16 { get; init; } = true;
    /// <summary>Tile size for big frames on small GPUs (0 = whole frame).</summary>
    public int Tile { get; init; }

    [JsonIgnore] public bool HasApiKey => !string.IsNullOrEmpty(ApiKey);

    public string EffectiveUrl => (string.IsNullOrWhiteSpace(BaseUrl) ? Providers.FirstOrDefault(p => p.Id == Provider)?.DefaultUrl ?? "http://localhost:11434/v1" : BaseUrl).TrimEnd('/');

    public static IReadOnlyList<LlmProvider> Providers { get; } =
    [
        new("ollama", "Ollama", "http://localhost:11434/v1", "ollama pull qwen2.5:7b · vision: ollama pull qwen2.5vl:7b"),
        new("lmstudio", "LM Studio", "http://localhost:1234/v1", "Developer tab → Start server"),
        new("unsloth", "Unsloth Studio", "http://localhost:8888/v1", "unsloth studio -p 8888 · needs the API key Studio generates"),
        new("llamacpp", "llama.cpp server", "http://localhost:8080/v1", "llama-server -m model.gguf --port 8080"),
        new("vllm", "vLLM", "http://localhost:8000/v1", "vllm serve <model>"),
        new("localai", "LocalAI", "http://localhost:8080/v1", ""),
        new("jan", "Jan", "http://localhost:1337/v1", ""),
        new("openai", "OpenAI-compatible cloud", "https://api.openai.com/v1", "Needs an API key"),
        new("custom", "Custom", "http://localhost:8000/v1", "Any OpenAI-compatible /v1 endpoint"),
    ];
}
