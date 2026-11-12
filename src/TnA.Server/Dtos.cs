using TnA.Core.Encoding;
using TnA.Core.Localization;
using TnA.Core.Media;
using TnA.Core.Queue;
using TnA.Core.Settings;
using TnA.Core.Tools;

namespace TnA.Server;

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
        j.Edit.Tracks is not null || j.Edit.IsTrimmed || j.Edit.ExternalFiles.Count > 0);
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

public sealed record ToolsDto(string? Ffmpeg, string? Ffprobe, string? Scxvid, string? DoviTool, string? Hdr10PlusTool, string Instructions, string? VsPipe = null);

public sealed record StateDto(string Version, string Platform, string Language, AppSettings Settings, OptionsDto Options, ToolsDto Tools,
    QueueStateDto Queue, IReadOnlyList<JobDto> Jobs, bool PowerActionsAllowed, IReadOnlyList<Preset> Presets);

public sealed record StreamDto(int Index, string Kind, string Codec, string? Language, string? Title, string? Details, bool Default, bool Forced);

public sealed record FileDto(string Path, string Name, bool IsSource, string? Summary, double Duration, IReadOnlyList<StreamDto> Streams);

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
        m.AllStreams.Select(s => new StreamDto(s.Index, s.Kind, s.CodecName, s.Language, s is AttachmentStream at ? at.FileName : s.Title, Details(s), s.IsDefault, s.IsForced)).ToList());

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
    int? StallTimeoutMinutes = null, int? KeepLogs = null, string? TempDirectory = null);

public sealed record BrowseEntry(string Name, string Path, bool IsDirectory, long Size);
public sealed record BrowseResult(string? Path, string? Parent, IReadOnlyList<BrowseEntry> Entries, IReadOnlyList<string> Roots);

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
        : L.T("Install FFmpeg from Settings → Components, or with your package manager."), q.Tools?.VsPipe);
}
