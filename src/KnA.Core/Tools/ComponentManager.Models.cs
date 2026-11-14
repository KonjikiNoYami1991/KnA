using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using KnA.Core.Ai;
using KnA.Core.Localization;

namespace KnA.Core.Tools;

/// <summary>A model the assistant can use, pulled through Ollama.</summary>
public sealed record LlmModel(string Name, bool Vision, string SizeLabel, string Description);

/// <summary>
/// Models as components: the neural upscaler's checkpoints ("model:&lt;id&gt;", downloaded from the official releases)
/// and the assistant's LLMs ("llm:&lt;name&gt;", pulled by the Ollama server the assistant talks to).
/// </summary>
public sealed partial class ComponentManager
{
    /// <summary>The assistant's settings (LLM server), given by the queue.</summary>
    public Func<AiSettings>? AiSettings { get; set; }
    /// <summary>Called after a pull (model name, is a vision model): the queue makes it the assistant's model if none is set.</summary>
    public Action<string, bool>? LlmPulled { get; set; }

    /// <summary>Ollama models that answer KnA's JSON well (text) and see pictures (vision, for the content analysis).</summary>
    public static IReadOnlyList<LlmModel> RecommendedLlms { get; } =
    [
        new("qwen3:8b", false, "≈5 GB", "Settings assistant: follows the JSON schema well (8 GB VRAM or 16 GB RAM)."),
        new("qwen3:4b", false, "≈2.5 GB", "Lighter assistant for small GPUs, mini PCs and CPUs."),
        new("qwen2.5vl:7b", true, "≈6 GB", "Vision model for the content analysis (anime / live action, grain, letterbox)."),
        new("qwen3-vl:8b", true, "≈6 GB", "Newer vision model, better at reading frames."),
        new("gemma3:4b", true, "≈3.3 GB", "Small text + vision model: one model for both tasks."),
    ];

    private static string OllamaRoot(AiSettings s)
    {
        var url = s.EffectiveUrl.TrimEnd('/');
        return url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? url[..^3] : url;
    }

