using KnA.Core.Encoding;
using KnA.Core.Media;
using Xunit;

namespace KnA.Core.Tests;

public class SizeTests
{
    private static MediaInfo Film() => new()
    {
        Path = "/m/film.mkv", Duration = TimeSpan.FromMinutes(100),
        Video = [new VideoStream { Index = 0, Kind = "video", CodecName = "hevc", Width = 1920, Height = 1080, FrameRate = 24 }],
        Audio = [new AudioStream { Index = 1, Kind = "audio", CodecName = "ac3", Channels = 6, Language = "ita", BitRate = 640_000 }],
        Subtitles = [new SubtitleStream { Index = 2, Kind = "subtitle", CodecName = "subrip", Language = "ita", SizeBytes = 60_000 }],
    };

    private static CompileContext Ctx(JobSettings s, JobEdit? edit = null)
    {
        var m = Film();
        return new() { Source = m, Media = new Dictionary<string, MediaInfo> { [m.Path] = m }, Settings = s, Edit = edit ?? new() };
    }

    private static readonly JobSettings Target = new()
    {
        Profile = ProfileKind.UniversalMkv, Codec = VideoCodec.HEVC, Resolution = 0, Subtitles = SubtitleMode.Softsub, AudioCodec = AudioCodec.Copy,
        Encoder = new EncoderOverrides { RateControl = RateControl.TargetSize, TargetSizeMB = 4096 },
    };

    [Fact]
    public void Target_size_leaves_the_rest_to_the_video_and_counts_copied_audio()
    {
        var b = SizePlanner.For(Ctx(Target))!;
        Assert.Equal(6000, b.Seconds, 1);
        Assert.Equal(640, b.AudioKbps, 1);
        // 4 GiB over 100 min = 5726 kb/s, minus 3 % margin, audio, subtitles and 0.5 % of Matroska overhead.
        Assert.InRange(b.VideoKbps, 4850, 4950);
        var total = (b.VideoKbps + b.AudioKbps + b.SubtitleKbps) * 1000 / 8 * b.Seconds;
        Assert.True(total < SizePlanner.MiB(4096), $"{total} bytes");
    }

    [Fact]
    public void Target_size_becomes_an_average_bitrate_in_the_command_and_follows_the_trim()
    {
        var job = CommandBuilder.Compile(Ctx(Target));
        Assert.False(job.HasErrors, string.Join("\n", job.Issues.Select(i => i.Message)));
        var cmd = job.Plan.Arguments.ToList();
        int i = cmd.IndexOf("-b:v");
        Assert.True(i > 0, string.Join(" ", cmd));
        Assert.DoesNotContain("-crf", cmd);

        // Half the film in the same size: about twice the bitrate.
        var half = SizePlanner.For(Ctx(Target, new JobEdit { TrimEnd = 3000 }))!;
        Assert.InRange(half.VideoKbps, 2 * 4850 + 600, 2 * 4950 + 700);
    }

    [Fact]
    public void Too_small_a_target_warns_and_keeps_a_minimum()
    {
        var s = Target with { Encoder = Target.Encoder with { TargetSizeMB = 100 } };
        var job = CommandBuilder.Compile(Ctx(s));
        Assert.Contains(job.Issues, x => !x.IsError && x.Message.Contains("100"));
        Assert.Contains($"{SizePlanner.MinimumVideoKbps}k", job.Plan.Arguments);
    }
}

public class ToolHelpTests
{
    [Fact]
    public void Ffmpeg_avoptions_with_named_values_and_kind_filter()
    {
        const string text = """
libx264 AVOptions:
  -preset            <string>     E..V....... Set the encoding preset (cf. x264 --fullhelp) (default "medium")
  -aq-mode           <int>        E..V....... AQ method (from -1 to INT_MAX) (default -1)
     none            0            E..V.......
     variance        1            E..V....... Variance AQ (complexity mask)
  -ssim              <boolean>    E..V....... Calculate and print SSIM stats. (default auto)
AVCodecContext AVOptions:
  -ab                <int64>      E...A...... set bitrate (in bits/s) (from 0 to INT_MAX) (default 128000)
  -flags             <flags>      ED.VAS..... (default 0)
     mv4                          E..V....... use four motion vectors per macroblock (MPEG-4)
  -skip_frame        <int>        .D.V....... skip decoding for the selected frames (from INT_MIN to INT_MAX) (default default)
""";
        var v = KnA.Core.Tools.ToolHelp.ParseFfmpeg(text, 'V');
        Assert.Equal(["-preset", "-aq-mode", "-ssim", "-flags"], v.Select(o => o.Name));
        Assert.Equal(["none", "variance"], v[1].Values);
        Assert.Equal(["0", "1"], v[2].Values);
        Assert.Contains(KnA.Core.Tools.ToolHelp.ParseFfmpeg(text, 'A'), o => o.Name == "-ab");
        Assert.DoesNotContain(KnA.Core.Tools.ToolHelp.ParseFfmpeg(text, 'A'), o => o.Name == "-preset");
    }

