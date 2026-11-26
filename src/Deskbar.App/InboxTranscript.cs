using Deskbar.Core.Tasks;

namespace Deskbar.App;

// THE POLL OF A PAGE'S TRANSCRIPT (the Inbox page's chat: Claude's messages and steps). It keeps one question answered: what
// does the session of the task whose page is open say now? Nothing else in the app reads a transcript for display.
//
// THE RULES (the PM's rulings, 2026-11-26, and the plan, section 3):
//   * Deskbar only READS the transcript. Nothing here starts, stops, resumes or writes to a session.
//   * ONLY WHILE A PAGE IS OPEN, ONLY FOR THAT TASK'S ONE SESSION (ClaudeChatSource): no page, no reader object, no timer.
//     Follow starts it, Stop (the page closed, the view left the Inbox, the window closed) ends it and forgets the items,
//     Pause and Resume cover the panel being hidden and shown again with its page still open.
//   * THE READ IS OFF THE UI THREAD, one at a time (the reader keeps state and is not thread safe), about every two
//     seconds: after a read has finished the next is scheduled, so a slow disk can never stack reads. The result comes back
//     to the UI thread, where it is dropped if the page has moved on meanwhile (a generation number).
//   * AN IDLE POLL IS CHEAP: ClaudeChatReader answers at once when the file's length has not changed, and a session that
//     has ended is read once and then only looked at again for the state to change (a resume makes it live).
//   * NOTHING FROM A TRANSCRIPT IS WRITTEN ANYWHERE: no cache file, no trace line. The items live in this object's memory
//     until Stop, and what the page draws from them is theirs.
//
// WHICH SESSION is asked on every tick, on the UI thread, from the board (the thread's own session states and the
// remembered transcript path), so a session that has just started (no file yet), one that ended and one that was resumed
// are all noticed at the next tick without a redraw of the page.
//
// The clock, the reader, the runner and the source are handed in, so every rule here is a test without a disk or a thread.
internal sealed class InboxTranscript
{
    /// <summary>The time between a read's end and the next read.</summary>
    internal static readonly TimeSpan Every = TimeSpan.FromSeconds(2);

    readonly Func<Guid, ChatSource> sourceOf;
    readonly Func<Func<string, ChatUpdate>> newReader;
    readonly Action<Func<ChatUpdate?>, Action<ChatUpdate?>> run;
    readonly Func<TimeSpan, Action, IDisposable> timer;
    readonly Action changed;
    IDisposable? waiting;
    Func<string, ChatUpdate>? reader;
    int generation;
    bool inFlight, paused, readOnce;

    /// <param name="sourceOf">Which transcript a task's page reads (TasksWindowActions.TranscriptOf); UI thread.</param>
    /// <param name="newReader">A new reader, whose Read is called off the UI thread, one call at a time.</param>
    /// <param name="run">Runs the first off the UI thread and hands its result to the second on the UI thread.</param>
    /// <param name="timer">A one-shot timer on the UI thread.</param>
    /// <param name="changed">Something the page draws has changed: new items, or another source.</param>
    public InboxTranscript(Func<Guid, ChatSource> sourceOf, Func<Func<string, ChatUpdate>> newReader,
        Action<Func<ChatUpdate?>, Action<ChatUpdate?>> run, Func<TimeSpan, Action, IDisposable> timer, Action changed)
    {
        this.sourceOf = sourceOf;
        this.newReader = newReader;
        this.run = run;
        this.timer = timer;
        this.changed = changed;
    }

    /// <summary>The task whose page is followed, none while nothing is.</summary>
    public Guid? Task { get; private set; }

    /// <summary>The transcript chosen at the last tick.</summary>
    public ChatSource Source { get; private set; } = ChatSource.Nothing;

    /// <summary>The chat as the last read left it; none before the first read.</summary>
    public ChatUpdate? Latest { get; private set; }

    /// <summary>For the tests: how many readers were made, and how many reads were started.</summary>
    internal int ReadersMade { get; private set; }

    internal int Reads { get; private set; }

    /// <summary>Whether a timer is waiting or a read is running: something of this object is alive.</summary>
    internal bool Alive => waiting is not null || inFlight;

    /// <summary>
    /// The page of <paramref name="task"/> is drawn. The same task again (the page was rebuilt) changes nothing; another
    /// task ends the first one's follow and starts a new one, with its first read at once.
    /// </summary>
    public void Follow(Guid task)
    {
        if (Task == task) return;
        Stop();
        Task = task;
        paused = false;
        Tick();
    }

    /// <summary>The page is gone: no timer, no read, and the items forgotten.</summary>
    public void Stop()
    {
        generation++;
        waiting?.Dispose();
        waiting = null;
        Task = null;
        Source = ChatSource.Nothing;
        Latest = null;
        reader = null;
        inFlight = false;
        readOnce = false;
    }

    /// <summary>The panel is hidden with its page open: no timer, no new read; the items stay for when it shows again.</summary>
    public void Pause()
    {
        if (Task is null) return;
        generation++;
        waiting?.Dispose();
        waiting = null;
        inFlight = false;
        paused = true;
    }

    /// <summary>The panel is shown again: a read at once.</summary>
    public void Resume()
    {
        if (Task is null || !paused) return;
        paused = false;
        Tick();
    }

    /// <summary>One look: which transcript now, and a read of it unless there is nothing new to read. Public for the tests.</summary>
    public void Tick()
    {
        waiting?.Dispose();
        waiting = null;
        if (Task is not { } task || paused) return;
        var source = sourceOf(task);
        if (source != Source)
        {
            // Another session or file: what was read belongs to the old one. The same session going from running to ended
            // keeps its items (its file is the same), so only the file or the session counts.
            if (source.Path != Source.Path || source.SessionId != Source.SessionId)
            {
                reader = null;
                Latest = null;
                readOnce = false;
                generation++;
                inFlight = false;
            }
            Source = source;
            changed();
        }
        if (source.Path is null || source.Kind == ChatSourceKind.None || source.Kind == ChatSourceKind.Expired || inFlight
            || source is { Kind: ChatSourceKind.Ended } && readOnce)
        {
            Schedule();
            return;
        }
        reader ??= MakeReader();
        var (read, path, mine) = (reader, source.Path, generation);
        inFlight = true;
        Reads++;
        run(() =>
        {
            try { return read(path); }
            catch (Exception) { return null; }
        }, update =>
        {
            if (mine != generation) return;
            inFlight = false;
            readOnce = true;
            if (update is not null)
            {
                var first = Latest is null;
                Latest = update;
                if (update.Changed || first) changed();
            }
            Schedule();
        });
    }

    Func<string, ChatUpdate> MakeReader()
    {
        ReadersMade++;
        return newReader();
    }

    void Schedule()
    {
        if (Task is null || paused) return;
        waiting = timer(Every, Tick);
    }
}
