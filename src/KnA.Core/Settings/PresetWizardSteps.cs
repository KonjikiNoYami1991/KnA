using System.Globalization;
using KnA.Core.Encoding;
using KnA.Core.Localization;
using KnA.Core.Tools;

namespace KnA.Core.Settings;

public enum WizardStepKind { Choice, Number, Text, Summary }

public sealed record WizardChoice(string Value, string Label, string Hint = "", bool Enabled = true);

/// <summary>One question of the preset wizard; <see cref="Default"/> is the answer used when none was given.</summary>
public sealed record WizardStep(string Key, string Title, string? Help, WizardStepKind Kind, IReadOnlyList<WizardChoice> Choices, string Default,
    double Min = 0, double Max = 0, string? Unit = null);

/// <summary>
/// The preset wizard, question by question: what the result is for, the kind of picture, the codec and the encoder
/// (only GPUs that passed the hardware test), depth, how the quality is decided (level, constant quality value,
/// bitrate or file size), resolution, HDR, picture clean-up, container, audio codec / bitrate / channels / loudness,
/// languages to keep, subtitles, OCR and segmented encoding; then a summary of what the preset will do. Questions that
/// don't apply to the earlier answers are skipped. Shared by the desktop and the web wizard (same steps, same result).
/// </summary>
public static partial class PresetWizard
{
    private static string Get(IReadOnlyDictionary<string, string> a, string key, string fallback) =>
        a.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

    private static ProfileKind ProfileFor(IReadOnlyDictionary<string, string> a) => Get(a, "goal", "Archive") switch
    {
        "Tv" => Get(a, "tvAudio", "Aac") == "Ac3" ? ProfileKind.BlurayAc3 : ProfileKind.BlurayAac,
        "Web" or "Phone" => ProfileKind.Streaming,
        "OldPlayer" => ProfileKind.XvidMp3,
        "Editing" => ProfileKind.Workraw,
        "Lossless" => ProfileKind.Professional,
        "Images" => ProfileKind.Images,
        "Extract" => ProfileKind.Demux,
        "Remux" => ProfileKind.RemuxMkv,
        _ => ProfileKind.UniversalMkv,
    };

    private static VideoCodec CodecFor(IReadOnlyDictionary<string, string> a, ProfileDefinition p)
    {
        var fallback = p.Kind == ProfileKind.Professional ? VideoCodec.ProRes : p.Codecs.FirstOrDefault(VideoCodec.H264);
        var c = Enum.TryParse<VideoCodec>(Get(a, "codec", ""), out var v) ? v : fallback;
        return p.Codecs.Contains(c) ? c : fallback;
    }

    private static IReadOnlyList<EncoderBackend> WorkingBackends(VideoCodec codec, ProfileDefinition p, FfmpegCapabilities? caps) =>
        !p.AllowsHardware || caps is null ? [EncoderBackend.Software]
        : [EncoderBackend.Software, .. VideoEncoders.AllBackends.Where(b => b != EncoderBackend.Software && (caps.IsUsable(codec, b, false) || caps.IsUsable(codec, b, true)))];

    private static EncoderBackend BackendFor(IReadOnlyDictionary<string, string> a, VideoCodec codec, ProfileDefinition p, FfmpegCapabilities? caps)
    {
        var working = WorkingBackends(codec, p, caps);
        var wanted = Get(a, "encoder", "Software");
        if (wanted == "gpu") return working.FirstOrDefault(b => b != EncoderBackend.Software, EncoderBackend.Software);
        return Enum.TryParse<EncoderBackend>(wanted, out var b) && working.Contains(b) ? b : EncoderBackend.Software;
    }

