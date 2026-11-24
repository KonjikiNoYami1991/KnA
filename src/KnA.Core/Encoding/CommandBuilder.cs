using System.Globalization;
using KnA.Core.Localization;
using KnA.Core.Media;

namespace KnA.Core.Encoding;

public sealed record EncodePlan(IReadOnlyList<string> Arguments, string OutputPath)
{
    /// <summary>Human readable command line (for logs and the "show command" button).</summary>
    public string CommandLine(string ffmpeg = "ffmpeg") =>
        string.Join(' ', new[] { ffmpeg }.Concat(Arguments).Select(Quote));

    private static string Quote(string a) =>
        a.Length > 0 && a.All(c => !char.IsWhiteSpace(c) && c is not '"' and not '\'' and not ';' and not '|' and not '&' and not '[' and not ']')
            ? a : "\"" + a.Replace("\"", "\\\"") + "\"";
}

public sealed record BuildOptions
{
    /// <summary>Destination folder; null = next to the source file.</summary>
    public string? OutputDirectory { get; init; }
    public string NameTemplate { get; init; } = OutputNaming.DefaultTemplate;
    public string? VaapiDevice { get; init; }
    public Settings.HardwareDecoding HardwareDecoding { get; init; } = Settings.HardwareDecoding.Auto;
    /// <summary>Extra arguments appended before the output (power users).</summary>
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];
    public string AppVersion { get; init; } = "3.0";
    /// <summary>Detected encoders (which candidate to use, GPU checks); null = not detected yet.</summary>
    public Tools.FfmpegCapabilities? Capabilities { get; init; }
    /// <summary>Audio encoders of the FFmpeg build that runs the job.</summary>
    public AudioEncoderSet AudioEncoders { get; init; } = AudioEncoderSet.Default;
    /// <summary>x86-64 level of the processor (see <see cref="Tools.CpuFeatures"/>); 4 = AVX-512 for x265.</summary>
    public int CpuLevel { get; init; }
    /// <summary>Installed rigaya encoders (NVEncC / QSVEncC / VCEEncC) by backend.</summary>
    public IReadOnlyDictionary<EncoderBackend, string> RigayaEncoders { get; init; } = new Dictionary<EncoderBackend, string>();
}

/// <summary>Subtitles ready to be burned (prepared by the runner in the job working directory).</summary>
public sealed record PreparedSubtitles(string? RelativeSubtitleFile, string? RelativeFontsDir, int? BitmapStreamIndex,
    string? BitmapFile = null, double BitmapDelaySeconds = 0);

/// <summary>Files produced by external HDR tools for this job.</summary>
public sealed record HdrSideFiles(string? DolbyVisionRpu, string? Hdr10PlusJson);

public sealed record CompileContext
{
    public required MediaInfo Source { get; init; }
    /// <summary>Probe results of every file involved (source + external files), by path.</summary>
    public IReadOnlyDictionary<string, MediaInfo> Media { get; init; } = new Dictionary<string, MediaInfo>();
    public required JobSettings Settings { get; init; }
    public JobEdit Edit { get; init; } = new();
    public PreparedSubtitles? Burn { get; init; }
    public BuildOptions Options { get; init; } = new();
    public HdrEnvironment Hdr { get; init; } = HdrEnvironment.Unknown;
    public HdrSideFiles? HdrFiles { get; init; }
    public string? ForcedOutputPath { get; init; }
    /// <summary>Preview mode: render one frame (or a short clip) at this time instead of the whole job.</summary>
    public PreviewRequest? Preview { get; init; }
    /// <summary>Chunked encoding: encode only the video between these final-timeline times (one segment).</summary>
    public ChunkRequest? Chunk { get; init; }
    /// <summary>Chunked encoding, last step: take the video from the concatenated segments instead of encoding it.</summary>
    public ConcatVideo? Concat { get; init; }
    /// <summary>Chunked encoding, assembly step A: everything except the video (audio, subtitles, attachments,
    /// chapters), trimmed on the final timeline, written to an intermediate Matroska file.</summary>
    public bool RestOnly { get; init; }
    /// <summary>Script audio (VapourSynth output N) rendered to files: key "script|stream".</summary>
    public IReadOnlyDictionary<string, string> ScriptAudio { get; init; } = new Dictionary<string, string>();
    /// <summary>Audio tracks already encoded by an external encoder (fdkaac): key "file|stream" → .m4a, copied as is.</summary>
    public IReadOnlyDictionary<string, string> PreEncodedAudio { get; init; } = new Dictionary<string, string>();
    /// <summary>What the upscaling methods need (paths found by the runner); null = not available.</summary>
    public UpscaleEnvironment? Upscale { get; init; }
    /// <summary>FFmetadata file with the edited chapters (written by the runner); null = the source's chapters.</summary>
    public string? ChaptersFile { get; init; }
}

/// <summary>Files and programs for GPU shaders and the neural sidecar (see <see cref="Ai.UpscaleSidecar"/>).</summary>
public sealed record UpscaleEnvironment
{
    public string Ffmpeg { get; init; } = "ffmpeg";
    public string? Python { get; init; }
    public string? Script { get; init; }
    public string? ModelPath { get; init; }
    public int ModelScale { get; init; } = 4;
    public bool ModelKnown { get; init; }
    public string? ShaderFile { get; init; }
    public Ai.AiSettings Ai { get; init; } = new();
}

public enum PreviewKind { Frame, Clip }
public sealed record ChunkRequest(double Start, double End)
{
    /// <summary>
    /// Resumable single-pass encode: the video is written as pieces of this length (segment muxer, a forced IDR
    /// at every cut); <see cref="SegmentList"/> (CSV) lists every piece once it is complete.
    /// </summary>
    public double? SegmentSeconds { get; init; }
    public int SegmentStartNumber { get; init; }
    public string? SegmentList { get; init; }
    /// <summary>Decoded, filtered frames as y4m on stdout (for an external encoder) instead of an encoded file.</summary>
    public bool Y4m { get; init; }
}
public sealed record ConcatVideo(string ListFile, double Offset);
public sealed record PreviewRequest(PreviewKind Kind, double Time, double ClipSeconds = 6, int MaxWidth = 1280);

public sealed record CompiledJob(
    EncodePlan Plan,
    IReadOnlyList<ValidationIssue> Issues,
    HdrPlan Hdr,
    IReadOnlyList<TrackPlan> Tracks,
    string Container,
    TimeSpan OutputDuration)
{
    /// <summary>vspipe to start and connect to FFmpeg's stdin (VapourSynth video), or null.</summary>
    public PipeSource? Pipe { get; init; }
    /// <summary>The video is encoded by NVEncC / QSVEncC / VCEEncC (see <see cref="RigayaEncoders"/>), or null.</summary>
    public RigayaPlan? Rigaya { get; init; }
    /// <summary>Where each step of a rigaya encode runs (GPU or CPU) and why; null for the other encoders.</summary>
    public GpuPipeline? Gpu { get; init; }
    /// <summary>The picture chain written as a VapourSynth script (shown in the editor), null when nothing is encoded.</summary>
    public string? Script { get; init; }

    public bool HasErrors => Issues.Any(i => i.IsError);
}

/// <summary>
/// Turns a job into one FFmpeg command. Pure (no I/O): easy to test.
///
/// Timeline rules:
///  • every (file, delay) pair is a separate input with -itsoffset, so each track keeps its own delay;
///  • filters and burned subtitles work on the original timeline;
///  • the trim is applied LAST, as output options (-ss/-t after all -map), so imports, delays,
///    filters and subtitles are already in place when the cut happens.
/// </summary>
public static class CommandBuilder
{
    public static readonly IReadOnlyList<string> ProgressArgs = ["-progress", "pipe:1", "-nostats", "-stats_period", "0.5"];

