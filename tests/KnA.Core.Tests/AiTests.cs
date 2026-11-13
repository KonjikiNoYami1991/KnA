using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using KnA.Core.Ai;
using KnA.Core.Encoding;
using KnA.Core.Tools;

namespace KnA.Core.Tests;

public class AiTests
{
    /// <summary>OpenAI-compatible fake: refuses json_schema (like some servers), answers in a ```json fence.</summary>
    private sealed class FakeServer(string answer) : HttpMessageHandler
    {
        public List<JsonObject> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/models"))
                return Json("""{"data":[{"id":"qwen2.5:7b"},{"id":"llava:13b"}]}""");
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
            Requests.Add(body);
            if (body["response_format"]?["type"]?.GetValue<string>() == "json_schema")
                return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":"unsupported response_format"}""") };
            var content = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = answer } }) };
            return Json(content.ToJsonString());
        }

        private static HttpResponseMessage Json(string s) => new(HttpStatusCode.OK) { Content = new StringContent(s, System.Text.Encoding.UTF8, "application/json") };
    }

    private static readonly AiSettings Settings = new() { Provider = "ollama", Model = "qwen2.5:7b", VisionModel = "llava:13b" };

    [Fact]
    public async Task Lists_models()
    {
        var models = await new LlmClient(Settings, new HttpClient(new FakeServer(""))).ListModelsAsync();
        Assert.Equal(["llava:13b", "qwen2.5:7b"], models);
    }

    [Fact]
    public async Task Natural_language_request_becomes_validated_settings()
    {
        var fake = new FakeServer("""
            <think>small file, phone</think>Sure! ```json
            {"profile":"Streaming","codec":"HEVC","backend":"Nvenc","resolution":720,"quality":"BelowNormal",
             "subtitles":"Hardsub","audioCodec":"HeAac","container":"zip","explanation":"Piccolo e compatibile."}
            ```
            """);
        var caps = new FfmpegCapabilities("t", [new EncoderCapability(VideoCodec.HEVC, EncoderBackend.Nvenc, "hevc_nvenc", true, false, false, "no GPU")], true, DateTimeOffset.Now);
        var r = await new Assistant(Settings, caps, new HttpClient(fake)).SuggestAsync("piccolo per il telefono", new JobSettings(), null);

        Assert.Equal(ProfileKind.Streaming, r.Settings.Profile);
        Assert.Equal(VideoCodec.HEVC, r.Settings.Codec);
        Assert.Equal(EncoderBackend.Software, r.Settings.Backend);         // NVENC failed the hardware test here
        Assert.Equal(720, r.Settings.Resolution);
        Assert.Equal(AudioCodec.HeAac, r.Settings.AudioCodec);
        Assert.Null(r.Settings.Container);                                  // "zip" is not a container
        Assert.Contains(r.Warnings, w => w.Contains("zip"));
        Assert.Equal("Piccolo e compatibile.", r.Explanation);
        Assert.Equal("json_object", fake.Requests[^1]["response_format"]?["type"]?.GetValue<string>());
    }

    [Fact]
    public async Task Vision_analysis_sends_frames_and_reads_the_content_type()
    {
        var fake = new FakeServer("""{"contentType":"anime","upscale":"Anime4K","upscaleModel":"A","deband":"Light","observations":"Light banding in the sky."}""");
        var media = new Media.MediaInfo { Path = "/x.mkv", Video = [new Media.VideoStream { Kind = "video", Width = 1280, Height = 720, FrameRate = 23.976 }] };
        var r = await new Assistant(Settings, null, new HttpClient(fake)).AnalyzeAsync(media, new JobSettings { Resolution = 1080 }, [[1, 2, 3]]);
        Assert.Equal("anime", r.ContentType);
        Assert.Equal(UpscaleMethod.Anime4K, r.Settings.Upscale.Method);
        Assert.Equal("A", r.Settings.Upscale.Model);
        Assert.Equal(FilterStrength.Light, r.Settings.VideoFilters.Deband);
        Assert.Contains("banding", r.Explanation);
        var parts = fake.Requests[^1]["messages"]![1]!["content"]!.AsArray();
        Assert.Equal("image_url", parts[1]!["type"]!.GetValue<string>());
        Assert.Equal("llava:13b", fake.Requests[^1]["model"]!.GetValue<string>());
    }

    [Fact]
    public void Extracts_json_from_chatty_answers()
    {
        var o = LlmClient.ExtractJson("Here: {\"a\":\"}{\",\"x\":{\"y\":1}} and {\"z\":\"b\"}");
        Assert.Equal("}{", o?["a"]?.GetValue<string>());
        Assert.Null(LlmClient.ExtractJson("no json { here"));
    }
}
