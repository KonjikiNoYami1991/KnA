using TnA.Core.Encoding;
using TnA.Core.Queue;
using TnA.Core.Settings;
using TnA.Core.Tools;

namespace TnA.Server;

public sealed record JobDto(
    Guid Id, string FileName, string Folder, string SourcePath, JobSettings Settings, string Label,
    JobStatus Status, string? Stage, string? Message, double Percent, string? MediaSummary, string? ProbeError,
    string? OutputPath, double? Fps, double? Speed, long? Size, long? EstimatedSize, double? RemainingSeconds,
    double? ElapsedSeconds, double PositionSeconds, double DurationSeconds)
{
    public static JobDto From(QueueJob j) => new(
        j.Id, j.FileName, j.Folder, j.SourcePath, j.Settings, j.Settings.Label, j.Status, j.Stage, j.Message,
        Math.Round(j.Percent, 2), j.Media?.Summary, j.ProbeError, j.OutputPath,
        j.Progress?.Fps, j.Progress?.Speed, j.Progress?.SizeBytes, j.Progress?.EstimatedFinalSize,
        j.Progress?.Remaining?.TotalSeconds, j.Elapsed?.TotalSeconds,
        j.Progress?.Position.TotalSeconds ?? 0, j.Media?.Duration.TotalSeconds ?? 0);
}

public sealed record QueueStateDto(bool Running, bool Paused, Guid? CurrentJob, int Total, int Ready, int Done, int Failed);

public sealed record OptionDto(string Value, string Label, string? Description = null);

public sealed record ProfileDto(string Value, string Label, string Description, IReadOnlyList<VideoCodec> Codecs,
    bool AllowsHardware, bool UsesQuality, bool UsesResolution, bool UsesSubtitleMode, string Container);

public sealed record OptionsDto(
    IReadOnlyList<ProfileDto> Profiles, IReadOnlyList<OptionDto> Codecs, IReadOnlyList<OptionDto> Backends,
    IReadOnlyList<OptionDto> Qualities, IReadOnlyList<OptionDto> Resolutions, IReadOnlyList<OptionDto> SubtitleModes,
    IReadOnlyList<OptionDto> PostActions, IReadOnlyList<string> Extensions)
{
    public static OptionsDto Create() => new(
        Core.Encoding.Profiles.All.Select(p => new ProfileDto(p.Kind.ToString(), p.Name, p.Description, p.Codecs, p.AllowsHardware,
            p.UsesQuality, p.UsesResolution, p.UsesSubtitleMode, p.Container)).ToList(),
        Enum.GetValues<VideoCodec>().Select(c => new OptionDto(c.ToString(), c.Display())).ToList(),
        Enum.GetValues<EncoderBackend>().Select(b => new OptionDto(b.ToString(), b.Display())).ToList(),
        Enum.GetValues<QualityLevel>().Select(q => new OptionDto(q.ToString(), q.Display())).ToList(),
        Core.Encoding.Resolutions.All.Select(r => new OptionDto(r.ToString(), Core.Encoding.Resolutions.Display(r))).ToList(),
        Enum.GetValues<SubtitleMode>().Select(m => new OptionDto(m.ToString(), m.Display())).ToList(),
        Enum.GetValues<PostQueueAction>().Select(a => new OptionDto(a.ToString(), a.Display())).ToList(),
        MediaExtensions.Video);
}

public sealed record ToolsDto(string? Ffmpeg, string? Ffprobe, string? Scxvid, bool CanDownload, string Instructions);

public sealed record StateDto(string Version, string Platform, AppSettings Settings, OptionsDto Options, ToolsDto Tools,
    QueueStateDto Queue, IReadOnlyList<JobDto> Jobs, bool PowerActionsAllowed);

public sealed record AddJobsRequest(List<string> Paths, JobSettings Settings, List<string>? Extensions);
public sealed record IdsRequest(List<Guid> Ids);
public sealed record UpdateJobsRequest(List<Guid> Ids, JobSettings Settings);
public sealed record ClearRequest(List<JobStatus> Statuses);
public sealed record MoveRequest(int Delta);

public sealed record SettingsUpdate(
    string? FfmpegPath, string? VaapiDevice, string? OutputDirectory, JobSettings? Defaults,
    PostQueueAction? PostAction, string? ExtraArguments, List<string>? FolderExtensions, string? Theme);

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

    public static ToolsDto Tools(QueueService q) => new(q.Tools?.Ffmpeg, q.Tools?.Ffprobe, q.Tools?.Scxvid,
        FfmpegDownloader.DownloadUrl is not null, FfmpegDownloader.ManualInstructions);
}
