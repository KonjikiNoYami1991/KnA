using System.Globalization;
using System.Text;
using KnA.Core.Localization;
using KnA.Core.Media;

namespace KnA.Core.Encoding;

/// <summary>
/// The picture chain of an encode written as a VapourSynth script, like StaxRip shows it: always generated and shown
/// in the editor (Filters tab), with each step marked where it really runs. Steps with a GPU equivalent run inside the
/// GPU encoder (rigaya's --vpp-*) or as FFmpeg filters; the catalogue filters (QTGMC, SMDegrain…) are the script itself.
/// </summary>
public static class ScriptPreview
{
    private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    public static string Build(JobSettings s, VideoStream video, string source, HdrPlan hdr, ScaleResult? scale, JobEdit edit,
        TrackPlan? burn, PreparedSubtitles? prepared, GpuPipeline? gpu)
    {
        var f = s.VideoFilters;
        var sb = new StringBuilder();
        string Where(string option, string cpu) =>
            gpu is null ? cpu
            : gpu.Steps.FirstOrDefault(x => x.Option?.StartsWith(option, StringComparison.Ordinal) == true) is { Place: StepPlace.Gpu } g ? "GPU: " + g.Option
            : gpu.UsesFfmpeg ? cpu : "GPU: " + option;
        void Step(string comment, string? line)
        {
            sb.Append("# ").AppendLine(comment);
            if (line is not null) sb.AppendLine(line);
        }

        sb.AppendLine("# " + L.T("Script of this encode (VapourSynth), written by KnA from the settings: each step says where it runs."));
        sb.AppendLine("import vapoursynth as vs");
        sb.AppendLine("core = vs.core");
        var fs = s.Frameserver;
        bool catalogue = fs.Kind == FrameserverKind.VapourSynth && fs.Chain is { Count: > 0 };
        sb.AppendLine($"clip = core.lsmas.LWLibavSource(source=r\"{source}\")");
        if (catalogue)
        {
            sb.AppendLine("# " + L.T("Filters chosen from the catalogue (VapourSynth):"));
            foreach (var item in fs.Chain!)
                if (FilterCatalog.Line(fs.Kind, item) is { } l) sb.AppendLine(l);
        }
        else if (fs.Kind != FrameserverKind.None)
            sb.AppendLine("# " + L.T("Your {0} script runs first (Filters → VapourSynth / AviSynth).", fs.Kind == FrameserverKind.AviSynth ? "AviSynth" : "VapourSynth"));

        var parts = edit.KeptParts(video.FrameCount > 0 && video.FrameRate > 0 ? video.FrameCount / video.FrameRate : 0);
        if (parts.Count > 0 && video.FrameRate > 0)
        {
            var cuts = parts.Select(p => $"clip[{(long)Math.Round(p.Start * video.FrameRate)}:{(p.End >= 1e9 ? "" : ((long)Math.Round(p.End * video.FrameRate)).ToString(CultureInfo.InvariantCulture))}]");
            Step(L.T("Trim") + " — " + Where("--trim", "FFmpeg"), "clip = " + string.Join(" + ", cuts));
        }
        var deint = FilterBuilder.Deinterlacing(video, f);
        if (deint is DeinterlaceMode.Force or DeinterlaceMode.Auto)
            Step(L.T("Deinterlace (Bwdif)") + " — " + Where("--vpp-bwdif", "FFmpeg bwdif"), "clip = core.bwdif.Bwdif(clip, field=1)");
        else if (deint == DeinterlaceMode.Ivtc)
            Step(L.T("Inverse telecine") + " — " + Where("--vpp-ivtc", "FFmpeg fieldmatch + decimate"), "clip = core.vivtc.VFM(clip, order=1)\nclip = core.vivtc.VDecimate(clip)");
        if (f.Crop is { IsEmpty: false } c)
            Step(L.T("Crop") + " — " + Where("--crop", "FFmpeg crop"),
                $"clip = core.std.Crop(clip, left={c.Left & ~1}, right={c.Right & ~1}, top={c.Top & ~1}, bottom={c.Bottom & ~1})");
        if (hdr.Action == HdrAction.ToneMap)
            Step(L.T("HDR → SDR ({0})", s.ToneMap.ToString()) + " — " + Where("--vpp-colorspace", "FFmpeg " + (hdr.Filter?.Split('=')[0] ?? "tonemap")), null);
        var dn = f.Denoise switch
        {
            FilterStrength.Light => (2.0, 1.5, 3.0, 2.25),
            FilterStrength.Medium => (4.0, 3.0, 6.0, 4.5),
            FilterStrength.Strong => (7.0, 5.5, 10.0, 8.0),
            _ => ((double, double, double, double)?)null,
        };
        if (dn is var (ls, cs, lt, ct))
            Step(L.T("Denoise (hqdn3d)") + " — " + Where("--vpp-hqdn3d", "FFmpeg hqdn3d"),
                $"clip = core.hqdn3d.Hqdn3d(clip, lum_spac={N(ls)}, chrom_spac={N(cs)}, lum_tmp={N(lt)}, chrom_tmp={N(ct)})");
        var db = f.Deband switch { FilterStrength.Light => (12, 32), FilterStrength.Medium => (15, 48), FilterStrength.Strong => (22, 64), _ => ((int, int)?)null };
        if (db is var (range, thr))
            Step(L.T("Deband") + " — " + Where("--vpp-deband", "FFmpeg deband"), $"clip = core.neo_f3kdb.Deband(clip, range={range}, y={thr}, cb={thr}, cr={thr})");
        if (burn is not null)
            Step(L.T("Burned subtitles") + " — " + Where("--vpp-subburn", "FFmpeg (libass)"),
                prepared?.RelativeSubtitleFile is { } sub ? $"clip = core.sub.TextFile(clip, file=r\"{sub}\"" + (prepared.RelativeFontsDir is { } fd ? $", fontdir=r\"{fd}\"" : "") + ")"
                : "# " + L.T("picture subtitles of track #{0}", burn.Stream));
        if (scale is { } sc)
            Step(L.T("Resize to {0}×{1} (Lanczos)", sc.Width, sc.Height) + " — " + Where("--output-res", "FFmpeg scale"),
                $"clip = core.resize.Lanczos(clip, width={sc.Width}, height={sc.Height})");
        var sh = f.Sharpen switch { FilterStrength.Light => 0.3, FilterStrength.Medium => 0.5, FilterStrength.Strong => 0.8, _ => 0.0 };
        if (sh > 0) Step(L.T("Sharpen (CAS)") + " — " + Where("--vpp-cas", "FFmpeg cas"), $"clip = core.cas.CAS(clip, sharpness={N(sh)})");
        if (f.Fps is > 0 and var fps) Step(L.T("Frame rate {0} — FFmpeg fps (no GPU equivalent)", N(fps)), null);
        if (!string.IsNullOrWhiteSpace(f.Custom)) Step(L.T("Custom FFmpeg filters — FFmpeg: {0}", f.Custom.Trim()), null);
        if (!string.IsNullOrWhiteSpace(s.Encoder.GpuFilters) && RigayaEncoders.Wanted(s)) Step(L.T("Your GPU filters — GPU: {0}", s.Encoder.GpuFilters!.Trim()), null);
        sb.AppendLine("clip.set_output()");
        return sb.ToString();
    }
}
