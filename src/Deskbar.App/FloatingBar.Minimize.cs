using System.Reactive.Linq;
using Deskbar.Core.Domain;
using Deskbar.Core.Overview;

namespace Deskbar.App;

// THE ICON CATCHES UP WITH A MINIMIZE OR RESTORE AS SOON AS IT HAPPENS. Petre, minimizing VS Code from its icon and
// clicking it again: "vscode window came back late", then "it brings the window back". The window came back at
// once; the BAR went on drawing its icon dimmed as minimized, for five and a half seconds that time.
//
// Measured, with the trace lines at the bottom of this file, over a dozen clicks:
//
//   16:26:57.190 icon-click app=Code                          <- minimize
//   16:26:57.222 drawn minimized=[]                            <- the click's own redraw: too early, not iconic yet
//   16:26:57.257 minimize event iconic=False                   <- Windows' event, ALSO before the window is iconic
//   16:26:58.131 up ...                                        <- nothing in between: the icon stays bright
//   16:26:58.140 minimize event iconic=True rebuilding=True    <- delivered only when the NEXT click pumped messages
//   16:26:58.155 drawn minimized=[A0856]                       <- dimmed, just as the restore is about to run
//   16:26:58.172 icon-click app=Code                          <- restore: and then no event, no redraw at all
//
// So neither source can be waited on. The click's own redraw comes before ShowWindowAsync has done anything, and the
// minimize events reach this thread late, sometimes not until the next input. Every click delivered the previous
// click's news, which looks right when clicking fast and is exactly the report when clicking once and waiting.
//
// So the click asks Windows itself: from the moment of the click, IsIconic every 40ms until it differs from what it
// was before the click, then one redraw. Bounded at 1.6s, the length of the slowest minimize animation measured
// above (0.95s) with room to spare; a window that never flips (a jump that did not toggle anything) gets no redraw,
// because there is nothing new to draw. Started for EVERY icon click, and a new click replaces the watch of the
// last. It was first started only for a click that looked able to toggle (the window minimized, or its row marked
// focused), and the next trace showed why that cannot work: Beeper's row was not yet marked focused when a click
// minimized it, because the row is a snapshot older than the click (the reason ToggleWindow decides from live facts,
// see there). Forty IsIconic calls cost nothing; a missed redraw is the bug.
public partial class FloatingBar
{
    static readonly TimeSpan SettlePoll = TimeSpan.FromMilliseconds(40);
    const int SettlePolls = 40;

    IDisposable? settleWatch;

    // Called by the icon's click, BEFORE the toggle runs, so `wasMinimized` is the state the click set out to change.
    void RedrawWhenToggleSettles(WindowRow row, bool wasMinimized)
    {
        var window = row.Window.Handle;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        settleWatch?.Dispose();
        settleWatch = Observable.Interval(SettlePoll)
            .Take(SettlePolls)
            // Back on the UI thread for both the question and the redraw: the bar is not thread safe, and IsIconic
            // costs nothing.
            .ObserveOn(SynchronizationContext.Current!)
            .Select(_ => activator.IsMinimized(window))
            .Where(now => now != wasMinimized)
            .Take(1)
            .Subscribe(now =>
            {
                if (ClickTrace.On)
                    ClickTrace.Write($"toggle settled hwnd={window.Value:X} minimized={now} after {clock.ElapsedMilliseconds}ms");
                if (IsVisible) Rebuild();
            });
    }

    // TRACE LINES, all behind the trace switch. The minimize event as Windows delivers it, with what IsIconic says at
    // that moment; and the set of windows the bar DRAWS as minimized, written only when it changes, like the band and
    // geometry dumps, so the log stays readable with tracing on all day. (The third, a redraw postponed because a
    // button was down over the bar, is in Rebuild.)

    // From the composition root, for every minimize and restore of a window the monitor knows.
    internal void NoteMinimizeEvent(WindowHandle window)
    {
        if (ClickTrace.On)
            ClickTrace.Write($"minimize event hwnd={window.Value:X} iconic={activator.IsMinimized(window)} rebuilding={rebuilding} pending={rebuildRequested}");
    }

    // The drawn state, from a FRESH overview only: a re-layout reuses the last one and says nothing new.
    void DumpMinimized(Overview overview)
    {
        if (!ClickTrace.On) return;

        var rows = overview.Pinned
            .Concat(overview.Workspaces.SelectMany(g => g.Running))
            .Concat(overview.OtherDesktops.SelectMany(d => d.Windows))
            .ToList();
        var minimized = rows.Where(r => r.IsMinimized).Select(r => $"{r.Window.Handle.Value:X}").Distinct().Order();
        var focused = rows.FirstOrDefault(r => r.IsActive);
        var line = $"drawn minimized=[{string.Join(", ", minimized)}] focused=" +
                   (focused is null ? "none" : $"{focused.Window.Handle.Value:X} minimized={focused.IsMinimized} front={focused.IsFrontmostOnMonitor}");

        if (line == lastMinimizedDump) return;
        lastMinimizedDump = line;
        ClickTrace.Write(line);
    }

    string lastMinimizedDump = "";
}
