using System.Globalization;
using TnA.Core.Localization;
using TnA.Core.Media;

namespace TnA.Core.Encoding;

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
    public string? VaapiDevice { get; init; }
    /// <summary>Extra arguments appended before the output (power users).</summary>
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];
    public string AppVersion { get; init; } = "2.0";
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
}

public enum PreviewKind { Frame, Clip }
public sealed record PreviewRequest(PreviewKind Kind, double Time, double ClipSeconds = 6, int MaxWidth = 1280);

public sealed record CompiledJob(
    EncodePlan Plan,
    IReadOnlyList<ValidationIssue> Issues,
    HdrPlan Hdr,
    IReadOnlyList<TrackPlan> Tracks,
    string Container,
    TimeSpan OutputDuration)
{
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
        var s = ctx.Settings;
        var p = s.ProfileDefinition;
        if (p.IsKeyframes || p.IsDemux) throw new InvalidOperationException("Use the dedicated builder for this profile.");

        var media = new Dictionary<string, MediaInfo>(ctx.Media) { [ctx.Source.Path] = ctx.Source };
        var issues = new List<ValidationIssue>();
        var tracks = ctx.Edit.Tracks ?? TrackPlanner.Default(ctx.Source, s);
        var preview = ctx.Preview;

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

        bool copyVideo = preview is null && (p.Kind == ProfileKind.RemuxMkv
                         || (p.Kind == ProfileKind.RemuxMp4 && Containers.AcceptsVideo("mp4", video.CodecName)));
        var encodeCodec = p.Kind == ProfileKind.RemuxMp4 ? VideoCodec.H264 : s.EffectiveCodec;
        if (copyVideo && !Containers.AcceptsVideo(container, video.CodecName))
            issues.Add(Containers.Error("The {0} video cannot be stored in {1}.", video.CodecName, container.ToUpperInvariant()));
        if (!copyVideo && p.EncodesVideo && preview is null && !Containers.AcceptsVideo(container, Containers.OutputVideoCodec(encodeCodec)))
            issues.Add(Containers.Error("{0} video cannot be stored in {1}. Choose another container or codec.",
                encodeCodec.Short(), container.ToUpperInvariant()));

        bool trimmed = ctx.Edit.IsTrimmed;
        var hdrSettings = preview?.Kind == PreviewKind.Clip ? s with { Hdr = HdrMode.ToneMapSdr } : s;
        var hdr = HdrPlanner.Plan(video, preview?.Kind == PreviewKind.Frame ? s with { Hdr = HdrMode.ToneMapSdr } : hdrSettings,
            copyVideo, trimmed, ctx.Hdr);
        issues.AddRange(hdr.Issues);

        var inputs = new InputTable(ctx.Source.Path);
        int vin = inputs.Get(videoTrack.File, videoTrack.DelaySeconds);
        // Orientation: FFmpeg rotates the frames itself (autorotate) from the display matrix we set here;
        // with stream copy only the matrix changes (lossless fix of wrong rotation flags).
        inputs.AddOptions(vin, FilterBuilder.OrientationInputArgs(video, s.VideoFilters));
        var oriented = FilterBuilder.Oriented(video, s.VideoFilters);
        var backend = preview is null ? (p.Kind == ProfileKind.RemuxMp4 ? s.EffectiveBackend : s.EffectiveBackend) : EncoderBackend.Software;
        bool tenBit = preview is null && s.EffectiveTenBit && hdr.Action != HdrAction.ToneMap;

        var body = new List<string>();
        var burnTrack = tracks.FirstOrDefault(t => t.Burn && t.Kind == "subtitle");
        if (burnTrack is not null && !p.UsesVideoFilters && p.Kind != ProfileKind.RemuxMp4)
            burnTrack = null;
        if (burnTrack is not null && copyVideo)
        {
            issues.Add(Containers.Warning("Subtitles cannot be burned while the video is copied: ignored."));
            burnTrack = null;
        }

        if (copyVideo)
        {
            body.AddRange(["-map", $"{vin}:{video.Index}", "-c:v", "copy"]);
            if (video.CodecName == "hevc" && container is "mp4" or "mov") body.AddRange(["-tag:v", "hvc1"]);
        }
        else
        {
            var pre = FilterBuilder.VideoPre(oriented, s.VideoFilters, hdr);
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
            var cropped = FilterBuilder.AfterCrop(oriented, s.VideoFilters.Crop);
            var post = FilterBuilder.VideoPost(cropped, s, textFilter);
            if (preview is not null && preview.MaxWidth > 0)
                post.Add($"scale='min({preview.MaxWidth},iw)':-2");
            if (p.IsImages && preview is null && ImageSelectFilter(s.Images, ctx, trimmed) is { } sel) post.Add(sel);
            if (preview is null && p.EncodesVideo && VideoEncoders.FilterTail(backend, encodeCodec, tenBit) is { } tail) post.Add(tail);
            if (preview?.Kind == PreviewKind.Clip || (preview is null && !p.IsImages && hdr.Action == HdrAction.ToneMap && !tenBit))
                post.Add("format=yuv420p");

            string graph;
            if (burnTrack is not null && burn?.BitmapStreamIndex is { } bIdx)
            {
                int bin = inputs.Get(burn.BitmapFile ?? burnTrack.File, burn.BitmapFile is null ? burnTrack.DelaySeconds : burn.BitmapDelaySeconds);
                var preChain = pre.Count > 0 ? string.Join(',', pre) : "null";
                graph = $"[{vin}:{video.Index}]{preChain}[base];[base][{bin}:{bIdx}]overlay=eof_action=pass:repeatlast=0"
                        + (post.Count > 0 ? "," + string.Join(',', post) : "") + "[vout]";
            }
            else
            {
                var all = pre.Concat(post).ToList();
                graph = $"[{vin}:{video.Index}]" + (all.Count > 0 ? string.Join(',', all) : "null") + "[vout]";
            }
            body.AddRange(["-filter_complex", graph, "-map", "[vout]"]);

            if (preview?.Kind == PreviewKind.Frame)
            {
                body.AddRange(["-frames:v", "1", "-c:v", "png"]);
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
                    p.Kind, s.VideoFilters.Fps ?? video.FrameRate, container is "mp4" or "mov"));
                ApplyHdrToEncoder(venc, hdr, ctx.HdrFiles);
                body.AddRange(venc);
            }
        }
        if (preview?.Kind != PreviewKind.Frame && !p.IsImages) body.AddRange(hdr.ColorArgs);

        // ------------------------------------------------------------ audio / subtitles / attachments
        int outIndex = 1;
        int audioOut = 0, subOut = 0;
        if (preview?.Kind == PreviewKind.Clip)
        {
            var a = tracks.FirstOrDefault(t => t.Include && t.Kind == "audio");
            if (a is not null && TrackPlanner.Lookup(a, media) is AudioStream ast)
            {
                body.AddRange(["-map", $"{inputs.Get(a.File, a.DelaySeconds)}:{ast.Index}", "-c:a:0", "aac", "-ac:a:0", "2", "-b:a:0", "160k"]);
                if (FilterBuilder.Audio(s.AudioFilters, "aac", 2) is { } af) body.AddRange(["-filter:a:0", af]);
            }
        }
        else if (preview is null && !p.IsImages)
        {
            foreach (var t in tracks.Where(t => t.Include && t.Kind == "audio"))
            {
                if (TrackPlanner.Lookup(t, media) is not AudioStream ast)
                {
                    issues.Add(Containers.Warning("Audio track {0} of {1} not found: skipped.", t.Stream, Path.GetFileName(t.File)));
                    continue;
                }
                body.AddRange(["-map", $"{inputs.Get(t.File, t.DelaySeconds)}:{ast.Index}"]);
                body.AddRange(AudioPlanner.BuildArgs(ast, audioOut, s.Profile, s.Quality, container, s.AudioFilters));
                body.AddRange(StreamMetadata(outIndex, t));
                audioOut++;
                outIndex++;
            }

            foreach (var t in tracks.Where(t => t.Include && t.Kind == "subtitle"))
            {
                var sst = TrackPlanner.Lookup(t, media) as SubtitleStream;
                var codec = Containers.SubtitleCodec(container, sst, t.Codec);
                if (codec is null)
                {
                    issues.Add(Containers.Warning("Subtitle track \"{0}\" ({1}) cannot be stored in {2}: skipped.",
                        t.Title ?? t.Language ?? t.Stream.ToString(CultureInfo.InvariantCulture), t.Codec, container.ToUpperInvariant()));
                    continue;
                }
                body.AddRange(["-map", $"{inputs.Get(t.File, t.DelaySeconds)}:{t.Stream}", $"-c:s:{subOut}", codec]);
                body.AddRange(StreamMetadata(outIndex, t));
                subOut++;
                outIndex++;
            }

            if (Containers.AcceptsAttachments(container))
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
                    body.AddRange(["-attach", t.File, $"-metadata:s:{outIndex.ToString(CultureInfo.InvariantCulture)}", "mimetype=" + FontMime(t.File)]);
                    outIndex++;
                }
            }

            body.AddRange(["-map_chapters", ctx.Edit.KeepChapters ? "0" : "-1"]);
            // Transport streams carry service/provider tags that some muxers reject: keep only our own.
            body.AddRange(["-map_metadata", ctx.Source.IsTransportStream ? "-1" : "0"]);
            body.AddRange(["-metadata", $"comment=Encoded with TnA - Tanoshimi no Autohardsubber {ctx.Options.AppVersion}"]);
        }

        // ------------------------------------------------------------ head, trim (last), output
        var a2 = new List<string> { "-hide_banner", "-nostdin", preview is null ? "-n" : "-y", "-ignore_unknown" };
        if (preview is null && VideoEncoders.GlobalArgs(backend, ctx.Options.VaapiDevice) is { Count: > 0 } g && p.EncodesVideo && !copyVideo)
            a2.AddRange(g);

        if (preview is not null)
        {
            // Fast seek on every input but keep original timestamps, then cut exactly at the requested time.
            const double preroll = 8;
            a2.AddRange(inputs.ToArgs(delay => Math.Max(0, preview.Time - delay - preroll)));
            a2.Add("-copyts");
        }
        else a2.AddRange(inputs.ToArgs());

        a2.AddRange(body);
        a2.AddRange(["-max_muxing_queue_size", "4096"]);
        if (copyVideo || container is "mkv" or "mp4" or "mov") a2.AddRange(["-avoid_negative_ts", "make_zero"]);

        var duration = ctx.Source.Duration;
        if (preview is not null)
        {
            a2.AddRange(["-ss", Sec(preview.Time)]);
            if (preview.Kind == PreviewKind.Clip) a2.AddRange(["-t", Sec(preview.ClipSeconds), "-movflags", "+faststart"]);
            a2.AddRange(preview.Kind == PreviewKind.Frame
                ? (ctx.ForcedOutputPath is { } png ? ["-f", "image2", "-update", "1", png] : ["-f", "image2pipe", "-"])
                : ["-f", "mp4", ctx.ForcedOutputPath ?? "preview.mp4"]);
            return new CompiledJob(new EncodePlan(a2, ctx.ForcedOutputPath ?? "-"), issues, hdr, tracks, container,
                TimeSpan.FromSeconds(preview.Kind == PreviewKind.Clip ? preview.ClipSeconds : 0));
        }

        if (TrimArgs(ctx.Edit, ctx.Source.Duration, issues) is { } trim)
        {
            a2.AddRange(trim.Args);
            duration = trim.Duration;
            if (copyVideo) issues.Add(Containers.Warning("Copied video can only be cut cleanly on keyframes: the first frames may look broken."));
        }

        switch (container)
        {
            case "mp4" or "mov": a2.AddRange(["-movflags", "+faststart"]); break;
            case "avi": a2.AddRange(["-f", "avi"]); break;
            case "ts": a2.AddRange(["-f", "mpegts"]); break;
            case "webm": a2.AddRange(["-f", "webm"]); break;
        }
        a2.AddRange(ctx.Options.ExtraArguments);
        a2.AddRange(ProgressArgs);

        string output;
        if (p.IsImages)
        {
            var folder = ctx.ForcedOutputPath ?? OutputNaming.ImageFolder(ctx.Source.Path, ctx.Edit.OutputFolder ?? ctx.Options.OutputDirectory);
            output = Path.Combine(folder, Path.GetFileNameWithoutExtension(ctx.Source.Path).Replace("%", "%%") + "_%06d." + container);
            a2.AddRange(["-f", "image2"]);
            if (s.Images.Mode is ImageMode.SceneChanges or ImageMode.Interval or ImageMode.Count) a2.AddRange(["-fps_mode", "vfr"]);
            a2.Add(output);
            output = folder;
        }
        else
        {
            output = ctx.ForcedOutputPath ?? OutputNaming.Build(ctx.Source.Path, s.Label, container, ctx.Options.OutputDirectory);
            a2.Add(output);
        }
        return new CompiledJob(new EncodePlan(a2, output), issues, hdr, tracks, container, duration);
    }

    /// <summary>Output-side trim (after every filter and import). Returns null when not trimmed.</summary>
    public static (List<string> Args, TimeSpan Duration)? TrimArgs(JobEdit edit, TimeSpan sourceDuration, List<ValidationIssue> issues)
    {
        if (!edit.IsTrimmed) return null;
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

    private static string FontMime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".otf" => "application/vnd.ms-opentype",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        _ => "application/x-truetype-font",
    };

    private static CompiledJob Fail(ValidationIssue issue, IReadOnlyList<TrackPlan> tracks, string container) =>
        new(new EncodePlan([], ""), [issue], new HdrPlan(), tracks, container, TimeSpan.Zero);

    internal static string Sec(double seconds) => seconds.ToString("0.###", CultureInfo.InvariantCulture);
}

