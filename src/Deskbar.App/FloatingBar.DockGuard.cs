using System.Runtime.InteropServices;
using System.Windows.Interop;
using CSharpFunctionalExtensions;
using Deskbar.Windows.Monitoring;

namespace Deskbar.App;

// KEEPING THE DOCKED STRIP. Petre, with a screenshot from an RDP session on another computer: "fullscreen not
// respected". The debug dump from that session (2.1.26) showed what was really wrong: the bar docked left at
// 0..258 px, its app bar registered as far as the bar knew, and the screen's work area still 0..1920, the full
// width. Windows was not holding the strip, so every maximized window ran under the bar. No fullscreen notice
// had arrived at all; "fullscreen" was the maximized windows.
//
// Two ways to that state are known, and the dump could not tell them apart: Explorer restarting (a new Explorer
// forgets every app bar, which AppBar now answers on TaskbarCreated), and the session's resolution changing
// (the dump had cloaked windows sized for 2560x1400, so it had changed) with the work area reset and not
// re-applied after the bar docked again. Rather than trust the registration, this checks the OUTCOME once a
// second: if the bar is docked on an edge and the work area of its screen still reaches under the strip, the
// registration is dropped and made afresh.
//
// Rate-limited, for a shell that will not honour an app bar at all: one attempt every RetryEvery, never within
// SettleFor of a dock (the shell may not have applied it yet), and each attempt traced, so a log of attempts
// that change nothing says the shell is the one refusing. Across the seam is left alone: its strips are on two
// screens and the check below speaks for one edge.
public partial class FloatingBar
{
    static readonly TimeSpan SettleFor = TimeSpan.FromSeconds(3);
    static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(10);

    // When the bar last docked or re-docked (DockTo sets it), and when this last re-registered. UTC, as durations
    // are all that is read from them.
    DateTime lastDocked = DateTime.MinValue;
    DateTime lastStripRepair = DateTime.MinValue;

    // How often the strip has had to be taken back since start, and when last, for the debug dump.
    int stripRepairs;

    // Called on the app's 1s timer (App, beside ReclaimTopmost).
    //
    // Never during a width drag. A bar dragged wider than its strip reaches past the work area by design, the
    // strip follows only when the drag ends, and this read that as the shell losing the strip: it re-registered
    // and re-docked under the pointer (measured, 23:19:29 on 2026-10-25, in the middle of Petre's "jumpy" resize).
    public void KeepDockedStrip()
    {
        if (dockedEdge is not ("left" or "right") || Minimized || !IsVisible || docking || resizing is not null || appBar is null) return;
        var now = DateTime.UtcNow;
        if (now - lastDocked < SettleFor || now - lastStripRepair < RetryEvery) return;
        if (StripState() is not { Held: false } state) return;

        lastStripRepair = now;
        stripRepairs++;
        ClickTrace.Write($"dock: the shell is not holding the {dockedEdge} strip (bar {DebugDump.Format(state.Bar)}, work {DebugDump.Format(state.Work)}), registering again");
        appBar.Remove();
        DockTo(dockedEdge).TapError(error => ClickTrace.Write($"dock: registering again failed: {error}"));
    }

    // The bar's rectangle and its screen's work area, both physical pixels, and whether the work area keeps clear
    // of the strip: null for no verdict (see the overload of StripHeld below). Null as a whole while the window or
    // its screen cannot be read.
    (NativeMethods.RECT Bar, NativeMethods.RECT Work, bool? Held)? StripState()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0 || dockedEdge is not ("left" or "right") || !NativeMethods.GetWindowRect(hwnd, out var bar)) return null;
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST), ref info)) return null;
        return (bar, info.rcWork, StripHeld(dockedEdge, bar, info.rcMonitor, info.rcWork));
    }

    // The verdict, but only from the screen the bar is ON: null when the screen Windows answered with is not the
    // one under the bar's centre. MONITOR_DEFAULTTONEAREST always answers, and when the bar's own screen has dropped
    // out it answers with ANOTHER one, whose work area says nothing about the bar's strip. Petre: "after a few
    // restarts and unlocks, bar ended up on another screen, docked to the edge". Measured at 00:23:13 on 2026-11-01:
    // a screen waking on unlock dropped out for a moment, the bar docked right on it at -395..0 was judged against
    // the primary's work area 0..3840, the guard registered again, and the shell handed it the primary's right
    // edge. The layout settled four seconds later on the same desk, so the readjustment kept it there. With no
    // verdict the guard waits: if the screen comes back the strip is still its own, and if it does not, the
    // display-layout readjustment docks the bar again on whatever screen it ends up on.
    internal static bool? StripHeld(string edge, NativeMethods.RECT bar, NativeMethods.RECT monitor, NativeMethods.RECT work) =>
        Contains(monitor, (bar.Left + bar.Right) / 2, (bar.Top + bar.Bottom) / 2) ? StripHeld(edge, bar, work) : null;

    static bool Contains(NativeMethods.RECT r, int x, int y) => x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;

    // The work area keeps clear of a strip on the left when it starts at or past the strip's right edge, and of one
    // on the right when it ends at or before the strip's left edge. "At or past" rather than "exactly at" because
    // something else docked on the same edge, further in, pushes the work area further still.
    internal static bool StripHeld(string edge, NativeMethods.RECT bar, NativeMethods.RECT work) => edge switch
    {
        "left" => work.Left >= bar.Right,
        "right" => work.Right <= bar.Left,
        _ => true,
    };

    // The dump's lines about it (FloatingBar.Diagnostics.cs).
    string DescribeStripForDump() =>
        (StripState() is { } state
            ? $"{state.Held switch { true => "yes", false => "NO", null => "can't tell, the bar is not on the screen measured" }} (work {DebugDump.Format(state.Work)})"
            : "n/a (not docked on an edge)")
        + $"; taken back {stripRepairs} time(s){(stripRepairs > 0 ? $", last {lastStripRepair.ToLocalTime():HH:mm:ss}" : "")}";
}
