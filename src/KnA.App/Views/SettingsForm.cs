using Avalonia.Controls;
using Avalonia.Threading;
using KnA.Core.Ai;
using KnA.Core.Encoding;
using KnA.Core.Localization;
using KnA.Core.Tools;

namespace KnA.App.Views;

/// <summary>Part of the full form shown by one tab of the editor (each setting appears in exactly one place).</summary>
public enum FormSection { Video, Audio, Subtitles, Output }

/// <summary>
/// Encode settings form, shared by the side panel ("quick") and the editor tabs ("full", one <see cref="FormSection"/> each).
/// Mirrors wwwroot/form.js: same fields, same visibility rules.
/// </summary>
public sealed class SettingsForm : StackPanel
{
    private readonly Func<FfmpegCapabilities?> _caps;
    private readonly bool _full;
    private JobSettings _s;
    private IReadOnlyList<string> _containers = [];

    public event Action<JobSettings>? Changed;

    private readonly FormSection? _section;

    /// <summary>Text describing what a target size leaves for each stream (editor: depends on the file).</summary>
    public Func<JobSettings, string?>? SizeInfo { get; init; }

    /// <summary>Installs a component (editor): offered where a feature needs one the packages don't carry (PyTorch).</summary>
    public Func<string, Task>? InstallComponent { get; init; }
    /// <summary>Whether the AI upscaler's Python + PyTorch is installed (editor).</summary>
    public Func<bool>? AiRuntimeInstalled { get; init; }

    /// <summary>Loads a program's options for the parameter editors (editor only).</summary>
    public Func<string, JobSettings, CancellationToken, Task<(string Program, IReadOnlyList<ToolOption> Options)>>? ParameterOptions { get; init; }

    public SettingsForm(JobSettings settings, Func<FfmpegCapabilities?> caps, bool full, Func<FfmpegTools?>? tools = null, FormSection? section = null)
    {
        _section = full ? section : null;
        _tools = tools;
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
        if (s == _s) return;   // a control rebuilt with the same value is not a change
        _s = s;
        if (rebuild) Dispatcher.UIThread.Post(Rebuild);
        Changed?.Invoke(s);
    }

    /// <summary>Which AAC encoder will be used (same text as the web UI).</summary>
    private string AacNote()
    {
        var builds = _caps()?.Builds ?? [];
        var tools = _tools?.Invoke();
        if (tools?.Qaac is not null) return L.T("AAC: qaac (Apple Core Audio, best quality); libfdk_aac / fdkaac as fallback.");
        if (builds.Any(b => b.Has("libfdk_aac"))) return L.T("AAC: libfdk_aac (Fraunhofer, best quality).");
        if (builds.Any(b => b.Has("aac_at"))) return L.T("AAC: Apple AudioToolbox (excellent quality).");
        if (tools?.Fdkaac is not null) return L.T("AAC: fdkaac program (Fraunhofer, best quality).");
        return L.T("AAC: FFmpeg's own encoder. Install fdkaac in Settings → Components for better quality.");
    }

    /// <summary>External programs found (qaac, fdkaac, rigaya encoders), given by the window.</summary>
    private readonly Func<FfmpegTools?>? _tools;

    /// <summary>The block belongs to the section shown (the quick panel and a sectionless full form show everything).</summary>
    private bool In(FormSection sec) => _section is null || _section == sec;

    public void Rebuild()
    {
        Children.Clear();
        var s = _s;
        var p = s.ProfileDefinition;
        var codec = p.Kind == ProfileKind.RemuxMp4 ? VideoCodec.H264 : s.EffectiveCodec;
        bool tenBitPossible = VideoEncoders.Supports10Bit(codec, EncoderBackend.Software) && !codec.IsProfessional()
                              && p.Kind is ProfileKind.UniversalMkv or ProfileKind.Streaming;
        bool pro = p.Kind == ProfileKind.Professional;

        if (In(FormSection.Video)) VideoBlock(s, p, codec, tenBitPossible, pro);
        if (In(FormSection.Subtitles)) SubtitleBlock(s, p);
        if (In(FormSection.Audio)) AudioBlock(s, p);
        if (In(FormSection.Output)) OutputBlock(s, p);
    }

