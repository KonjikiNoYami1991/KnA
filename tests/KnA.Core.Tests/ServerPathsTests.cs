using KnA.Core.Remote;

namespace KnA.Core.Tests;

/// <summary>Files of the laptop the server reads in place (the same NAS share): mappings found from one match.</summary>
public sealed class ServerPathsTests
{
    [Theory]
    [InlineData(@"Z:\Anime\Frieren\ep01.mkv", "/mnt/nas/video/Anime/Frieren/ep01.mkv", @"Z:\", "/mnt/nas/video")]
    [InlineData(@"\\nas\video\Anime\ep01.mkv", "/volume1/video/Anime/ep01.mkv", @"\\nas", "/volume1")]
    [InlineData("/Volumes/video/Anime/ep01.mkv", "/media/Anime/ep01.mkv", "/Volumes/video", "/media")]
    [InlineData("/home/me/nas/Film/a.mkv", @"D:\Shares\Film\a.mkv", "/home/me/nas", @"D:\Shares")]
    public void One_match_teaches_the_shared_folder(string local, string server, string localPrefix, string serverPrefix)
    {
        var m = ServerPaths.Learn(local, server)!;
        Assert.Equal(localPrefix, m.Local);
        Assert.Equal(serverPrefix, m.Server);
        Assert.Equal(server, ServerPaths.Map(local, m));
    }

    [Fact]
    public void Other_files_of_the_share_are_mapped_both_ways()
    {
        var m = new PathMapping(@"Z:\", "/mnt/nas/video");
        Assert.Equal("/mnt/nas/video/Film/Dune (2021).mkv", ServerPaths.Map(@"Z:\Film\Dune (2021).mkv", m));
        Assert.Equal("/mnt/nas/video/Film/Dune (2021).mkv", ServerPaths.Map(@"z:\Film\Dune (2021).mkv", m));   // drive letters ignore case
        Assert.Null(ServerPaths.Map(@"C:\Users\me\a.mkv", m));
        var back = ServerPaths.Unmap("/mnt/nas/video/Film/Dune [x].mkv", m)!;
        Assert.EndsWith("Film" + Path.DirectorySeparatorChar + "Dune [x].mkv", back);
        Assert.StartsWith("Z:", back);
        Assert.Null(ServerPaths.Unmap("/srv/other/a.mkv", m));
    }

    [Fact]
    public void A_name_alone_is_not_a_mapping() =>
        Assert.Null(ServerPaths.Learn(@"C:\Downloads\ep01.mkv", "/media/Anime/ep01.mkv"));

    [Fact]
    public async Task The_fingerprint_tells_the_same_file_from_another()
    {
        var dir = Directory.CreateTempSubdirectory("kna-fp-").FullName;
        try
        {
            var rnd = new Random(7);
            byte[] Bytes(int n) { var b = new byte[n]; rnd.NextBytes(b); return b; }
            var big = Bytes(5 << 20);
            await File.WriteAllBytesAsync(Path.Combine(dir, "a.bin"), big);
            await File.WriteAllBytesAsync(Path.Combine(dir, "b.bin"), big);
            big[(5 << 20) / 2] ^= 0xFF;                                  // a byte inside the middle sample
            await File.WriteAllBytesAsync(Path.Combine(dir, "c.bin"), big);
            var small = Bytes(2 << 20);
            await File.WriteAllBytesAsync(Path.Combine(dir, "s1.bin"), small);
            small[1_500_000] ^= 1;                                       // small files are hashed whole
            await File.WriteAllBytesAsync(Path.Combine(dir, "s2.bin"), small);
            async Task<string> F(string n) => await FileFingerprint.SampledAsync(Path.Combine(dir, n));
            Assert.Equal(await F("a.bin"), await F("b.bin"));
            Assert.NotEqual(await F("a.bin"), await F("c.bin"));
            Assert.NotEqual(await F("s1.bin"), await F("s2.bin"));
        }
        finally { Directory.Delete(dir, true); }
    }
}
