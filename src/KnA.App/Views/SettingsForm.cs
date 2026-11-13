using Avalonia.Controls;
using Avalonia.Threading;
using KnA.Core.Ai;
using KnA.Core.Encoding;
using KnA.Core.Localization;
using KnA.Core.Tools;

namespace KnA.App.Views;

/// <summary>
/// Encode settings form, shared by the side panel ("quick") and the editor Output tab ("full").
/// Mirrors wwwroot/form.js: same fields, same visibility rules.
/// </summary>
public sealed class SettingsForm : StackPanel
{
    private readonly Func<FfmpegCapabilities?> _caps;
    private readonly bool _full;
    private JobSettings _s;
    private IReadOnlyList<string> _containers = [];

    public event Action<JobSettings>? Changed;

    public SettingsForm(JobSettings settings, Func<FfmpegCapabilities?> caps, bool full, Func<bool>? fdkaac = null)
    {
        FdkaacAvailable = fdkaac;
        _s = settings;
        _caps = caps;
        _full = full;
        Spacing = 12;
        Rebuild();
    }

    public JobSettings Settings
    {
        get => _s;
        set { _s = value; Rebuild(); }
    }

    /// <summary>Containers valid for the current file (editor only).</summary>
    public IReadOnlyList<string> AllowedContainers
    {
        get => _containers;
        set { if (!_containers.SequenceEqual(value)) { _containers = value; Rebuild(); } }
    }

    private void Set(JobSettings s, bool rebuild = true)
    {
        _s = s;
        if (rebuild) Dispatcher.UIThread.Post(Rebuild);
        Changed?.Invoke(s);
    }

    /// <summary>Which AAC encoder will be used (same text as the web UI).</summary>
    private string AacNote()
    {
        var builds = _caps()?.Builds ?? [];
        if (builds.Any(b => b.Has("libfdk_aac"))) return L.T("AAC: libfdk_aac (Fraunhofer, best quality).");
        if (builds.Any(b => b.Has("aac_at"))) return L.T("AAC: Apple AudioToolbox (excellent quality).");
        if (FdkaacAvailable?.Invoke() == true) return L.T("AAC: fdkaac program (Fraunhofer, best quality).");
        return L.T("AAC: FFmpeg's own encoder. Install fdkaac in Settings → Components for better quality.");
    }

    /// <summary>Whether the standalone fdkaac was found (given by the window).</summary>
    private readonly Func<bool>? FdkaacAvailable;