    private async Task<List<ComponentStatus>> CheckModelsAsync(CancellationToken ct)
    {
        var list = new List<ComponentStatus>();
        var dir = UpscaleModels.ModelDir(dataDirectory);
        foreach (var m in UpscaleModels.All)
        {
            var path = Path.Combine(dir, m.File);
            bool have = File.Exists(path);
            list.Add(new ComponentStatus("model:" + m.Id, L.T("Upscale model: {0}", m.Name),
                L.T(m.Description) + " " + L.T("Real-ESRGAN ({0}), for the AI upscaler.", m.License),
                have ? $"{new FileInfo(path).Length / 1048576.0:0.#} MB" : null, have ? path : null, have, null, false, true,
                have ? null : L.T("Downloaded automatically by the first job that uses it, or now.")));
        }

        var ai = AiSettings?.Invoke();
        if (ai is null) return list;
        if (ai.Provider != "ollama")
        {
            list.Add(new ComponentStatus("llm", L.T("Assistant models"), L.T("Models for the settings assistant and the content analysis."),
                null, null, false, null, false, false,
                L.T("KnA downloads models through Ollama. With {0}, download them from its own model list.", ai.Provider)));
            return list;
        }
        HashSet<string>? have2 = null;
        string? note = null;
        try { have2 = await OllamaModelsAsync(ai, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            note = L.T("Ollama is not reachable at {0}: install it from ollama.com (or start it), then come back here.", OllamaRoot(ai));
        }
        foreach (var m in RecommendedLlms)
        {
            bool installed = have2?.Contains(m.Name) == true || have2?.Contains(m.Name + ":latest") == true;
            list.Add(new ComponentStatus("llm:" + m.Name, (m.Vision ? L.T("Vision model: {0}", m.Name) : L.T("Assistant model: {0}", m.Name)),
                L.T(m.Description) + " " + m.SizeLabel, installed ? "Ollama" : null, null, installed, null, false, have2 is not null,
                installed ? (ai.Model == m.Name || ai.VisionModel == m.Name ? L.T("In use.") : L.T("Choose it in Settings → AI.")) : note));
        }
        return list;
    }

    private static async Task<HashSet<string>> OllamaModelsAsync(AiSettings ai, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(4));
        var text = await GitHubReleases.Client.GetStringAsync(OllamaRoot(ai) + "/api/tags", cts.Token);
        var models = JsonNode.Parse(text)?["models"]?.AsArray() ?? [];
        return models.Select(m => m?["name"]?.GetValue<string>()).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>ollama pull through its HTTP API (streamed status lines with the bytes done).</summary>
    public async Task PullLlmAsync(string model, IProgress<double>? progress, IProgress<string>? log, CancellationToken ct)
    {
        var ai = AiSettings?.Invoke() ?? new AiSettings();
        var body = new JsonObject { ["model"] = model, ["stream"] = true }.ToJsonString();
        using var req = new HttpRequestMessage(HttpMethod.Post, OllamaRoot(ai) + "/api/pull")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        using var res = await GitHubReleases.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException(L.T("Ollama answered {0}: {1}", (int)res.StatusCode, await res.Content.ReadAsStringAsync(ct)));
        using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(ct));
        string? last = null;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0) continue;
            JsonNode? n;
            try { n = JsonNode.Parse(line); } catch (JsonException) { continue; }
            if (n?["error"]?.GetValue<string>() is { } err) throw new InvalidOperationException("Ollama: " + err);
            var status = n?["status"]?.GetValue<string>();
            if (status is not null && status != last && !status.StartsWith("pulling ", StringComparison.Ordinal)) { log?.Report(status); last = status; }
            if (n?["total"]?.GetValue<long>() is > 0 and var total && n["completed"]?.GetValue<long>() is { } done)
                progress?.Report(Math.Min(100, done * 100.0 / total));
            if (status == "success") { progress?.Report(100); return; }
        }
        throw new InvalidOperationException(L.T("Ollama stopped before the model was complete."));
    }

    private async Task InstallModelAsync(string id, IProgress<double>? progress, IProgress<string>? log, CancellationToken ct)
    {
        if (id.StartsWith("model:", StringComparison.Ordinal))
        {
            var name = id[6..];
            log?.Report(L.T("Downloading {0}…", name));
            var path = await UpscaleModels.EnsureAsync(name, dataDirectory, progress, ct);
            log?.Report(path);
            SaveState(id, DateTimeOffset.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
        else
        {
            var name = id[4..];
            log?.Report(L.T("Downloading {0} with Ollama…", name));
            await PullLlmAsync(name, progress, log, ct);
            LlmPulled?.Invoke(name, RecommendedLlms.FirstOrDefault(m => m.Name == name)?.Vision == true);
        }
    }
}

public sealed partial class ComponentManager
{
    /// <summary>Building a release package: nothing that may not be redistributed is fetched (Apple's qaac files).</summary>
    public bool ForPackaging { get; set; }

    /// <summary>
    /// Fills the tools/ and ai/ folders of a portable copy at <paramref name="root"/> (it must contain portable.txt) with
    /// the given components, exactly as Settings → Components would: used by the release workflow
    /// ("KnA.Server --bundle &lt;root&gt; ffmpeg,nvencc,…"). Components that don't exist for this platform are skipped.
    /// </summary>
    public static async Task<int> BundleAsync(string root, IReadOnlyList<string> ids, TextWriter output, CancellationToken ct = default)
    {
        if (!File.Exists(Path.Combine(root, Settings.AppEnv.PortableMarker)))
        {
            output.WriteLine($"{root}: {Settings.AppEnv.PortableMarker} missing (not a portable folder)");
            return 2;
        }
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(data);
        var cm = new ComponentManager(data) { ForPackaging = true };
        int failed = 0;
        foreach (var id in ids)
        {
            output.WriteLine($"== {id}");
            try
            {
                await cm.InstallAsync(id, null, new Progress<string>(l => output.WriteLine("   " + l)), ct);
                output.WriteLine($"   ok");
            }
            catch (PlatformNotSupportedException ex) { output.WriteLine($"   skipped: {ex.Message}"); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                output.WriteLine($"   FAILED: {ex.Message}");
                failed++;
            }
        }
        // Temporary files of the installs are not part of a package.
        try { Directory.Delete(Path.Combine(data, "temp"), true); } catch (IOException) { }
        return failed == 0 ? 0 : 1;
    }
}