    /// <summary>Legacy entry point used by older callers/tests.</summary>
    public static EncodePlan Build(MediaInfo media, JobSettings s, PreparedSubtitles? subs, BuildOptions o, string? forcedOutputPath = null) =>
        Compile(new CompileContext
        {
            Source = media, Media = new Dictionary<string, MediaInfo> { [media.Path] = media },
            Settings = s, Burn = subs, Options = o, ForcedOutputPath = forcedOutputPath,
        }).Plan;

    public static CompiledJob Compile(CompileContext ctx)
    {
        if (ctx.Settings.ProfileDefinition is { IsKeyframes: true } or { IsDemux: true }) throw new InvalidOperationException("Use the dedicated builder for this profile.");
        var issues = new List<ValidationIssue>();
        // Target file size → average video bitrate (no-op when the job doesn't use it or it is already resolved).
        if (ctx.Preview is null) ctx = SizePlanner.Resolve(ctx, issues);
        var s = ctx.Settings;
        var p = s.ProfileDefinition;

        var media = new Dictionary<string, MediaInfo>(ctx.Media) { [ctx.Source.Path] = ctx.Source };
        var tracks = ctx.Edit.Tracks ?? TrackPlanner.Default(ctx.Source, s);
        var preview = ctx.Preview;
        var chunk = ctx.Chunk;
        var concat = ctx.Concat;
        bool chunkedJob = s.Chunks.Enabled && p.EncodesVideo && preview is null && !ctx.Edit.IsMultiSegment;

        // ------------------------------------------------------------ container
        string container = s.EffectiveContainer;
        bool hasAttachments = tracks.Any(t => t.Include && t.Kind == "attachment");
        if (hasAttachments && !p.IsImages && container != "mkv")
        {
            issues.Add(Containers.Warning("Attachments need Matroska: the output container becomes MKV."));
            container = "mkv";
        }
        if (preview is not null) container = preview.Kind == PreviewKind.Frame ? "png" : "mp4";

        // ------------------------------------------------------------ video
        var videoTrack = tracks.FirstOrDefault(t => t.Include && t.Kind == "video");
        if (videoTrack is null || TrackPlanner.Lookup(videoTrack, media) is not VideoStream video)
            return Fail(Containers.Error("No video track selected."), tracks, container);

        bool copyVideo = preview is null && chunk is null && (concat is not null || p.Kind == ProfileKind.RemuxMkv
                         || (p.Kind == ProfileKind.RemuxMp4 && Containers.AcceptsVideo("mp4", video.CodecName))
                         || (videoTrack.CopyVideo && (p.EncodesVideo || p.IsRemux)));
        if (videoTrack.CopyVideo && copyVideo && p.EncodesVideo && concat is null)
        {
            issues.Add(Containers.Warning("The video is copied as it is (chosen in the track list): resolution, filters and burned subtitles are not applied."));
            chunkedJob = false;
        }
        var encodeCodec = p.Kind == ProfileKind.RemuxMp4 ? VideoCodec.H264 : s.EffectiveCodec;
        if (copyVideo && concat is null && !Containers.AcceptsVideo(container, video.CodecName))
            issues.Add(Containers.Error("The {0} video cannot be stored in {1}.", video.CodecName, container.ToUpperInvariant()));
        if ((!copyVideo || concat is not null) && p.EncodesVideo && preview is null && chunk is null && !Containers.AcceptsVideo(container, Containers.OutputVideoCodec(encodeCodec)))
            issues.Add(Containers.Error("{0} video cannot be stored in {1}. Choose another container or codec.",
                encodeCodec.Short(), container.ToUpperInvariant()));

        // The chosen GPU must really have this encoder (detected with a test encode), otherwise stop here
        // instead of failing halfway through the job.
        string? encoderName = null;
        bool rigaya = !copyVideo && p.EncodesVideo && preview is null && concat is null && !ctx.RestOnly && RigayaEncoders.Wanted(s);
        if (rigaya && !ctx.Options.RigayaEncoders.ContainsKey(s.Backend))
            issues.Add(Containers.Error("{0} is not installed: add it in Settings → Components, or choose FFmpeg as the encoder program.",
                RigayaEncoders.DisplayName(s.Backend)));
        if (rigaya && encodeCodec == VideoCodec.AV1 && ctx.Options.Capabilities is { IsCurrent: true } rc && !RigayaProfiles.Av1InHardware(s.Backend, rc))
            issues.Add(Containers.Error("This GPU has no AV1 encoder (the hardware test found none): AV1 needs NVIDIA RTX 40/50, Intel Arc / Core Ultra or AMD RX 7000 or newer. Choose HEVC or H.264."));
        if (rigaya && s.Chunks.Enabled && chunk is null)
            chunkedJob = false; // the encoder splits the file itself (--parallel)
        if (!copyVideo && p.EncodesVideo && preview is null && !rigaya)
        {
            var wantedBackend = p.AllowsHardware ? s.Backend : EncoderBackend.Software;
            if (s.BackendUnavailable)
            {
                var others = VideoEncoders.BackendsFor(encodeCodec).Select(b => b.Display());
                issues.Add(Containers.Error("{0} has no {1} encoder. Available: {2}.", encodeCodec.Display(), s.Backend.Display(), string.Join(", ", others)));
            }
            else if (ctx.Options.Capabilities is { } caps && caps.IsCurrent)
            {
                bool tb = s.EffectiveTenBit;
                if (caps.Resolve(encodeCodec, wantedBackend, tb) is { } cap) encoderName = cap.Encoder;
                else if (caps.Find(encodeCodec, wantedBackend) is { } failed)
                    issues.Add(Containers.Error("{0}{1} did not pass the test on this machine ({2}). Pick another encoder or re-run the hardware detection.",
                        failed.Encoder, tb && !encodeCodec.IsProfessional() ? " (10-bit)" : "",
                        failed.Compiled ? failed.Error ?? "?" : "not in this FFmpeg build"));
            }
        }

        bool trimmed = ctx.Edit.IsTrimmed;
        var hdrSettings = preview?.Kind == PreviewKind.Clip ? s with { Hdr = HdrMode.ToneMapSdr } : s;
        // Chunks are encoded separately: dynamic HDR metadata (per-frame RPU/JSON) cannot follow them.
        var hdr = HdrPlanner.Plan(video, preview?.Kind == PreviewKind.Frame ? s with { Hdr = HdrMode.ToneMapSdr } : hdrSettings,
            copyVideo && concat is null, trimmed || chunkedJob, ctx.Hdr);
        if (chunkedJob && video.Hdr.IsHdr && (video.Hdr.IsDolbyVision || video.Hdr.HasHdr10Plus) && hdr.Action == HdrAction.Preserve)
            issues.Add(Containers.Warning("Chunked encoding keeps static HDR only (Dolby Vision / HDR10+ dynamic metadata need a single pass)."));
        issues.AddRange(hdr.Issues);

        var inputs = new InputTable(ctx.Source.Path);
        foreach (var (path, m) in media)
            if (Frameserver.IsVapourSynth(path) && m.PrimaryVideo is { } pv)
            {
                inputs.PipeFps[path] = pv.FrameRate > 0 ? pv.FrameRate : 25;
                inputs.PipeFrames[path] = pv.FrameCount;
            }
        if (Frameserver.IsVapourSynth(videoTrack.File))
        {
            if (p.IsRemux || p.IsDemux) issues.Add(Containers.Error("Scripts can only be encoded (not remuxed or demuxed)."));
            double vfps = inputs.PipeFps.GetValueOrDefault(videoTrack.File, 25);
            if (chunk is not null) inputs.PipeEnd = d => (long)Math.Ceiling((chunk.End - d) * vfps) + 2;
            else if (preview is not null) inputs.PipeEnd = d => (long)Math.Ceiling((preview.Time - d + preview.ClipSeconds + 1) * vfps);
        }
        var oriented = FilterBuilder.Oriented(video, s.VideoFilters);

        // ------------------------------------------------------------ upscaling (GPU shaders / AI model)
        var up = s.Upscale;
        var croppedSource = FilterBuilder.AfterCrop(oriented, s.VideoFilters.Crop);
        var enlarge = p.UsesResolution ? Ai.Upscalers.Enlarge(croppedSource, s.Resolution) : null;
        bool wantUpscale = up.Method != UpscaleMethod.Standard && p.UsesVideoFilters && !copyVideo && concat is null && !ctx.RestOnly
                           && (enlarge is not null || up.AlwaysApply);
        bool placebo = wantUpscale && Ai.Upscalers.UsesLibplacebo(up.Method);
        bool neural = wantUpscale && up.Method == UpscaleMethod.Neural;
        var env = ctx.Upscale;
        if (placebo)
        {
            if (ctx.Options.Capabilities is { IsCurrent: true, HasLibplacebo: false })
            {
                issues.Add(Containers.Error("{0} needs an FFmpeg with libplacebo and a working Vulkan GPU.", Ai.UpscaleText.Display(up.Method)));
                placebo = false;
            }
            else if (up.Method != UpscaleMethod.Placebo && (env?.ShaderFile is not { } sh || !File.Exists(sh)))
            {
                issues.Add(Containers.Error("The {0} shaders are missing: install them in Settings → Components.", up.Method == UpscaleMethod.Anime4K ? "Anime4K" : "FSRCNNX"));
                placebo = false;
            }
        }
        if (neural)
        {
            string? problem = Frameserver.IsVapourSynth(videoTrack.File) ? L.T("AI upscaling cannot read VapourSynth scripts (add the model to the script instead).")
                : env?.Python is null ? L.T("Python was not found: install the AI upscaler in Settings → Components.")
                : env.Script is null ? L.T("The AI upscaler script (ai/kna_upscale.py) is missing from this installation.")
                : env.ModelPath is null || (!env.ModelKnown && !File.Exists(env.ModelPath)) ? L.T("Model file not found: {0}", env.ModelPath ?? up.Model ?? "?")
                : null;
            if (problem is not null) { issues.Add(new ValidationIssue(true, problem)); neural = false; }
            else if (preview is null) issues.Add(Containers.Warning("AI upscaling runs the model on every frame: expect a slow encode (seconds per frame without a GPU)."));
        }
        int aiScale = env?.ModelScale ?? 4;
        if (neural)
        {
            // The sidecar decodes the source itself (disc protocols, rotation and the pre-filters included) and
            // writes RGB48 frames enlarged by the model; FFmpeg reads them on stdin.
            var inArgs = new List<string>(MediaProber.RobustInputArgs(videoTrack.File));
            inArgs.AddRange(FilterBuilder.OrientationInputArgs(video, s.VideoFilters));
            inArgs.AddRange(Media.Discs.DiscSource.InputArgs(videoTrack.File));
            var preChain = string.Join(',', FilterBuilder.VideoPre(oriented, s.VideoFilters, hdr));
            int w = croppedSource.Width, h = croppedSource.Height;
            string fps = video.FrameRateRational is { Length: > 0 } r && r != "0/0" ? r : (video.FrameRate > 0 ? video.FrameRate : 25).ToString("0.###", CultureInfo.InvariantCulture);
            string matrix = Ai.UpscaleSidecar.Matrix(video, hdr.Action);
            string file = videoTrack.File, model = env!.ModelPath!, python = env.Python!, script = env.Script!, ffmpeg = env.Ffmpeg;
            var aiSettings = env.Ai;
            inputs.AiFormat = (w * aiScale, h * aiScale, fps);
            inputs.AiCommand = (start, end) => [python, .. Ai.UpscaleSidecar.Arguments(script, ffmpeg, file, inArgs, preChain, w, h, fps, matrix, model, aiScale, aiSettings, start, end)];
            if (chunk is not null) inputs.AiEnd = d => chunk.End - d + 1;
            else if (preview is not null) inputs.AiEnd = d => preview.Time - d + preview.ClipSeconds + 1;
        }

        // The audio/subtitle assembly step never reads the video (and must not open a VapourSynth pipe).
        int vin = ctx.RestOnly ? -1 : inputs.Get(neural ? InputTable.AiInput : videoTrack.File, videoTrack.DelaySeconds);
        int vIndex = neural ? 0 : video.Index;
        // Orientation: FFmpeg rotates the frames itself (autorotate) from the display matrix we set here;
        // with stream copy only the matrix changes (lossless fix of wrong rotation flags).
        if (vin >= 0 && !neural) inputs.AddOptions(vin, FilterBuilder.OrientationInputArgs(video, s.VideoFilters));
        // GPU decoding of the source (frames are copied back to memory, so every filter keeps working).
        if (vin >= 0 && !neural && !copyVideo && concat is null && !Frameserver.IsVapourSynth(videoTrack.File))
            inputs.AddOptions(vin, VideoEncoders.HwDecodeArgs(ctx.Options.HardwareDecoding,
                p.AllowsHardware ? s.EffectiveBackend : EncoderBackend.Software, ctx.Options.VaapiDevice));
        var backend = preview is null ? (p.Kind == ProfileKind.RemuxMp4 ? s.EffectiveBackend : s.EffectiveBackend) : EncoderBackend.Software;
        bool tenBit = preview is null && s.EffectiveTenBit;

        var body = new List<string>();
        IReadOnlyList<string>? gpuGlobal = null;   // device of the GPU filters (NVENC/QSV/VideoToolbox)
        ScaleResult? rigayaScale = null;          // the resize the rigaya encoder does on the GPU
        PreparedSubtitles? burnPrepared = null;   // the burned subtitle as prepared for this run (or the standard names)
        var burnTrack = tracks.FirstOrDefault(t => t.Burn && t.Kind == "subtitle");
        if (burnTrack is not null && !p.UsesVideoFilters && p.Kind != ProfileKind.RemuxMp4)
            burnTrack = null;
        if (concat is not null) burnTrack = null; // already burned into the segments
        if (burnTrack is not null && copyVideo)
        {
            issues.Add(Containers.Warning("Subtitles cannot be burned while the video is copied: ignored."));
            burnTrack = null;
        }

        bool restOnly = ctx.RestOnly;
        if (restOnly) { /* no video in the intermediate file */ }
        else if (concat is not null)
        {
            int cin = inputs.Get(concat.ListFile, concat.Offset);
            inputs.AddOptions(cin, ["-f", "concat", "-safe", "0"]);
            body.AddRange(["-map", $"{cin}:0", "-c:v", "copy"]);
            if (encodeCodec == VideoCodec.HEVC && container is "mp4" or "mov") body.AddRange(["-tag:v", "hvc1"]);
        }
        else if (copyVideo)
        {
            body.AddRange(["-map", $"{vin}:{video.Index}", "-c:v", "copy"]);
            if (video.CodecName == "hevc" && container is "mp4" or "mov") body.AddRange(["-tag:v", "hvc1"]);
        }
        else
        {
            var pre = neural ? [] : FilterBuilder.VideoPre(oriented, s.VideoFilters, hdr);
            var burn = ctx.Burn;
            if (burnTrack is not null && burn is null)
            {
                // Validation/preview without prepared files: assume the standard names.
                var bs = TrackPlanner.Lookup(burnTrack, media) as SubtitleStream;
                burn = bs?.IsBitmap == true
                    ? new PreparedSubtitles(null, null, burnTrack.Stream, burnTrack.File, burnTrack.DelaySeconds)
                    : new PreparedSubtitles("sub.ass", media.Values.Any(m => m.Attachments.Any(a => a.IsFont)) ? "fonts" : null, null);
            }
            string? textFilter = null;
            if (burnTrack is not null && burn?.RelativeSubtitleFile is { } subFile)
            {
                textFilter = $"subtitles=filename={subFile}";
                if (burn.RelativeFontsDir is { } fonts) textFilter += $":fontsdir={fonts}";
            }
            var cropped = neural ? Enlarged(croppedSource, aiScale) : croppedSource;
            string? scaleOverride = null;
            if (placebo)
                scaleOverride = Ai.Upscalers.PlaceboFilter(up, enlarge ?? new ScaleResult(croppedSource.Width & ~1, croppedSource.Height & ~1, false),
                    env?.ShaderFile, tenBit || hdr.Action == HdrAction.Preserve);
            else if (neural && enlarge is null)
                scaleOverride = $"scale={croppedSource.Width & ~1}:{croppedSource.Height & ~1}:flags=lanczos"; // restoration only: back to the source size
            var post = FilterBuilder.VideoPost(cropped, s, textFilter, scaleOverride);
            burnPrepared = burn;
            if (rigaya && scaleOverride is null) rigayaScale = ScaleCalculator.Compute(cropped, p.UsesResolution ? s.Resolution : 0);
            if (neural) post.Insert(0, $"scale=out_color_matrix={Ai.UpscaleSidecar.Matrix(video, hdr.Action)}:out_range=tv,format=yuv444p16le");
            if (preview is not null && preview.MaxWidth > 0)
                post.Add($"scale='min({preview.MaxWidth},iw)':-2");
            if (p.IsImages && preview is null && ImageSelectFilter(s.Images, ctx, trimmed) is { } sel) post.Add(sel);
            // A segment keeps exactly its frames (pts in the final timeline thanks to -copyts), then restarts at 0.
            if (chunk is not null) post.Add($"trim=start={Sec(chunk.Start)}:end={Sec(chunk.End)},setpts=PTS-STARTPTS");
            if (preview?.Kind == PreviewKind.Clip || (preview is null && !p.IsImages && hdr.Action == HdrAction.ToneMap && !tenBit))
                post.Add("format=yuv420p");
            if (preview is null && p.EncodesVideo && VideoEncoders.FilterTail(backend, encodeCodec, tenBit) is { } tail) post.Add(tail);
            // Filters of the GPU that encodes: after the upload, right before the FFmpeg hardware encoder.
            if (preview is null && chunk?.Y4m != true && p.EncodesVideo && !rigaya && !p.IsImages && backend != EncoderBackend.Software
                && s.VideoFilters.Gpu is { Length: > 0 } gpuChain && !string.IsNullOrWhiteSpace(gpuChain))
            {
                if (VideoEncoders.FilterTail(backend, encodeCodec, tenBit) is not null) post.Add(gpuChain.Trim());
                else if (VideoEncoders.GpuFilterUpload(backend, tenBit) is { } upload)
                {
                    post.Add(upload.Upload);
                    post.Add(gpuChain.Trim());
                    gpuGlobal = upload.Global;
                }
                else issues.Add(Containers.Warning("{0} has no GPU filters in FFmpeg: the GPU filters are ignored.", backend.Display()));
            }

            int bin = burnTrack is not null && burn?.BitmapStreamIndex is not null
                ? inputs.Get(burn.BitmapFile ?? burnTrack.File, burn.BitmapFile is null ? burnTrack.DelaySeconds : burn.BitmapDelaySeconds) : -1;
            string BuildGraph()
            {
                if (bin >= 0 && burn?.BitmapStreamIndex is { } bIdx)
                {
                    var preChain = pre.Count > 0 ? string.Join(',', pre) : "null";
                    // With the AI upscaler the picture is already enlarged: enlarge the subtitle bitmaps by the same factor.
                    string subIn = neural ? "[ksub]" : $"[{bin}:{bIdx}]";
                    string subScale = neural ? $"[{bin}:{bIdx}]scale=iw*{aiScale}:ih*{aiScale}[ksub];" : "";
                    return $"{subScale}[{vin}:{vIndex}]{preChain}[base];[base]{subIn}overlay=eof_action=pass:repeatlast=0"
                           + (post.Count > 0 ? "," + string.Join(',', post) : "") + "[vout]";
                }
                var all = pre.Concat(post).ToList();
                return $"[{vin}:{vIndex}]" + (all.Count > 0 ? string.Join(',', all) : "null") + "[vout]";
            }
            body.AddRange(["-filter_complex", BuildGraph(), "-map", "[vout]"]);

            if (preview?.Kind == PreviewKind.Frame)
            {
                body.AddRange(["-frames:v", "1", "-c:v", "png"]);
            }
            else if (chunk?.Y4m == true)
            {
                if (!(preview is null && !p.IsImages && hdr.Action == HdrAction.ToneMap && !tenBit)) post.Add(tenBit ? "format=yuv420p10le" : "format=yuv420p");
                body[^3] = BuildGraph();   // the format above must be in the graph
                body.AddRange(["-c:v", "rawvideo", "-pix_fmt", tenBit ? "yuv420p10le" : "yuv420p", "-strict", "-1"]);
            }
            else if (preview?.Kind == PreviewKind.Clip)
            {
                body.AddRange(["-c:v", "libx264", "-preset", "ultrafast", "-crf", "22", "-pix_fmt", "yuv420p"]);
            }
            else if (p.IsImages)
            {
                body.AddRange(ImageEncoderArgs(s.Images));
            }
            else
            {
                var venc = VideoEncoders.BuildArgs(new VideoEncodeRequest(encodeCodec, backend, s.Quality, tenBit,
                    p.Kind, s.VideoFilters.Fps ?? video.FrameRate, container is "mp4" or "mov")
                {
                    Encoder = encoderName, Pro = s.Pro, SourceBitDepth = Math.Max(8, video.BitDepth),
                });
                ApplyHdrToEncoder(venc, hdr, ctx.HdrFiles);
                // x265 leaves its AVX-512 kernels off unless asked (x86-64-v4: Zen 4/5, Intel server and Rocket/Tiger/Ice Lake).
                if (ctx.Options.CpuLevel >= 4 && venc.IndexOf("libx265") >= 0 && venc.IndexOf("-x265-params") is var xp and >= 0)
                    venc[xp + 1] += ":asm=avx512";
                VideoEncoders.ApplyOverrides(venc, s.Encoder, s.VideoFilters.Fps ?? video.FrameRate);
                // Frames already on the GPU: the encoder takes them as they are (no software pixel format).
                if (gpuGlobal is not null && venc.IndexOf("-pix_fmt") is var pf and >= 0) venc.RemoveRange(pf, Math.Min(2, venc.Count - pf));
                body.AddRange(venc);
            }
        }
        if (preview?.Kind != PreviewKind.Frame && !p.IsImages && concat is null && !restOnly) body.AddRange(hdr.ColorArgs);

        // ------------------------------------------------------------ audio / subtitles / attachments
        int outIndex = ctx.RestOnly ? 0 : 1;   // the intermediate audio/subtitle file has no video stream
        int audioOut = 0, subOut = 0;
        if (preview?.Kind == PreviewKind.Clip)
        {
            var a = tracks.FirstOrDefault(t => t.Include && t.Kind == "audio");
            if (a is not null && TrackPlanner.Lookup(a, media) is AudioStream ast)
            {
                body.AddRange(["-map", AudioInput(a, ast, inputs, ctx), "-c:a:0", "aac", "-ac:a:0", "2", "-b:a:0", "160k"]);
                if (FilterBuilder.Audio(AudioPlanner.FiltersFor(a, s), "aac", 2) is { } af) body.AddRange(["-filter:a:0", af]);
            }
        }
        else if (preview is null && chunk is null && !p.IsImages)
        {
            foreach (var t in tracks.Where(t => t.Include && t.Kind == "audio"))
            {
                if (TrackPlanner.Lookup(t, media) is not AudioStream ast)
                {
                    issues.Add(Containers.Warning("Audio track {0} of {1} not found: skipped.", t.Stream, Path.GetFileName(t.File)));
                    continue;
                }
                var aplan = AudioPlanner.PlanTrack(t, ast, s, container, ctx.Options.AudioEncoders);   // the final container decides, also for the intermediate file
                if (aplan.Issue is { } ai) issues.Add(ai);
                if (ctx.PreEncodedAudio.GetValueOrDefault(t.Key) is { } pre)
                {
                    // Already encoded (fdkaac) with filters applied: copy it with the track's delay.
                    body.AddRange(["-map", $"{inputs.Get(pre, t.DelaySeconds)}:0", $"-c:a:{audioOut.ToString(CultureInfo.InvariantCulture)}", "copy"]);
                }
                else
                {
                    body.AddRange(["-map", AudioInput(t, ast, inputs, ctx)]);
                    body.AddRange(AudioPlanner.BuildArgs(aplan, audioOut, AudioPlanner.FiltersFor(t, s), ToolArguments.Combine(s.Arguments.AudioFfmpeg, t.Arguments)));
                }
                if (container == "mxf" && !aplan.IsCopy) body.AddRange([$"-ar:a:{audioOut.ToString(CultureInfo.InvariantCulture)}", "48000"]); // MXF: 48 kHz only
                body.AddRange(StreamMetadata(outIndex, t));
                audioOut++;
                outIndex++;
            }

            foreach (var t in tracks.Where(t => t.Include && t.Kind == "subtitle"))
            {
                var sst = TrackPlanner.Lookup(t, media) as SubtitleStream;
                bool ocr = KnA.Core.Ocr.SubtitleOcr.Wanted(t, s);
                // Picture track to read with OCR: the runner replaces it with the SRT before encoding (not in previews).
                if (ocr && sst is not null && KnA.Core.Ocr.SubtitleOcr.CanRead(sst.CodecName)) continue;
                var codec = Containers.SubtitleCodec(restOnly ? "mkv" : container, sst, t.Codec);
                if (t.Output is { Length: > 0 } wanted && !ocr)
                {
                    var chosen = Containers.ChosenSubtitleCodec(restOnly ? "mkv" : container, sst, t.Codec, wanted);
                    if (chosen is null)
                        issues.Add(Containers.Warning("Subtitle track \"{0}\": {1} is not possible here, the automatic choice is used.",
                            t.Title ?? t.Language ?? t.Stream.ToString(CultureInfo.InvariantCulture), wanted));
                    else codec = chosen;
                }
                if (codec is null)
                {
                    issues.Add(Containers.Warning("Subtitle track \"{0}\" ({1}) cannot be stored in {2}: skipped.",
                        t.Title ?? t.Language ?? t.Stream.ToString(CultureInfo.InvariantCulture), t.Codec, container.ToUpperInvariant()));
                    continue;
                }
                body.AddRange(["-map", $"{inputs.Get(t.File, t.DelaySeconds)}:{t.Stream}", $"-c:s:{subOut}", codec]);
                if (codec != "copy") body.AddRange(ToolArguments.ForStream(ToolArguments.Combine(s.Arguments.Subtitles, t.Arguments), 's', subOut));
                body.AddRange(StreamMetadata(outIndex, t));
                subOut++;
                outIndex++;
            }

            if (Containers.AcceptsAttachments(restOnly ? "mkv" : container))
            {
                int attOut = 0;
                foreach (var t in tracks.Where(t => t.Include && t.Kind == "attachment" && t.Stream >= 0))
                {
                    body.AddRange(["-map", $"{inputs.Get(t.File, 0)}:{t.Stream}"]);
                    attOut++;
                    outIndex++;
                }
                if (attOut > 0) body.AddRange(["-c:t", "copy"]);
                foreach (var t in tracks.Where(t => t.Include && t.Kind == "attachment" && t.Stream < 0))
                {
                    body.AddRange(["-attach", t.File, $"-metadata:s:{outIndex.ToString(CultureInfo.InvariantCulture)}", "mimetype=" + TrackPlanner.MimeType(t.File)]);
                    if (!string.IsNullOrWhiteSpace(t.Title) && t.Title != Path.GetFileName(t.File))
                        body.AddRange([$"-metadata:s:{outIndex.ToString(CultureInfo.InvariantCulture)}", "filename=" + t.Title]);
                    outIndex++;
                }
            }

            bool scriptSource = Frameserver.IsVapourSynth(ctx.Source.Path);
            if (ctx.Edit.KeepChapters && ctx.ChaptersFile is { } chaptersFile)
            {
                int ci = inputs.Get(chaptersFile, 0);
                inputs.AddOptions(ci, ["-f", "ffmetadata"]);
                body.AddRange(["-map_chapters", ci.ToString(CultureInfo.InvariantCulture)]);
            }
            else body.AddRange(["-map_chapters", ctx.Edit.KeepChapters && !scriptSource ? "0" : "-1"]);
            // Transport streams carry service/provider tags that some muxers reject: keep only our own.
            body.AddRange(["-map_metadata", ctx.Source.IsTransportStream || scriptSource ? "-1" : "0"]);
            body.AddRange(["-metadata", $"comment=Encoded with KnA - Konjiki no Autoencoder {ctx.Options.AppVersion}"]);
        }

        // ------------------------------------------------------------ head, trim (last), output
        var a2 = new List<string> { "-hide_banner", "-nostdin", preview is null && chunk is null ? "-n" : "-y", "-ignore_unknown" };
        if (preview is null && !restOnly && VideoEncoders.GlobalArgs(backend, ctx.Options.VaapiDevice) is { Count: > 0 } g && p.EncodesVideo && !copyVideo)
            a2.AddRange(g);
        else if (gpuGlobal is not null)
            a2.AddRange(gpuGlobal);
        else if (body.Any(x => x.Contains("libplacebo=", StringComparison.Ordinal)))
            a2.AddRange(Ai.Upscalers.VulkanDeviceArgs());

        if (chunk is not null)
        {
            // Same fast-seek + original timestamps technique as previews: the filters and burned subtitles
            // see exactly the timeline of a single-pass encode, and trim= keeps the segment's frames.
            // The input seek is exact (frames before it never reach the filters, -copyts keeps the timeline):
            // only temporal filters need a little history to look the same across segment borders.
            double preroll = s.VideoFilters.Denoise != FilterStrength.Off || s.VideoFilters.Deinterlace == DeinterlaceMode.Force
                || (s.VideoFilters.Deinterlace == DeinterlaceMode.Auto && video.IsInterlaced) ? 1 : 0;
            a2.AddRange(inputs.ToArgs(delay => Math.Max(0, chunk.Start - delay - preroll)));
            a2.Add("-copyts");
            a2.AddRange(body);
            a2.AddRange(["-an", "-sn", "-dn", "-max_muxing_queue_size", "4096"]);
            a2.AddRange(ctx.Options.ExtraArguments);
            a2.AddRange(ProgressArgs);
            if (chunk.Y4m)
            {
                int pi = a2.IndexOf("pipe:1");
                if (pi >= 0) a2[pi] = "pipe:2";   // stdout carries the frames
                a2.AddRange(["-f", "yuv4mpegpipe", "pipe:1"]);
            }
            else if (chunk.SegmentSeconds is { } pieceLength)
            {
                var piece = Sec(pieceLength);
                a2.AddRange(["-force_key_frames", $"expr:gte(t,n_forced*{piece})"]);
                if (ForcedIdrEncoder(ResolvedEncoder(body))) a2.AddRange(["-forced-idr", "1"]);
                a2.AddRange(["-f", "segment", "-segment_format", "matroska", "-segment_time", piece,
                    "-segment_start_number", chunk.SegmentStartNumber.ToString(CultureInfo.InvariantCulture), "-reset_timestamps", "1",
                    "-segment_list", chunk.SegmentList ?? "pieces.csv", "-segment_list_type", "csv", ctx.ForcedOutputPath ?? "piece_%05d.mkv"]);
            }
            else a2.AddRange(["-f", "matroska", ctx.ForcedOutputPath ?? "chunk.mkv"]);
            return new CompiledJob(new EncodePlan(a2, ctx.ForcedOutputPath ?? "chunk.mkv"), issues, hdr, tracks, "mkv",
                TimeSpan.FromSeconds(chunk.End - chunk.Start)) { Pipe = inputs.Pipe };
        }

        if (preview is not null)
        {
            // Fast seek on every input but keep original timestamps, then cut exactly at the requested time.
            // Exact input seek: only the frames from the requested time on are filtered (a long preroll made every
            // still frame tone-map, scale and burn ~200 frames first). Clips keep a second of audio/filters history.
            double preroll = preview.Kind == PreviewKind.Clip ? 1 : 0;
            a2.AddRange(inputs.ToArgs(delay => Math.Max(0, preview.Time - delay - preroll)));
            a2.Add("-copyts");
        }
        else a2.AddRange(inputs.ToArgs());

        a2.AddRange(body);
        a2.AddRange(["-max_muxing_queue_size", "4096"]);
        // Not in the intermediate audio/subtitle file: without video, "make_zero" would move a delayed track to 0.
        if (!restOnly && (copyVideo || container is "mkv" or "mp4" or "mov")) a2.AddRange(["-avoid_negative_ts", "make_zero"]);

        var duration = ctx.Source.Duration;
        if (preview is not null)
        {
            a2.AddRange(["-ss", Sec(preview.Time)]);
            if (preview.Kind == PreviewKind.Clip) a2.AddRange(["-t", Sec(preview.ClipSeconds), "-movflags", "+faststart"]);
            a2.AddRange(preview.Kind == PreviewKind.Frame
                ? (ctx.ForcedOutputPath is { } png ? ["-f", "image2", "-update", "1", png] : ["-f", "image2pipe", "-"])
                : ["-f", "mp4", ctx.ForcedOutputPath ?? "preview.mp4"]);
            return new CompiledJob(new EncodePlan(a2, ctx.ForcedOutputPath ?? "-"), issues, hdr, tracks, container,
                TimeSpan.FromSeconds(preview.Kind == PreviewKind.Clip ? preview.ClipSeconds : 0)) { Pipe = inputs.Pipe };
        }

        if (ctx.Edit.IsMultiSegment)
        {
            // Several kept parts: the runner encodes each part with this same command and joins them.
            var parts = ctx.Edit.KeptParts(ctx.Source.Duration.TotalSeconds);
            if (parts.Count == 0) issues.Add(Containers.Error("The trim end must be after the start."));
            else if (p.IsImages) issues.Add(Containers.Error("Several cut segments can't be joined in this profile: split them into separate jobs."));
            else
            {
                duration = TimeSpan.FromSeconds(parts.Sum(x => x.End - x.Start));
                for (int i = 1; i < parts.Count; i++)
                    if (parts[i].Start < parts[i - 1].End - 0.0005) { issues.Add(Containers.Error("The cut segments overlap.")); break; }
                if (copyVideo && concat is null) issues.Add(Containers.Warning("Copied video can only be cut cleanly on keyframes: the first frames may look broken."));
                if (s.Chunks.Enabled && p.EncodesVideo) issues.Add(Containers.Warning("With several cut segments the chunked encoding is not used."));
            }
        }
        else if (TrimArgs(ctx.Edit, ctx.Source.Duration, issues) is { } trim)
        {
            a2.AddRange(trim.Args);
            duration = trim.Duration;
            if (copyVideo && concat is null) issues.Add(Containers.Warning("Copied video can only be cut cleanly on keyframes: the first frames may look broken."));
        }

        if (restOnly)
        {
            a2.AddRange(ProgressArgs);
            a2.AddRange(["-f", "matroska", ctx.ForcedOutputPath ?? "rest.mkv"]);
            return new CompiledJob(new EncodePlan(a2, ctx.ForcedOutputPath ?? "rest.mkv"), issues, hdr, tracks, "mkv", duration);
        }

        switch (container)
        {
            case "mp4" or "mov": a2.AddRange(["-movflags", "+faststart"]); break;
            case "avi": a2.AddRange(["-f", "avi"]); break;
            case "ts": a2.AddRange(["-f", "mpegts"]); break;
            case "webm": a2.AddRange(["-f", "webm"]); break;
            case "mxf": a2.AddRange(["-f", "mxf"]); break;
        }
        a2.AddRange(ctx.Options.ExtraArguments);
        if (!p.IsImages) foreach (var (opt, value) in VideoEncoders.ParseExtra(s.Arguments.Muxer ?? "")) { a2.Add(opt); if (value is not null) a2.Add(value); }
        a2.AddRange(ProgressArgs);

        string output;
        if (p.IsImages)
        {
            var folder = ctx.ForcedOutputPath ?? OutputNaming.Folder(ctx.Source.Path, L.T("Images") + (ctx.Edit.PartLabel is { Length: > 0 } pl ? " " + pl : ""), ctx.Edit.OutputFolder ?? ctx.Options.OutputDirectory);
            output = Path.Combine(folder, KnA.Core.Media.Discs.DiscSource.DisplayName(ctx.Source.Path).Replace("%", "%%") + "_%06d." + container);
            a2.AddRange(["-f", "image2"]);
            if (s.Images.Mode is ImageMode.SceneChanges or ImageMode.Interval or ImageMode.Count) a2.AddRange(["-fps_mode", "vfr"]);
            a2.Add(output);
            output = folder;
        }
        else
        {
            output = ctx.ForcedOutputPath ?? OutputNaming.Build(ctx.Source.Path, s, container, ctx.Edit.OutputFolder ?? ctx.Options.OutputDirectory,
                OutputNaming.WithPart(ctx.Options.NameTemplate, ctx.Edit.PartLabel));
            a2.Add(output);
        }
        RigayaPlan? rigayaPlan = null;
        GpuPipeline? gpuPipeline = null;
        if (rigaya && !copyVideo)
            (rigayaPlan, gpuPipeline) = PlanRigaya(ctx, s, p, media, videoTrack, video, encodeCodec, tenBit, hdr, rigayaScale,
                burnTrack, burnPrepared, placebo || neural ? Ai.UpscaleText.Display(up.Method) : null, issues);
        string? scriptText = null;
        if (p.EncodesVideo && !p.IsImages && !copyVideo && preview is null && concat is null && chunk is null && !ctx.RestOnly)
            scriptText = ScriptPreview.Build(s, video, videoTrack.File, hdr,
                placebo || neural ? null : ScaleCalculator.Compute(croppedSource, p.UsesResolution ? s.Resolution : 0),
                ctx.Edit, burnTrack, burnPrepared, gpuPipeline);
        return new CompiledJob(new EncodePlan(a2, output), issues, hdr, tracks, container, duration) { Pipe = inputs.Pipe, Rigaya = rigayaPlan, Gpu = gpuPipeline, Script = scriptText };
    }