    /// <summary>The questions that apply to the answers given so far, in order.</summary>
    public static IReadOnlyList<WizardStep> Steps(IReadOnlyDictionary<string, string> a, FfmpegCapabilities? caps, AudioEncoderSet? audio = null)
    {
        audio ??= AudioEncoderSet.Default;
        var steps = new List<WizardStep>();
        var goal = Get(a, "goal", "Archive");
        var p = Profiles.Get(ProfileFor(a));
        WizardChoice C(string v, string l, string h = "", bool e = true) => new(v, l, h, e);

        steps.Add(new("goal", L.T("What is the result for?"), null, WizardStepKind.Choice, [
            C("Archive", L.T("Archive / collection"), L.T("MKV, best quality per MB, subtitles kept")),
            C("Tv", L.T("TV or Blu-ray player"), L.T("H.264 that every player reads")),
            C("Web", L.T("Web / streaming"), L.T("MP4 with fast start")),
            C("Phone", L.T("Phone / tablet"), L.T("Small 720p files")),
            C("OldPlayer", L.T("Old DivX player"), L.T("XviD + MP3 in AVI")),
            C("Editing", L.T("Timing / typesetting"), L.T("Workraw for Aegisub")),
            C("Lossless", L.T("Editing / mastering / lossless"), L.T("ProRes, DNxHR, FFV1… for editors and archives without loss")),
            C("Remux", L.T("Change container only"), L.T("MKV without re-encoding: choose and label the tracks")),
            C("Images", L.T("Images"), L.T("Frames as PNG/JPEG…")),
            C("Extract", L.T("Extract tracks"), L.T("Demux without re-encoding"))], "Archive"));

        bool encodes = p.EncodesVideo;
        if (goal == "Tv")
            steps.Add(new("tvAudio", L.T("Which audio does the player read?"), L.T("Blu-ray players read both; old TVs and soundbars often only Dolby Digital."), WizardStepKind.Choice, [
                C("Aac", "AAC", L.T("Smaller, every recent TV")),
                C("Ac3", L.T("Dolby Digital (AC-3)"), L.T("Home theatres and older players"))], "Aac"));

        if (encodes && goal is not ("OldPlayer" or "Editing" or "Lossless"))
            steps.Add(new("content", L.T("What kind of picture is it?"), L.T("Sets the encoder's tuning for it."), WizardStepKind.Choice, [
                C("live", L.T("Live action (film, TV series)"), ""),
                C("animation", L.T("Animation / anime"), L.T("Flat colours, sharp lines")),
                C("grain", L.T("Grainy film"), L.T("Keeps the grain instead of smoothing it")),
                C("screen", L.T("Screen recording / slides"), L.T("Still pictures, text"))], "live"));

        VideoCodec codec = CodecFor(a, p);
        if (encodes && p.Codecs.Count > 1)
        {
            var hints = new Dictionary<VideoCodec, string>
            {
                [VideoCodec.H264] = L.T("Plays everywhere, biggest files"),
                [VideoCodec.HEVC] = L.T("Half the size of H.264, very compatible"),
                [VideoCodec.AV1] = L.T("Smallest files, newer devices"),
                [VideoCodec.VP9] = L.T("Web browsers (WebM), YouTube"),
                [VideoCodec.VVC] = L.T("Newest standard: very slow, few players"),
                [VideoCodec.MPEG2] = L.T("DVD players and old hardware"),
                [VideoCodec.ProRes] = L.T("Editing in Final Cut / Premiere / DaVinci"),
                [VideoCodec.DNxHR] = L.T("Editing in Avid / DaVinci"),
                [VideoCodec.FFV1] = L.T("Lossless archive, compact"),
                [VideoCodec.UtVideo] = L.T("Lossless, very fast to decode"),
            };
            steps.Add(new("codec", L.T("Which video codec?"), null, WizardStepKind.Choice,
                p.Codecs.Select(c => C(c.ToString(), c.Display(), hints.GetValueOrDefault(c, ""))).ToList(), p.Codecs[0].ToString()));
        }

        if (encodes && p.AllowsHardware)
        {
            var working = WorkingBackends(codec, p, caps);
            var choices = new List<WizardChoice> { C("Software", EncoderBackend.Software.Display(), L.T("Slowest, best compression")) };
            foreach (var b in VideoEncoders.BackendsFor(codec).Where(b => b != EncoderBackend.Software))
            {
                bool ok = working.Contains(b);
                choices.Add(C(b.ToString(), b.Display(), ok ? L.T("Much faster, slightly bigger files") : caps is null ? L.T("hardware not detected yet") : L.T("no such GPU in this computer"), ok));
            }
            if (choices.Count(x => x.Enabled) > 1)
                steps.Add(new("encoder", L.T("Which encoder?"), L.T("Only the GPUs that passed the hardware test can be chosen."), WizardStepKind.Choice, choices, "Software"));
        }
        var backend = BackendFor(a, codec, p, caps);

        bool tenBitPossible = encodes && !codec.IsProfessional() && VideoEncoders.Supports10Bit(codec, backend) && codec != VideoCodec.H264
                              && p.Kind is ProfileKind.UniversalMkv or ProfileKind.Streaming && !VideoEncoders.Always10Bit(codec);
        if (tenBitPossible)
            steps.Add(new("tenBit", L.T("Colour depth"), null, WizardStepKind.Choice, [
                C("true", L.T("10-bit"), L.T("Smoother gradients, less banding; needs HEVC/AV1 10-bit decoding")),
                C("false", L.T("8-bit"), L.T("Most compatible"))], "true"));

        if (encodes && p.UsesQuality && !codec.IsProfessional())
        {
            steps.Add(new("rate", L.T("How is the quality decided?"), null, WizardStepKind.Choice, [
                C("level", L.T("Quality level"), L.T("KnA's tuned values, from best to smallest")),
                C("crf", L.T("Constant quality value"), L.T("CRF / CQ / QP written by hand: same quality everywhere, unknown size")),
                C("bitrate", L.T("Average bitrate"), L.T("Predictable size per minute")),
                C("size", L.T("Maximum file size"), L.T("Fits a given size (audio and subtitles are subtracted)"))], "level"));
            switch (Get(a, "rate", "level"))
            {
                case "crf":
                    var enc = VideoEncoders.EncoderName(codec, backend) ?? "";
                    var qo = VideoEncoders.QualityOption(enc);
                    if (qo is { } q)
                    {
                        var auto = VideoEncoders.AutoValues(new VideoEncodeRequest(codec, backend, QualityLevel.Normal, false, p.Kind, 24, false)).Quality;
                        steps.Add(new("crf", L.T("{0} value ({1}–{2})", q.Label, q.Min, q.Max),
                            enc.EndsWith("_amf", StringComparison.Ordinal) || enc.EndsWith("_videotoolbox", StringComparison.Ordinal) ? L.T("Higher = better and bigger.") : L.T("Lower = better and bigger."),
                            WizardStepKind.Number, [], (auto ?? Math.Round((q.Min + q.Max) / 2.5)).ToString(CultureInfo.InvariantCulture), q.Min, q.Max));
                    }
                    break;
                case "bitrate":
                    steps.Add(new("bitrate", L.T("Average video bitrate"), L.T("1080p: about 3000–8000 kb/s for HEVC, 5000–12000 for H.264."), WizardStepKind.Number, [], "6000", 100, 500000, "kb/s"));
                    break;
                case "size":
                    steps.Add(new("size", L.T("Maximum file size"), L.T("As Windows counts it (1 MB = 1024 KB). KnA aims 3 % lower so one-pass encoders stay below."), WizardStepKind.Number, [], "4096", 1, 10_000_000, "MB"));
                    break;
                default:
                    steps.Add(new("quality", L.T("Quality"), null, WizardStepKind.Choice,
                        Enum.GetValues<QualityLevel>().Select(q => C(q.ToString(), q.Display())).ToList(), "Normal"));
                    break;
            }
        }
        else if (encodes && codec.IsProfessional() && codec is VideoCodec.ProRes or VideoCodec.DNxHR)
            steps.Add(new("quality", L.T("Quality"), L.T("Chooses the ProRes / DNxHR profile (Proxy/LB … 4444/444)."), WizardStepKind.Choice,
                Enum.GetValues<QualityLevel>().Where(q => q != QualityLevel.Draft).Select(q => C(q.ToString(), q.Display())).ToList(), "High"));

        if (p.UsesResolution && goal != "OldPlayer")
            steps.Add(new("resolution", L.T("Resolution"), L.T("Never enlarged unless an upscaler is chosen later in the editor."), WizardStepKind.Choice,
                Resolutions.All.Select(r => C(r.ToString(CultureInfo.InvariantCulture), Resolutions.Display(r))).ToList(), goal == "Phone" ? "720" : "0"));

        if (p.UsesVideoFilters && goal is "Archive" or "Web" or "Lossless" or "Tv" or "Images")
            steps.Add(new("hdr", L.T("HDR sources"), null, WizardStepKind.Choice, [
                C("Auto", HdrMode.Auto.Display(), L.T("Kept when the output can carry it, otherwise converted")),
                C("Preserve", HdrMode.Preserve.Display(), L.T("HDR10, HLG, HDR10+ and Dolby Vision when possible")),
                C("ToneMapSdr", HdrMode.ToneMapSdr.Display(), L.T("For SDR screens and players"))], "Auto"));

        if (p.UsesVideoFilters && goal is not ("Lossless" or "Editing"))
            steps.Add(new("cleanup", L.T("Picture clean-up"), L.T("Filters applied before encoding; deinterlacing is automatic."), WizardStepKind.Choice, [
                C("none", L.T("None"), L.T("The picture as it is")),
                C("light", L.T("Light denoise"), L.T("Removes a little noise, keeps detail")),
                C("deband", L.T("Deband"), L.T("Smooth gradients (anime, dark scenes)")),
                C("both", L.T("Denoise + deband"), L.T("Old or noisy sources")),
                C("strong", L.T("Strong clean-up"), L.T("Very noisy sources: denoise, deband and sharpen"))],
                Get(a, "content", "live") == "animation" ? "deband" : "none"));

        if (encodes && p.Containers.Count > 1)
        {
            var allowed = Containers.Allowed(new JobSettings { Profile = p.Kind, Codec = codec });
            if (allowed.Count > 1)
                steps.Add(new("container", L.T("Container"), L.T("Only the containers that can hold this codec."), WizardStepKind.Choice,
                    allowed.Select(c => C(c, Containers.Display(c))).ToList(), allowed.Contains(p.Container) ? p.Container : allowed[0]));
        }

        if (p.UsesAudio && goal is not ("Tv" or "OldPlayer" or "Editing"))
        {
            var container = Get(a, "container", p.Container);
            var codecs = new List<WizardChoice> { C("Auto", L.T("Automatic (profile)"), L.T("Copied when the format allows it")), C("Copy", AudioCodec.Copy.Display(), L.T("Never re-encoded (when the container accepts it)")) };
            foreach (var ac in new[] { AudioCodec.Aac, AudioCodec.HeAac, AudioCodec.Opus, AudioCodec.Ac3, AudioCodec.Eac3, AudioCodec.Mp3, AudioCodec.Flac, AudioCodec.Alac, AudioCodec.Pcm24 })
            {
                var enc = audio.Best(ac);
                bool fits = enc is not null && Containers.AcceptsAudio(container, AudioEncoderSet.OutputCodec(enc));
                codecs.Add(C(ac.ToString(), ac.Display(), enc is null ? L.T("no encoder here") : fits ? "" : L.T("not possible in {0}", container.ToUpperInvariant()), fits));
            }
            steps.Add(new("audioCodec", L.T("Audio codec"), null, WizardStepKind.Choice, codecs, "Auto"));
            var chosen = Get(a, "audioCodec", "Auto");
            if (chosen is "Aac" or "HeAac" or "Opus" or "Ac3" or "Eac3" or "Mp3" or "Auto")
                steps.Add(new("audioBitrate", L.T("Audio bitrate"), L.T("Per stereo pair: 5.1 tracks get three times as much."), WizardStepKind.Choice, [
                    C("0", L.T("Automatic (from the quality)")), C("96", "96 kb/s"), C("128", "128 kb/s"), C("160", "160 kb/s"), C("192", "192 kb/s"), C("256", "256 kb/s"), C("320", "320 kb/s")], "0"));
        }
        if (p.UsesAudio && goal is not ("OldPlayer" or "Editing"))
            steps.Add(new("mix", L.T("Channels and loudness"), null, WizardStepKind.Choice, [
                C("keep", L.T("Keep as it is"), L.T("Surround stays surround")),
                C("stereo", L.T("Stereo"), L.T("Downmix surround")),
                C("normalize", L.T("Normalize loudness"), L.T("Even volume between episodes")),
                C("both", L.T("Stereo + normalize"), L.T("Phones, laptops, night listening"))], goal == "Phone" ? "stereo" : "keep"));

        if (p.UsesTrackPlan && goal is not ("Images" or "Editing"))
        {
            steps.Add(new("audioLanguages", L.T("Audio languages to keep"), L.T("Codes separated by commas, in order of preference (ita, jpn, eng…); empty = all."), WizardStepKind.Text, [], ""));
            steps.Add(new("dropCommentary", L.T("Commentary tracks"), null, WizardStepKind.Choice, [
                C("false", L.T("Keep them")), C("true", L.T("Leave them out"))], "false"));
        }

        if (p.UsesSubtitleMode)
        {
            steps.Add(new("subtitles", L.T("Subtitles"), null, WizardStepKind.Choice, [
                C("Hardsub", L.T("Burn into the picture"), L.T("Always visible, any player")),
                C("Softsub", L.T("Keep as tracks"), L.T("Can be turned off, needs MKV")),
                C("None", L.T("No subtitles"), "")], goal is "Phone" or "Web" or "Tv" ? "Hardsub" : "Softsub"));
            var mode = Get(a, "subtitles", goal is "Phone" or "Web" or "Tv" ? "Hardsub" : "Softsub");
            if (mode != "None")
                steps.Add(new("subtitleLanguages", L.T("Subtitle languages"), L.T("Codes separated by commas (ita, eng…); empty = all. With burning, the first match is burned."), WizardStepKind.Text, [], ""));
            if (mode == "Hardsub")
                steps.Add(new("sidecar", L.T("External subtitles"), null, WizardStepKind.Choice, [
                    C("true", L.T("Prefer a .ass/.srt next to the video"), L.T("Fansub releases with separate subtitles")),
                    C("false", L.T("Only the subtitles inside the file"))], "true"));
            if (mode == "Softsub")
                steps.Add(new("ocr", L.T("Picture subtitles (PGS, DVD)"), null, WizardStepKind.Choice, [
                    C("false", L.T("Keep them as pictures")),
                    C("true", L.T("Convert to text with OCR"), L.T("Lighter, and MP4 or players without PGS can show them"))], "false"));
        }

        if (encodes && backend == EncoderBackend.Software && goal is "Archive" or "Lossless" or "Web")
            steps.Add(new("chunks", L.T("Long encodes"), null, WizardStepKind.Choice, [
                C("false", L.T("One piece"), L.T("Simplest")),
                C("true", L.T("Segments (resumable, parallel)"), L.T("Resumes after a stop or crash, uses more cores"))], "false"));

        steps.Add(new("name", L.T("Name your preset"), L.T("What the preset will do:"), WizardStepKind.Summary, [], ""));
        return steps;
    }

