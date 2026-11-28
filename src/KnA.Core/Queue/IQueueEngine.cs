using KnA.Core.Encoding;

namespace KnA.Core.Queue;

/// <summary>
/// The queue the desktop app shows: this computer's (<see cref="QueueService"/>) or a KnA server's
/// (<see cref="Remote.RemoteQueue"/>), with the same jobs, events and operations.
/// </summary>
public interface IQueueEngine
{
    IReadOnlyList<QueueJob> Jobs { get; }
    bool IsRunning { get; }
    bool IsPaused { get; }
    QueueJob? CurrentJob { get; }

    event Action<QueueJob>? JobChanged;
    event Action<Guid>? JobRemoved;
    event Action? QueueChanged;

    void Remove(IEnumerable<Guid> ids);
    void Reset(IEnumerable<Guid> ids);
    void Move(Guid id, int delta);
    void UpdateSettings(IEnumerable<Guid> ids, Func<JobSettings, JobSettings> update);
    void RemoveByStatus(params JobStatus[] statuses);
}