    /// <summary>
    /// GPU-first plan of a NVEncC / QSVEncC / VCEEncC encode: KnA's filters become the encoder's --vpp-* filters and
    /// the encoder reads the file (--avhw / --avsw) or the script (--vpy / --avs) itself. What has no GPU equivalent
    /// is named in the plan (and in the log) and the frames then come from FFmpeg on a pipe.
    /// </summary>
    private static (RigayaPlan, GpuPipeline) PlanRigaya(CompileContext ctx, JobSettings s, ProfileDefinition p, IReadOnlyDictionary<string, MediaInfo> media,
        TrackPlan videoTrack, VideoStream video, VideoCodec encodeCodec, bool tenBit, HdrPlan hdr, ScaleResult? scale,
        TrackPlan? burnTrack, PreparedSubtitles? burn, string? upscaler, List<ValidationIssue> issues)
    {
        double fr = s.VideoFilters.Fps ?? video.FrameRate;
        string file = videoTrack.File;
        var kind = Frameserver.KindOf(file);
        bool script = kind != FrameserverKind.None;
        var stepsBefore = new List<PipelineStep>();
        var extra = new List<string>();
        var readVideo = video;
        // A catalogue chain made only of filters the encoder has on the GPU (crop, resize, Bwdif, CAS…): the script is
        // not needed to run, the encoder reads the file and does the same steps.
        if (script && file != ctx.Source.Path && ctx.Source.PrimaryVideo is { } src && !src.Hdr.IsHdr
            && RigayaFilters.ChainOnGpu(s.Frameserver, s.Backend) is { } chain
            && !(chain.Any(c => c.Args.Contains("--crop")) && s.VideoFilters.Crop is { IsEmpty: false }))
        {
            foreach (var (name, args) in chain)
            {
                // KnA's own resize (Video tab) is the final size: the script's one is then not needed.
                if (args.Contains("--output-res") && scale is not null) continue;
                extra.AddRange(args);
                stepsBefore.Add(new PipelineStep(name, StepPlace.Gpu, args.Count > 0 ? string.Join(' ', args) : null, L.T("From the script: the same filter runs on the GPU.")));
            }
            file = ctx.Source.Path;
            readVideo = src;
            script = false;
            kind = FrameserverKind.None;
        }
        var t = RigayaFilters.Translate(new RigayaFilters.Input
        {
            Backend = s.Backend, Video = readVideo, Settings = s, Hdr = hdr, Scale = scale,
            Burn = burnTrack, Prepared = burn, SourceMedia = media.GetValueOrDefault(file), Script = script,
            Edit = ctx.Edit, DelaySeconds = videoTrack.DelaySeconds, Upscaler = upscaler,
        });
        var blockers = t.Blockers.ToList();
        if (!script && Media.Discs.DiscSource.IsTitlePath(file)) blockers.Add(L.T("disc source"));
        if (!script && media.GetValueOrDefault(file)?.Video.Count(v => !v.IsAttachedPicture) is not 1) blockers.Add(L.T("several video streams"));
        if (script && kind == FrameserverKind.AviSynth && !OperatingSystem.IsWindows()) blockers.Add(L.T("AviSynth script outside Windows"));
        // The encoders find KnA's portable VapourSynth only on Windows (--vsdir); elsewhere vspipe feeds them.
        else if (script && kind == FrameserverKind.VapourSynth && !OperatingSystem.IsWindows()) blockers.Add(L.T("VapourSynth script outside Windows (read through vspipe)"));

        var steps = t.Steps.ToList();
        if (script && s.Frameserver.Kind != FrameserverKind.None && file != ctx.Source.Path)
        {
            // The script's own filters: each stays on the CPU, with the GPU filter that could replace it.
            int at = steps.FindIndex(x => x.Place == StepPlace.CpuScript);
            steps.InsertRange(at + 1, RigayaFilters.ScriptSteps(s.Frameserver));
        }
        int afterRead = Math.Max(0, steps.FindIndex(x => x.Place == StepPlace.Gpu && x.Option is "--avhw") + 1);
        steps.InsertRange(afterRead, stepsBefore);
        string? why = blockers.Count > 0 ? string.Join(", ", blockers.Distinct()) : null;
        if (why is not null)
        {
            // FFmpeg then decodes and runs every KnA filter itself: only the encoder (and the user's own --vpp-*) stay on the GPU.
            int last = steps.Count - 1;   // the encode itself
            steps = steps.Select((x, k) => x.Place == StepPlace.Gpu && k < last && x.Name != L.T("Your GPU filters")
                ? x with { Place = StepPlace.CpuFfmpeg, Option = null, Note = L.T("On the GPU ({0}) once nothing needs FFmpeg.", x.Option ?? "") }
                : x).ToList();
            steps.Insert(0, new PipelineStep(L.T("FFmpeg decodes and filters on the CPU"), StepPlace.CpuFfmpeg, null,
                L.T("Needed for: {0}. Frames go to {1} on a pipe.", why, RigayaEncoders.DisplayName(s.Backend))));
        }

        var native = RigayaEncoders.EncoderArgs(s.Backend, encodeCodec, s.Quality, tenBit, s.Encoder, fr, hdr, video, why is null && !script);
        var plan = new RigayaPlan(s.Backend, RigayaEncoders.EncoderArgs(s.Backend, encodeCodec, s.Quality, tenBit, s.Encoder, fr, hdr, video, false))
        {
            PipeReason = why,
            TrimStart = t.TrimStart,
            NativeArgs = why is null
                ? RigayaEncoders.NativeArgs(file, script ? (kind == FrameserverKind.VapourSynth ? "--vpy" : "--avs") : RigayaEncoders.HwDecodable(readVideo.CodecName) ? "--avhw" : "--avsw",
                    [.. extra, .. t.Args, .. native], readVideo, hdr, encodeCodec, s.VideoFilters, "video.mkv", s.Chunks.Enabled && !script)
                : null,
        };
        if (s.Chunks.Enabled && why is not null)
            issues.Add(Containers.Warning("{0} splits the file in parallel only when it reads the source itself ({1} prevents it).",
                RigayaEncoders.DisplayName(s.Backend), why));
        if (why is not null)
            issues.Add(Containers.Warning("{0}: {1} has no GPU equivalent, so FFmpeg decodes and filters on the CPU (slower). See the Filters tab.",
                RigayaEncoders.DisplayName(s.Backend), why));
        return (plan, new GpuPipeline(steps));
    }