    [Fact]
    public void Gnu_help_of_standalone_encoders()
    {
        const string text = """
Usage: fdkaac [options] input_file
Options:
 -h, --help                     Print this help message
 -p, --profile <n>              Profile (audio object type)
                                 2: MPEG-4 AAC LC (default)
                                 5: MPEG-4 HE-AAC (SBR)
 -b, --bitrate <n>              Bitrate in bits per seconds (for CBR)
 --afterburner <n>              Afterburner
   -c,--codec <string>          set encode codec
                                  - h264(default), hevc, av1
   --aq                         enable spatial AQ
""";
        var o = KnA.Core.Tools.ToolHelp.ParseGnu(text);
        Assert.Contains(o, x => x.Name == "--profile" && x.Argument == "n" && x.Values.SequenceEqual(["2", "5"]));
        Assert.Contains(o, x => x.Name == "--codec" && x.Values.SequenceEqual(["h264", "hevc", "av1"]));
        Assert.Contains(o, x => x.Name == "--aq" && x.IsFlag);
        Assert.Contains(o, x => x.Name == "--afterburner");
    }

    [Fact]
    public async Task Real_ffmpeg_encoder_options_are_read()
    {
        var ffmpeg = KnA.Core.Tools.FfmpegLocator.Locate(null, Path.GetTempPath())?.Ffmpeg;
        if (ffmpeg is null) return;
        var o = await KnA.Core.Tools.ToolHelp.FfmpegEncoderAsync(ffmpeg, "libx264", 'V');
        Assert.Contains(o, x => x.Name == "-crf");
        Assert.Contains(o, x => x.Name == "-g");   // codec-wide option
    }
}

public class ToolArgumentsTests
{
    [Fact]
    public void Hand_set_parameters_reach_every_program()
    {
        var plan = new AudioTrackPlan { Encoder = AudioEncoderSet.Fdkaac, Channels = 2, Kbps = 128 };
        var fdk = AudioPlanner.FdkaacArgs(plan, "o.m4a", "--afterburner 1 -m 3");
        Assert.Equal(["--ignorelength", "-S", "-p", "2", "-m", "3", "-b", "128000", "--afterburner", "1", "-o", "o.m4a", "-"], fdk);

        var ff = AudioPlanner.BuildArgs(new AudioTrackPlan { Encoder = "aac", Channels = 2, Kbps = 128 }, 1, new AudioFilterSettings(), "-aac_coder twoloop -b:a 160k");
        Assert.Equal(["-aac_coder:a:1", "twoloop", "-b:a:1", "160k"], ff.TakeLast(4));

        Assert.Equal(["-c", "1", "-dummy:s:0", "x"], ["-c", "1", .. ToolArguments.ForStream("-dummy x", 's', 0)]);
    }
}

