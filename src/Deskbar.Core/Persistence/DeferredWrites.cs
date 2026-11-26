namespace Deskbar.Core.Persistence;

// Saves off the UI thread, so a busy disk cannot hold up a click (task ea01fe71, "deskbar is super sluggish when
// resources are tough; needs a breather").
//
// state.json and tasks.json are written on every change, and the write is DurableFile.Write: the bytes flushed to the
// disk, then swapped in. That costs about 6 ms on an idle machine (DurableFile's header). MEASURED on Petre's machine
// with the CPU at 100% and memory at 97%: 8 state saves in 20 s took 385 ms, one of them 130 ms, and 3 task saves took
// 218 ms, one of them 105 ms, all on the UI thread, so the bar stood still for each.
//
// What this does: the caller still SERIALIZES on its own thread (so the text is a snapshot of the state at the moment
// of the change, and no other thread ever reads the app's objects), and hands the text here. One background thread
// writes it with the same DurableFile.Write. Per file only the newest text waits: three changes during one slow write
// end in one more write, of the last state, and an older text can never land after a newer one because a single
// thread writes them in turn.
//
// What it costs: a change is on the disk a few milliseconds later than before, instead of when Save returns. A crash in
// that gap loses that one change, which the old code also lost to a crash a moment earlier. Every orderly way out
// (Exit, sign-out, the handover to an update or a restart) calls Flush first, so a successor never reads an old file.
// A failed write is traced here; the caller's Result says only that the text was queued.
public sealed class DeferredWrites : IDisposable
{
    readonly Action<string, string> write;
    readonly Action<string>? trace;
    readonly object gate = new();
    // The newest text per path that is waiting to be written. Insertion order is not kept, and does not need to be:
    // each path is its own file.
    readonly Dictionary<string, string> waiting = [];
    // True while the writer thread is between taking a batch and finishing it. Flush waits for this AND an empty queue.
    bool busy;
    bool disposed;
    // Set by the writer thread, under the lock, as it returns: after Dispose, with nothing left to write.
    bool exited;

    public DeferredWrites(Action<string, string> write, Action<string>? trace = null)
    {
        this.write = write;
        this.trace = trace;
        // A thread of its own, not the thread pool: the pool is slow to grow under exactly the CPU load this is for,
        // and a save queued behind it would wait. Background, so it never keeps the process alive.
        new Thread(Run) { IsBackground = true, Name = "Deskbar saves" }.Start();
    }

    // Queues `content` for `path`, replacing any text still waiting for the same path. Never blocks on the disk.
    public void Write(string path, string content)
    {
        lock (gate)
        {
            // Once the writer thread has gone (Dispose, and the queue drained) there is no thread to hand it to, so the
            // write happens here, on the caller: late saves during shutdown must still reach the disk. While the thread
            // is still alive (a Dispose whose Flush timed out on a stuck disk) the text queues as usual instead: a
            // write here would race the thread's own on the same .tmp file, and the thread could then land an older
            // queued text over this newer one.
            if (exited)
            {
                WriteOne(path, content);
                return;
            }

            waiting[path] = content;
            Monitor.PulseAll(gate);
        }
    }

    // Waits until everything queued before the call is on the disk, or the timeout passes. True when it all landed.
    public bool Flush(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        lock (gate)
        {
            while (waiting.Count > 0 || busy)
            {
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || !Monitor.Wait(gate, left)) return waiting.Count == 0 && !busy;
            }

            return true;
        }
    }

    // Flushes, then stops the thread and waits (bounded) for it to go. A Write after that writes synchronously; one
    // that arrives while the thread is still busy with a stuck disk queues behind it (see Write).
    public void Dispose()
    {
        Flush(TimeSpan.FromSeconds(10));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        lock (gate)
        {
            disposed = true;
            Monitor.PulseAll(gate);
            while (!exited)
            {
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || !Monitor.Wait(gate, left)) return;
            }
        }
    }

    void Run()
    {
        while (true)
        {
            KeyValuePair<string, string>[] batch;
            lock (gate)
            {
                while (waiting.Count == 0 && !disposed) Monitor.Wait(gate);
                if (waiting.Count == 0)
                {
                    exited = true; // disposed and drained: Write writes on its caller from now on
                    Monitor.PulseAll(gate);
                    return;
                }
                batch = [.. waiting];
                waiting.Clear();
                busy = true;
            }

            // Outside the lock, so a Write arriving now queues at once instead of waiting for the disk.
            Array.ForEach(batch, pair => WriteOne(pair.Key, pair.Value));

            lock (gate)
            {
                busy = false;
                Monitor.PulseAll(gate); // wakes Flush
            }
        }
    }

    // NOTHING THROWS OUT OF HERE: an exception on this thread would end the process. A failed write is traced, and the
    // next change to the same file writes it whole again.
    void WriteOne(string path, string content)
    {
        try { write(path, content); }
        catch (Exception e) { trace?.Invoke($"saves: could not write {path}: {e.Message}"); }
    }
}