public static class OutputNaming
{
    /// <summary>"Name [Label].ext" next to the source (or in a chosen folder), never overwriting an existing file.</summary>
    public static string Build(string source, string label, string extension, string? outputDirectory)
    {
        var dir = string.IsNullOrWhiteSpace(outputDirectory) ? Path.GetDirectoryName(source)! : outputDirectory;
        var name = Path.GetFileNameWithoutExtension(source);
        var safeLabel = Safe(label);
        var candidate = Path.Combine(dir, $"{name} [{safeLabel}].{extension}");
        for (int i = 2; File.Exists(candidate); i++)
            candidate = Path.Combine(dir, $"{name} [{safeLabel}] ({i.ToString(CultureInfo.InvariantCulture)}).{extension}");
        return candidate;
    }

    /// <summary>A new folder "Name [suffix]" (numbered when it already exists).</summary>
    public static string Folder(string source, string suffix, string? outputDirectory)
    {
        var dir = string.IsNullOrWhiteSpace(outputDirectory) ? Path.GetDirectoryName(source)! : outputDirectory;
        var name = Path.GetFileNameWithoutExtension(source);
        var candidate = Path.Combine(dir, $"{name} [{Safe(suffix)}]");
        for (int i = 2; Directory.Exists(candidate); i++)
            candidate = Path.Combine(dir, $"{name} [{Safe(suffix)}] ({i.ToString(CultureInfo.InvariantCulture)})");
        return candidate;
    }

    public static string ImageFolder(string source, string? outputDirectory) => Folder(source, L.T("Images"), outputDirectory);

    public static string Safe(string text) =>
        string.Concat(text.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '-' : c)).Trim();
}