    private void VideoBlock(JobSettings s, ProfileDefinition p, VideoCodec codec, bool tenBitPossible, bool pro)
    {
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
                // The FFmpeg encoder may fail (old driver, missing SDK) where rigaya's encoder works.
                bool rigayaOnly = !ok && RigayaEncoders.Supports(codec, b) && _tools is not null && KnA.Core.Queue.JobRunner.RigayaFor(_tools()).ContainsKey(b);
                string note = ok ? "" : rigayaOnly ? L.T("only with {0}", RigayaEncoders.DisplayName(b))
                    : caps!.Find(codec, b) is { Compiled: false } ? L.T("not in this FFmpeg build")
                    : tenBit && caps.IsUsable(codec, b, false) ? L.T("8-bit only") : L.T("no compatible GPU/driver");
                ok |= rigayaOnly;
                items.Add((b, b.Display() + (note.Length == 0 ? "" : " — " + note), ok));
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
        if (p.UsesQuality && !_full)
        {
            // One place decides the quality: when the editor sets it by hand, the quick panel says so instead of
            // showing a level that would be ignored.
            if (s.Encoder.RateControl == RateControl.Auto)
                resQ.Add(Ui.Field("Quality", Ui.Combo(Enum.GetValues<QualityLevel>().Select(q => (q, q.Display())), s.Quality, v => Set(s with { Quality = v }))));
            else
                resQ.Add(Ui.Field("Quality", Ui.Muted(RateText(s.Encoder))));
        }
        if (resQ.Count > 0) Children.Add(Ui.Columns([.. resQ]));

        if (_full && p.EncodesVideo && p.UsesQuality) RateControlBlock(s, codec);

        if (_full && p.UsesVideoFilters)
        {
            Children.Add(Ui.Field("HDR", Ui.Combo(Enum.GetValues<HdrMode>().Select(m => (m, m.Display())), s.Hdr, v => Set(s with { Hdr = v }))));
            if (s.Hdr != HdrMode.Preserve)
            {
                Children.Add(Ui.Field("HDR → SDR tone mapping", Ui.Combo(Enum.GetValues<ToneMapCurve>().Select(c => (c, c.Display())), s.ToneMap, v => Set(s with { ToneMap = v }, false))));
                Children.Add(Ui.Toggle(L.T("Adapt brightness scene by scene (dynamic peak)"), s.ToneMapDynamic, v => Set(s with { ToneMapDynamic = v }, false)));
                Children.Add(Ui.Muted(L.T("Used only when an HDR source becomes SDR. Off: the brightness follows the mastering metadata and stays stable across the film; on, dim scenes are brightened.")));
            }
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
            if (up.Method == UpscaleMethod.Neural && AiRuntimeInstalled?.Invoke() == false)
            {
                // PyTorch is the one part of the AI upscaler the packages can't carry (several GB, depends on the GPU)
                var install = new Button { Content = L.T("Install the AI upscaler (Python + PyTorch, several GB)"), Classes = { "primary" } };
                var note = Ui.Muted(L.T("Needed once for the neural models: KnA picks the PyTorch build for your GPU (CUDA, ROCm, Intel, Apple or CPU)."));
                install.Click += async (_, _) =>
                {
                    install.IsEnabled = false;
                    note.Text = L.T("Installing PyTorch ({0}): this can take a while…", "");
                    try { await InstallComponent!("ai-runtime"); note.Text = L.T("Installed {0}.", "PyTorch"); install.IsVisible = false; }
                    catch (Exception ex) { note.Text = ex.Message; install.IsEnabled = true; }
                };
                box.Children.Add(install);
                box.Children.Add(note);
            }
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

        if (_full && p.EncodesVideo) Children.Add(AdvancedEncoder(s, codec));
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

    }

    private void SubtitleBlock(JobSettings s, ProfileDefinition p)
    {
        if (p.UsesSubtitleMode)
        {
            Children.Add(Ui.Field("Subtitles",
                Ui.Combo(Enum.GetValues<SubtitleMode>().Select(m => (m, m.Display())), s.Subtitles, v => Set(s with { Subtitles = v })),
                _full ? L.T("Automatic plan: the editor (Tracks tab) can override it per file.") : null));
            if (s.Subtitles == SubtitleMode.Hardsub)
                Children.Add(Ui.Toggle(L.T("Prefer external .ass/.srt next to the video"), s.UseSidecarSubtitles, v => Set(s with { UseSidecarSubtitles = v }, false)));
            if (s.Subtitles == SubtitleMode.Softsub || _full)
            {
                Children.Add(Ui.Toggle(L.T("Convert picture subtitles (PGS, DVD) to text with OCR"), s.OcrPictureSubtitles, v => Set(_s with { OcrPictureSubtitles = v }, false)));
                if (s.OcrPictureSubtitles)
                    Children.Add(Ui.Muted(L.T("Tesseract with the language model of each track, like Subtitle Edit: lighter files, and subtitles MP4 and players without PGS support can show. Settings → Components installs it.")));
            }
        }

        if (_full && ParameterOptions is { } po && p.UsesTrackPlan)
            Children.Add(Params(L.T("Subtitle encoder parameters"), s.Arguments.Subtitles, KnA.Core.Queue.QueueService.ParameterTools.Subtitles,
                v => Set(_s with { Arguments = _s.Arguments with { Subtitles = v } }, false), "-alignment 1"));
    }

    private void AudioBlock(JobSettings s, ProfileDefinition p)
    {
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

        if (_full && p.UsesAudio && ParameterOptions is not null)
        {
            var tools = _tools?.Invoke();
            Children.Add(Params(L.T("FFmpeg audio encoder parameters"), s.Arguments.AudioFfmpeg, KnA.Core.Queue.QueueService.ParameterTools.Audio,
                v => Set(_s with { Arguments = _s.Arguments with { AudioFfmpeg = v } }, false), "-aac_coder twoloop"));
            if (tools?.Fdkaac is not null)
                Children.Add(Params(L.T("fdkaac parameters"), s.Arguments.Fdkaac, KnA.Core.Queue.QueueService.ParameterTools.Fdkaac,
                    v => Set(_s with { Arguments = _s.Arguments with { Fdkaac = v } }, false), "--afterburner 1"));
            if (tools?.Qaac is not null)
                Children.Add(Params(L.T("qaac parameters"), s.Arguments.Qaac, KnA.Core.Queue.QueueService.ParameterTools.Qaac,
                    v => Set(_s with { Arguments = _s.Arguments with { Qaac = v } }, false), "--quality 2"));
        }
    }

    private void OutputBlock(JobSettings s, ProfileDefinition p)
    {
        if (_full && _containers.Count > 1)
            Children.Add(Ui.Field("Container",
                Ui.Combo(_containers.Select(c => (c, Containers.Display(c))), s.Container ?? p.Container, v => Set(s with { Container = v }))));

        if (_full && (p.UsesTrackPlan || p.IsDemux)) Children.Add(TrackSelection(s));
        if (_full && !p.IsImages && ParameterOptions is not null)
            Children.Add(Params(L.T("Container parameters"), s.Arguments.Muxer, KnA.Core.Queue.QueueService.ParameterTools.Muxer,
                v => Set(_s with { Arguments = _s.Arguments with { Muxer = v } }, false), "-reserve_index_space 50k"));
        if (p.IsDemux)
        {
            Children.Add(Ui.Toggle(L.T("Raw elementary streams (.h264, .aac…) instead of MKA/MKS wrappers"), s.Demux.Raw, v => Set(_s with { Demux = _s.Demux with { Raw = v } }, false)));
            Children.Add(Ui.Toggle(L.T("Extract attachments (fonts)"), s.Demux.Attachments, v => Set(_s with { Demux = _s.Demux with { Attachments = v } }, false)));
            Children.Add(Ui.Toggle(L.T("Extract chapters"), s.Demux.Chapters, v => Set(_s with { Demux = _s.Demux with { Chapters = v } }, false)));
        }
    }

    /// <summary>Short description of a rate control set by hand (quick panel).</summary>
    private static string RateText(EncoderOverrides o) => o.RateControl switch
    {
        RateControl.Quality => L.T("Constant quality {0} (set in the editor)", o.QualityValue?.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture) ?? "?"),
        RateControl.Bitrate => L.T("{0} kb/s (set in the editor)", o.BitrateKbps ?? 0),
        RateControl.TargetSize => L.T("Maximum {0} MB (set in the editor)", o.TargetSizeMB?.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture) ?? "?"),
        _ => "",
    };

    /// <summary>
    /// The one place where the video quality is decided: a quality level (KnA's tuned values), a constant quality
    /// value, an average bitrate, or a maximum file size (bitrates computed for every stream).
    /// </summary>
    private void RateControlBlock(JobSettings s, VideoCodec codec)
    {
        var o = s.Encoder;
        bool rigaya = RigayaEncoders.Wanted(s);
        var enc = rigaya ? RigayaEncoders.ToolId(s.Backend)! : VideoEncoders.EncoderName(codec, s.EffectiveBackend) ?? "";
        var quality = rigaya ? RigayaEncoders.QualityOption(enc, codec) : VideoEncoders.QualityOption(enc);
        void SetO(Func<EncoderOverrides, EncoderOverrides> f, bool rebuild = false) => Set(_s with { Encoder = f(_s.Encoder) }, rebuild);
        var rc = new List<(RateControl, string)> { (RateControl.Auto, L.T("Quality level")) };
        if (quality is { } qo) rc.Add((RateControl.Quality, L.T("Constant quality ({0})", qo.Label)));
        rc.Add((RateControl.Bitrate, L.T("Average bitrate")));
        rc.Add((RateControl.TargetSize, L.T("Maximum file size")));
        var mode = o.RateControl == RateControl.Quality && quality is null ? RateControl.Auto : o.RateControl;
        // The values shown as defaults are stored with the choice (a number on screen is never ignored).
        Children.Add(Ui.Field("Rate control", Ui.Combo(rc, mode, v => SetO(x => x with
        {
            RateControl = v,
            BitrateKbps = v == RateControl.Bitrate ? x.BitrateKbps ?? 6000 : x.BitrateKbps,
            TargetSizeMB = v == RateControl.TargetSize ? x.TargetSizeMB ?? 4096 : x.TargetSizeMB,
            QualityValue = v == RateControl.Quality && quality is { } dq ? x.QualityValue ?? Math.Round((dq.Min + dq.Max) / 2.5) : x.QualityValue,
        }, true))));
        switch (mode)
        {
            case RateControl.Auto:
                Children.Add(Ui.Field("Quality", Ui.Combo(Enum.GetValues<QualityLevel>().Select(q => (q, q.Display())), s.Quality, v => Set(_s with { Quality = v })),
                    L.T("KnA's tuned values for {0}: from best to smallest.", rigaya ? RigayaEncoders.DisplayName(s.Backend) : enc)));
                break;
            case RateControl.Quality when quality is { } q:
                Children.Add(Ui.Field(L.T("{0} value ({1}–{2})", q.Label, q.Min, q.Max),
                    Ui.Number(o.QualityValue ?? Math.Round((q.Min + q.Max) / 2.5), q.Min, q.Max, 0.5, v => SetO(x => x with { QualityValue = v })),
                    enc == "vceencc" ? null : L.T("Lower = better and bigger.")));
                break;
            case RateControl.Bitrate:
                Children.Add(Ui.Columns(
                    Ui.Field("Bitrate (kb/s)", Ui.Number(o.BitrateKbps ?? 6000, 100, 500000, 100, v => SetO(x => x with { BitrateKbps = (int)v }), "0")),
                    Ui.Field("Maximum (kb/s, 0 = none)", Ui.Number(o.MaxBitrateKbps ?? 0, 0, 1000000, 100, v => SetO(x => x with { MaxBitrateKbps = v > 0 ? (int)v : null }), "0"))));
                break;
            case RateControl.TargetSize:
                var info = Ui.Muted(SizeInfo?.Invoke(s) ?? L.T("Audio, subtitles and the container are subtracted; the video gets the rest."));
                Children.Add(Ui.Field("Maximum size (MB)", Ui.Number(o.TargetSizeMB ?? 4096, 1, 10_000_000, 50, v =>
                {
                    SetO(x => x with { TargetSizeMB = v }, false);
                    info.Text = SizeInfo?.Invoke(_s) ?? info.Text;
                }, "0.#"), L.T("As Windows counts it (1 MB = 1024 KB). KnA aims 3 % lower so one-pass encoders stay below.")));
                Children.Add(info);
                break;
        }
    }

    /// <summary>A collapsible editor of every parameter of the program that runs for <paramref name="tool"/>.</summary>
    private Control Params(string title, string? text, string tool, Action<string?> changed, string example, bool expanded = false)
    {
        var editor = new ParameterEditor(text, ct => ParameterOptions!(tool, _s, ct), changed, example);
        if (expanded) return Ui.Field(title, editor);
        return new Expander
        {
            Header = title + (string.IsNullOrWhiteSpace(text) ? "" : " ●"), Content = editor, IsExpanded = !string.IsNullOrWhiteSpace(text),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
        };
    }

    /// <summary>Hand-set encoder parameters: every field empty = KnA's values for the quality level.</summary>
    private Control AdvancedEncoder(JobSettings s, VideoCodec codec)
    {
        var o = s.Encoder;
        bool rigaya = RigayaEncoders.Wanted(s);
        var enc = rigaya ? RigayaEncoders.ToolId(s.Backend)! : VideoEncoders.EncoderName(codec, s.EffectiveBackend) ?? "";
        (string Option, double Min, double Max, string Label)? quality = rigaya ? RigayaEncoders.QualityOption(enc, codec) : VideoEncoders.QualityOption(enc);
        void SetO(Func<EncoderOverrides, EncoderOverrides> f, bool rebuild = false) => Set(_s with { Encoder = f(_s.Encoder) }, rebuild);
        static string? Clean(string v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        var box = new StackPanel { Spacing = 10 };
        if (RigayaEncoders.Supports(codec, s.EffectiveBackend))
        {
            var name = RigayaEncoders.DisplayName(s.EffectiveBackend);
            box.Children.Add(Ui.Field("Encoder program", Ui.Combo(
                [(EncoderProgram.FFmpeg, L.T("FFmpeg ({0})", VideoEncoders.EncoderName(codec, s.EffectiveBackend) ?? "")), (EncoderProgram.Rigaya, name + " (rigaya)")],
                o.Program, v => SetO(x => x with { Program = v, Speed = null, Tune = null, Extra = null }, true)),
                L.T("{0} reads the source with GPU decoding, resizes on the GPU and copies Dolby Vision / HDR10+ when no FFmpeg filter is needed (otherwise FFmpeg decodes and filters, and sends the frames to it). Install it in Settings → Components.", name)));
            if (rigaya && _tools is not null && !KnA.Core.Queue.JobRunner.RigayaFor(_tools()).ContainsKey(s.Backend))
                box.Children.Add(Ui.Muted("⚠ " + L.T("{0} is not installed: add it in Settings → Components, or choose FFmpeg as the encoder program.", name)));
        }
        box.Children.Add(Ui.Muted(L.T("Encoder: {0}. Empty fields keep KnA's values for the chosen quality.", rigaya ? RigayaEncoders.DisplayName(s.Backend) : enc)));

        var speed = new List<(string?, string)> { (null, L.T("From the quality level")) };
        if (rigaya) speed.AddRange(RigayaEncoders.SpeedValues(enc).Select(v => ((string?)v, v)));
        else if (VideoEncoders.SpeedOption(enc) is { } so) speed.AddRange(so.Values.Select(v => ((string?)v, v)));
        var tunes = new List<(string?, string)> { (null, L.T("None")) };
        tunes.AddRange((rigaya ? RigayaEncoders.TuneValues(enc, codec) : VideoEncoders.TuneValues(enc)).Select(t => ((string?)t, t)));
        box.Children.Add(Ui.Columns(
            Ui.Field(L.T("Speed preset (fastest → slowest)"), Ui.Combo(speed, o.Speed, v => SetO(x => x with { Speed = v }))),
            Ui.Field("Tune", Ui.Combo(tunes, o.Tune, v => SetO(x => x with { Tune = v })))));
        box.Children.Add(Ui.Columns(
            Ui.Field("Keyframe every (s, 0 = auto)", Ui.Number(o.KeyframeSeconds ?? 0, 0, 60, 0.5, v => SetO(x => x with { KeyframeSeconds = v > 0 ? v : null }))),
            Ui.Field("B-frames (−1 = auto)", Ui.Number(o.BFrames ?? -1, -1, 16, 1, v => SetO(x => x with { BFrames = v >= 0 ? (int)v : null }), "0")),
            Ui.Field("Lookahead (frames, −1 = auto)", Ui.Number(o.Lookahead ?? -1, -1, 250, 1, v => SetO(x => x with { Lookahead = v >= 0 ? (int)v : null }), "0"))));
        if (ParameterOptions is not null)
            box.Children.Add(Params(L.T("All {0} parameters", rigaya ? RigayaEncoders.DisplayName(s.Backend) : enc), o.Extra,
                KnA.Core.Queue.QueueService.ParameterTools.Video, v => SetO(x => x with { Extra = v }),
                rigaya ? "--aq-temporal --bref-mode each" : "-x265-params psy-rd=2:aq-strength=0.8", expanded: true));
        else
            box.Children.Add(Ui.Field("Extra encoder options", Ui.Text(o.Extra, v => SetO(x => x with { Extra = Clean(v) }),
                    rigaya ? "--aq-temporal --bref-mode each" : "-x265-params psy-rd=2:aq-strength=0.8", mono: true),
                rigaya ? L.T("{0} options (see its documentation), applied last.", RigayaEncoders.DisplayName(s.Backend)) : L.T("FFmpeg syntax, applied last (they replace KnA's values).")));
        if (!o.IsEmpty)
            box.Children.Add(Ui.Button(L.T("Back to automatic"), () => Set(_s with { Encoder = new() })));
        return new Expander { Header = L.T("Advanced encoder") + (o.IsEmpty ? "" : " ●"), Content = box, IsExpanded = !o.IsEmpty, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
    }

    /// <summary>Rules that switch off unwanted tracks of every file (kept in presets).</summary>
    private Control TrackSelection(JobSettings s)
    {
        var r = s.TrackRules;
        void SetR(Func<TrackRules, TrackRules> f) => Set(_s with { TrackRules = f(_s.TrackRules) }, false);
        static string? Clean(string v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        var box = new StackPanel { Spacing = 10 };
        box.Children.Add(Ui.Muted(L.T("Applied to the automatic track plan of every file: the tracks left out can still be ticked by hand in the editor.")));
        box.Children.Add(Ui.Columns(
            Ui.Field("Audio languages to keep", Ui.Text(r.AudioLanguages, v => SetR(x => x with { AudioLanguages = Clean(v) }), "ita, jpn"), L.T("In order of preference; empty = all.")),
            Ui.Field("Subtitle languages to keep", Ui.Text(r.SubtitleLanguages, v => SetR(x => x with { SubtitleLanguages = Clean(v) }), "ita"), L.T("Empty = all."))));
        box.Children.Add(Ui.Toggle(L.T("Leave out commentary tracks"), r.DropCommentary, v => SetR(x => x with { DropCommentary = v })));
        box.Children.Add(Ui.Toggle(L.T("Leave out the attachments of the file"), r.DropAttachments, v => SetR(x => x with { DropAttachments = v })));
        return new Expander { Header = L.T("Track selection") + (r.IsEmpty ? "" : " ●"), Content = box, IsExpanded = !r.IsEmpty, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
    }
}
