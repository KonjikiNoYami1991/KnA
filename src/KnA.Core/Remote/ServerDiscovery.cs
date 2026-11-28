using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace KnA.Core.Remote;

/// <summary>
/// KnA servers on the local network, found by themselves: the desktop app sends a small UDP broadcast
/// ("KNA-DISCOVER 1") to port 8585 and every kna-server answers with its name, version and address. Typing the address
/// still works when broadcasts don't pass (other subnets, VPNs, Docker bridge networks).
/// </summary>
public static class ServerDiscovery
{
    public const int DefaultPort = 8585;
    public const string Question = "KNA-DISCOVER 1";

    private sealed record Answer(string App, string Name, string Version, string Platform, int Port, string Scheme, string? Url);

    /// <summary>The answer a server gives (null when the question is not KnA's).</summary>
    public static byte[]? AnswerFor(byte[] question, string name, string version, string platform, int port, string scheme, string? publicUrl)
    {
        if (!System.Text.Encoding.ASCII.GetString(question).StartsWith("KNA-DISCOVER", StringComparison.Ordinal)) return null;
        return JsonSerializer.SerializeToUtf8Bytes(new Answer("KnA", name, version, platform, port, scheme, publicUrl), Settings.SettingsStore.Json);
    }

    /// <summary>The server's side: answers discovery questions until <paramref name="ct"/>.</summary>
    public static async Task RespondAsync(int udpPort, Func<byte[]> answer, CancellationToken ct)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, udpPort));
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await udp.ReceiveAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { continue; }
            if (r.Buffer.Length > 64 || !System.Text.Encoding.ASCII.GetString(r.Buffer).StartsWith(Question[..12], StringComparison.Ordinal)) continue;
            try { await udp.SendAsync(answer(), r.RemoteEndPoint, ct); }
            catch (SocketException) { }
        }
    }

    /// <summary>The desktop app's side: asks the local networks and collects the answers for <paramref name="wait"/>.</summary>
    public static async Task<IReadOnlyList<FoundServer>> FindAsync(TimeSpan wait, IEnumerable<IPEndPoint>? targets = null, CancellationToken ct = default)
    {
        var found = new Dictionary<string, FoundServer>(StringComparer.OrdinalIgnoreCase);
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var question = System.Text.Encoding.ASCII.GetBytes(Question);
        foreach (var t in targets ?? BroadcastTargets())
        {
            try { await udp.SendAsync(question, t, ct); }
            catch (SocketException) { }
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(wait);
        while (!timeout.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await udp.ReceiveAsync(timeout.Token); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { continue; }
            if (Parse(r.Buffer, r.RemoteEndPoint.Address) is { } s) found.TryAdd(s.Url, s);
        }
        return found.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static FoundServer? Parse(byte[] data, IPAddress from)
    {
        try
        {
            var a = JsonSerializer.Deserialize<Answer>(data, Settings.SettingsStore.Json);
            if (a is null || a.App != "KnA" || a.Port is <= 0 or > 65535) return null;
            var host = from.IsIPv4MappedToIPv6 ? from.MapToIPv4().ToString() : from.ToString();
            var url = a.Url is { Length: > 0 } u ? u : $"{(a.Scheme == "https" ? "https" : "http")}://{host}:{a.Port}/";
            return new FoundServer(a.Name, url, a.Version, a.Platform);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>255.255.255.255 and each local IPv4 network's own broadcast address.</summary>
    public static IEnumerable<IPEndPoint> BroadcastTargets(int port = DefaultPort)
    {
        var list = new List<IPEndPoint> { new(IPAddress.Broadcast, port) };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
                foreach (var ua in nic.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && a.PrefixLength is > 0 and < 32))
                {
                    var ip = BitConverter.ToUInt32(ua.Address.GetAddressBytes().Reverse().ToArray());
                    var mask = uint.MaxValue << (32 - ua.PrefixLength);
                    list.Add(new IPEndPoint(new IPAddress(BitConverter.GetBytes(ip | ~mask).Reverse().ToArray()), port));
                }
        }
        catch (NetworkInformationException) { }
        return list.DistinctBy(e => e.ToString());
    }
}
