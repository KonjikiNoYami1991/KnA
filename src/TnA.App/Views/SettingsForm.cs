using Avalonia.Controls;
using Avalonia.Threading;
using TnA.Core.Encoding;
using TnA.Core.Localization;
using TnA.Core.Tools;

namespace TnA.App.Views;

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

    public SettingsForm(JobSettings settings, Func<FfmpegCapabilities?> caps, bool full)
    {
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

    public void Rebuild()
    {
        Children.Clear();
        var s = _s;
        var p = s.ProfileDefinition;
        var codec = p.Kind == ProfileKind.RemuxMp4 ? VideoCodec.H264 : s.EffectiveCodec;
        bool tenBitPossible = VideoEncoders.Supports10Bit(codec, EncoderBackend.Software) && p.Kind is ProfileKind.UniversalMkv or ProfileKind.Streaming;

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

        if (p.EncodesVideo)
            Children.Add(Ui.Toggle(L.T("10-bit output") + " (HEVC / AV1)", s.TenBit && tenBitPossible, v => Set(s with { TenBit = v }), tenBitPossible));

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
