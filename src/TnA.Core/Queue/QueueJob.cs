using System.Text;
using TnA.Core.Encoding;
using TnA.Core.Media;
using TnA.Core.Processing;

namespace TnA.Core.Queue;

public enum JobStatus { Ready, Running, Paused, Done, Warning, Error, Stopped }

public sealed class QueueJob
{
    private readonly StringBuilder _log = new();
    private const int MaxLogChars = 400_000;

    public Guid Id { get; } = Guid.NewGuid();
    public required string SourcePath { get; init; }
    public string FileName => Path.GetFileName(SourcePath);
    public string Folder => Path.GetDirectoryName(SourcePath) ?? "";

    public JobSettings Settings { get; set; } = new();
    public JobStatus Status { get; internal set; } = JobStatus.Ready;
    public string? Message { get; internal set; }
    public string? Stage { get; internal set; }
    public MediaInfo? Media { get; internal set; }
    public string? ProbeError { get; internal set; }
    public string? OutputPath { get; internal set; }
    public string? CommandLine { get; internal set; }
    public EncodeProgress? Progress { get; internal set; }
    public DateTimeOffset? StartedAt { get; internal set; }
    public DateTimeOffset? FinishedAt { get; internal set; }

    public double Percent => Status == JobStatus.Done ? 100 : Progress?.Percent ?? 0;

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
        OutputPath = null;
        CommandLine = null;
        StartedAt = FinishedAt = null;
        lock (_log) _log.Clear();
    }
}
