using System.Diagnostics;
using System.Text;

namespace KnA.Core.Tools;

/// <summary>
/// KnA's own VapourSynth: a portable Python 3.13 (python-build-standalone) with the official VapourSynth wheel, the
/// plugins of the filter catalogue (wheels too) and vsjetpack, in tools/vapoursynth — the same on Windows, Linux and
/// macOS, nothing installed on the system. Every package version is fixed, so the catalogue's script lines always
/// match the functions they call.
/// On Linux and macOS VapourSynth finds its Python through a small configuration file (vapoursynth.toml) holding
/// absolute paths; KnA writes it at run time with the current paths in a folder of its own (the program can be moved,
/// KnA.app is read-only) and points the programs that load VapourSynth to it (XDG_CONFIG_HOME). On Windows the wheel
/// needs no configuration (python.exe and python3.dll sit next to Lib\site-packages).
/// </summary>
public static class VapourSynthRuntime
{
    /// <summary>VapourSynth release KnA installs (the "latest" shown in Settings → Components).</summary>
    public const string Release = "R82";
    public const int PythonMinor = 13;

    /// <summary>Installed together (one pip call): VapourSynth, vsjetpack and the catalogue's plugins, exact versions.</summary>
    public static IReadOnlyList<string> Required(string arch) =>
    [
        "vapoursynth==82", "vsjetpack==3.0.0", "scipy==1.18.1", "numpy==2.5.5", "vsnoise==1.3",
        "vapoursynth-bestsource==23", "vapoursynth-lsmas==1310.0.0.0", "vapoursynth-bwdif==5.1", "vapoursynth-cas==3.0",
        "vapoursynth-vivtc==2.0", "vapoursynth-znedi3==3.3", "vapoursynth-mvtools==29", "vapoursynth-mvutensils==10",
        "vapoursynth-zsmooth==0.20.0", "vapoursynth-resize2==0.5.1", "vapoursynth-awarp==3.1", "vapoursynth-edgemasks==5.0",
        "vapoursynth-subtext==7.0", "vapoursynth-knlmeanscl==1.3", "vapoursynth-vszip==24.0.0", "vapoursynth-akarin==1.5.0",
        "vapoursynth-descale==12", "vapoursynth-deblock==9.0", "vapoursynth-bm3d==12.0",
        // DFTTest: the hand-optimised x86 build, the portable C++ one on Arm (no x86 build there).
        arch == "x64" ? "dfttest2[cpu]==11.1.1" : "dfttest2[gcc]==11.1.1",
    ];

    /// <summary>Installed one by one, skipped where no build exists (e.g. vs-placebo on Intel Macs).</summary>
    public static IReadOnlyList<string> Optional { get; } =
        ["vs-placebo==2.0.5", "vapoursynth-hysteresis==1.5.0", "vapoursynth-sangnom==45.0", "vapoursynth-dmetrics==2.0"];

    /// <summary>Script that checks the installation: the plugins and Python modules the catalogue uses.</summary>
    public const string SmokeTest = """
        import vapoursynth as vs
        import vsdeinterlace, vsdenoise, vsdeband, vsdehalo, vsrgtools
        core = vs.core
        missing = [ns for ns in ("lsmas", "bs", "bwdif", "cas", "vivtc", "znedi3", "mv", "mvu", "zsmooth", "awarp", "sub", "knlm", "vszip", "akarin")
                   if not hasattr(core, ns)]
        if missing:
            raise RuntimeError("missing plugins: " + ", ".join(missing))
        clip = core.std.BlankClip(width=64, height=64, length=2)
        clip.set_output(0)
        """;

    /// <summary>Folder whose "vapoursynth" subfolder holds the generated configuration (null = none needed).</summary>
    public static string? ConfigHome { get; private set; }
    /// <summary>The portable Python folder of the VapourSynth in use (null = a system installation).</summary>
    public static string? Root { get; private set; }

    /// <summary>The portable Python folder of a vspipe inside a pip package (…/site-packages/vapoursynth/vspipe), else null.</summary>
    public static string? RootOf(string vspipe)
    {
        var package = Path.GetDirectoryName(Path.GetFullPath(vspipe));
        var site = package is null ? null : Path.GetDirectoryName(package);
        if (site is null || !string.Equals(Path.GetFileName(site), "site-packages", StringComparison.OrdinalIgnoreCase)) return null;
        var lib = Path.GetDirectoryName(site)!;
        // Windows: <root>\Lib\site-packages; Linux/macOS: <root>/lib/python3.N/site-packages.
        var root = Path.GetFileName(lib).StartsWith("python3", StringComparison.Ordinal) ? Path.GetDirectoryName(Path.GetDirectoryName(lib)!) : Path.GetDirectoryName(lib);
        return root is not null && (File.Exists(Path.Combine(root, "python.exe")) || Directory.Exists(Path.Combine(root, "bin"))) ? root : null;
    }

