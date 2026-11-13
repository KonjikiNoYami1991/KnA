using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using KnA.Core.Encoding;
using KnA.Core.Localization;
using KnA.Core.Media;
using KnA.Core.Tools;

namespace KnA.Core.Ai;

public sealed record AiSuggestion(JobSettings Settings, string Explanation, IReadOnlyList<string> Changes, IReadOnlyList<string> Warnings)
{
    /// <summary>Vision analysis only: what the model saw.</summary>
    public string? ContentType { get; init; }
}

/// <summary>
/// The LLM assistant. It never runs anything itself: the model returns a small JSON object restricted to KnA's
/// own options (enums are listed in the schema), KnA validates every field, checks the encoders against the
/// hardware detection and only then proposes the settings, which the user applies.
///  • <see cref="SuggestAsync"/>: "make it small for Telegram, 720p, burn the subtitles" → settings;
///  • <see cref="AnalyzeAsync"/>: a vision model looks at frames of the file and picks filters and the upscaler.
/// </summary>
public sealed class Assistant(AiSettings ai, FfmpegCapabilities? caps, HttpClient? http = null)
{
    private readonly LlmClient _llm = new(ai, http);

    private static JsonArray Names<T>() where T : struct, Enum => new(Enum.GetNames<T>().Select(n => (JsonNode)n).ToArray());

    /// <summary>JSON schema of the answer: every field optional, values restricted to KnA's options.</summary>
    public static JsonObject Schema(bool vision)
    {
        var props = new JsonObject
        {
            ["explanation"] = new JsonObject { ["type"] = "string", ["description"] = "One or two sentences for the user, in the user's language." },
            ["profile"] = new JsonObject { ["type"] = "string", ["enum"] = Names<ProfileKind>() },
            ["codec"] = new JsonObject { ["type"] = "string", ["enum"] = Names<VideoCodec>() },
            ["backend"] = new JsonObject { ["type"] = "string", ["enum"] = Names<EncoderBackend>() },
            ["quality"] = new JsonObject { ["type"] = "string", ["enum"] = Names<QualityLevel>() },
            ["resolution"] = new JsonObject { ["type"] = "integer", ["enum"] = new JsonArray(Resolutions.All.Select(r => (JsonNode)r).ToArray()) },
            ["subtitles"] = new JsonObject { ["type"] = "string", ["enum"] = Names<SubtitleMode>() },
            ["tenBit"] = new JsonObject { ["type"] = "boolean" },
            ["container"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(Containers.All.Select(c => (JsonNode)c).ToArray()) },
            ["audioCodec"] = new JsonObject { ["type"] = "string", ["enum"] = Names<AudioCodec>() },
            ["audioBitrate"] = new JsonObject { ["type"] = "integer", ["description"] = "kb/s per stereo pair, omit for automatic" },
            ["downmixStereo"] = new JsonObject { ["type"] = "boolean" },
            ["normalizeLoudness"] = new JsonObject { ["type"] = "boolean" },
            ["hdr"] = new JsonObject { ["type"] = "string", ["enum"] = Names<HdrMode>() },
            ["deinterlace"] = new JsonObject { ["type"] = "string", ["enum"] = Names<DeinterlaceMode>() },
            ["denoise"] = new JsonObject { ["type"] = "string", ["enum"] = Names<FilterStrength>() },
            ["deband"] = new JsonObject { ["type"] = "string", ["enum"] = Names<FilterStrength>() },
            ["sharpen"] = new JsonObject { ["type"] = "string", ["enum"] = Names<FilterStrength>() },
            ["upscale"] = new JsonObject { ["type"] = "string", ["enum"] = Names<UpscaleMethod>() },
            ["upscaleModel"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray([.. UpscaleModels.All.Select(m => (JsonNode)m.Id), .. Upscalers.Anime4KModes.Select(m => (JsonNode)m.Id)]),
            },
            ["proresProfile"] = new JsonObject { ["type"] = "string", ["enum"] = Names<ProResProfile>() },
        };
        if (vision)
        {
            props["contentType"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("anime", "live_action", "cgi", "screen_recording", "mixed") };
            props["observations"] = new JsonObject { ["type"] = "string", ["description"] = "Noise, banding, blur, interlacing, letterbox seen in the frames." };
        }
        return new JsonObject { ["type"] = "object", ["properties"] = props, ["additionalProperties"] = false };
    }

