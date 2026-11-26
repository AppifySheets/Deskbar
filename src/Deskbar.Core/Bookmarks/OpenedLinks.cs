using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Bookmarks;

// The browser window a link went into: its handle, and the process that owned it when it was noted. The process is
// kept so a handle Windows hands out again to some other program's window is not taken for the browser's.
public sealed record LinkWindow(WindowHandle Handle, int ProcessId);

// What the app hands BookmarkOpener so a Link chip can be drawn open (docs/superpowers/specs/2026-11-26-workspace-
// bookmarks-design.md, "Opening", option B: "a link counts as open while the browser window it went into stays open").
// Core has no clock and no Win32, so all three come from outside:
//
//   Watch  given the desktop of the bookmark's workspace, the window the link went into, or nothing. Started just
//          before the link is opened and ended by the first answer (LinkWindowWatch.Receiver is the rule the app
//          builds it from). Never starts or opens anything: it only listens.
//   Alive  whether a noted window still exists and still belongs to the same process (IsWindow plus the process id).
//          Asked when a chip is drawn and when `bookmark list` answers, so a window closed since is never drawn open.
//   Gone   the window list's "gone for real" (WindowMonitor's Disappeared, which keys off IsWindow, never off a window
//          missing from the candidate list), so a note does not outlive its window in memory.
public sealed record LinkWindows(
    Func<Guid, IObservable<LinkWindow>> Watch,
    Func<LinkWindow, bool> Alive,
    IObservable<WindowHandle> Gone);

// Which browser window each opened link went into: bookmark id -> window. In memory only. A restart starts with every
// link chip at rest and nothing about it is written to state.json, because a window handle means nothing to the next
// run.
//
// Several links can share one window (Open all opening two links into the same browser window notes it for both), so
// a window that goes takes every note on it. A note for a link opened again is replaced by the newer window.
public sealed class OpenedLinks
{
    ImmutableDictionary<Guid, LinkWindow> notes = ImmutableDictionary<Guid, LinkWindow>.Empty;
    readonly Subject<Unit> changed = new();

    // Pulses when a note is added, replaced or dropped, and only then: the bar redraws its rows on it.
    public IObservable<Unit> Changed => changed.AsObservable();

    // The bookmarks with a note, alive or not.
    public IReadOnlyCollection<Guid> Noted => notes.Keys.ToList();

    public void Note(Guid bookmarkId, LinkWindow window) => Become(notes.SetItem(bookmarkId, window));

    public void Forget(WindowHandle window) =>
        Become(notes.RemoveRange(notes.Where(note => note.Value.Handle == window).Select(note => note.Key)));

    public void Forget(Guid bookmarkId) => Become(notes.Remove(bookmarkId));

    // Open while noted AND the noted window still lives: a window that closed between the gone-for-real sweeps (the
    // window list repairs itself every 5 s) is never reported open.
    public bool IsOpen(Guid bookmarkId, Func<LinkWindow, bool> alive) =>
        notes.TryGetValue(bookmarkId, out var window) && alive(window);

    // ImmutableDictionary hands back the same instance when a change changes nothing (the same note again, a forget of
    // something never noted), which is how a no-op is told apart without comparing contents.
    void Become(ImmutableDictionary<Guid, LinkWindow> next)
    {
        if (ReferenceEquals(next, notes)) return;
        notes = next;
        changed.OnNext(Unit.Default);
    }
}