    /// <summary>The settings the answers describe.</summary>
    public static JobSettings Build(IReadOnlyDictionary<string, string> a, FfmpegCapabilities? caps)
    {
        var p = Profiles.Get(ProfileFor(a));
        var goal = Get(a, "goal", "Archive");
        var codec = CodecFor(a, p);
        var backend = BackendFor(a, codec, p, caps);
        double Num(string key, double fallback) => double.TryParse(Get(a, key, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;
        var quality = Enum.TryParse<QualityLevel>(Get(a, "quality", "Normal"), out var q) ? q : QualityLevel.Normal;
        int resolution = int.TryParse(Get(a, "resolution", goal == "Phone" ? "720" : "0"), out var r) ? r : 0;
        if (goal == "Phone" && resolution is 0 or > 720) resolution = 720;

        var enc = VideoEncoders.EncoderName(codec, backend) ?? "";
        var tune = Get(a, "content", "live") switch
        {
            "animation" when enc is "libx264" or "libx265" => "animation",
            "grain" when enc is "libx264" or "libx265" => "grain",
            "screen" when enc == "libx264" => "stillimage",
            "live" when enc == "libx264" => "film",
            _ => null,
        };
        var o = new EncoderOverrides { Tune = tune };
        switch (Get(a, "rate", "level"))
        {
            case "crf" when VideoEncoders.QualityOption(enc) is { } qo:
                o = o with { RateControl = RateControl.Quality, QualityValue = Math.Clamp(Num("crf", (qo.Min + qo.Max) / 2.5), qo.Min, qo.Max) };
                break;
            case "bitrate":
                o = o with { RateControl = RateControl.Bitrate, BitrateKbps = (int)Math.Max(100, Num("bitrate", 6000)) };
                break;
            case "size":
                o = o with { RateControl = RateControl.TargetSize, TargetSizeMB = Math.Max(1, Num("size", 4096)) };
                break;
        }

        var cleanup = Get(a, "cleanup", "none");
        var filters = new VideoFilterSettings
        {
            Denoise = cleanup switch { "light" or "both" => FilterStrength.Light, "strong" => FilterStrength.Medium, _ => FilterStrength.Off },
            Deband = cleanup switch { "deband" or "both" => FilterStrength.Light, "strong" => FilterStrength.Medium, _ => FilterStrength.Off },
            Sharpen = cleanup == "strong" ? FilterStrength.Light : FilterStrength.Off,
        };
        var mix = Get(a, "mix", goal == "Phone" ? "stereo" : "keep");
        var audioCodec = Enum.TryParse<AudioCodec>(Get(a, "audioCodec", "Auto"), out var ac) ? ac : AudioCodec.Auto;
        int bitrate = int.TryParse(Get(a, "audioBitrate", "0"), out var br) ? br : 0;
        var subtitles = Enum.TryParse<SubtitleMode>(Get(a, "subtitles", goal is "Phone" or "Web" or "Tv" ? "Hardsub" : "Softsub"), out var sm) ? sm : SubtitleMode.Hardsub;
        if (p.Kind == ProfileKind.Workraw) subtitles = SubtitleMode.None;
        var container = Get(a, "container", "");
        bool tenBit = Get(a, "tenBit", "true") == "true" && codec != VideoCodec.H264;

        return new JobSettings
        {
            Profile = p.Kind, Codec = codec, Backend = backend, Quality = quality, Resolution = resolution, TenBit = tenBit,
            Container = container.Length > 0 && p.Containers.Contains(container) ? container : null,
            Hdr = Enum.TryParse<HdrMode>(Get(a, "hdr", "Auto"), out var h) ? h : HdrMode.Auto,
            Encoder = o,
            VideoFilters = filters,
            AudioCodec = audioCodec,
            AudioBitrate = bitrate > 0 ? bitrate : null,
            AudioFilters = new AudioFilterSettings { DownmixStereo = mix is "stereo" or "both", Normalize = mix is "normalize" or "both" },
            Subtitles = subtitles,
            UseSidecarSubtitles = Get(a, "sidecar", "true") == "true",
            OcrPictureSubtitles = Get(a, "ocr", "false") == "true",
            TrackRules = new TrackRules
            {
                AudioLanguages = Get(a, "audioLanguages", "") is { Length: > 0 } al ? al : null,
                SubtitleLanguages = Get(a, "subtitleLanguages", "") is { Length: > 0 } sl ? sl : null,
                DropCommentary = Get(a, "dropCommentary", "false") == "true",
            },
            Chunks = new ChunkSettings { Enabled = Get(a, "chunks", "false") == "true" },
        };
    }

    /// <summary>What a preset made from these settings will do, one line per part (the wizard's last page).</summary>
    public static IReadOnlyList<string> Summary(JobSettings s)
    {
        var p = s.ProfileDefinition;
        var l = new List<string> { L.T("Profile: {0}", p.Name) };
        if (p.EncodesVideo)
        {
            var enc = VideoEncoders.EncoderName(s.EffectiveCodec, s.EffectiveBackend) ?? "";
            l.Add(L.T("Video: {0} with {1}", s.EffectiveCodec.Display() + (s.EffectiveTenBit && !s.EffectiveCodec.IsProfessional() ? " 10-bit" : ""), s.EffectiveBackend.Display() + (enc.Length > 0 ? $" ({enc})" : "")));
            l.Add(s.Encoder.RateControl switch
            {
                RateControl.Quality => L.T("Quality: constant {0}", s.Encoder.QualityValue?.ToString("0.#", CultureInfo.CurrentCulture) ?? "?"),
                RateControl.Bitrate => L.T("Quality: {0} kb/s on average", s.Encoder.BitrateKbps ?? 0),
                RateControl.TargetSize => L.T("Quality: at most {0} MB per file", s.Encoder.TargetSizeMB?.ToString("0.#", CultureInfo.CurrentCulture) ?? "?"),
                _ => L.T("Quality: {0}", s.Quality.Display()),
            } + (s.Encoder.Tune is { } t ? " · tune " + t : ""));
        }
        if (p.UsesResolution) l.Add(L.T("Resolution: {0}", Resolutions.Display(s.Resolution)));
        if (p.UsesVideoFilters)
        {
            l.Add("HDR: " + s.Hdr.Display());
            var f = new List<string>();
            if (s.VideoFilters.Denoise != FilterStrength.Off) f.Add(L.T("Denoise") + " " + s.VideoFilters.Denoise.Display().ToLowerInvariant());
            if (s.VideoFilters.Deband != FilterStrength.Off) f.Add(L.T("Deband") + " " + s.VideoFilters.Deband.Display().ToLowerInvariant());
            if (s.VideoFilters.Sharpen != FilterStrength.Off) f.Add(L.T("Sharpen") + " " + s.VideoFilters.Sharpen.Display().ToLowerInvariant());
            l.Add(L.T("Filters: {0}", f.Count > 0 ? string.Join(", ", f) : L.T("none")));
        }
        if (p.EncodesVideo || p.IsRemux) l.Add(L.T("Container: {0}", Containers.Display(s.EffectiveContainer)));
        if (p.UsesAudio)
        {
            var a = s.AudioCodec.Display() + (s.AudioBitrate is { } b ? $", {b} kb/s" : "");
            if (s.AudioFilters.DownmixStereo) a += ", " + L.T("Stereo").ToLowerInvariant();
            if (s.AudioFilters.Normalize) a += ", " + L.T("Normalize loudness").ToLowerInvariant();
            l.Add(L.T("Audio: {0}", a));
        }
        if (p.UsesSubtitleMode) l.Add(L.T("Subtitles: {0}", s.Subtitles.Display() + (s.OcrPictureSubtitles ? " + OCR" : "")));
        if (!s.TrackRules.IsEmpty)
        {
            var r = new List<string>();
            if (s.TrackRules.AudioLanguages is { } al) r.Add(L.T("audio {0}", al));
            if (s.TrackRules.SubtitleLanguages is { } sl) r.Add(L.T("subtitles {0}", sl));
            if (s.TrackRules.DropCommentary) r.Add(L.T("no commentary"));
            l.Add(L.T("Tracks kept: {0}", string.Join(", ", r)));
        }
        if (s.Chunks.Enabled) l.Add(L.T("Segmented encoding (resumable, parallel)"));
        return l;
    }
}