    private static VideoStream Enlarged(VideoStream v, int k) => new()
    {
        Index = 0, CodecName = "rawvideo", Kind = "video", Width = v.Width * k, Height = v.Height * k,
        SampleAspectRatio = v.SampleAspectRatio, FrameRate = v.FrameRate, FrameRateRational = v.FrameRateRational, FrameCount = v.FrameCount,
        PixelFormat = "rgb48le", BitDepth = 16, Hdr = v.Hdr, ColorTransfer = v.ColorTransfer, ColorPrimaries = v.ColorPrimaries,
    };

    /// <summary>The video encoder of an argument list ("-c:v name").</summary>
    public static string? ResolvedEncoder(IReadOnlyList<string> args)
    {
        for (int i = 0; i < args.Count - 1; i++) if (args[i] is "-c:v" or "-codec:v" or "-vcodec") return args[i + 1];
        return null;
    }

    /// <summary>Encoders whose forced keyframes can be made IDR (-forced-idr 1): needed to cut a resumable encode cleanly.</summary>
    public static bool ForcedIdrEncoder(string? encoder) => encoder is "libx264" or "libx265" or "h264_nvenc" or "hevc_nvenc" or "av1_nvenc";

    /// <summary>Encoders a resumable (pieces) encode supports: forced IDR, or keyframes that are always closed.</summary>
    public static bool ResumableEncoder(string? encoder) => ForcedIdrEncoder(encoder) || encoder is "libsvtav1" or "libvpx-vp9" or "libaom-av1";

