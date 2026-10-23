using System.Runtime.InteropServices;
using System.Windows.Interop;
using CSharpFunctionalExtensions;
using TaskSpaces.Windows.Monitoring;

namespace TaskSpaces.App;

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
    public void KeepDockedStrip()
    {
        if (dockedEdge is not ("left" or "right") || Minimized || !IsVisible || docking || appBar is null) return;
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
    // of the strip. Null while the window or its screen cannot be read.
    (NativeMethods.RECT Bar, NativeMethods.RECT Work, bool Held)? StripState()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0 || dockedEdge is not ("left" or "right") || !NativeMethods.GetWindowRect(hwnd, out var bar)) return null;
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST), ref info)) return null;
        return (bar, info.rcWork, StripHeld(dockedEdge, bar, info.rcWork));
    }

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
            ? $"{(state.Held ? "yes" : "NO")} (work {DebugDump.Format(state.Work)})"
            : "n/a (not docked on an edge)")
        + $"; taken back {stripRepairs} time(s){(stripRepairs > 0 ? $", last {lastStripRepair.ToLocalTime():HH:mm:ss}" : "")}";
}
