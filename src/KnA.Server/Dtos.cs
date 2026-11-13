using KnA.Core.Ai;
using KnA.Core.Encoding;
using KnA.Core.Localization;
using KnA.Core.Media;
using KnA.Core.Queue;
using KnA.Core.Settings;
using KnA.Core.Tools;

namespace KnA.Server;

public sealed record JobDto(
    Guid Id, string FileName, string Folder, string SourcePath, JobSettings Settings, JobEdit Edit, string Label,
    JobStatus Status, string? Stage, string? Message, double Percent, string? MediaSummary, string? ProbeError,
    string? OutputPath, double? Fps, double? Speed, long? Size, long? EstimatedSize, double? RemainingSeconds,
    double? ElapsedSeconds, double PositionSeconds, double DurationSeconds, string? HdrSummary,
    IReadOnlyList<ValidationIssue> Issues, bool IsEdited)
{
    public static JobDto From(QueueJob j) => new(
        j.Id, j.FileName, j.Folder, j.SourcePath, j.Settings, j.Edit, j.Settings.Label, j.Status, j.Stage, j.Message,
        Math.Round(j.Percent, 2), j.Media?.Summary, j.ProbeError, j.OutputPath,
        j.Progress?.Fps, j.Progress?.Speed, j.Progress?.SizeBytes, j.Progress?.EstimatedFinalSize,
        j.Progress?.Remaining?.TotalSeconds, j.Elapsed?.TotalSeconds,
        j.Progress?.Position.TotalSeconds ?? 0, j.Progress?.Duration.TotalSeconds ?? j.Media?.Duration.TotalSeconds ?? 0,
        j.HdrSummary, j.Issues,
        j.Edit.Tracks is not null || j.Edit.IsTrimmed || j.Edit.ExternalFiles.Count > 0 || j.Edit.Chapters is not null);
}

public sealed record QueueStateDto(bool Running, bool Paused, Guid? CurrentJob, int Total, int Ready, int Done, int Failed);

public sealed record OptionDto(string Value, string Label, string? Description = null);

public sealed record ProfileDto(string Value, string Label, string Description, IReadOnlyList<VideoCodec> Codecs,
    bool AllowsHardware, bool UsesQuality, bool UsesResolution, bool UsesSubtitleMode, string Container, IReadOnlyList<string> Containers,
    bool EncodesVideo, bool UsesVideoFilters, bool UsesAudio, bool IsImages, bool IsDemux, bool IsRemux);

