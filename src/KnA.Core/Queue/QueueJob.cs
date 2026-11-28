using System.Text;
using KnA.Core.Encoding;
using KnA.Core.Media;
using KnA.Core.Processing;

namespace KnA.Core.Queue;

public enum JobStatus { Ready, Running, Paused, Done, Warning, Error, Stopped }

public sealed class QueueJob
{
    private readonly StringBuilder _log = new();
    private const int MaxLogChars = 400_000;

    public Guid Id { get; init; } = Guid.NewGuid();
    public required string SourcePath { get; init; }
    /// <summary>The web server account that queued the job (null: desktop app, command line, watch folders, older queues).</summary>
    public Guid? Owner { get; set; }
    public string FileName => KnA.Core.Media.Discs.DiscSource.IsTitlePath(SourcePath)
        ? KnA.Core.Media.Discs.DiscSource.Label(SourcePath) : Path.GetFileName(SourcePath);
    public string Folder => KnA.Core.Media.Discs.DiscSource.ContainingFolder(SourcePath) ?? "";

    public JobSettings Settings { get; set; } = new();
    /// <summary>Per-file edits (trim, external files, track plan).</summary>
    public JobEdit Edit { get; set; } = new();
    /// <summary>Probe results of the external files used by <see cref="Edit"/>.</summary>
    public Dictionary<string, MediaInfo> ExternalMedia { get; } = new(StringComparer.Ordinal);
    /// <summary>Warnings/errors of the last validation.</summary>
    public IReadOnlyList<ValidationIssue> Issues { get; internal set; } = [];
    public string? HdrSummary { get; internal set; }
    /// <summary>Script generated from <see cref="JobSettings.Frameserver"/> (VapourSynth/AviSynth), when used.</summary>
    public string? FrameserverScript { get; internal set; }
    /// <summary>Whether VapourSynth reads the video (standard frameserver) and, when not, why (log, editor).</summary>
    public string? ScriptNote { get; internal set; }
    public JobStatus Status { get; internal set; } = JobStatus.Ready;
    public string? Message { get; internal set; }
    public string? Stage { get; internal set; }
    public MediaInfo? Media { get; internal set; }
    /// <summary>A KnA server's job (desktop app in client mode): the server's one-line description of the media.</summary>
    public string? RemoteSummary { get; internal set; }
    public string? ProbeError { get; internal set; }
    public string? OutputPath { get; internal set; }
    public string? CommandLine { get; internal set; }
    public EncodeProgress? Progress { get; internal set; }
    public DateTimeOffset? StartedAt { get; internal set; }
    public DateTimeOffset? FinishedAt { get; internal set; }

    /// <summary>Progress of the current step when it is not the video encode (OCR, downloads, joining…), 0–100; null = not measurable.</summary>
    public double? StagePercent { get; internal set; }

    public double Percent => Status == JobStatus.Done ? 100 : Progress?.Percent ?? StagePercent ?? 0;

    /// <summary>A step is running whose progress cannot be measured: the bar moves without a percentage.</summary>
    public bool StageIndeterminate => Status == JobStatus.Running && Stage is not null && Progress is null && StagePercent is null;

    /// <summary>A new step of the job: its name, and progress starting from nothing (the bar follows each step).</summary>
    internal void BeginStage(string name, double? percent = null)
    {
        Stage = name;
        Progress = null;
        StagePercent = percent;
    }

    public TimeSpan? Elapsed => StartedAt is { } s ? (FinishedAt ?? DateTimeOffset.Now) - s : null;

    public string Log { get { lock (_log) return _log.ToString(); } }

    internal void AppendLog(string line)
    {
        lock (_log)
        {
            if (_log.Length > MaxLogChars) _log.Remove(0, _log.Length - MaxLogChars / 2);
            _log.AppendLine(line);
        }
    }

    internal void ResetForRun()
    {
        Status = JobStatus.Ready;
        Message = null;
        Stage = null;
        Progress = null;
        StagePercent = null;
        OutputPath = null;
        CommandLine = null;
        StartedAt = FinishedAt = null;
        lock (_log) _log.Clear();
    }
}
