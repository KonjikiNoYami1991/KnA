using KnA.Core.Encoding;
using KnA.Core.Media;
using KnA.Core.Ocr;
using KnA.Core.Settings;

namespace KnA.Core.Queue;

/// <summary>
/// The per-file editor works on explicit settings only: every choice the profile would make by itself (which tracks,
/// which audio codec, encoder and bitrate for each track, the subtitle formats, the container, the quality value
/// and speed preset of the encoder) is turned into a visible value in the fields. A profile or a preset can only be
/// imported: it fills the fields again, it does not stay "behind" them.
/// </summary>
public static class EditorModel
{
    /// <summary>
    /// Fills the automatic parts of <paramref name="s"/>/<paramref name="e"/> with the values the profile chooses for
    /// this file, and moves the job-wide program parameters onto the tracks they apply to. Values already explicit are
    /// kept. Saves the result to the job and returns it with the new analysis.
    /// </summary>
    public static async Task<(JobSettings Settings, JobEdit Edit, JobAnalysis Analysis)> MaterializeAsync(QueueService q, Guid id,
        JobSettings s, JobEdit e, CancellationToken ct = default)
    {
        var job = q.Find(id) ?? throw new KeyNotFoundException();
        var a = await q.SetEditAsync(id, e, ct, s);
        var p = s.ProfileDefinition;
        var container = s.Container is { } c && p.Containers.Contains(c) ? c : a.Container is { Length: > 0 } ac ? ac : p.Container;
        var encoders = JobRunner.AudioEncodersFor(q.Settings.Capabilities, q.Tools);
        var media = new Dictionary<string, MediaInfo>(a.External) { [job.SourcePath] = a.Source };
        var args = s.Arguments;

        var tracks = new List<TrackPlan>();
        foreach (var t0 in ScriptTracks(job, s, a.Tracks, e.Tracks is null))
        {
            var t = t0;
            if (t.Kind == "audio" && p.UsesAudio)
            {
                if (string.IsNullOrEmpty(t.Output) || string.Equals(t.Output, nameof(AudioCodec.Auto), StringComparison.OrdinalIgnoreCase))
                {
                    if (TrackPlanner.Lookup(t, media) is AudioStream st)
                    {
                        var plan = AudioPlanner.PlanTrack(t with { Output = null }, st, s, container, encoders);
                        if (plan.IsCopy) t = t with { Output = nameof(AudioCodec.Copy), Encoder = null };
                        else
                        {
                            var codec = plan.HeAac ? AudioCodec.HeAac : AudioEncoderSet.CodecOf(plan.Encoder) ?? AudioCodec.Aac;
                            t = t with
                            {
                                Output = codec.ToString(), Encoder = plan.Encoder,
                                Bitrate = t.Bitrate ?? (plan.Kbps is { } k ? k * 2 / Math.Max(2, plan.Channels) : null),
                                Channels = t.Channels ?? (plan.Channels < st.Channels ? plan.Channels : null),
                            };
                        }
                    }
                }
                // Job-wide parameters of the program that encodes this track become the track's own.
                var program = t.Encoder ?? (Enum.TryParse<AudioCodec>(t.Output, true, out var oc) ? encoders.Best(oc) : null);
                var global = program switch
                {
                    null => null,
                    AudioEncoderSet.Fdkaac => args.Fdkaac,
                    AudioEncoderSet.Qaac => args.Qaac,
                    _ when string.Equals(t.Output, nameof(AudioCodec.Copy), StringComparison.OrdinalIgnoreCase) => null,
                    _ => args.AudioFfmpeg,
                };
                if (!string.IsNullOrWhiteSpace(global)) t = t with { Arguments = ToolArguments.Combine(global, t.Arguments) };
            }
            else if (t.Kind == "subtitle" && p.UsesTrackPlan && !t.Burn && string.IsNullOrEmpty(t.Output))
            {
                var sst = TrackPlanner.Lookup(t, media) as SubtitleStream;
                if (s.OcrPictureSubtitles && SubtitleOcr.CanRead(sst?.CodecName ?? t.Codec)) t = t with { Output = SubtitleOcr.OutputValue };
                else
                {
                    var codec = Containers.SubtitleCodec(container, sst, t.Codec);
                    t = codec switch
                    {
                        null => t with { Include = false },
                        "subrip" => t with { Output = "srt" },
                        _ => t with { Output = codec },
                    };
                }
                if (t.Include && !string.Equals(t.Output, "copy", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(args.Subtitles))
                    t = t with { Arguments = ToolArguments.Combine(args.Subtitles, t.Arguments) };
            }
            tracks.Add(t);
        }

        var settings = s with
        {
            Container = p.EncodesVideo || p.IsRemux ? container : s.Container,
            Arguments = args with { AudioFfmpeg = null, Fdkaac = null, Qaac = null, Subtitles = null },
            Encoder = ExplicitRate(q, s, a.Source.PrimaryVideo, container),
        };
        var edit = e with { Tracks = tracks };
        var analysis = await q.SetEditAsync(id, edit, ct, settings);
        return (settings, edit, analysis);
    }

    /// <summary>
    /// With an explicit track list the VapourSynth/AviSynth script applied to the file must still be its video (and
    /// audio, when asked): its tracks are put in place of the file's when the frameserver is switched on, and taken
    /// away (the file's tracks back on) when it is switched off.
    /// </summary>
    private static IReadOnlyList<TrackPlan> ScriptTracks(QueueJob job, JobSettings s, IReadOnlyList<TrackPlan> tracks, bool automatic)
    {
        if (automatic || Frameserver.KindOf(job.SourcePath) != FrameserverKind.None) return tracks;
        var script = job.FrameserverScript;
        var list = tracks.ToList();
        bool IsScript(TrackPlan t) => t.File != job.SourcePath && !job.Edit.ExternalFiles.Contains(t.File) && Frameserver.KindOf(t.File) != FrameserverKind.None;
        int removed = list.RemoveAll(t => IsScript(t) && t.File != script);
        if (script is null || JobRunner.FrameserverMedia(job) is not { PrimaryVideo: { } fv } fm)
        {
            if (removed > 0 && !list.Any(t => t.Kind == "video" && t.Include))
                list = list.Select(t => t.Kind == "video" && t.File == job.SourcePath ? t with { Include = true } : t).ToList();
            if (removed > 0 && !list.Any(t => t.Kind == "audio" && t.Include))
                list = list.Select(t => t.Kind == "audio" && t.File == job.SourcePath ? t with { Include = true } : t).ToList();
            return list;
        }
        if (!list.Any(t => t.File == script && t.Kind == "video"))
        {
            list = list.Select(t => t.Kind == "video" ? t with { Include = false } : t).ToList();
            list.Insert(0, TrackPlanner.FromStream(script, fv, include: true) with { Title = "VapourSynth/AviSynth" });
        }
        bool scriptAudio = s.Frameserver.AudioFromScript && fm.Audio.Count > 0;
        if (scriptAudio && !list.Any(t => t.File == script && t.Kind == "audio"))
        {
            list = list.Select(t => t.Kind == "audio" ? t with { Include = false } : t).ToList();
            int at = list.FindIndex(t => t.Kind == "audio");
            var add = fm.Audio.Select(x => TrackPlanner.FromStream(script, x, include: true)).ToList();
            list.InsertRange(at < 0 ? list.Count : at, add);
        }
        else if (!scriptAudio && list.RemoveAll(t => t.File == script && t.Kind == "audio") > 0 && !list.Any(t => t.Kind == "audio" && t.Include))
            list = list.Select(t => t.Kind == "audio" && t.File == job.SourcePath ? t with { Include = true } : t).ToList();
        return list;
    }

    /// <summary>"Quality level" made visible: the constant-quality value and the speed preset KnA would use.</summary>
    public static EncoderOverrides ExplicitRate(QueueService q, JobSettings s, VideoStream? video, string container)
    {
        var o = s.Encoder;
        var p = s.ProfileDefinition;
        if (!p.EncodesVideo || p.IsImages || o.RateControl != RateControl.Auto) return o;
        if (RigayaEncoders.Wanted(s) && RigayaEncoders.ToolId(s.Backend) is { } rid)
            return o with
            {
                RateControl = RateControl.Quality, QualityValue = RigayaEncoders.DefaultQuality(rid, s.EffectiveCodec, s.Quality),
                Speed = o.Speed ?? RigayaEncoders.DefaultSpeed(rid, s.Quality),
            };
        var caps = q.Settings.Capabilities;
        var req = new VideoEncodeRequest(s.EffectiveCodec, s.EffectiveBackend, s.Quality, s.EffectiveTenBit, s.Profile,
            s.VideoFilters.Fps ?? video?.FrameRate ?? 25, container is "mp4" or "mov")
        {
            Encoder = caps?.Resolve(s.EffectiveCodec, s.EffectiveBackend, s.EffectiveTenBit)?.Encoder, Pro = s.Pro,
            SourceBitDepth = Math.Max(8, video?.BitDepth ?? 8),
        };
        var (quality, speed) = VideoEncoders.AutoValues(req);
        if (quality is null) return o with { Speed = o.Speed ?? speed };
        return o with { RateControl = RateControl.Quality, QualityValue = quality, Speed = o.Speed ?? speed };
    }

    /// <summary>
    /// Imports a profile into the Video section: the video parameters go back to what the profile allows and wants
    /// (codec, CPU/GPU, depth, container, encoder parameters), and the audio and subtitle choices of each track are
    /// made again by the profile. <see cref="MaterializeAsync"/> then fills them in.
    /// </summary>
    public static (JobSettings Settings, JobEdit Edit) ApplyProfile(JobSettings s, JobEdit e, ProfileKind kind)
    {
        var p = Profiles.Get(kind);
        var codec = p.Codecs.Contains(s.Codec) ? s.Codec : p.Codecs.FirstOrDefault(VideoCodec.H264);
        var backend = p.AllowsHardware && VideoEncoders.EncoderName(codec, s.Backend) is not null ? s.Backend : EncoderBackend.Software;
        bool tenBitProfile = kind is ProfileKind.UniversalMkv or ProfileKind.Streaming or ProfileKind.Professional;
        var program = s.Encoder.Program == EncoderProgram.Rigaya && RigayaEncoders.Supports(codec, backend) ? EncoderProgram.Rigaya : EncoderProgram.FFmpeg;
        var settings = s with
        {
            Profile = kind, Codec = codec, Backend = backend,
            TenBit = tenBitProfile && VideoEncoders.Supports10Bit(codec, backend) && codec != VideoCodec.H264,
            Container = null, Pro = new(), Encoder = new EncoderOverrides { Program = program },
            AudioCodec = AudioCodec.Auto, AudioBitrate = null,
            Subtitles = p.UsesSubtitleMode ? s.Subtitles : SubtitleMode.None,
        };
        return (settings, ResetOutputs(e, audio: true, subtitles: true));
    }

    /// <summary>
    /// Imports the chosen parts of a preset. The track plan is rebuilt when the preset decides the subtitles or the
    /// track selection; the audio choices of each track are made again when it decides the audio.
    /// </summary>
    public static (JobSettings Settings, JobEdit Edit) ApplyPreset(JobSettings s, JobEdit e, Preset preset, PresetParts parts)
    {
        var settings = s.Merge(preset.Settings with { PresetName = preset.Name }, parts);
        if (parts.HasFlag(PresetParts.Video)) settings = settings with { Container = preset.Settings.Container };
        if (parts.HasFlag(PresetParts.Subtitles) || parts.HasFlag(PresetParts.Tracks)) return (settings, e with { Tracks = null });
        return (settings, ResetOutputs(e, parts.HasFlag(PresetParts.Audio) || parts.HasFlag(PresetParts.Video), parts.HasFlag(PresetParts.Video)));
    }

    /// <summary>The tracks with the chosen kinds' outputs back to "the profile decides" (filled in again by <see cref="MaterializeAsync"/>).</summary>
    public static JobEdit ResetOutputs(JobEdit e, bool audio, bool subtitles)
    {
        if (e.Tracks is null) return e;
        return e with
        {
            Tracks = e.Tracks.Select(t => t.Kind switch
            {
                "audio" when audio => t with { Output = null, Encoder = null, Bitrate = null, Channels = null, Arguments = null },
                "subtitle" when subtitles && !t.Burn => t with { Output = null, Arguments = null },
                _ => t,
            }).ToList(),
        };
    }

    /// <summary>
    /// The settings to store in a preset: the per-track choices become the job-wide ones the preset applies to other
    /// files (when every kept track agrees), and the subtitle mode follows what the tracks do.
    /// </summary>
    public static JobSettings ForPreset(JobSettings s, JobEdit e)
    {
        if (e.Tracks is not { } tracks) return s;
        var audio = tracks.Where(t => t.Include && t.Kind == "audio").ToList();
        var subs = tracks.Where(t => t.Kind == "subtitle").ToList();
        var r = s;
        if (audio.Count > 0 && audio.Select(t => t.Output ?? "").Distinct().Count() == 1
            && Enum.TryParse<AudioCodec>(audio[0].Output, true, out var codec))
        {
            r = r with { AudioCodec = codec };
            if (audio.Select(t => t.Bitrate).Distinct().Count() == 1) r = r with { AudioBitrate = audio[0].Bitrate };
            if (audio.Select(t => t.Arguments ?? "").Distinct().Count() == 1 && audio[0].Arguments is { Length: > 0 } aa)
            {
                var enc = audio[0].Encoder;
                r = r with
                {
                    Arguments = enc switch
                    {
                        AudioEncoderSet.Fdkaac => r.Arguments with { Fdkaac = aa },
                        AudioEncoderSet.Qaac => r.Arguments with { Qaac = aa },
                        _ => r.Arguments with { AudioFfmpeg = aa },
                    },
                };
            }
        }
        if (s.ProfileDefinition.UsesSubtitleMode)
            r = r with
            {
                Subtitles = subs.Any(t => t.Burn) ? SubtitleMode.Hardsub : subs.Any(t => t.Include) ? SubtitleMode.Softsub : SubtitleMode.None,
                OcrPictureSubtitles = subs.Any(t => t.Include && string.Equals(t.Output, SubtitleOcr.OutputValue, StringComparison.OrdinalIgnoreCase)),
            };
        var kept = subs.Where(t => t.Include && t.Arguments is { Length: > 0 }).ToList();
        if (kept.Count > 0 && kept.Select(t => t.Arguments).Distinct().Count() == 1)
            r = r with { Arguments = r.Arguments with { Subtitles = kept[0].Arguments } };
        return r;
    }
}
