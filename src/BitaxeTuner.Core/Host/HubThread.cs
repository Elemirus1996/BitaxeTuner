using System.Collections.Concurrent;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.Core.Host;

/// <summary>
/// Eigener Thread mit <see cref="SynchronizationContext"/> für den <see cref="MinerHub"/> ohne Oberfläche
/// (Server, Tests). Alle Fortsetzungen laufen nacheinander auf diesem Thread – dieselben Bedingungen wie
/// auf dem UI-Thread der Desktop-App, damit der Hub keine eigenen Sperren braucht.
/// </summary>
public sealed class HubThread : IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
    private readonly Thread _thread;
    private readonly Context _context;

    public HubThread(string name = "BitaxeTuner-Hub")
    {
        _context = new Context(this);
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.Start();
    }

    public SynchronizationContext SynchronizationContext => _context;

    /// <summary>Aktion auf dem Hub-Thread ausführen und auf ihr Ende (inkl. await-Fortsetzungen) warten.</summary>
    public Task<T> RunAsync<T>(Func<Task<T>> action)
    {
        if (_queue.IsAddingCompleted) return Task.FromException<T>(new ObjectDisposedException(nameof(HubThread)));
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _context.Post(async _ =>
        {
            try { tcs.SetResult(await action()); }
            catch (Exception ex) { tcs.SetException(ex); }
        }, null);
        return tcs.Task;
    }

    public Task RunAsync(Func<Task> action) => RunAsync(async () => { await action(); return true; });

    public Task<T> RunAsync<T>(Func<T> action) => RunAsync(() => Task.FromResult(action()));

    private void Run()
    {
        SynchronizationContext.SetSynchronizationContext(_context);
        foreach (var (callback, state) in _queue.GetConsumingEnumerable())
        {
            try { callback(state); }
            catch (Exception ex) { Console.Error.WriteLine(L.T("[Hub] Unbehandelter Fehler: {0}", ex)); }
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        if (Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(5));
    }

    private sealed class Context(HubThread owner) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            if (!owner._queue.IsAddingCompleted) owner._queue.Add((d, state));
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (Thread.CurrentThread == owner._thread) { d(state); return; }
            using var done = new ManualResetEventSlim();
            Exception? error = null;
            Post(_ => { try { d(state); } catch (Exception ex) { error = ex; } finally { done.Set(); } }, null);
            done.Wait();
            if (error is not null) throw error;
        }

        public override SynchronizationContext CreateCopy() => this;
    }
}
