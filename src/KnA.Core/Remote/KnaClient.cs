using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using KnA.Core.Encoding;
using KnA.Core.Localization;
using KnA.Core.Settings;

namespace KnA.Core.Remote;

/// <summary>A clear reason why the server can't be used (shown as it is to the person).</summary>
public sealed class RemoteException(string message, HttpStatusCode? status = null, string? certificate = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
    /// <summary>SHA-256 of an HTTPS certificate the system does not trust: the person may pin it once.</summary>
    public string? UntrustedCertificate { get; } = certificate;
}

/// <summary>
/// The desktop app's side of a KnA server's API (the same one the web UI uses): HTTP Basic with the account, JSON,
/// Server-Sent Events for progress, chunked resumable uploads and range-resumed downloads. HTTPS certificates the system
/// does not trust are accepted only when pinned (<see cref="RemoteServerSettings.PinnedCertificate"/>).
/// </summary>
public sealed class KnaClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly HttpClient _events;
    private string? _lastCertificate;

    public Uri BaseUri { get; }
    public string User { get; }

    public KnaClient(string url, string user, string password, string? pinnedCertificate = null)
    {
        BaseUri = NormalizeUrl(url);
        User = user;
        HttpClient Make(TimeSpan timeout)
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, cert, _, errors) => Validate(cert, errors, pinnedCertificate) },
            };
            var c = new HttpClient(handler) { BaseAddress = BaseUri, Timeout = timeout };
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(user + ":" + password)));
            c.DefaultRequestHeaders.AcceptLanguage.ParseAdd(L.IsItalian() ? "it" : "en");
            c.DefaultRequestHeaders.UserAgent.ParseAdd("KnA-desktop/" + Queue.AppInfo.Version);
            return c;
        }
        _http = Make(TimeSpan.FromMinutes(10));
        _events = Make(Timeout.InfiniteTimeSpan);
    }

    /// <summary>"192.168.1.10" → http://192.168.1.10:8585/, "kna.example.com/kna" → https://kna.example.com/kna/.</summary>
    public static Uri NormalizeUrl(string url)
    {
        var u = (url ?? "").Trim();
        if (u.Length == 0) throw new RemoteException(L.T("Enter the address of the KnA server."));
        if (!u.Contains("://", StringComparison.Ordinal))
        {
            var host = u.Split('/')[0];
            bool local = IPAddress.TryParse(host.Split(':')[0], out _) || !host.Contains('.') || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase);
            u = (local ? "http://" : "https://") + u;
            if (local && !host.Contains(':')) u = u.Replace(host, host + ":8585");
        }
        if (!Uri.TryCreate(u.EndsWith('/') ? u : u + "/", UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new RemoteException(L.T("\"{0}\" is not a valid address (e.g. 192.168.1.10:8585 or https://kna.example.com).", url));
        return uri;
    }

    /// <summary>Plain HTTP outside the local network: passwords and files travel readable.</summary>
    public static bool IsInsecure(Uri uri) => uri.Scheme == "http" && !IsLocalHost(uri.Host);

    public static bool IsLocalHost(string host)
    {
        if (host is "localhost" || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || !host.Contains('.')) return true;
        if (!IPAddress.TryParse(host, out var ip)) return false;
        if (IPAddress.IsLoopback(ip)) return true;
        var b = ip.MapToIPv4().GetAddressBytes();
        return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal || b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168
               || b[0] == 100 && b[1] is >= 64 and <= 127 || b[0] == 169 && b[1] == 254;
    }

    private bool Validate(X509Certificate? cert, SslPolicyErrors errors, string? pinned)
    {
        if (errors == SslPolicyErrors.None) return true;
        if (cert is null) return false;
        var fp = Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()));
        _lastCertificate = fp;
        return pinned is not null && string.Equals(pinned, fp, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ requests

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct, bool events = false)
    {
        HttpResponseMessage res;
        try { res = await (events ? _events : _http).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (HttpRequestException ex) when (_lastCertificate is { } fp && ex.InnerException is System.Security.Authentication.AuthenticationException or IOException)
        {
            throw new RemoteException(L.T("The server's HTTPS certificate is not trusted by this computer (self-signed?). Fingerprint: {0}", Pretty(fp)), certificate: fp);
        }
        catch (HttpRequestException ex)
        {
            throw new RemoteException(L.T("Cannot reach the KnA server at {0}: {1}", BaseUri, ex.InnerException?.Message ?? ex.Message));
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new RemoteException(L.T("The KnA server at {0} does not answer.", BaseUri));
        }
        if (res.IsSuccessStatusCode) return res;
        var detail = await ProblemAsync(res, ct);
        res.Dispose();
        throw res.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new RemoteException(L.T("Wrong user name or password for {0}.", BaseUri), res.StatusCode),
            HttpStatusCode.TooManyRequests => new RemoteException(detail ?? L.T("Too many failed sign-ins: try again later."), res.StatusCode),
            _ => new RemoteException(detail ?? $"HTTP {(int)res.StatusCode} {res.ReasonPhrase}", res.StatusCode),
        };
    }

    private static async Task<string?> ProblemAsync(HttpResponseMessage res, CancellationToken ct)
    {
        try
        {
            var text = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(text);
            foreach (var k in new[] { "detail", "title" })
                if (doc.RootElement.TryGetProperty(k, out var v) && v.GetString() is { Length: > 0 } s) return s;
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException) { }
        return null;
    }

    public static string Pretty(string fingerprint) =>
        string.Join(':', Enumerable.Range(0, fingerprint.Length / 2).Select(i => fingerprint.Substring(i * 2, 2)));

    internal async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var res = await SendAsync(new HttpRequestMessage(HttpMethod.Get, path), ct);
        return await ReadAsync<T>(res, ct);
    }

    /// <summary>The answer as <typeparamref name="T"/>; an empty answer (no content, "null") is the default value.</summary>
    private static async Task<T> ReadAsync<T>(HttpResponseMessage res, CancellationToken ct)
    {
        var text = await res.Content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(text) ? default! : JsonSerializer.Deserialize<T>(text, SettingsStore.Json)!;
    }

    internal async Task<JsonElement> PostAsync(string path, object? body, CancellationToken ct, HttpMethod? method = null)
    {
        var req = new HttpRequestMessage(method ?? HttpMethod.Post, path) { Content = body is null ? null : JsonContent.Create(body, options: SettingsStore.Json) };
        using var res = await SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    // ------------------------------------------------------------------ server, account, state

    /// <summary>What the server is; an old server without this endpoint, or an API this app doesn't know, is said plainly.</summary>
    public async Task<ServerInfo> VersionAsync(CancellationToken ct = default)
    {
        ServerInfo info;
        try { info = await GetAsync<ServerInfo>("api/version", ct); }
        catch (RemoteException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            throw new RemoteException(L.T("The KnA server at {0} is too old for the desktop app: update it (Settings → Updates & components in its web page).", BaseUri));
        }
        if (info.App != "KnA") throw new RemoteException(L.T("{0} is not a KnA server.", BaseUri));
        if (info.ApiVersion < ServerInfo.ClientApiVersion)
            throw new RemoteException(L.T("The KnA server at {0} is version {1}, too old for this desktop app ({2}): update it.", BaseUri, info.Version, Queue.AppInfo.Version));
        if (info.ApiVersion > ServerInfo.ClientApiVersion + 10)
            throw new RemoteException(L.T("The KnA server at {0} is version {1}, much newer than this desktop app ({2}): update the app.", BaseUri, info.Version, Queue.AppInfo.Version));
        return info;
    }

    /// <summary>Checks the account: its name and role on the server.</summary>
    public async Task<(string Name, bool IsAdmin)> SignInAsync(CancellationToken ct = default)
    {
        var st = await GetAsync<JsonElement>("api/auth/status", ct);
        if (st.TryGetProperty("authDisabled", out var off) && off.GetBoolean()) return (User, true);
        if (!st.TryGetProperty("user", out var u) || u.ValueKind != JsonValueKind.Object)
            throw new RemoteException(L.T("Wrong user name or password for {0}.", BaseUri), HttpStatusCode.Unauthorized);
        return (u.GetProperty("name").GetString() ?? User, u.GetProperty("isAdmin").GetBoolean());
    }

    public Task<RemoteState> StateAsync(CancellationToken ct = default) => GetAsync<RemoteState>("api/state", ct);

    /// <summary>Reads the server's live events until <paramref name="ct"/> or the connection ends.</summary>
    public async Task ListenAsync(Action<string, JsonElement> onEvent, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "api/events");
        req.Headers.Accept.ParseAdd("text/event-stream");
        using var res = await SendAsync(req, ct, events: true);
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        var data = new StringBuilder();
        while (!ct.IsCancellationRequested && await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.StartsWith("data:", StringComparison.Ordinal)) { data.Append(line.AsSpan(5).TrimStart()); continue; }
            if (line.Length > 0 || data.Length == 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(data.ToString());
                onEvent(doc.RootElement.GetProperty("type").GetString() ?? "", doc.RootElement.GetProperty("payload").Clone());
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
            data.Clear();
        }
    }

    // ------------------------------------------------------------------ queue

    /// <summary>Queues files the server can reach itself (its own folders); returns the new jobs.</summary>
    public async Task<IReadOnlyList<Guid>> AddServerPathsAsync(IEnumerable<string> paths, JobSettings settings, CancellationToken ct = default)
    {
        var r = await PostAsync("api/jobs", new { paths = paths.ToList(), settings }, ct);
        return Ids(r);
    }

    private static IReadOnlyList<Guid> Ids(JsonElement r) =>
        r.ValueKind == JsonValueKind.Object && r.TryGetProperty("jobs", out var j) && j.ValueKind == JsonValueKind.Array
            ? j.EnumerateArray().Select(x => x.GetGuid()).ToList() : [];

    /// <summary>A folder of the server (null: its roots), videos and folders only.</summary>
    public Task<ServerFolder> BrowseAsync(string? path, CancellationToken ct = default) =>
        GetAsync<ServerFolder>("api/browse?mode=video" + (path is null ? "" : "&path=" + Uri.EscapeDataString(path)), ct);

    /// <summary>The server's smart drop with its own paths: externals join their job (or the video with the same name).</summary>
    public Task<Queue.DropResult> DropAsync(IReadOnlyList<string> serverPaths, Guid? targetJob, JobSettings settings, CancellationToken ct = default) =>
        SendJsonAsync<Queue.DropResult>(HttpMethod.Post, "api/drop/apply", new { paths = serverPaths, targetJob, settings }, ct);

    public Task RemoveAsync(IEnumerable<Guid> ids, CancellationToken ct = default) => PostAsync("api/jobs/remove", new { ids = ids.ToList() }, ct);
    public Task ResetAsync(IEnumerable<Guid> ids, CancellationToken ct = default) => PostAsync("api/jobs/reset", new { ids = ids.ToList() }, ct);
    public Task MoveAsync(Guid id, int delta, CancellationToken ct = default) => PostAsync($"api/jobs/{id}/move", new { delta }, ct);
    public Task UpdateSettingsAsync(IEnumerable<Guid> ids, JobSettings settings, CancellationToken ct = default) =>
        PostAsync("api/jobs/settings", new { ids = ids.ToList(), settings }, ct);
    public Task StartAsync(CancellationToken ct = default) => PostAsync("api/queue/start", null, ct);
    public Task StopAsync(CancellationToken ct = default) => PostAsync("api/queue/stop", null, ct);
    public Task PauseAsync(CancellationToken ct = default) => PostAsync("api/queue/pause", null, ct);
    public Task ResumeAsync(CancellationToken ct = default) => PostAsync("api/queue/resume", null, ct);

    /// <summary>A JSON answer as <typeparamref name="T"/> (the engine's own types: the server and the app share them).</summary>
    internal async Task<T> SendJsonAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var req = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body, options: SettingsStore.Json) };
        using var res = await SendAsync(req, ct);
        return await ReadAsync<T>(res, ct);
    }

    /// <summary>Saves a GET answer into <paramref name="file"/> (previews), written whole or not at all.</summary>
    internal async Task<string> SaveToFileAsync(string path, string file, CancellationToken ct)
    {
        using var res = await SendAsync(new HttpRequestMessage(HttpMethod.Get, path), ct);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var tmp = file + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        await using (var output = File.Create(tmp))
            await res.Content.CopyToAsync(output, ct);
        File.Move(tmp, file, overwrite: true);
        return file;
    }

    internal async Task<Stream> OpenStreamAsync(string path, CancellationToken ct)
    {
        var res = await SendAsync(new HttpRequestMessage(HttpMethod.Get, path), ct);
        return await res.Content.ReadAsStreamAsync(ct);
    }

    public async Task<string> TextAsync(string path, CancellationToken ct = default)
    {
        using var res = await SendAsync(new HttpRequestMessage(HttpMethod.Get, path), ct);
        return await res.Content.ReadAsStringAsync(ct);
    }

    public Task<string> LogAsync(Guid id, CancellationToken ct = default) => TextAsync($"api/jobs/{id}/log", ct);
    public Task<string> CommandAsync(Guid id, CancellationToken ct = default) => TextAsync($"api/jobs/{id}/command", ct);

    /// <summary>The result is home: the server forgets the job and deletes the files it kept for this client.</summary>
    public Task CollectedAsync(Guid id, CancellationToken ct = default) => PostAsync($"api/jobs/{id}/collected", null, ct);

    // ------------------------------------------------------------------ uploads

    /// <summary>
    /// Sends a local file in pieces (16 MB, smaller when a proxy refuses them), resuming an earlier upload
    /// (<paramref name="uploadId"/>) where the server has it, and queues it. Returns the new job.
    /// </summary>
    public async Task<Guid?> UploadAsync(string localPath, JobSettings settings, Action<string> uploadStarted, string? uploadId,
        IProgress<long>? progress = null, CancellationToken ct = default)
    {
        var done = await UploadCoreAsync(localPath, new { enqueue = true, settings }, uploadStarted, uploadId, progress, ct);
        return Ids(done).FirstOrDefault() is var id && id != Guid.Empty ? id : null;
    }

    /// <summary>Uploads a file the job uses (subtitle, audio, font…) without queueing it; returns its path on the server.</summary>
    public async Task<string> UploadFileAsync(string localPath, CancellationToken ct = default)
    {
        var done = await UploadCoreAsync(localPath, new { enqueue = false }, _ => { }, null, null, ct);
        return done.GetProperty("path").GetString() ?? throw new RemoteException(L.T("Upload of {0} refused: HTTP {1}", Path.GetFileName(localPath), 500));
    }

    private async Task<JsonElement> UploadCoreAsync(string localPath, object complete, Action<string> uploadStarted, string? uploadId,
        IProgress<long>? progress, CancellationToken ct)
    {
        var size = new FileInfo(localPath).Length;
        long offset = 0;
        int chunk = 16 << 20;
        if (uploadId is not null)
        {
            try
            {
                var st = await GetAsync<JsonElement>($"api/uploads/{uploadId}", ct);
                offset = st.GetProperty("offset").GetInt64();
            }
            catch (RemoteException ex) when (ex.Status == HttpStatusCode.NotFound) { uploadId = null; }
        }
        if (uploadId is null)
        {
            var c = await PostAsync("api/uploads", new { name = Path.GetFileName(localPath), size, folder = (string?)null }, ct);
            uploadId = c.GetProperty("id").GetString()!;
            offset = c.GetProperty("offset").GetInt64();
            if (c.TryGetProperty("chunk", out var ch)) chunk = ch.GetInt32();
            uploadStarted(uploadId);
        }
        await using var file = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, useAsync: true);
        var buffer = new byte[chunk];
        int failures = 0;
        while (offset < size)
        {
            ct.ThrowIfCancellationRequested();
            file.Position = offset;
            int n = await file.ReadAtLeastAsync(buffer.AsMemory(0, (int)Math.Min(chunk, size - offset)), (int)Math.Min(chunk, size - offset), throwOnEndOfStream: false, ct);
            var req = new HttpRequestMessage(HttpMethod.Put, $"api/uploads/{uploadId}?offset={offset}") { Content = new ByteArrayContent(buffer, 0, n) };
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            try
            {
                using var res = await _http.SendAsync(req, ct);
                if (res.StatusCode == HttpStatusCode.RequestEntityTooLarge)
                {
                    if (chunk <= 256 << 10) throw new RemoteException(L.T("The reverse proxy in front of KnA refuses even small uploads (HTTP 413): raise its request size limit (nginx: client_max_body_size 32m)."));
                    chunk = Math.Max(256 << 10, chunk / 4);
                    continue;
                }
                var body = await res.Content.ReadAsStringAsync(ct);
                if (res.StatusCode == HttpStatusCode.Conflict || res.IsSuccessStatusCode)
                {
                    offset = JsonDocument.Parse(body).RootElement.GetProperty("offset").GetInt64();
                    progress?.Report(offset);
                    failures = 0;
                    continue;
                }
                throw new RemoteException(L.T("Upload of {0} refused: HTTP {1}", Path.GetFileName(localPath), (int)res.StatusCode), res.StatusCode);
            }
            catch (HttpRequestException) when (++failures <= 8)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, failures * 2)), ct);
                var st = await GetAsync<JsonElement>($"api/uploads/{uploadId}", ct);
                offset = st.GetProperty("offset").GetInt64();
            }
        }
        return await PostAsync($"api/uploads/{uploadId}/complete", complete, ct);
    }

    // ------------------------------------------------------------------ downloads

    /// <summary>
    /// Downloads a job's result into <paramref name="folder"/>, resuming a partial download; a folder result (images,
    /// demux) comes as a ZIP and is unpacked. Returns the local path.
    /// </summary>
    public async Task<string> DownloadResultAsync(Guid jobId, string serverOutputPath, string folder, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        var name = Path.GetFileName(serverOutputPath.TrimEnd('/', '\\'));
        if (name.Length == 0) name = jobId.ToString("N");
        var target = Path.Combine(folder, name);
        var part = Path.Combine(folder, "." + name + ".kna-download");
        long have = File.Exists(part) ? new FileInfo(part).Length : 0;
        var req = new HttpRequestMessage(HttpMethod.Get, $"api/jobs/{jobId}/download");
        if (have > 0) req.Headers.Range = new RangeHeaderValue(have, null);
        using var res = await SendAsync(req, ct);
        bool zip = res.Content.Headers.ContentType?.MediaType == "application/zip";
        if (res.StatusCode != HttpStatusCode.PartialContent) have = 0;   // the server sent it all again
        long total = (res.Content.Headers.ContentRange?.Length ?? res.Content.Headers.ContentLength + have) ?? 0;
        await using (var input = await res.Content.ReadAsStreamAsync(ct))
        await using (var output = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
        {
            var buffer = new byte[1 << 20];
            int n;
            while ((n = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, n), ct);
                have += n;
                if (total > 0) progress?.Report(have * 100.0 / total);
            }
        }
        if (zip)
        {
            target = UniquePath(target);
            System.IO.Compression.ZipFile.ExtractToDirectory(part, target);
            File.Delete(part);
            return target;
        }
        target = UniquePath(target);
        File.Move(part, target);
        return target;
    }

    /// <summary>"name.mkv", or "name (2).mkv" when it exists (an earlier result is never overwritten).</summary>
    public static string UniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? "";
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var p = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(p) && !Directory.Exists(p)) return p;
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _events.Dispose();
    }
}