    /// <summary>Output-side trim (after every filter and import). Returns null when not trimmed.</summary>
    public static (List<string> Args, TimeSpan Duration)? TrimArgs(JobEdit edit, TimeSpan sourceDuration, List<ValidationIssue> issues)
    {
        if (!edit.IsTrimmed || edit.IsMultiSegment) return null;
        double start = Math.Max(0, edit.TrimStart ?? 0);
        double end = edit.TrimEnd is > 0 and var e ? e : sourceDuration.TotalSeconds;
        if (sourceDuration > TimeSpan.Zero) end = Math.Min(end, sourceDuration.TotalSeconds);
        if (end - start < 0.05)
        {
            issues.Add(Containers.Error("The trim end must be after the start."));
            return null;
        }
        var args = new List<string>();
        if (start > 0) args.AddRange(["-ss", Sec(start)]);
        if (edit.TrimEnd is > 0) args.AddRange(["-t", Sec(end - start)]);
        return (args, TimeSpan.FromSeconds(end - start));
    }

    /// <summary>"input:stream" for an audio track; script audio is read from its pre-rendered file.</summary>
    private static string AudioInput(TrackPlan t, AudioStream ast, InputTable inputs, CompileContext ctx)
    {
        if (Frameserver.IsVapourSynth(t.File))
        {
            var file = ctx.ScriptAudio.GetValueOrDefault(t.SourceKey) ?? Path.ChangeExtension(t.File, $".audio{t.Stream}.w64");
            return $"{inputs.Get(file, t.DelaySeconds)}:0";
        }
        return $"{inputs.Get(t.File, t.DelaySeconds)}:{ast.Index}";
    }