    /// <summary>
    /// Makes a portable VapourSynth usable: on Linux/macOS writes vapoursynth.toml (current paths) under
    /// <paramref name="dataDirectory"/> and remembers it for the programs started afterwards. Returns false when the
    /// installation is incomplete. A system VapourSynth is left alone.
    /// </summary>
    public static bool Prepare(string vspipe, string dataDirectory)
    {
        var root = RootOf(vspipe);
        Root = root;
        if (root is null || OperatingSystem.IsWindows()) { ConfigHome = null; return root is not null || File.Exists(vspipe); }
        var package = Path.GetDirectoryName(Path.GetFullPath(vspipe))!;
        var vsscript = Directory.EnumerateFiles(package, "libvsscript*").FirstOrDefault();
        // The real files, not the python3 / libpython….so links: VapourSynth checks the executable's modification time.
        static string? Real(IEnumerable<string> files) => files.Select(f => new FileInfo(f))
            .Select(f => f.LinkTarget is null ? f.FullName : f.ResolveLinkTarget(true)?.FullName).FirstOrDefault(f => f is not null && File.Exists(f));
        var python = Real(Directory.EnumerateFiles(Path.Combine(root, "bin"), "python3*").Where(f => !f.EndsWith("-config", StringComparison.Ordinal)).OrderByDescending(f => f.Length));
        var libpython = Real(Directory.EnumerateFiles(Path.Combine(root, "lib"), OperatingSystem.IsMacOS() ? "libpython3.*.dylib" : "libpython3.*.so*")
            .Where(f => !Path.GetFileName(f).StartsWith("libpython3.so", StringComparison.Ordinal)));
        if (vsscript is null || python is null || libpython is null) { ConfigHome = null; return false; }
        var home = Path.Combine(dataDirectory, "vapoursynth-config");
        var file = Path.Combine(home, "vapoursynth", "vapoursynth.toml");
        var text = Toml(vsscript, python, libpython);
        try
        {
            if (!File.Exists(file) || File.ReadAllText(file) != text)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, text, new UTF8Encoding(false));
            }
            ConfigHome = home;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ConfigHome = null; return false; }
    }

    /// <summary>
    /// The line VapourSynth's own "vapoursynth config" writes: the VSScript library (key, "/lib64/" read as "/lib/") →
    /// Python executable, Python library and the executable's modification time.
    /// </summary>
    public static string Toml(string vsscript, string python, string libpython)
    {
        static string Q(string s) => "\"" + s.Replace("\\", "\\\\") + "\"";
        long mtime = new DateTimeOffset(File.GetLastWriteTimeUtc(python)).ToUnixTimeSeconds();
        return $"{Q(vsscript.Replace("/lib64/", "/lib/"))} = [{Q(python)},{Q(libpython)},{Q(mtime.ToString(System.Globalization.CultureInfo.InvariantCulture))}]\n";
    }

    /// <summary>Programs that load VapourSynth (vspipe, its Python, an encoder reading a script) get the configuration.</summary>
    public static void Apply(ProcessStartInfo psi, string fileName, IEnumerable<string> args)
    {
        if (ConfigHome is not { } home) return;
        bool vs = Path.GetFileNameWithoutExtension(fileName).Equals("vspipe", StringComparison.OrdinalIgnoreCase)
                  || (Root is { } r && Path.GetFullPath(fileName).StartsWith(r, StringComparison.Ordinal))
                  || args.Any(a => a is "--vpy" || a.EndsWith(".vpy", StringComparison.OrdinalIgnoreCase));
        if (vs) psi.Environment["XDG_CONFIG_HOME"] = home;
    }

    /// <summary>
    /// Plugin namespaces and vsjetpack modules of the VapourSynth in use (read once in the background), null while
    /// unknown or without VapourSynth. The editors mark the catalogue filters whose plugin is missing.
    /// </summary>
    public static IReadOnlySet<string>? Available { get; private set; }
    private static string? _probed;

    private const string ProbeScript = """
        import sys, importlib.util
        import vapoursynth as vs
        core = vs.core
        names = [p.namespace for p in core.plugins()]
        names += [m for m in ("vsdeinterlace", "vsdenoise", "vsdeband", "vsdehalo", "vsrgtools") if importlib.util.find_spec(m)]
        print("KNA-PLUGINS:" + ",".join(names), file=sys.stderr)
        core.std.BlankClip(width=16, height=16, length=1).set_output(0)
        """;

    /// <summary>Reads which plugins the VapourSynth of <paramref name="vspipe"/> has (once per vspipe).</summary>
    public static async Task ProbeAsync(string? vspipe, string tempDirectory, CancellationToken ct = default)
    {
        if (vspipe is null) { Available = null; _probed = null; return; }
        if (_probed == vspipe && Available is not null) return;
        try
        {
            Directory.CreateDirectory(tempDirectory);
            var script = Path.Combine(tempDirectory, "kna-plugins.vpy");
            await File.WriteAllTextAsync(script, ProbeScript, ct);
            var r = await Processing.ProcessRunner.RunAsync(vspipe, ["--info", script], workingDirectory: tempDirectory, timeout: TimeSpan.FromMinutes(1), ct: ct);
            var line = (r.StdErr + "\n" + r.StdOut).Split('\n').FirstOrDefault(l => l.StartsWith("KNA-PLUGINS:", StringComparison.Ordinal));
            Available = line is null ? null : line["KNA-PLUGINS:".Length..].Trim().Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            _probed = vspipe;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Available = null; }
    }

    /// <summary>Whether a catalogue filter can run here (unknown = yes).</summary>
    public static bool Has(string? plugin) => plugin is null || Available is not { } a || a.Contains(plugin);

    /// <summary>The Python of a portable VapourSynth (to run the smoke test and pip).</summary>
    public static string? PythonOf(string root) =>
        OperatingSystem.IsWindows() ? Path.Combine(root, "python.exe") : Path.Combine(root, "bin", "python3");
}
