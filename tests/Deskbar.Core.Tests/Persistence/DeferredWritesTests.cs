using System.Collections.Concurrent;
using Deskbar.Core.Persistence;
using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests;

// DeferredWrites: saves leave the UI thread, the newest text per file wins, an older text never lands after a newer
// one, Flush waits for the disk, and a failed write is traced instead of thrown (task ea01fe71).
public sealed class DeferredWritesTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), $"deskbar-tests-{Guid.NewGuid():N}");

    public void Dispose() { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }

    static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public void Write_returns_before_the_disk_and_Flush_waits_for_it()
    {
        using var gate = new ManualResetEventSlim();
        var written = new ConcurrentQueue<(string Path, string Text)>();
        using var saves = new DeferredWrites((path, text) => { gate.Wait(Wait); written.Enqueue((path, text)); });

        saves.Write("a", "1"); // would block for 10 s if it wrote here

        Assert.False(saves.Flush(TimeSpan.FromMilliseconds(50)));
        gate.Set();
        Assert.True(saves.Flush(Wait));
        Assert.Equal([("a", "1")], written.ToArray());
    }

    // Three changes during one slow write end in ONE more write, of the last text: the middle one is never written and
    // the first is never written after the last.
    [Fact]
    public void Only_the_newest_waiting_text_per_file_is_written_and_in_order()
    {
        using var firstStarted = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var written = new ConcurrentQueue<string>();
        using var saves = new DeferredWrites((_, text) =>
        {
            firstStarted.Set();
            release.Wait(Wait);
            written.Enqueue(text);
        });

        saves.Write("state.json", "v1");
        Assert.True(firstStarted.Wait(Wait));
        saves.Write("state.json", "v2");
        saves.Write("state.json", "v3");
        release.Set();

        Assert.True(saves.Flush(Wait));
        Assert.Equal(["v1", "v3"], written.ToArray());
    }

    [Fact]
    public void Each_file_keeps_its_own_newest_text()
    {
        var written = new ConcurrentDictionary<string, string>();
        using var saves = new DeferredWrites((path, text) => written[path] = text);

        saves.Write("state.json", "s");
        saves.Write("tasks.json", "t");

        Assert.True(saves.Flush(Wait));
        Assert.Equal("s", written["state.json"]);
        Assert.Equal("t", written["tasks.json"]);
    }

    // An exception on the writer thread would end the process: it is traced, and the next write still happens.
    [Fact]
    public void A_failed_write_is_traced_and_the_next_one_still_lands()
    {
        var traced = new ConcurrentQueue<string>();
        var written = new ConcurrentQueue<string>();
        using var saves = new DeferredWrites((_, text) =>
        {
            if (text == "bad") throw new IOException("disk full");
            written.Enqueue(text);
        }, traced.Enqueue);

        saves.Write("state.json", "bad");
        Assert.True(saves.Flush(Wait));
        saves.Write("state.json", "good");
        Assert.True(saves.Flush(Wait));

        Assert.Equal(["good"], written.ToArray());
        Assert.Contains(traced, line => line.Contains("disk full") && line.Contains("state.json"));
    }

    // After Dispose (the app's Exit), a late save is not lost: it is written on the caller's thread.
    [Fact]
    public void A_write_after_Dispose_is_written_at_once()
    {
        var written = new ConcurrentQueue<string>();
        var saves = new DeferredWrites((_, text) => written.Enqueue(text));
        saves.Write("state.json", "before");
        saves.Dispose();
        saves.Write("state.json", "after");

        Assert.Equal(["before", "after"], written.ToArray());
    }

    // The stores still serialize on the caller and hand the text over: through DeferredWrites with DurableFile.Write,
    // a saved board reads back once flushed.
    [Fact]
    public void A_task_store_writing_through_it_reads_back_after_Flush()
    {
        using var saves = new DeferredWrites(DurableFile.Write);
        var board = TaskBoard.Empty;
        var store = new JsonTaskStore(dir, write: saves.Write);

        Assert.True(store.Save(board).IsSuccess);
        Assert.True(saves.Flush(Wait));

        Assert.True(new JsonTaskStore(dir).Load().IsSuccess);
        Assert.True(File.Exists(Path.Combine(dir, "tasks.json")));
    }
}
