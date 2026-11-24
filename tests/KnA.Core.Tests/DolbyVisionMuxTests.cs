using System.Globalization;
using KnA.Core.Encoding;
using KnA.Core.Media;
using KnA.Core.Processing;
using KnA.Core.Queue;
using KnA.Core.Settings;
using KnA.Core.Tools;
using Xunit;

namespace KnA.Core.Tests;

/// <summary>The end of an HDR encode: injected metadata and the Dolby Vision configuration record (mkvmerge).</summary>
public class DolbyVisionMuxTests
{
    [Fact]
    public void Timestamp_file_is_mkvmerge_v2_in_display_order()
    {
        Assert.Equal("# timestamp format v2\n500\n541.708\n583.417\n", HdrMetadata.TimestampsV2([0.5, 0.583417, 0.541708]));
    }

    [Fact]
    public void Mkvmerge_gets_the_static_metadata_language_and_name()
    {
        var plan = new HdrPlan
        {
            Mastering = new MasteringDisplay(0.68, 0.32, 0.265, 0.69, 0.15, 0.06, 0.3127, 0.329, 0.0001, 1000), MaxCll = 1000, MaxFall = 400,
        };
        var a = HdrMetadata.MkvmergeVideoArgs(plan, new HdrMetadata.VideoCount([0], "ita", "Film", false), "ts.txt");
        Assert.Equal(["--timestamps", "0:ts.txt", "--language", "0:ita", "--track-name", "0:Film", "--max-content-light", "0:1000",
            "--max-frame-light", "0:400", "--chromaticity-coordinates", "0:0.68,0.32,0.265,0.69,0.15,0.06",
            "--white-colour-coordinates", "0:0.3127,0.329", "--max-luminance", "0:1000", "--min-luminance", "0:0.0001"], a);
    }

    /// <summary>
    /// With FFmpeg, dovi_tool and mkvmerge on this machine: an HEVC file with audio and a start offset gets a Dolby Vision
    /// RPU injected and the configuration record, keeping its timing, audio and titles (MKV and MP4).
    /// </summary>
    [Theory]
    [InlineData("mkv", 72)]
    [InlineData("mp4", 72)]
    [InlineData("mkv", 74)]   // two frames too many (rounding at a trim): fitted with dovi_tool's editor
    [InlineData("mkv", 90)]   // a real mismatch: nothing injected, said
    public async Task Injected_rpu_gets_the_configuration_record_and_keeps_everything_else(string container, int rpuFrames)
    {
        var dir = Directory.CreateTempSubdirectory("kna-dv-").FullName;
        try
        {
            var tools = FfmpegLocator.Locate(null, dir);
            var dovi = FfmpegLocator.LocateTool(["dovi_tool"], dir);
            var mkvmerge = FfmpegLocator.LocateMkvmerge(dir);
            if (tools is null || dovi is null || mkvmerge is null) return;   // tools missing here: nothing to check
            tools = tools with { DoviTool = dovi, Mkvmerge = mkvmerge };
            var output = Path.Combine(dir, "film." + container);
            var r = await ProcessRunner.RunAsync(tools.Ffmpeg, ["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=24000/1001:duration=3",
                "-f", "lavfi", "-i", "sine=f=440:d=3", "-pix_fmt", "yuv420p10le", "-c:v", "libx265", "-x265-params", "log-level=error",
                "-c:a", "aac", "-metadata:s:a:0", "language=ita", "-metadata", "title=Film", "-output_ts_offset", "0.5", output]);
            Assert.Equal(0, r.ExitCode);
            File.WriteAllText(Path.Combine(dir, "gen.json"), """{"cm_version":"V40","length":LEN,"level6":{"max_display_mastering_luminance":1000,"min_display_mastering_luminance":1,"max_content_light_level":1000,"max_frame_average_light_level":400}}""".Replace("LEN", rpuFrames.ToString(CultureInfo.InvariantCulture)));
            var rpu = Path.Combine(dir, "RPU.bin");
            Assert.Equal(0, (await ProcessRunner.RunAsync(dovi, ["generate", "-j", Path.Combine(dir, "gen.json"), "-o", rpu])).ExitCode);

            var job = new QueueJob { SourcePath = output };
            var ctx = new RunnerContext(tools, new AppSettings(), new SettingsStore(Path.Combine(dir, "data")), new ProcessGroup(), _ => { });
            var plan = new HdrPlan { Action = HdrAction.Preserve, DolbyVision = HdrCarry.Inject, DolbyVisionProfile = "8.1", MaxCll = 1000, MaxFall = 400 };
            var final = new CompiledJob(new EncodePlan([], output), [], plan, [], container, TimeSpan.FromSeconds(3));
            var warnings = await HdrMetadata.FinishAsync(job, final, new HdrSideFiles(rpu, null), dir, ctx, CancellationToken.None);
            if (rpuFrames == 90)
            {
                Assert.Contains(warnings, w => w.Contains("90"));
                var untouched = await ProcessRunner.RunAsync(tools.Ffprobe, ["-v", "error", "-show_entries", "stream_side_data=side_data_type", "-of", "compact", output]);
                Assert.DoesNotContain("DOVI", untouched.StdOut);
                return;
            }
            Assert.Empty(warnings);

            var p = await ProcessRunner.RunAsync(tools.Ffprobe, ["-v", "error", "-show_entries",
                "stream=codec_type,start_time:stream_tags=language:stream_side_data=side_data_type,dv_profile:format_tags=title", "-of", "compact", output]);
            Assert.Contains("DOVI configuration record", p.StdOut);
            Assert.Contains("dv_profile=8", p.StdOut);
            Assert.Contains("codec_type=audio", p.StdOut);
            Assert.Contains("language=ita", p.StdOut);
            Assert.Contains("title=Film", p.StdOut);
            var start = p.StdOut.Split('\n').First(l => l.Contains("codec_type=video"));
            Assert.Contains("start_time=0.5", start);
            var audio = p.StdOut.Split('\n').First(l => l.Contains("codec_type=audio"));
            Assert.Contains("start_time=0.4", audio);
        }
        finally { Directory.Delete(dir, true); }
    }
}
