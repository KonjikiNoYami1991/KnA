using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KnA.Core.Localization;

namespace KnA.Core.Ai;

/// <summary>
/// Minimal OpenAI-compatible chat client: Ollama (/v1), LM Studio, Unsloth Studio (llama-server), llama.cpp,
/// vLLM, LocalAI, Jan and cloud services all speak it. JSON answers are requested with a JSON schema
/// (structured outputs); servers that refuse it are asked again in plain JSON mode, then free text.
/// </summary>
public sealed class LlmClient(AiSettings settings, HttpClient? http = null)
{
    private readonly HttpClient _http = http ?? new HttpClient();

    private HttpRequestMessage Request(HttpMethod method, string path, JsonNode? body = null)
    {
        var r = new HttpRequestMessage(method, settings.EffectiveUrl + path);
        if (settings.HasApiKey) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        if (body is not null) r.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        return r;
    }

    public async Task<List<string>> ListModelsAsync(CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        using var res = await _http.SendAsync(Request(HttpMethod.Get, "/models"), cts.Token);
        var text = await res.Content.ReadAsStringAsync(cts.Token);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException(L.T("The LLM server answered {0}: {1}", (int)res.StatusCode, Short(text)));
        var data = JsonNode.Parse(text)?["data"]?.AsArray();
        return data?.Select(m => m?["id"]?.GetValue<string>()).OfType<string>().Order(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
    }

    /// <param name="images">JPEG/PNG bytes sent as data URLs (vision models).</param>
    public async Task<JsonObject> ChatJsonAsync(string model, string system, string user, JsonObject schema,
        IReadOnlyList<byte[]>? images = null, CancellationToken ct = default)
    {
        JsonNode content = user;
        if (images is { Count: > 0 })
        {
            var parts = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = user } };
            foreach (var img in images)
                parts.Add(new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject { ["url"] = "data:image/jpeg;base64," + Convert.ToBase64String(img) },
                });
            content = parts;
        }
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = system },
            new JsonObject { ["role"] = "user", ["content"] = content },
        };

        string? lastError = null;
        foreach (var mode in new[] { "json_schema", "json_object", "none" })
        {
            var body = new JsonObject { ["model"] = model, ["messages"] = messages.DeepClone(), ["temperature"] = 0.2, ["stream"] = false };
            if (mode == "json_schema")
                body["response_format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JsonObject { ["name"] = "kna_answer", ["schema"] = schema.DeepClone(), ["strict"] = false },
                };
            else if (mode == "json_object") body["response_format"] = new JsonObject { ["type"] = "json_object" };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 10, 3600)));
            using var res = await _http.SendAsync(Request(HttpMethod.Post, "/chat/completions", body), cts.Token);
            var text = await res.Content.ReadAsStringAsync(cts.Token);
            if (!res.IsSuccessStatusCode)
            {
                lastError = $"{(int)res.StatusCode}: {Short(text)}";
                // 400/422 = this server doesn't know the response format: try the next mode. Anything else is final.
                if ((int)res.StatusCode is 400 or 422 or 500 && mode != "none") continue;
                throw new InvalidOperationException(L.T("The LLM server answered {0}: {1}", (int)res.StatusCode, Short(text)));
            }
            var answer = JsonNode.Parse(text)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
            if (ExtractJson(answer) is { } obj) return obj;
            lastError = L.T("The model did not answer with JSON: {0}", Short(answer));
        }
        throw new InvalidOperationException(lastError ?? "LLM error");
    }

    /// <summary>First JSON object in a text (models like to wrap it in ```json fences or add a sentence).</summary>
    public static JsonObject? ExtractJson(string text)
    {
        // Reasoning models: drop <think>…</think>.
        int think = text.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (think >= 0) text = text[(think + 8)..];
        for (int start = text.IndexOf('{'); start >= 0; start = text.IndexOf('{', start + 1))
        {
            int depth = 0;
            bool str = false, esc = false;
            for (int i = start; i < text.Length; i++)
            {
                char c = text[i];
                if (str) { if (esc) esc = false; else if (c == '\\') esc = true; else if (c == '"') str = false; continue; }
                if (c == '"') str = true;
                else if (c == '{') depth++;
                else if (c == '}' && --depth == 0)
                {
                    try { if (JsonNode.Parse(text[start..(i + 1)]) is JsonObject o) return o; }
                    catch (JsonException) { }
                    break;
                }
            }
        }
        return null;
    }

    private static string Short(string s) => s.Length > 300 ? s[..300] + "…" : s.Trim();
}
