using System.Net;
using System.Net.Sockets;
using KnA.Core.Encoding;
using KnA.Core.Queue;
using KnA.Core.Remote;

namespace KnA.Core.Tests;

/// <summary>Desktop app on a KnA server: addresses, discovery, the mirror of the server's jobs.</summary>
public sealed class RemoteTests
{
    [Theory]
    [InlineData("192.168.1.10", "http://192.168.1.10:8585/")]
    [InlineData("192.168.1.10:9000", "http://192.168.1.10:9000/")]
    [InlineData("nas", "http://nas:8585/")]
    [InlineData("nas.local", "http://nas.local:8585/")]
    [InlineData("kna.example.com", "https://kna.example.com/")]
    [InlineData("example.com/kna", "https://example.com/kna/")]
    [InlineData("http://10.0.0.5:8585", "http://10.0.0.5:8585/")]
    [InlineData(" https://kna.example.com/kna/ ", "https://kna.example.com/kna/")]
    public void Addresses_are_completed_by_themselves(string typed, string expected) =>
        Assert.Equal(expected, KnaClient.NormalizeUrl(typed).ToString());

    [Fact]
    public void Bad_addresses_are_said_plainly()
    {
        Assert.Throws<RemoteException>(() => KnaClient.NormalizeUrl(""));
        Assert.Throws<RemoteException>(() => KnaClient.NormalizeUrl("ftp://x"));
    }

    [Theory]
    [InlineData("http://192.168.1.10:8585/", false)]
    [InlineData("http://10.1.2.3/", false)]
    [InlineData("http://100.80.1.2/", false)]      // Tailscale / CGNAT
    [InlineData("http://nas:8585/", false)]
    [InlineData("http://kna.example.com/", true)]
    [InlineData("http://203.0.113.7/", true)]
    [InlineData("https://kna.example.com/", false)]
    public void Plain_http_outside_the_local_network_is_flagged(string url, bool insecure) =>
        Assert.Equal(insecure, KnaClient.IsInsecure(new Uri(url)));

    [Fact]
    public async Task A_server_answers_the_discovery_question()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int port;
        using (var probe = new UdpClient(0)) port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        var answer = ServerDiscovery.AnswerFor(System.Text.Encoding.ASCII.GetBytes(ServerDiscovery.Question), "homelab", "3.3.0", "Linux", 8590, "http", null)!;
        var responder = ServerDiscovery.RespondAsync(port, () => answer, cts.Token);
        await Task.Delay(200);
        var found = await ServerDiscovery.FindAsync(TimeSpan.FromSeconds(1.5), [new IPEndPoint(IPAddress.Loopback, port)], cts.Token);
        var s = Assert.Single(found);
        Assert.Equal("homelab", s.Name);
        Assert.Equal("http://127.0.0.1:8590/", s.Url);       // the address it answered from, its HTTP port
        Assert.Equal("3.3.0", s.Version);
        cts.Cancel();
        await responder;

        var proxied = ServerDiscovery.AnswerFor("KNA-DISCOVER 1"u8.ToArray(), "x", "1", "y", 8585, "http", "https://kna.example.com/");
        Assert.Equal("https://kna.example.com/", ServerDiscovery.Parse(proxied!, IPAddress.Loopback)!.Url);
        Assert.Null(ServerDiscovery.AnswerFor("HELLO"u8.ToArray(), "x", "1", "y", 8585, "http", null));
        Assert.Null(ServerDiscovery.Parse("{\"app\":\"Other\",\"port\":1}"u8.ToArray(), IPAddress.Loopback));
    }

    [Fact]
    public void The_servers_view_of_a_job_is_mirrored()
    {
        var job = new QueueJob { Id = Guid.NewGuid(), SourcePath = "/media/a.mkv" };
        RemoteQueue.Mirror(job, new RemoteJob
        {
            Id = job.Id, Status = JobStatus.Running, Stage = "Video", Encoding = true, PositionSeconds = 30, DurationSeconds = 120,
            Fps = 240, Speed = 10, Size = 1 << 20, ElapsedSeconds = 3, MediaSummary = "1080p HEVC", Settings = new JobSettings { Resolution = 720 },
        });
        Assert.Equal(JobStatus.Running, job.Status);
        Assert.Equal(25, job.Percent, 3);
        Assert.Equal("1080p HEVC", job.RemoteSummary);
        Assert.Equal(720, job.Settings.Resolution);
        Assert.NotNull(job.Elapsed);
        Assert.Null(job.FinishedAt);

        RemoteQueue.Mirror(job, new RemoteJob { Id = job.Id, Status = JobStatus.Running, Stage = "OCR", StagePercent = 40 });
        Assert.Equal(40, job.Percent, 3);
        Assert.False(job.StageIndeterminate);
        RemoteQueue.Mirror(job, new RemoteJob { Id = job.Id, Status = JobStatus.Running, Stage = "Joining", Indeterminate = true });
        Assert.True(job.StageIndeterminate);
        RemoteQueue.Mirror(job, new RemoteJob { Id = job.Id, Status = JobStatus.Done, OutputPath = "/media/a [x].mkv", ElapsedSeconds = 90 });
        Assert.Equal(100, job.Percent);
        Assert.NotNull(job.FinishedAt);
    }

    [Fact]
    public void Results_never_overwrite_an_earlier_file()
    {
        var dir = Directory.CreateTempSubdirectory("kna-remote-").FullName;
        try
        {
            var p = Path.Combine(dir, "a.mkv");
            Assert.Equal(p, KnaClient.UniquePath(p));
            File.WriteAllText(p, "");
            Assert.Equal(Path.Combine(dir, "a (2).mkv"), KnaClient.UniquePath(p));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_keychain_entry_names_server_and_account() =>
        Assert.Equal("http://nas:8585|alice", SecretStores.Key("http://nas:8585/", "alice"));
}