    private static void ApplyHdrToEncoder(List<string> venc, HdrPlan hdr, HdrSideFiles? files)
    {
        if (hdr.Action != HdrAction.Preserve || hdr.X265Params.Count == 0) return;
        var extra = new List<string>(hdr.X265Params);
        if (hdr.UseDolbyVisionRpu && files?.DolbyVisionRpu is { } rpu)
            extra.AddRange(["dolby-vision-profile=8.1", "dolby-vision-rpu=" + EscapeX265(rpu)]);
        if (hdr.UseHdr10PlusJson && files?.Hdr10PlusJson is { } json)
            extra.Add("dhdr10-info=" + EscapeX265(json));
        int i = venc.IndexOf("-x265-params");
        if (i >= 0) venc[i + 1] = venc[i + 1] + ":" + string.Join(':', extra);
        else venc.AddRange(["-x265-params", string.Join(':', extra)]);
    }

    // x265-params uses ':' as separator: Windows drive letters must be escaped.
    private static string EscapeX265(string path) => path.Replace("\\", "/").Replace(":", "\\:");

    private static IEnumerable<string> StreamMetadata(int outIndex, TrackPlan t)
    {
        string n = outIndex.ToString(CultureInfo.InvariantCulture);
        yield return $"-metadata:s:{n}";
        yield return "language=" + (string.IsNullOrWhiteSpace(t.Language) ? "und" : t.Language);
        yield return $"-metadata:s:{n}";
        yield return "title=" + (t.Title ?? "");
        yield return $"-disposition:{n}";
        yield return (t.Default, t.Forced) switch
        {
            (true, true) => "default+forced",
            (true, false) => "default",
            (false, true) => "forced",
            _ => "0",
        };
    }

