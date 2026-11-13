using System.Diagnostics;
using KnA.Core.Processing;

namespace KnA.Core.Queue;

/// <summary>The child processes of the running job, so they can be paused/resumed/killed together.</summary>
public sealed class ProcessGroup
{
    private readonly List<Process> _processes = [];
    private bool _paused;

    public bool IsPaused => _paused;

    public void Add(Process p)
    {
        lock (_processes)
        {
            _processes.Add(p);
            if (_paused) ProcessControl.Suspend(p);
        }
    }

    public void Remove(Process p)
    {
        lock (_processes) _processes.Remove(p);
    }

    public void Pause()
    {
        lock (_processes)
        {
            _paused = true;
            foreach (var p in _processes) Try(() => ProcessControl.Suspend(p));
        }
    }

    public void Resume()
    {
        lock (_processes)
        {
            _paused = false;
            foreach (var p in _processes) Try(() => ProcessControl.Resume(p));
        }
    }

    public void KillAll()
    {
        lock (_processes)
        {
            foreach (var p in _processes)
            {
                // A suspended process must be resumed before it can die cleanly on some platforms.
                if (_paused) Try(() => ProcessControl.Resume(p));
                ProcessControl.KillTree(p);
            }
            _paused = false;
        }
    }

    private static void Try(Action a)
    {
        try { a(); } catch (Exception) { /* process already gone */ }
    }
}
