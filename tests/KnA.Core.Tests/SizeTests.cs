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