    private string SystemPrompt(bool vision)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You configure KnA, a video encoder built on FFmpeg. Answer ONLY with a JSON object that follows the schema; omit fields you would not change.");
        sb.AppendLine("Profiles: " + string.Join("; ", Profiles.All.Select(p => $"{p.Kind}={p.EnglishName} ({p.EnglishDescription}) codecs [{string.Join(",", p.Codecs)}]")));
        sb.AppendLine("Quality goes from VeryHigh (best, biggest) to Draft. Resolution is the target height, 0 = keep. The standard scaler never enlarges: to enlarge set 'upscale'.");
        sb.AppendLine("Upscale: Placebo = GPU EWA Lanczos; Anime4K = CNN shaders for anime (model A/B/C/A+A); Fsrcnnx = neural shader; Neural = Real-ESRGAN models (" +
                      string.Join(", ", UpscaleModels.All.Select(m => $"{m.Id}: {m.Description}")) + ").");
        sb.AppendLine("Hardware encoders that passed the test on this machine: " + (caps is null ? "unknown (prefer Software)" :
            string.Join(", ", caps.Encoders.Where(e => e.Usable && e.Backend != EncoderBackend.Software).Select(e => $"{e.Codec}/{e.Backend}{(e.Works10Bit ? " 10-bit" : "")}").Distinct())) + ".");
        if (vision) sb.AppendLine("You also receive frames of the video: judge the content type and the source quality, then pick filters and the upscaler (anime → Anime4K or realesr-animevideov3; live action → Fsrcnnx or realesr-general-x4v3). Do not denoise clean sources.");
        sb.AppendLine("Write 'explanation' (and 'observations') in " + (L.IsItalian() ? "Italian" : "English") + ".");
        return sb.ToString();
    }

    public async Task<AiSuggestion> SuggestAsync(string request, JobSettings current, MediaInfo? media, CancellationToken ct = default)
    {
        var model = ai.Model ?? throw new InvalidOperationException(L.T("Choose a model in Settings → AI."));
        var user = $"Request: {request}\nCurrent settings: profile {current.Profile}, codec {current.Codec}, backend {current.Backend}, quality {current.Quality}, " +
                   $"resolution {current.Resolution}, subtitles {current.Subtitles}, audio {current.AudioCodec}." +
                   (media is null ? "" : $"\nFile: {media.Summary}");
        var answer = await _llm.ChatJsonAsync(model, SystemPrompt(false), user, Schema(false), null, ct);
        return Apply(answer, current);
    }

    /// <param name="frames">JPEG frames spread over the video.</param>
    public async Task<AiSuggestion> AnalyzeAsync(MediaInfo media, JobSettings current, IReadOnlyList<byte[]> frames, CancellationToken ct = default)
    {
        var model = ai.VisionModel ?? ai.Model ?? throw new InvalidOperationException(L.T("Choose a model in Settings → AI."));
        var v = media.PrimaryVideo;
        var user = $"Analyse this video and propose settings for a high quality archive of it.\nFile: {media.Summary}\n" +
                   (v is null ? "" : $"Video: {v.Width}x{v.Height}, {v.FrameRate.ToString("0.###", CultureInfo.InvariantCulture)} fps, {(v.IsInterlaced ? "interlaced" : "progressive")}, {v.BitDepth}-bit, {v.Hdr.Description}.\n") +
                   $"Current settings: profile {current.Profile}, codec {current.Codec}, resolution {current.Resolution}.";
        var answer = await _llm.ChatJsonAsync(model, SystemPrompt(true), user, Schema(true), frames, ct);
        var s = Apply(answer, current);
        var observations = answer["observations"]?.GetValue<string>();
        return s with
        {
            ContentType = answer["contentType"]?.GetValue<string>(),
            Explanation = observations is { Length: > 0 } ? s.Explanation + (s.Explanation.Length > 0 ? " " : "") + observations : s.Explanation,
        };
    }

    /// <summary>Validates the model's answer field by field and merges it into the current settings.</summary>
    public AiSuggestion Apply(JsonObject a, JobSettings current)
    {
        var changes = new List<string>();
        var warnings = new List<string>();
        var s = current;

        T? Enum<T>(string key) where T : struct, System.Enum
        {
            if (a[key] is not JsonValue v || !v.TryGetValue<string>(out var text)) return null;
            if (System.Enum.TryParse<T>(text, true, out var e) && System.Enum.IsDefined(e)) { changes.Add($"{key} = {e}"); return e; }
            warnings.Add(L.T("Ignored an unknown value for {0}: {1}", key, text));
            return null;
        }
        bool? Bool(string key) => a[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
        int? Int(string key) => a[key] is JsonValue v && (v.TryGetValue<int>(out var i) || (v.TryGetValue<double>(out var d) && (i = (int)d) == i)) ? i : null;

        if (Enum<ProfileKind>("profile") is { } p) s = s with { Profile = p, Container = null };
        if (Enum<VideoCodec>("codec") is { } c) s = s with { Codec = c };
        if (Enum<EncoderBackend>("backend") is { } b) s = s with { Backend = b };
        if (Enum<QualityLevel>("quality") is { } q) s = s with { Quality = q };
        if (Int("resolution") is { } r)
        {
            if (Resolutions.All.Contains(r)) { s = s with { Resolution = r }; changes.Add($"resolution = {r}"); }
            else warnings.Add(L.T("Ignored an unknown value for {0}: {1}", "resolution", r));
        }
        if (Enum<SubtitleMode>("subtitles") is { } sm) s = s with { Subtitles = sm };
        if (Bool("tenBit") is { } tb) { s = s with { TenBit = tb }; changes.Add($"tenBit = {tb}"); }
        if (a["container"] is JsonValue cv && cv.TryGetValue<string>(out var cont))
        {
            if (Containers.All.Contains(cont)) { s = s with { Container = cont }; changes.Add($"container = {cont}"); }
            else warnings.Add(L.T("Ignored an unknown value for {0}: {1}", "container", cont));
        }
        if (Enum<AudioCodec>("audioCodec") is { } ac) s = s with { AudioCodec = ac };
        if (Int("audioBitrate") is { } abr && abr is >= 8 and <= 1536) { s = s with { AudioBitrate = abr }; changes.Add($"audioBitrate = {abr}"); }
        if (Bool("downmixStereo") is { } dm) { s = s with { AudioFilters = s.AudioFilters with { DownmixStereo = dm } }; changes.Add($"downmixStereo = {dm}"); }
        if (Bool("normalizeLoudness") is { } nl) { s = s with { AudioFilters = s.AudioFilters with { Normalize = nl } }; changes.Add($"normalize = {nl}"); }
        if (Enum<HdrMode>("hdr") is { } hdr) s = s with { Hdr = hdr };
        if (Enum<DeinterlaceMode>("deinterlace") is { } di) s = s with { VideoFilters = s.VideoFilters with { Deinterlace = di } };
        if (Enum<FilterStrength>("denoise") is { } dn) s = s with { VideoFilters = s.VideoFilters with { Denoise = dn } };
        if (Enum<FilterStrength>("deband") is { } db) s = s with { VideoFilters = s.VideoFilters with { Deband = db } };
        if (Enum<FilterStrength>("sharpen") is { } sh) s = s with { VideoFilters = s.VideoFilters with { Sharpen = sh } };
        if (Enum<UpscaleMethod>("upscale") is { } um) s = s with { Upscale = s.Upscale with { Method = um } };
        if (a["upscaleModel"] is JsonValue mv && mv.TryGetValue<string>(out var model))
        {
            bool ok = s.Upscale.Method == UpscaleMethod.Anime4K ? Upscalers.Anime4KModes.Any(m => m.Id == model)
                : s.Upscale.Method == UpscaleMethod.Neural && UpscaleModels.All.Any(m => m.Id == model);
            if (ok) { s = s with { Upscale = s.Upscale with { Model = model } }; changes.Add($"upscaleModel = {model}"); }
        }
        if (Enum<ProResProfile>("proresProfile") is { } pr) s = s with { Pro = s.Pro with { ProRes = pr } };

        // The profile decides which codecs exist; the hardware detection decides which GPU works.
        if (!s.ProfileDefinition.Codecs.Contains(s.Codec) && s.ProfileDefinition.Codecs.Count > 0)
        {
            warnings.Add(L.T("{0} is not available in the {1} profile: {2} is used.", s.Codec, s.ProfileDefinition.Name, s.ProfileDefinition.Codecs[0]));
            s = s with { Codec = s.ProfileDefinition.Codecs[0] };
        }
        if (s.Backend != EncoderBackend.Software && (VideoEncoders.EncoderName(s.Codec, s.Backend) is null
                                                     || caps is not null && !caps.IsUsable(s.Codec, s.Backend, s.EffectiveTenBit) && !caps.IsUsable(s.Codec, s.Backend, false)))
        {
            warnings.Add(L.T("{0} on {1} did not pass the hardware test here: CPU encoding is used.", s.Codec.Display(), s.Backend.Display()));
            s = s with { Backend = EncoderBackend.Software };
        }
        if (s.Upscale.Method is UpscaleMethod.Placebo or UpscaleMethod.Anime4K or UpscaleMethod.Fsrcnnx && caps is { HasLibplacebo: false })
            warnings.Add(L.T("{0} needs an FFmpeg with libplacebo and a working Vulkan GPU.", UpscaleText.Display(s.Upscale.Method)));

        var explanation = a["explanation"] is JsonValue ev && ev.TryGetValue<string>(out var e2) ? e2 : "";
        return new AiSuggestion(s with { PresetName = null }, explanation, changes, warnings);
    }
}