public sealed record OptionsDto(
    IReadOnlyList<ProfileDto> Profiles, IReadOnlyList<OptionDto> Codecs, IReadOnlyList<OptionDto> Backends,
    IReadOnlyList<OptionDto> Qualities, IReadOnlyList<OptionDto> Resolutions, IReadOnlyList<OptionDto> SubtitleModes,
    IReadOnlyList<OptionDto> PostActions, IReadOnlyList<string> Extensions, IReadOnlyList<OptionDto> Containers,
    IReadOnlyList<OptionDto> HdrModes, IReadOnlyList<OptionDto> Strengths, IReadOnlyList<OptionDto> Deinterlace,
    IReadOnlyList<OptionDto> ImageFormats, IReadOnlyList<OptionDto> ImageModes)
{
    public IReadOnlyList<OptionDto> AudioCodecs { get; init; } = Enum.GetValues<AudioCodec>().Select(a => new OptionDto(a.ToString(), a.Display())).ToList();
    public IReadOnlyList<OptionDto> ProResProfiles { get; init; } = Enum.GetValues<ProResProfile>().Select(a => new OptionDto(a.ToString(), a.Display())).ToList();
    public IReadOnlyList<OptionDto> DnxhrProfiles { get; init; } = Enum.GetValues<DnxhrProfile>().Select(a => new OptionDto(a.ToString(), a.Display())).ToList();
    public IReadOnlyList<OptionDto> ToneMapCurves { get; init; } = Enum.GetValues<ToneMapCurve>().Select(a => new OptionDto(a.ToString(), a.Display())).ToList();
    public IReadOnlyList<OptionDto> ChromaFormats { get; init; } = Enum.GetValues<ChromaFormat>().Select(a => new OptionDto(a.ToString(), a.Display())).ToList();
    public IReadOnlyList<OptionDto> UpscaleMethods { get; init; } = Enum.GetValues<UpscaleMethod>().Select(a => new OptionDto(a.ToString(), a.Display(), a.Description())).ToList();
    public IReadOnlyList<OptionDto> Anime4KModes { get; init; } = KnA.Core.Ai.Upscalers.Anime4KModes.Select(m => new OptionDto(m.Id, m.Label, m.Description)).ToList();
    public IReadOnlyList<OptionDto> NeuralModels { get; init; } = KnA.Core.Ai.UpscaleModels.All.Select(m => new OptionDto(m.Id, m.Name, m.Description)).ToList();
    public IReadOnlyList<OptionDto> LlmProviders { get; init; } = KnA.Core.Ai.AiSettings.Providers.Select(p => new OptionDto(p.Id, p.Name, p.DefaultUrl + (p.Notes.Length > 0 ? " · " + p.Notes : ""))).ToList();
    public IReadOnlyList<OptionDto> WatchActions { get; init; } =
    [
        new("Keep", L.T("Leave the source where it is")),
        new("MoveToDoneFolder", L.T("Move the source into \"_done\"")),
        new("Delete", L.T("Delete the source")),
    ];
    /// <summary>Which codecs allow a 10-bit switch, per codec (the UI greys it out otherwise).</summary>
    public IReadOnlyList<string> TenBitCodecs { get; init; } = Enum.GetValues<VideoCodec>().Where(c => VideoEncoders.Supports10Bit(c, EncoderBackend.Software) && !c.IsProfessional()).Select(c => c.ToString()).ToList();

    public static OptionsDto Create() => new(
        Core.Encoding.Profiles.All.Select(p => new ProfileDto(p.Kind.ToString(), p.Name, p.Description, p.Codecs, p.AllowsHardware,
            p.UsesQuality, p.UsesResolution, p.UsesSubtitleMode, p.Container, p.Containers,
            p.EncodesVideo, p.UsesVideoFilters, p.UsesAudio, p.IsImages, p.IsDemux, p.IsRemux)).ToList(),
        Enum.GetValues<VideoCodec>().Select(c => new OptionDto(c.ToString(), c.Display())).ToList(),
        Enum.GetValues<EncoderBackend>().Select(b => new OptionDto(b.ToString(), b.Display())).ToList(),
        Enum.GetValues<QualityLevel>().Select(q => new OptionDto(q.ToString(), q.Display())).ToList(),
        Core.Encoding.Resolutions.All.Select(r => new OptionDto(r.ToString(), Core.Encoding.Resolutions.Display(r))).ToList(),
        Enum.GetValues<SubtitleMode>().Select(m => new OptionDto(m.ToString(), m.Display())).ToList(),
        Enum.GetValues<PostQueueAction>().Select(a => new OptionDto(a.ToString(), a.Display())).ToList(),
        MediaExtensions.Video,
        Core.Encoding.Containers.All.Select(c => new OptionDto(c, Core.Encoding.Containers.Display(c))).ToList(),
        Enum.GetValues<HdrMode>().Select(m => new OptionDto(m.ToString(), m.Display())).ToList(),
        Enum.GetValues<FilterStrength>().Select(m => new OptionDto(m.ToString(), m.Display())).ToList(),
        Enum.GetValues<DeinterlaceMode>().Select(m => new OptionDto(m.ToString(), m.Display())).ToList(),
        Enum.GetValues<ImageFormat>().Select(m => new OptionDto(m.ToString(), m.ToString().ToUpperInvariant())).ToList(),
        Enum.GetValues<ImageMode>().Select(m => new OptionDto(m.ToString(), m.Display())).ToList());
}

public sealed record ToolsDto(string? Ffmpeg, string? Ffprobe, string? Scxvid, string? DoviTool, string? Hdr10PlusTool, string Instructions, string? VsPipe = null, string? Fdkaac = null);

public sealed record StateDto(string Version, string Platform, string Language, System.Text.Json.Nodes.JsonNode? Settings, OptionsDto Options, ToolsDto Tools,
    QueueStateDto Queue, IReadOnlyList<JobDto> Jobs, bool PowerActionsAllowed, IReadOnlyList<Preset> Presets);

public sealed record StreamDto(int Index, string Kind, string Codec, string? Language, string? Title, string? Details, bool Default, bool Forced);

public sealed record FileDto(string Path, string Name, bool IsSource, string? Summary, double Duration, IReadOnlyList<StreamDto> Streams,
    IReadOnlyList<ChapterMark> Chapters);

