using System.Collections.Concurrent;

namespace SnapShotKit.Daemon;

/// <summary>
/// Runs capture work on one dedicated foreground thread.
///
/// This exists because of an unexplained but perfectly reproducible failure: when a grab is invoked
/// from a .NET thread pool thread, the PipeWire stream reaches STREAMING and the compositor then
/// delivers no buffer at all, with no error anywhere. Invoked from a dedicated foreground thread it
/// works every time. The native calls happen on the same libpipewire thread in both cases, so the
/// mechanism is not understood. See docs/spikes/005-thread-pool-capture-failure.md.
///
/// D-Bus handlers run on pool threads, which is why the daemon cannot simply call the engine.
/// </summary>
public sealed class CaptureThread : IDisposable
{
    readonly BlockingCollection<Action> queue = new();
    readonly Thread thread;

    public CaptureThread()
    {
        thread = new Thread(Run)
        {
            Name = "snapshotkit-capture",
            IsBackground = false
        };

        thread.Start();
    }

    void Run()
    {
        foreach (var work in queue.GetConsumingEnumerable())
        {
            work();
        }
    }

    public Task<T> RunAsync<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        queue.Add(() =>
        {
            try
            {
                completion.SetResult(work());
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });

        return completion.Task;
    }

    public void Dispose() => queue.CompleteAdding();
}