    public void Rebuild()
    {
        Children.Clear();
        var s = _s;
        var p = s.ProfileDefinition;
        var codec = p.Kind == ProfileKind.RemuxMp4 ? VideoCodec.H264 : s.EffectiveCodec;
        bool tenBitPossible = VideoEncoders.Supports10Bit(codec, EncoderBackend.Software) && !codec.IsProfessional()
                              && p.Kind is ProfileKind.UniversalMkv or ProfileKind.Streaming;
        bool pro = p.Kind == ProfileKind.Professional;

        Children.Add(Ui.Field("Profile",
            Ui.Combo(Profiles.All.Select(x => (x.Kind, x.Name)), s.Profile, v => Set(s with { Profile = v, Container = null })),
            p.Description));

        if (p.Codecs.Count > 1)
            Children.Add(Ui.Field("Video codec",
                Ui.Combo(p.Codecs.Select(c => (c, c.Display())), s.EffectiveCodec, v => Set(s with { Codec = v }))));

        if (p.EncodesVideo || p.Kind == ProfileKind.RemuxMp4)
        {
            var caps = _caps();
            bool tenBit = s.TenBit && tenBitPossible;
            var items = new List<(EncoderBackend, string, bool)>();
            foreach (var b in VideoEncoders.AllBackends)
            {
                if (!p.AllowsHardware && b != EncoderBackend.Software) continue;
                if (VideoEncoders.EncoderName(codec, b) is null) continue;
                bool ok = caps is null || caps.IsUsable(codec, b, tenBit);
                string note = ok ? "" : caps!.Find(codec, b) is { Compiled: false } ? L.T("not in this FFmpeg build")
                    : tenBit && caps.IsUsable(codec, b, false) ? L.T("8-bit only") : L.T("no compatible GPU/driver");
                items.Add((b, b.Display() + (ok ? "" : " — " + note), ok));
            }
            var current = items.FirstOrDefault(i => i.Item1 == s.Backend && i.Item3).Item1;
            if (!items.Any(i => i.Item1 == s.Backend && i.Item3))
            {
                current = items.FirstOrDefault(i => i.Item3).Item1;
                _s = s = s with { Backend = current };
            }
            var hint = !p.AllowsHardware ? L.T("This profile is CPU only.")
                : current == EncoderBackend.Software ? L.T("CPU encoding: slowest, best compression.")
                : L.T("Hardware encoding: much faster, slightly bigger files.");
            var combo = Ui.Combo(items, current, v => Set(s with { Backend = v }));
            combo.IsEnabled = p.AllowsHardware;
            Children.Add(Ui.Field("Encoder", combo, hint));
        }

        if (p.EncodesVideo && !pro)
            Children.Add(Ui.Toggle(L.T("10-bit output") + " (HEVC / AV1 / VP9 / VVC)", s.TenBit && tenBitPossible, v => Set(s with { TenBit = v }), tenBitPossible));
        if (pro)
        {
            var proCtl = new List<Control>();
            if (codec == VideoCodec.ProRes)
                proCtl.Add(Ui.Field("ProRes", Ui.Combo(Enum.GetValues<ProResProfile>().Select(x => (x, x.Display())), s.Pro.ProRes, v => Set(s with { Pro = s.Pro with { ProRes = v } }))));
            if (codec == VideoCodec.DNxHR)
                proCtl.Add(Ui.Field("DNxHR", Ui.Combo(Enum.GetValues<DnxhrProfile>().Select(x => (x, x.Display())), s.Pro.Dnxhr, v => Set(s with { Pro = s.Pro with { Dnxhr = v } }))));
            proCtl.Add(Ui.Field("Chroma", Ui.Combo(Enum.GetValues<ChromaFormat>().Select(x => (x, x.Display())), s.Pro.Chroma, v => Set(s with { Pro = s.Pro with { Chroma = v } }))));
            if (codec is VideoCodec.FFV1 or VideoCodec.FFVHuff or VideoCodec.JPEG2000)
                proCtl.Add(Ui.Field("Bit depth", Ui.Combo(new[] { 0, 8, 10, 12, 16 }.Select(d => (d, d == 0 ? L.T("Same as the source") : $"{d}-bit")), s.Pro.BitDepth, v => Set(s with { Pro = s.Pro with { BitDepth = v } }))));
            Children.Add(Ui.Columns([.. proCtl]));
        }

        var resQ = new List<Control>();
        if (p.UsesResolution)
            resQ.Add(Ui.Field("Resolution", Ui.Combo(Resolutions.All.Select(r => (r, Resolutions.Display(r))), s.Resolution, v => Set(s with { Resolution = v }))));
        if (p.UsesQuality)
            resQ.Add(Ui.Field("Quality", Ui.Combo(Enum.GetValues<QualityLevel>().Select(q => (q, q.Display())), s.Quality, v => Set(s with { Quality = v }))));
        if (resQ.Count > 0) Children.Add(Ui.Columns([.. resQ]));

        if (p.UsesSubtitleMode)
        {
            Children.Add(Ui.Field("Subtitles",
                Ui.Combo(Enum.GetValues<SubtitleMode>().Select(m => (m, m.Display())), s.Subtitles, v => Set(s with { Subtitles = v })),
                _full ? L.T("Automatic plan: the editor (Tracks tab) can override it per file.") : null));
            if (s.Subtitles == SubtitleMode.Hardsub)
                Children.Add(Ui.Toggle(L.T("Prefer external .ass/.srt next to the video"), s.UseSidecarSubtitles, v => Set(s with { UseSidecarSubtitles = v }, false)));
        }

        if (_full && _containers.Count > 1)
            Children.Add(Ui.Field("Container",
                Ui.Combo(_containers.Select(c => (c, Containers.Display(c))), s.Container ?? p.Container, v => Set(s with { Container = v }))));

        if (_full && p.UsesVideoFilters)
            Children.Add(Ui.Field("HDR", Ui.Combo(Enum.GetValues<HdrMode>().Select(m => (m, m.Display())), s.Hdr, v => Set(s with { Hdr = v }))));

        if (p.UsesAudio)
        {
            bool lossless = s.AudioCodec is AudioCodec.Copy or AudioCodec.Flac or AudioCodec.Alac or AudioCodec.WavPack or AudioCodec.TrueHd
                or AudioCodec.Pcm16 or AudioCodec.Pcm24 or AudioCodec.Pcm32Float;
            var audio = new List<Control>
            {
                Ui.Field("Audio", Ui.Combo(Enum.GetValues<AudioCodec>().Select(a => (a, a.Display())), s.AudioCodec, v => Set(s with { AudioCodec = v }))),
            };
            if (!lossless)
                audio.Add(Ui.Field("Bitrate (kb/s)", Ui.Number(s.AudioBitrate ?? 0, 0, 1536, 8, v => Set(_s with { AudioBitrate = v > 0 ? (int)v : null }, false), "0"),
                    L.T("0 = automatic")));
            Children.Add(Ui.Columns([.. audio]));
            if (s.AudioCodec is AudioCodec.Auto or AudioCodec.Aac or AudioCodec.HeAac) Children.Add(Ui.Muted(AacNote()));
        }

        if (p.UsesVideoFilters && p.UsesResolution)
        {
            var up = s.Upscale;
            var exp = new Expander { Header = L.T("Upscaling / AI"), IsExpanded = up.Method != UpscaleMethod.Standard, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
            var box = new StackPanel { Spacing = 10 };
            var caps2 = _caps();
            box.Children.Add(Ui.Field("Method",
                Ui.Combo(Enum.GetValues<UpscaleMethod>().Select(m => (m, UpscaleText.Display(m) + (Upscalers.UsesLibplacebo(m) && caps2 is { HasLibplacebo: false } ? " — " + L.T("needs libplacebo") : ""))),
                    up.Method, v => Set(s with { Upscale = up with { Method = v, Model = null } })),
                UpscaleText.Description(up.Method)));
            if (up.Method == UpscaleMethod.Anime4K)
                box.Children.Add(Ui.Field("Anime4K", Ui.Combo(Upscalers.Anime4KModes.Select(m => (m.Id, $"{m.Label} — {m.Description}")), up.Model ?? "A", v => Set(s with { Upscale = up with { Model = v } }))));
            if (up.Method == UpscaleMethod.Neural)
            {
                var models = UpscaleModels.All.Select(m => (m.Id, m.Name)).ToList();
                var cur = up.Model ?? UpscaleModels.All[0].Id;
                if (models.All(m => m.Id != cur)) models.Add((cur, Path.GetFileName(cur)));
                box.Children.Add(Ui.Field("Model", Ui.Combo(models, cur, v => Set(s with { Upscale = up with { Model = v } })),
                    UpscaleModels.All.FirstOrDefault(m => m.Id == cur)?.Description ?? L.T("Local model file")));
            }
            if (up.Method != UpscaleMethod.Standard)
            {
                box.Children.Add(Ui.Toggle(L.T("Also at the same size (restoration only)"), up.AlwaysApply, v => Set(_s with { Upscale = _s.Upscale with { AlwaysApply = v } }, false)));
                box.Children.Add(Ui.Muted(L.T("Used when the chosen resolution is above the source.")));
            }
            exp.Content = box;
            Children.Add(exp);
        }

        if (p.EncodesVideo)
        {
            Children.Add(Ui.Toggle(L.T("Segmented encoding (resumable, parallel)"), s.Chunks.Enabled, v => Set(s with { Chunks = s.Chunks with { Enabled = v } })));
            if (s.Chunks.Enabled)
            {
                Children.Add(Ui.Columns(
                    Ui.Field("Parallel segments", Ui.Number(s.Chunks.Workers, 1, 32, 1, v => Set(_s with { Chunks = _s.Chunks with { Workers = (int)v } }, false), "0")),
                    Ui.Field("Segment length (s)", Ui.Number(s.Chunks.TargetSeconds, 5, 3600, 5, v => Set(_s with { Chunks = _s.Chunks with { TargetSeconds = v } }, false), "0"))));
                Children.Add(Ui.Muted(L.T("Splits at keyframes, encodes segments side by side and resumes from the last finished one after a stop. Best with CPU encoders.")));
            }
        }

        if (p.IsImages)
        {
            var im = s.Images;
            Children.Add(Ui.Columns(
                Ui.Field("Image format", Ui.Combo(Enum.GetValues<ImageFormat>().Select(f => (f, f.ToString().ToUpperInvariant())), im.Format, v => Set(s with { Images = im with { Format = v } }))),
                Ui.Field("Which frames", Ui.Combo(Enum.GetValues<ImageMode>().Select(m => (m, m.Display())), im.Mode, v => Set(s with { Images = im with { Mode = v } })))));
            var extra = new List<Control>();
            if (im.Mode == ImageMode.Interval)
                extra.Add(Ui.Field("Every (seconds)", Ui.Number(im.IntervalSeconds, 0.04, 86400, 0.5, v => Set(_s with { Images = _s.Images with { IntervalSeconds = v } }, false))));
            if (im.Mode == ImageMode.Count)
                extra.Add(Ui.Field("Number of images", Ui.Number(im.Count, 1, 100000, 1, v => Set(_s with { Images = _s.Images with { Count = (int)v } }, false), "0")));
            if (im.Format is ImageFormat.Jpeg or ImageFormat.Webp)
                extra.Add(Ui.Field(L.T("Quality") + " (1–100)", Ui.Number(im.Quality, 1, 100, 1, v => Set(_s with { Images = _s.Images with { Quality = (int)v } }, false), "0")));
            if (extra.Count > 0) Children.Add(Ui.Columns([.. extra]));
            if (im.Format is ImageFormat.Png or ImageFormat.Tiff)
                Children.Add(Ui.Toggle(L.T("16 bits per channel"), im.SixteenBit, v => Set(_s with { Images = _s.Images with { SixteenBit = v } }, false)));
        }

        if (p.IsDemux)
        {
            Children.Add(Ui.Toggle(L.T("Raw elementary streams (.h264, .aac…) instead of MKA/MKS wrappers"), s.Demux.Raw, v => Set(_s with { Demux = _s.Demux with { Raw = v } }, false)));
            Children.Add(Ui.Toggle(L.T("Extract attachments (fonts)"), s.Demux.Attachments, v => Set(_s with { Demux = _s.Demux with { Attachments = v } }, false)));
            Children.Add(Ui.Toggle(L.T("Extract chapters"), s.Demux.Chapters, v => Set(_s with { Demux = _s.Demux with { Chapters = v } }, false)));
        }
    }
}
