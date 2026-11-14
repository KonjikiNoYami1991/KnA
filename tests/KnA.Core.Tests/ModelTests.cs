using System.Net;
using System.Text;
using KnA.Core.Ai;
using KnA.Core.Tools;
using Xunit;

namespace KnA.Core.Tests;

public class ModelComponentTests
{
    /// <summary>A stand-in for Ollama: /api/tags lists qwen3:4b, /api/pull streams progress lines.</summary>
    private static (HttpListener, string) FakeOllama(List<string> pulled)
    {
        for (int port = 18400; ; port++)
        {
            var l = new HttpListener();
            l.Prefixes.Add($"http://127.0.0.1:{port}/");
            try { l.Start(); }
            catch (HttpListenerException) { continue; }
            _ = Task.Run(async () =>
            {
                while (l.IsListening)
                {
                    HttpListenerContext c;
                    try { c = await l.GetContextAsync(); } catch (Exception) { return; }
                    string body = c.Request.Url!.AbsolutePath switch
                    {
                        "/api/tags" => """{"models":[{"name":"qwen3:4b"}]}""",
                        "/api/pull" => Pull(c, pulled),
                        _ => "{}",
                    };
                    var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                    c.Response.ContentType = "application/json";
                    await c.Response.OutputStream.WriteAsync(bytes);
                    c.Response.Close();
                }
            });
            return (l, $"http://127.0.0.1:{port}/v1");
        }

        static string Pull(HttpListenerContext c, List<string> pulled)
        {
            using var r = new StreamReader(c.Request.InputStream);
            pulled.Add(r.ReadToEnd());
            return "{\"status\":\"pulling manifest\"}\n{\"status\":\"pulling abc\",\"total\":200,\"completed\":100}\n"
                   + "{\"status\":\"pulling abc\",\"total\":200,\"completed\":200}\n{\"status\":\"verifying sha256 digest\"}\n{\"status\":\"success\"}\n";
        }
    }

    [Fact]
    public async Task Assistant_models_are_listed_and_pulled_through_ollama()
    {
        var pulled = new List<string>();
        var (server, url) = FakeOllama(pulled);
        using var _ = server;
        var dir = Directory.CreateTempSubdirectory("kna-models-").FullName;
        var ai = new AiSettings { BaseUrl = url };
        (string, bool)? got = null;
        var cm = new ComponentManager(dir) { AiSettings = () => ai, LlmPulled = (n, v) => got = (n, v) };
        var list = await cm.CheckAsync(null, online: false);
        Assert.Contains(list, c => c.Id == "model:realesr-animevideov3" && c.CanInstall && c.InstalledVersion is null);
        Assert.Equal("Ollama", list.Single(c => c.Id == "llm:qwen3:4b").InstalledVersion);
        var vision = list.Single(c => c.Id == "llm:qwen2.5vl:7b");
        Assert.True(vision.CanInstall);
        Assert.Null(vision.InstalledVersion);

        var percents = new List<double>();
        var lines = new List<string>();
        await cm.InstallAsync("llm:qwen2.5vl:7b", new SyncProgress<double>(percents.Add), new SyncProgress<string>(lines.Add));
        Assert.Contains("\"model\":\"qwen2.5vl:7b\"", Assert.Single(pulled));
        Assert.Contains(50, percents);
        Assert.Equal(100, percents[^1]);
        Assert.Contains("verifying sha256 digest", lines);
        Assert.Equal(("qwen2.5vl:7b", true), got);
    }

    [Fact]
    public async Task Without_ollama_the_models_say_how_to_get_it()
    {
        var cm = new ComponentManager(Directory.CreateTempSubdirectory("kna-models-").FullName)
        {
            AiSettings = () => new AiSettings { BaseUrl = "http://127.0.0.1:9/v1" },
        };
        var llm = (await cm.CheckAsync(null, online: false)).Where(c => c.Id.StartsWith("llm:")).ToList();
        Assert.NotEmpty(llm);
        Assert.All(llm, c => { Assert.False(c.CanInstall); Assert.Contains("ollama.com", c.Note); });
    }

    private sealed class SyncProgress<T>(Action<T> a) : IProgress<T> { public void Report(T value) => a(value); }
}