    private static string? ImageSelectFilter(ImageSettings img, CompileContext ctx, bool trimmed)
    {
        double length = ctx.Source.Duration.TotalSeconds;
        if (trimmed)
            length = (ctx.Edit.TrimEnd is > 0 and var e ? e : length) - (ctx.Edit.TrimStart ?? 0);
        return img.Mode switch
        {
            ImageMode.Interval => "fps=1/" + Math.Max(0.04, img.IntervalSeconds).ToString("0.###", CultureInfo.InvariantCulture),
            ImageMode.Count when length > 0 => "fps=" + (Math.Max(1, img.Count) / length).ToString("0.######", CultureInfo.InvariantCulture),
            ImageMode.SceneChanges => "select='gt(scene,0.3)'",
            _ => null,
        };
    }

    private static List<string> ImageEncoderArgs(ImageSettings img)
    {
        int q = Math.Clamp(img.Quality, 1, 100);
        return img.Format switch
        {
            ImageFormat.Jpeg => ["-c:v", "mjpeg", "-q:v", Math.Clamp((int)Math.Round(31 - q * 0.29), 1, 31).ToString(CultureInfo.InvariantCulture), "-pix_fmt", "yuvj444p"],
            ImageFormat.Tiff => ["-c:v", "tiff", "-compression_algo", "lzw", "-pix_fmt", img.SixteenBit ? "rgb48le" : "rgb24"],
            ImageFormat.Webp => ["-c:v", "libwebp", "-quality", q.ToString(CultureInfo.InvariantCulture)],
            ImageFormat.Bmp => ["-c:v", "bmp", "-pix_fmt", "bgr24"],
            _ => ["-c:v", "png", "-pix_fmt", img.SixteenBit ? "rgb48be" : "rgb24"],
        };
    }

