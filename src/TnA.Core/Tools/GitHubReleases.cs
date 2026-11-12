using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace TnA.Core.Tools;

public sealed record ReleaseAsset(string Name, string Url, long Size);

public sealed record ReleaseInfo(string Tag, string Name, string? Body, string HtmlUrl, DateTimeOffset PublishedAt, IReadOnlyList<ReleaseAsset> Assets)
{
    /// <summary>"v2.1.0" / "2.1" → Version, or null.</summary>
    public Version? Version => ParseVersion(Tag);

    public static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var t = tag.Trim().TrimStart('v', 'V');
        var core = new string(t.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()).Trim('.');
        if (!core.Contains('.')) core += ".0";
        return System.Version.TryParse(core, out var v) ? v : null;
    }
}

/// <summary>Reads releases through the public GitHub REST API (no token needed, 60 requests/hour).</summary>
public static class GitHubReleases
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("TnA-Autohardsubber");
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    public static HttpClient Client => Http;

    /// <returns>The latest release, or null when the repository has none.</returns>
    public static async Task<ReleaseInfo?> LatestAsync(string ownerRepo, CancellationToken ct = default) =>
        await GetAsync($"https://api.github.com/repos/{ownerRepo}/releases/latest", ct);

    public static async Task<ReleaseInfo?> ByTagAsync(string ownerRepo, string tag, CancellationToken ct = default) =>
        await GetAsync($"https://api.github.com/repos/{ownerRepo}/releases/tags/{Uri.EscapeDataString(tag)}", ct);

    private static async Task<ReleaseInfo?> GetAsync(string url, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        if (resp.StatusCode == HttpStatusCode.Forbidden && resp.Headers.TryGetValues("x-ratelimit-remaining", out var rl) && rl.FirstOrDefault() == "0")
            throw new InvalidOperationException(Localization.L.T("GitHub rate limit reached, try again in an hour."));
        resp.EnsureSuccessStatusCode();
        return Parse(await resp.Content.ReadAsStringAsync(ct));
    }

    public static ReleaseInfo Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        string S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        var assets = new List<ReleaseAsset>();
        if (r.TryGetProperty("assets", out var arr))
            foreach (var a in arr.EnumerateArray())
                assets.Add(new ReleaseAsset(S(a, "name"), S(a, "browser_download_url"), a.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0));
        DateTimeOffset.TryParse(S(r, "published_at"), out var published);
        return new ReleaseInfo(S(r, "tag_name"), S(r, "name"), S(r, "body"), S(r, "html_url"), published, assets);
    }

    /// <summary>Downloads a file with progress (0..100).</summary>
    public static async Task DownloadAsync(string url, string target, IProgress<double>? progress, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long? total = resp.Content.Headers.ContentLength;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var tmp = target + ".part";
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(tmp))
        {
            var buffer = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress?.Report((double)done / total.Value * 100);
            }
        }
        File.Move(tmp, target, overwrite: true);
    }
}