public sealed record AnalysisDto(IReadOnlyList<TrackPlan> Tracks, bool TracksAreDefault, IReadOnlyList<ValidationIssue> Issues,
    string Container, IReadOnlyList<string> AllowedContainers, string HdrSummary, string? CommandLine, double OutputDuration,
    IReadOnlyList<FileDto> Files, double FrameRate, int Rotation)
{
    public static AnalysisDto From(JobAnalysis a, string source)
    {
        var files = new List<FileDto> { File(a.Source, true) };
        files.AddRange(a.External.Values.Select(m => File(m, false)));
        return new AnalysisDto(a.Tracks, a.TracksAreDefault, a.Issues, a.Container, a.AllowedContainers, a.HdrSummary, a.CommandLine,
            a.OutputDuration.TotalSeconds, files, a.Source.PrimaryVideo?.FrameRate ?? 25, a.Source.PrimaryVideo?.Rotation ?? 0);
    }

    private static FileDto File(MediaInfo m, bool isSource) => new(m.Path, Path.GetFileName(m.Path), isSource, m.Summary, m.Duration.TotalSeconds,
        m.AllStreams.Select(s => new StreamDto(s.Index, s.Kind, s.CodecName, s.Language, s is AttachmentStream at ? at.FileName : s.Title, Details(s), s.IsDefault, s.IsForced)).ToList(),
        KnA.Core.Encoding.Chapters.FromSource(m));

    private static string? Details(StreamBase s) => s switch
    {
        VideoStream v => $"{v.Width}×{v.Height} · {v.FrameRate:0.###} fps" + (v.BitDepth > 8 ? $" · {v.BitDepth}-bit" : "") + (v.Hdr.IsHdr ? " · " + v.Hdr.Description : "") + (v.Rotation != 0 ? $" · ↻{v.Rotation}°" : ""),
        AudioStream au => $"{au.Channels} ch" + (au.SampleRate > 0 ? $" · {au.SampleRate / 1000.0:0.#} kHz" : "") + (au.IsLossless ? " · lossless" : ""),
        SubtitleStream ss => ss.IsBitmap ? "bitmap" : "text",
        AttachmentStream at => at.MimeType,
        _ => null,
    };
}

public sealed record AddJobsRequest(List<string> Paths, JobSettings Settings, List<string>? Extensions);
public sealed record IdsRequest(List<Guid> Ids);
public sealed record UpdateJobsRequest(List<Guid> Ids, JobSettings Settings);
public sealed record EditRequest(JobSettings? Settings, JobEdit Edit);
public sealed record ClearRequest(List<JobStatus> Statuses);
public sealed record MoveRequest(int Delta);

public sealed record SettingsUpdate(
    string? FfmpegPath, string? VaapiDevice, string? OutputDirectory, JobSettings? Defaults,
    PostQueueAction? PostAction, string? ExtraArguments, List<string>? FolderExtensions, string? Theme, bool? CheckUpdatesOnStart,
    string? Language = null, bool? RestoreQueue = null, string? OutputNameTemplate = null, ProcessPriority? Priority = null,
    int? StallTimeoutMinutes = null, int? KeepLogs = null, string? TempDirectory = null,
    List<string>? CustomFfmpegBuilds = null, Dictionary<string, string>? ToolPaths = null, string? FfmpegChannel = null,
    bool? DiscAllTitles = null, int? DiscMinMinutes = null, KnA.Core.Ai.AiSettings? Ai = null);

public sealed record UploadCreate(string Name, long Size, string? Folder);
public sealed record UploadComplete(bool Enqueue, JobSettings? Settings, string? Preset, Guid? AttachTo, string? AttachToSource = null);
public sealed record DropRequest(List<string> Paths, Guid? TargetJob, JobSettings? Settings);
public sealed record FolderRequest(string Path);
public sealed record AiSuggestRequest(string Prompt, JobSettings Settings, Guid? JobId);

public static class Mapping
{
    public static QueueStateDto QueueState(QueueService q)
    {
        var jobs = q.Jobs;
        return new QueueStateDto(q.IsRunning, q.IsPaused, q.CurrentJob?.Id, jobs.Count,
            jobs.Count(j => j.Status == JobStatus.Ready),
            jobs.Count(j => j.Status is JobStatus.Done or JobStatus.Warning),
            jobs.Count(j => j.Status is JobStatus.Error or JobStatus.Stopped));
    }

    public static ToolsDto Tools(QueueService q) => new(q.Tools?.Ffmpeg, q.Tools?.Ffprobe, q.Tools?.Scxvid, q.Tools?.DoviTool, q.Tools?.Hdr10PlusTool,
        OperatingSystem.IsMacOS() ? L.T("On macOS update FFmpeg with Homebrew: brew upgrade ffmpeg")
        : L.T("Install FFmpeg from Settings → Components, or with your package manager."), q.Tools?.VsPipe, q.Tools?.Fdkaac);

    /// <summary>Settings as sent to browsers: the LLM API key never leaves the server.</summary>
    public static System.Text.Json.Nodes.JsonNode? PublicSettings(AppSettings s, System.Text.Json.JsonSerializerOptions o)
    {
        var node = System.Text.Json.JsonSerializer.SerializeToNode(s, o);
        if (node?["ai"] is System.Text.Json.Nodes.JsonObject ai)
        {
            ai.Remove("apiKey");
            ai["hasApiKey"] = s.Ai.HasApiKey;
        }
        return node;
    }
}
