using JockeyBox.Tools;

namespace JockeyBox.Data;

/// <summary>
/// Coalescing, debounced background writer. A caller hands over a key plus the
/// work that persists that key; repeated calls inside the debounce window
/// collapse into one run of the newest work item per key.
///
/// <para>Used by <see cref="PlaylistStore"/> so a rename or a single-row move
/// does not rewrite the whole object graph on the UI thread, while
/// <see cref="Flush()"/> still lets a test (or a clean shutdown) guarantee the
/// work has landed.</para>
///
/// <para>The work runs on a dedicated background thread, one item at a time, in
/// arrival order. A throwing item is logged and does not stop the queue.</para>
/// </summary>
public sealed class WriteQueue : IDisposable
{
    /// <summary>Quiet period after the last enqueue before the batch runs.</summary>
    public static readonly TimeSpan DefaultDelay = TimeSpan.FromMilliseconds(350);

    private readonly object _gate = new();
    private readonly Dictionary<string, Action> _pending = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly Thread _thread;
    private readonly TimeSpan _delay;

    private bool _disposed;
    private bool _stopping;
    private int _runs;

    public WriteQueue(TimeSpan? delay = null)
    {
        _delay = delay ?? DefaultDelay;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "JockeyBox.WriteQueue",
        };
        _thread.Start();
    }

    /// <summary>How many times the batch runner has executed (diagnostics/tests).</summary>
    public int Runs => Volatile.Read(ref _runs);

    /// <summary>Number of keys waiting to be written.</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate) return _pending.Count;
        }
    }

    /// <summary>
    /// Queue <paramref name="work"/> under <paramref name="key"/>. A newer item
    /// for the same key replaces the older one; the newest work must be able to
    /// describe the full current state of that key.
    /// </summary>
    public void Enqueue(string key, Action work)
    {
        if (work is null) return;

        lock (_gate)
        {
            if (_disposed) return;

            if (!_pending.ContainsKey(key)) _order.Enqueue(key);
            _pending[key] = work;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// Run everything queued right now on the calling thread and wait for any
    /// in-flight batch to finish. Safe to call from the UI thread at shutdown or
    /// from a test; does nothing when the queue is empty.
    /// </summary>
    public void Flush()
    {
        while (true)
        {
            Batch batch;
            lock (_gate)
            {
                // At most one batch runs at a time: the worker marks _running
                // while it executes, so a concurrent flush waits instead of
                // applying an older snapshot after a newer one.
                while (_running) Monitor.Wait(_gate, 100);
                if (_pending.Count == 0) return;
                batch = Take();
            }

            Run(batch);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _stopping = true;
            Monitor.PulseAll(_gate);
        }

        Flush();

        lock (_gate)
        {
            _disposed = true;
            Monitor.PulseAll(_gate);
        }

        // The thread is a background thread: if it is mid-item we do not join
        // forever, but the flush above already ran everything queued.
        _thread.Join(TimeSpan.FromSeconds(5));
    }

    private bool _running;

    private Batch Take()
    {
        var items = new List<Action>(_pending.Count);
        while (_order.Count > 0)
        {
            var key = _order.Dequeue();
            if (_pending.Remove(key, out var work)) items.Add(work);
        }

        _running = items.Count > 0;
        return new Batch(items);
    }

    private void Run(Batch batch)
    {
        foreach (var work in batch.Items)
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                Log.Error("Deferred write failed", ex);
            }
        }

        if (batch.Items.Count > 0) Interlocked.Increment(ref _runs);

        lock (_gate)
        {
            _running = false;
            Monitor.PulseAll(_gate);
        }
    }

    private void Loop()
    {
        while (true)
        {
            Batch batch;
            lock (_gate)
            {
                while (_pending.Count == 0 && !_stopping) Monitor.Wait(_gate);
                if (_pending.Count == 0) return; // stopping and drained

                // Debounce: a new enqueue pulses us and restarts the wait.
                var deadline = DateTime.UtcNow + _delay;
                while (!_stopping)
                {
                    var left = deadline - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero) break;
                    Monitor.Wait(_gate, left);
                    deadline = DateTime.UtcNow + _delay;
                }

                if (_pending.Count == 0) continue;
                batch = Take();
            }

            Run(batch);
        }
    }

    private readonly record struct Batch(IReadOnlyList<Action> Items);
}
