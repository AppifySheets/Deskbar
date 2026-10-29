using Deskbar.Core.Domain;

namespace Deskbar.Core.Overview;

// THE WINDOWS IN FRONT ON ANOTHER SCREEN, for the bar's "fronted" marker (FloatingBar.Marker.cs). Petre: "in 2monitor
// setup, can you identify the window that's on top on ANOTHER screen, not the one that's active?", "mark it, too",
// then "b" from the mockups, and "let's do it on collapsed bars as well".
//
// Per row, two kinds of window are already known: the one you are in (IsActive, the current workspace's row) or the
// one a switch would bring forward (WillActivate, every other row). Call it the row's anchor. The fronted windows are
// the ones in front on every OTHER screen: what you see there now, or what you will see there once you switch.
//
// "In front on its screen" is the overview's own IsFrontmostOnMonitor, which comes from one z-order sweep per rebuild
// (ScreenLayout). That sweep keeps the windows of desktops you are not on, cloaked by the shell, in the z-order list
// Windows keeps for every window, so the question has an answer on every desktop, not only the current one. Measured
// on Petre's machine on 2026-10-29: the sweep listed every workspace's windows, cloaked ones with their monitors, and
// a probe that predicted each workspace's front windows before a switch and read them after it agreed (see the PR).
//
// A row with no anchor marks nothing: without one there is no "other" screen to speak of, and marking the front window
// of every screen would also mark the one you are looking at. Nor does a row with no z-order (IsFrontmostOnMonitor
// None), or a window on no known monitor. A minimized window is never in front, whatever the order says.
//
// The active window can be one pinned to every desktop, and the overview keeps those in Overview.Pinned, not in any
// group, while the bar draws them into the current row. Petre, with a pinned Teams meeting active on one screen and
// nothing marked on the other: "why second is not active?" The current group had no active window of its own, so it
// had no anchor. So the current group's anchor is the active window wherever the overview keeps it, pinned included.
public static class FrontsElsewhere
{
    public static IReadOnlySet<WindowHandle> Of(Overview overview)
    {
        var activePinned = overview.Pinned.FirstOrDefault(r => r.IsActive);
        return overview.Workspaces.Select(group => InRow(group.Running, group.IsCurrent ? activePinned : null))
            .Concat(overview.OtherDesktops.Select(desktop => InRow(desktop.Windows, desktop.IsCurrent ? activePinned : null)))
            .SelectMany(handles => handles)
            .ToHashSet();
    }

    // `activeElsewhere`: the active window when the overview keeps it outside these rows (a pinned one), for the
    // current group only.
    internal static IEnumerable<WindowHandle> InRow(IReadOnlyList<WindowRow> rows, WindowRow? activeElsewhere = null)
    {
        var anchor = rows.FirstOrDefault(r => r.IsActive) ?? activeElsewhere ?? rows.FirstOrDefault(r => r.WillActivate);
        if (anchor is null || anchor.Monitor.HasNoValue) return [];
        return rows
            .Where(r => r != anchor
                        && !r.IsMinimized
                        && r.IsFrontmostOnMonitor.GetValueOrDefault(false)
                        && r.Monitor.HasValue
                        && r.Monitor.Value != anchor.Monitor.Value)
            .Select(r => r.Window.Handle);
    }
}
