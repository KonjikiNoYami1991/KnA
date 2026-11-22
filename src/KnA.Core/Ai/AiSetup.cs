using System.Net.Sockets;
using System.Text.Json.Nodes;
using KnA.Core.Localization;

namespace KnA.Core.Ai;

/// <summary>Result of a connection check: the models found, or what went wrong and what to do about it.</summary>
public sealed record AiCheck(bool Ok, IReadOnlyList<string> Models, IReadOnlyList<string> VisionModels, string Message, string? Hint);

/// <summary>
/// The guided set-up of the AI assistant (desktop and web wizard): how to install and start each local server, a
/// connection check that explains failures in plain words, the models to download when none is there, and a real
/// test question to the chosen model before saving.
/// </summary>
public static class AiSetup
{
    /// <summary>What to do, step by step, to have the server running (and where the "model" comes from).</summary>
    public static IReadOnlyList<string> Install(string provider) => provider switch
    {
        "ollama" =>
        [
            L.T("Download Ollama from ollama.com and install it (Windows, macOS, Linux)."),
            L.T("It starts by itself in the background; on Linux: ollama serve."),
            L.T("Download a text model: ollama pull qwen2.5:7b (about 5 GB; 3b is lighter)."),
            L.T("For the frame analysis, a vision model too: ollama pull qwen2.5vl:7b"),
        ],
        "lmstudio" =>
        [
            L.T("Download LM Studio from lmstudio.ai and install it."),
            L.T("Search and download a model (e.g. Qwen2.5 7B Instruct; Qwen2.5-VL for the frame analysis)."),
            L.T("Developer tab → Start server (port 1234)."),
        ],
        "unsloth" =>
        [
            L.T("Install Unsloth Studio and start it: unsloth studio -p 8888"),
            L.T("Copy the API key Studio shows: the next step asks for it."),
        ],
        "llamacpp" =>
        [
            L.T("Download llama.cpp (github.com/ggml-org/llama.cpp, releases) and a GGUF model."),
            L.T("Start it: llama-server -m model.gguf --port 8080"),
        ],
        "vllm" => [L.T("Start vLLM with a model: vllm serve Qwen/Qwen2.5-7B-Instruct")],
        "localai" => [L.T("Start LocalAI (docker run -p 8080:8080 localai/localai) and install a model from its gallery.")],
        "jan" => [L.T("Download Jan from jan.ai, download a model, then Settings → Local API Server → Start.")],
        "openai" => [L.T("Create an API key in your provider's account: the next step asks for it. Frames and requests are sent to the cloud.")],
        _ => [L.T("Any server with an OpenAI-compatible /v1 API: type its address in the next step.")],
    };

    /// <summary>Whether the provider needs an API key.</summary>
    public static bool NeedsKey(string provider) => provider is "unsloth" or "openai";

    /// <summary>Models that see pictures (for the frame analysis), from their names.</summary>
    public static bool IsVision(string model)
    {
        var m = model.ToLowerInvariant();
        return new[] { "vl", "vision", "llava", "gemma3", "gemma-3", "minicpm-v", "moondream", "pixtral", "bakllava", "internvl", "llama4", "gpt-4o", "gpt-4.1", "gpt-5" }
            .Any(w => m.Contains(w, StringComparison.Ordinal));
    }

    /// <summary>Which models to download when the server has none.</summary>
    public static (string Text, string Vision) Suggested(string provider) => provider switch
    {
        "ollama" => ("ollama pull qwen2.5:7b", "ollama pull qwen2.5vl:7b"),
        "openai" => ("gpt-4o-mini", "gpt-4o-mini"),
        _ => ("Qwen2.5 7B Instruct", "Qwen2.5-VL 7B"),
    };

    /// <summary>Asks the server for its models; failures come back with a plain explanation and what to try.</summary>
    public static async Task<AiCheck> CheckAsync(AiSettings s, CancellationToken ct = default)
    {
        try
        {
            var models = await new LlmClient(s).ListModelsAsync(ct);
            if (models.Count == 0)
            {
                var (text, _) = Suggested(s.Provider);
                return new AiCheck(true, [], [], L.T("Connected, but the server has no model yet."), L.T("Download one ({0}), then check again.", text));
            }
            return new AiCheck(true, models, models.Where(IsVision).ToList(), L.T("Connected: {0} model(s) found.", models.Count), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or System.Text.Json.JsonException or UriFormatException)
        {
            string text = ex.Message;
            string hint = ex switch
            {
                HttpRequestException { InnerException: SocketException } or HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError } =>
                    L.T("Nothing answers at {0}: the server is not running, or it uses another port. Start it (previous step) and check the address.", s.EffectiveUrl),
                TaskCanceledException => L.T("No answer within 15 seconds: the server is busy loading a model, or a firewall blocks it. Try again in a moment."),
                InvalidOperationException when text.Contains("401", StringComparison.Ordinal) || text.Contains("403", StringComparison.Ordinal) =>
                    L.T("The server wants an API key (or the key is wrong)."),
                InvalidOperationException when text.Contains("404", StringComparison.Ordinal) =>
                    L.T("The server answered but not as an OpenAI-compatible API: the address usually ends with /v1."),
                UriFormatException => L.T("The address is not valid: write it like http://localhost:11434/v1"),
                _ => L.T("Check the address and that the server is running."),
            };
            return new AiCheck(false, [], [], L.T("Cannot reach the LLM server at {0}: {1}", s.EffectiveUrl, text), hint);
        }
    }

    /// <summary>A real question to the model (answer in JSON, as the assistant needs); null = it works.</summary>
    public static async Task<string?> TestAsync(AiSettings s, string model, CancellationToken ct = default)
    {
        try
        {
            var schema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["ok"] = new JsonObject { ["type"] = "boolean" } },
                ["required"] = new JsonArray("ok"),
            };
            var r = await new LlmClient(s).ChatJsonAsync(model, "Answer only with JSON.", "Reply {\"ok\": true}.", schema, ct: ct);
            return r.ContainsKey("ok") ? null : L.T("The model answered, but not in JSON: choose another model (instruction-tuned, 7B or more).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return ex.Message;
        }
    }
}
