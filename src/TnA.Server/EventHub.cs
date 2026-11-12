using System.Text.Json;
using System.Threading.Channels;
using TnA.Core.Queue;
using TnA.Core.Settings;

namespace TnA.Server;

/// <summary>Fans queue events out to every connected browser through Server-Sent Events.</summary>
public sealed class EventHub
{
    private readonly List<Channel<string>> _clients = [];
    // SSE needs one line per "data:" field, so never indent.
    private static readonly JsonSerializerOptions Compact = new(SettingsStore.Json) { WriteIndented = false };
    private readonly QueueService _queue;

    public EventHub(QueueService queue)
    {
        _queue = queue;
        queue.JobChanged += j => Broadcast("job", JobDto.From(j));
        queue.JobRemoved += id => Broadcast("removed", new { id });
        queue.QueueChanged += () => Broadcast("queue", new { queue = Mapping.QueueState(queue), order = queue.Jobs.Select(j => j.Id) });
    }

    public void Broadcast(string type, object payload)
    {
        var json = JsonSerializer.Serialize(new { type, payload }, Compact);
        lock (_clients)
            foreach (var c in _clients) c.Writer.TryWrite(json);
    }

    public async Task StreamAsync(HttpContext ctx, CancellationToken ct)
    {
        ctx.Response.Headers.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no"; // nginx / reverse proxies
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (_clients) _clients.Add(channel);
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
            lock (_clients) _clients.Remove(channel);
        }
    }
}