public class ReleaseNamesTests
{
    [Fact]
    public void One_package_per_system_as_the_workflow_names_them()
    {
        Assert.Equal("KnA-Windows-x64.zip", KnA.Core.Tools.AppUpdater.AssetName("desktop", "win-x64"));
        Assert.Equal("KnA-Windows-ARM64.zip", KnA.Core.Tools.AppUpdater.AssetName("desktop", "win-arm64"));
        Assert.Equal("KnA-macOS-AppleSilicon.zip", KnA.Core.Tools.AppUpdater.AssetName("desktop", "osx-arm64"));
        Assert.Equal("KnA-macOS-Intel.zip", KnA.Core.Tools.AppUpdater.AssetName("desktop", "osx-x64"));
        Assert.Equal("KnA-Linux-ARM64.tar.gz", KnA.Core.Tools.AppUpdater.AssetName("desktop", "linux-arm64"));
        Assert.Equal("KnA-Server-Linux-x64.tar.gz", KnA.Core.Tools.AppUpdater.AssetName("server", "linux-x64"));
        Assert.Equal("KnA-Server-Windows-x64.zip", KnA.Core.Tools.AppUpdater.AssetName("server", "win-x64"));
        // every name the updater asks for is one the release workflow produces
        var yml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "../../../../../.github/workflows/release.yml"));
        foreach (var name in new[] { "Windows-x64", "Windows-ARM64", "macOS-AppleSilicon", "macOS-Intel", "Linux-x64", "Linux-ARM64" })
            Assert.Matches("\"?name\"?\\s*:\\s*\"?" + System.Text.RegularExpressions.Regex.Escape(name) + "\"?", yml);   // YAML or the JSON matrix
    }
}

public class UpdaterTests
{
    private static async Task<(string Url, Action Stop)> Serve(byte[] body)
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        var http = new System.Net.HttpListener();
        http.Prefixes.Add($"http://127.0.0.1:{port}/");
        http.Start();
        _ = Task.Run(async () =>
        {
            while (http.IsListening)
            {
                try
                {
                    var c = await http.GetContextAsync();
                    c.Response.ContentLength64 = body.Length;
                    await c.Response.OutputStream.WriteAsync(body);
                    c.Response.Close();
                }
                catch (Exception) { return; }
            }
        });
        await Task.Yield();
        return ($"http://127.0.0.1:{port}/KnA-Linux-x64.tar.gz", () => http.Stop());
    }

    private static byte[] Package()
    {
        using var ms = new MemoryStream();
        using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new System.Formats.Tar.TarWriter(gz))
            foreach (var (name, text) in new[] { ("KnA/KnA", "new program"), ("KnA/portable.txt", "marker"), ("KnA/tools/ffmpeg/ffmpeg", "bundled ffmpeg"), ("KnA/lib.so", "new lib") })
                tar.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, name) { DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)) });
        return ms.ToArray();
    }

    [Fact]
    public async Task Update_replaces_program_files_only_and_checks_the_sha256()
    {
        var root = Directory.CreateTempSubdirectory("kna-upd-").FullName;
        var app = Path.Combine(root, "app");
        Directory.CreateDirectory(Path.Combine(app, "tools", "ffmpeg"));
        File.WriteAllText(Path.Combine(app, "KnA"), "old program");
        File.WriteAllText(Path.Combine(app, "tools", "ffmpeg", "ffmpeg"), "user's newer ffmpeg");
        var pkg = Package();
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pkg));
        var (url, stop) = await Serve(pkg);
        try
        {
            var bad = new KnA.Core.Tools.ReleaseAsset("KnA-Linux-x64.tar.gz", url, pkg.Length) { Sha256 = new string('0', 64) };
            await Assert.ThrowsAsync<InvalidDataException>(() => KnA.Core.Tools.AppUpdater.InstallAsync(bad, Path.Combine(root, "data"), null, default, app));
            Assert.Equal("old program", File.ReadAllText(Path.Combine(app, "KnA")));

            var good = bad with { Sha256 = sha };
            await KnA.Core.Tools.AppUpdater.InstallAsync(good, Path.Combine(root, "data"), null, default, app);
            Assert.Equal("new program", File.ReadAllText(Path.Combine(app, "KnA")));
            Assert.Equal("new lib", File.ReadAllText(Path.Combine(app, "lib.so")));
            Assert.Equal("user's newer ffmpeg", File.ReadAllText(Path.Combine(app, "tools", "ffmpeg", "ffmpeg")));   // tools stay
            Assert.False(File.Exists(Path.Combine(app, "portable.txt")));   // a non-portable copy stays non-portable
        }
        finally { stop(); }
    }

    [Fact]
    public void Permission_is_a_real_write_test()
    {
        var p = KnA.Core.Tools.AppUpdater.CheckPermission();
        Assert.True(p.CanInstall, p.Reason);   // the test folder is writable
    }
}
