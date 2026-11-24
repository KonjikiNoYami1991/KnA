using KnA.Core.Media;
using KnA.Core.Processing;
using KnA.Core.Tools;
using Xunit;

namespace KnA.Core.Tests;

/// <summary>KnA's portable VapourSynth: its configuration, and every catalogue filter run for real by vspipe.</summary>
public class VapourSynthTests(FeatureFixture fx) : IClassFixture<FeatureFixture>
{
    [Fact]
    public void The_configuration_holds_the_current_paths()
    {
        var root = Directory.CreateTempSubdirectory("kna-vsroot-").FullName;
        var package = Path.Combine(root, "lib", "python3.13", "site-packages", "vapoursynth");
        Directory.CreateDirectory(package);
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        File.WriteAllText(Path.Combine(package, "vspipe"), "");
        File.WriteAllText(Path.Combine(package, OperatingSystem.IsMacOS() ? "libvsscript.dylib" : "libvsscript.so"), "");
        File.WriteAllText(Path.Combine(root, "bin", "python3.13"), "");
        File.WriteAllText(Path.Combine(root, "lib", OperatingSystem.IsMacOS() ? "libpython3.13.dylib" : "libpython3.13.so.1.0"), "");
        Assert.Equal(root, VapourSynthRuntime.RootOf(Path.Combine(package, "vspipe")));
        Assert.Null(VapourSynthRuntime.RootOf("/usr/bin/vspipe"));
        if (OperatingSystem.IsWindows()) return;
        var data = Directory.CreateTempSubdirectory("kna-vsdata-").FullName;
        // The pure part only: the process-wide state belongs to the real VapourSynth of the other tests.
        var home = VapourSynthRuntime.WriteConfig(Path.Combine(package, "vspipe"), root, data);
        Assert.Equal(Path.Combine(data, "vapoursynth-config"), home);
        var toml = File.ReadAllText(Path.Combine(data, "vapoursynth-config", "vapoursynth", "vapoursynth.toml"));
        Assert.StartsWith($"\"{package}/libvsscript.", toml);
        Assert.Contains($"\"{root}/bin/python3.13\"", toml);
        // The programs that load VapourSynth get it, the others don't.
        var psi = new System.Diagnostics.ProcessStartInfo();
        VapourSynthRuntime.Apply(psi, Path.Combine(package, "vspipe"), ["-c", "y4m", "a.vpy", "-"], home, root);
        Assert.Equal(home, psi.Environment["XDG_CONFIG_HOME"]);
        var other = new System.Diagnostics.ProcessStartInfo();
        VapourSynthRuntime.Apply(other, "/usr/bin/ffmpeg", ["-i", "a.mkv"], home, root);
        Assert.NotEqual(home, other.Environment.TryGetValue("XDG_CONFIG_HOME", out var x) ? x : null);
    }

    /// <summary>Needs a VapourSynth (KNA_VSPIPE, e.g. a package's tools/vapoursynth): skipped otherwise.</summary>
    [Fact]
    public async Task Every_catalogue_filter_runs_in_vspipe()
    {
        var vspipe = Settings.AppEnv.Get("VSPIPE");
        if (fx.Tools is null || vspipe is null || !File.Exists(vspipe)) return;
        var data = Directory.CreateTempSubdirectory("kna-vsrun-").FullName;
        VapourSynthRuntime.Prepare(vspipe, data);
        var clip = Path.Combine(data, "in.mkv");
        var g = await ProcessRunner.RunAsync(fx.Tools.Ffmpeg, ["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=25:duration=1,interlace",
            "-c:v", "libx264", "-preset", "ultrafast", "-flags", "+ildct+ilme", clip]);
        Assert.Equal(0, g.ExitCode);
        var progressive = Path.Combine(data, "p.mkv");
        g = await ProcessRunner.RunAsync(fx.Tools.Ffmpeg, ["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=25:duration=1", "-c:v", "libx264", "-preset", "ultrafast", progressive]);
        Assert.Equal(0, g.ExitCode);
        var failed = new List<string>();
        foreach (var f in FilterCatalog.For(FrameserverKind.VapourSynth).Where(f => f.Id is not ("vs.custom" or "vs.knlm" or "vs.placebo")))
        {
            var fs = new FrameserverSettings { Kind = FrameserverKind.VapourSynth, Chain = [new FrameserverFilter(f.Id)] };
            var script = Path.Combine(data, f.Id + ".vpy");
            // vsjetpack's filters refuse interlaced input (they come after a deinterlacer).
            File.WriteAllText(script, fs.EffectiveScript!.Replace("{source}", f.EnglishCategory == "Deinterlace / IVTC" ? clip : progressive).Replace("{cachedir}", data));
            var r = await ProcessRunner.RunAsync(vspipe, ["-c", "y4m", script, "-"], workingDirectory: data, timeout: TimeSpan.FromMinutes(3));
            if (r.ExitCode != 0) failed.Add(f.Id + ": " + r.StdErr.Trim().Split('\n').LastOrDefault());
        }
        Assert.True(failed.Count == 0, string.Join("\n", failed));
    }
}
