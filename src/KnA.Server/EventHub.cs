using System.Text.Json;
using System.Threading.Channels;
using KnA.Core.Queue;
using KnA.Core.Settings;

namespace KnA.Server;

/// <summary>
/// Fans queue events out to every connected browser through Server-Sent Events. Each connection belongs to an account:
/// a user hears about their own jobs only, administrators about everything.
/// </summary>
public sealed class EventHub
{
    private sealed record Client(Channel<string> Channel, AppUser User);

    private readonly List<Client> _clients = [];
    // SSE needs one line per "data:" field, so never indent.
    private static readonly JsonSerializerOptions Compact = new(SettingsStore.Json) { WriteIndented = false };

    public EventHub(QueueService queue)
    {
        queue.JobChanged += j =>
        {
            var dto = JobDto.From(j);
            BroadcastTo(u => u.Sees(j), "job", dto);
        };
        queue.JobRemoved += id => Broadcast("removed", new { id });
        queue.QueueChanged += () =>
        {
            var jobs = queue.Jobs;
            BroadcastEach("queue", u =>
            {
                var seen = jobs.Where(u.Sees).ToList();
                return new { queue = Mapping.QueueState(queue, u.Sees), order = seen.Select(j => j.Id) };
            });
        };
    }

    public void Broadcast(string type, object payload) => BroadcastTo(_ => true, type, payload);

    /// <summary>To the connections of the accounts <paramref name="who"/> picks.</summary>
    public void BroadcastTo(Func<AppUser, bool> who, string type, object payload)
    {
        string? json = null;
        lock (_clients)
            foreach (var c in _clients)
                if (who(c.User)) c.Channel.Writer.TryWrite(json ??= Serialize(type, payload));
    }

    public void ToAdmins(string type, object payload) => BroadcastTo(u => u.IsAdmin, type, payload);

    /// <summary>Each connection gets its own payload (null: nothing for that account).</summary>
    public void BroadcastEach(string type, Func<AppUser, object?> payloadFor)
    {
        lock (_clients)
        {
            var cache = new Dictionary<AppUser, string?>();
            foreach (var c in _clients)
            {
                if (!cache.TryGetValue(c.User, out var json))
                    cache[c.User] = json = payloadFor(c.User) is { } p ? Serialize(type, p) : null;
                if (json is not null) c.Channel.Writer.TryWrite(json);
            }
        }
    }

    private static string Serialize(string type, object payload) => JsonSerializer.Serialize(new { type, payload }, Compact);

    public async Task StreamAsync(HttpContext ctx, CancellationToken ct)
    {
        ctx.Response.Headers.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no"; // nginx / reverse proxies
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.DropOldest });
        var client = new Client(channel, ctx.CurrentUser());
        lock (_clients) _clients.Add(client);
        try
        {
            await ctx.Response.WriteAsync("retry: 2000\n\n", ct);
            await ctx.Response.Body.FlushAsync(ct);
            using var heartbeat = new PeriodicTimer(TimeSpan.FromSeconds(15));
            var hb = Task.Run(async () =>
            {
                while (await heartbeat.WaitForNextTickAsync(ct)) channel.Writer.TryWrite("");
            }, ct);
            await foreach (var msg in channel.Reader.ReadAllAsync(ct))
            {
                await ctx.Response.WriteAsync(msg.Length == 0 ? ": ping\n\n" : $"data: {msg}\n\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            lock (_clients) _clients.Remove(client);
        }
    }
}