    private static CompiledJob Fail(ValidationIssue issue, IReadOnlyList<TrackPlan> tracks, string container) =>
        new(new EncodePlan([], ""), [issue], new HdrPlan(), tracks, container, TimeSpan.Zero);

    internal static string Sec(double seconds) => seconds.ToString("0.###", CultureInfo.InvariantCulture);
}

public static class OutputNaming
{
    public const string DefaultTemplate = "{name} [{label}]";

    /// <summary>Applies the user's name template ({name}, {label}, {profile}, {codec}, {res}, {quality}, {date}).</summary>
    public static string Build(string source, JobSettings s, string extension, string? outputDirectory, string? template)
    {
        var t = string.IsNullOrWhiteSpace(template) ? DefaultTemplate : template;
        if (!t.Contains("{name}", StringComparison.Ordinal)) t = "{name} " + t; // never lose the episode name
        var name = t.Replace("{name}", KnA.Core.Media.Discs.DiscSource.DisplayName(source))
            .Replace("{label}", Safe(s.Label))
            .Replace("{profile}", Safe(s.ProfileDefinition.Name))
            .Replace("{codec}", s.ProfileDefinition.EncodesVideo ? s.EffectiveCodec.Short() : "copy")
            .Replace("{res}", Resolutions.Display(s.Resolution))
            .Replace("{quality}", Safe(s.Quality.Display()))
            .Replace("{date}", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        name = Safe(name);
        var dir = string.IsNullOrWhiteSpace(outputDirectory) ? KnA.Core.Media.Discs.DiscSource.ContainingFolder(source)! : outputDirectory;
        var candidate = Path.Combine(dir, $"{name}.{extension}");
        if (candidate.Equals(source, StringComparison.OrdinalIgnoreCase)) candidate = Path.Combine(dir, $"{name} (KnA).{extension}");
        for (int i = 2; File.Exists(candidate); i++)
            candidate = Path.Combine(dir, $"{name} ({i.ToString(CultureInfo.InvariantCulture)}).{extension}");
        return candidate;
    }

    /// <summary>"Name [Label].ext" next to the source (or in a chosen folder), never overwriting an existing file.</summary>
    public static string Build(string source, string label, string extension, string? outputDirectory)
    {
        var dir = string.IsNullOrWhiteSpace(outputDirectory) ? KnA.Core.Media.Discs.DiscSource.ContainingFolder(source)! : outputDirectory;
        var name = KnA.Core.Media.Discs.DiscSource.DisplayName(source);
        var safeLabel = Safe(label);
        var candidate = Path.Combine(dir, $"{name} [{safeLabel}].{extension}");
        for (int i = 2; File.Exists(candidate); i++)
            candidate = Path.Combine(dir, $"{name} [{safeLabel}] ({i.ToString(CultureInfo.InvariantCulture)}).{extension}");
        return candidate;
    }

    /// <summary>A new folder "Name [suffix]" (numbered when it already exists).</summary>
    public static string Folder(string source, string suffix, string? outputDirectory)
    {
        var dir = string.IsNullOrWhiteSpace(outputDirectory) ? KnA.Core.Media.Discs.DiscSource.ContainingFolder(source)! : outputDirectory;
        var name = KnA.Core.Media.Discs.DiscSource.DisplayName(source);
        var candidate = Path.Combine(dir, $"{name} [{Safe(suffix)}]");
        for (int i = 2; Directory.Exists(candidate); i++)
            candidate = Path.Combine(dir, $"{name} [{Safe(suffix)}] ({i.ToString(CultureInfo.InvariantCulture)})");
        return candidate;
    }

    public static string ImageFolder(string source, string? outputDirectory) => Folder(source, L.T("Images"), outputDirectory);

    /// <summary>The name template with the part label of a split job appended ("… - part 2").</summary>
    public static string? WithPart(string? template, string? part) =>
        string.IsNullOrWhiteSpace(part) ? template : (string.IsNullOrWhiteSpace(template) ? DefaultTemplate : template) + " - " + part;

    public static string Safe(string text) =>
        string.Concat(text.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '-' : c)).Trim();
}
