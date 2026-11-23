using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using Deskbar.Core.Tasks;
using Deskbar.Windows.Monitoring;

namespace Deskbar.App;

// THE PANEL'S WIDTH GRIP (spec docs/superpowers/specs/2026-11-23-tasks-panel-design.md, "The panel window"; plan
// 2026-11-23-tasks-panel, Task 6). A 6 DIP Thumb (TasksWindow.xaml, "Grip") on the panel's INNER edge, the one facing
// the windows, away from the bar: the left edge while the panel docks on the right, the right edge while it docks on the
// left. TasksPanel knows the edge and says it (TasksPanel.Edge); TasksBoard hands it here (SetInnerEdge).
//
// A DRAG PREVIEWS, ITS END COMMITS. While the grip is dragged the window itself is resized in physical pixels, its outer
// edge (the one against the bar) held still, so Petre sees the width he is choosing. The shell's reservation does not
// follow during the drag: only at its end is the width handed to the controller (TasksWindowActions.ResizePanel), which
// saves it and has TasksPanel re-dock at it, in place, the way the bar's own reservation follows a width drag only once
// it ends (FloatingBar's width drag). A cancelled drag (Esc, a lost capture) puts the window back as it was.
//
// PIXELS FROM THE CURSOR, NOT THE THUMB'S DELTAS. The thumb moves with the edge it sits on, so its HorizontalChange is
// measured against a moving origin; the cursor's screen position against the one at the press is not. Both are physical
// pixels (the app is per-monitor DPI aware), as GetWindowRect and SetWindowPos are, so no DIP conversion happens until
// the width is handed over, at the window's own scale.
//
// The preview keeps the width between the panel's minimum (TasksPanelLayout.MinWidth) and half the window's screen,
// TasksPanelLayout.Clamp's own bounds, so what Petre sees while dragging is what the dock will grant.
public partial class TasksWindow
{
    // The press: where the cursor was, the window's rectangle then, the window's scale and the screen's width, physical
    // pixels. Null while no drag is under way.
    (int CursorX, NativeMethods.RECT Window, double Scale, int ScreenWidth)? gripStart;

    // The width the preview last gave the window, physical pixels.
    int previewWidth;

    // The edge the panel docks on: the grip is on the other side (see the top of this file).
    PanelEdge dockedOn = PanelEdge.Right;

    /// <summary>
    /// The panel's docked edge, from TasksPanel.Edge: the grip moves to the edge facing the windows, the left one for a
    /// panel docked on the right and the right one for a panel docked on the left.
    /// </summary>
    internal void SetInnerEdge(PanelEdge edge)
    {
        dockedOn = edge;
        DockPanel.SetDock(Grip, edge == PanelEdge.Right ? Dock.Left : Dock.Right);
    }

    void WireGrip()
    {
        Grip.DragStarted += (_, _) => BeginGripDrag();
        Grip.DragDelta += (_, _) => PreviewGripDrag();
        Grip.DragCompleted += (_, e) => EndGripDrag(e.Canceled);
    }

    void BeginGripDrag()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0 || !NativeMethods.GetCursorPos(out var cursor) || !NativeMethods.GetWindowRect(hwnd, out var rect)) return;
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        var screenWidth = NativeMethods.GetMonitorInfo(NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST), ref info)
            ? info.rcMonitor.Right - info.rcMonitor.Left
            : 0;
        gripStart = (cursor.X, rect, VisualTreeHelper.GetDpi(this).DpiScaleX, screenWidth);
        previewWidth = rect.Right - rect.Left;
    }

    void PreviewGripDrag()
    {
        if (gripStart is not { } start || !NativeMethods.GetCursorPos(out var cursor)) return;
        var moved = cursor.X - start.CursorX;
        // Docked on the right, the grip is the LEFT edge: dragging it left (a negative move) widens the panel.
        var wanted = start.Window.Right - start.Window.Left + (dockedOn == PanelEdge.Right ? -moved : moved);
        var least = (int)Math.Ceiling(TasksPanelLayout.MinWidth * start.Scale);
        var most = Math.Max(least, start.ScreenWidth / 2);
        previewWidth = Math.Clamp(wanted, least, start.ScreenWidth > 0 ? most : int.MaxValue);
        PlaceWidth(start.Window, previewWidth);
    }

    void EndGripDrag(bool cancelled)
    {
        if (gripStart is not { } start) return;
        gripStart = null;
        var before = start.Window.Right - start.Window.Left;
        if (cancelled)
        {
            PlaceWidth(start.Window, before);
            return;
        }
        // A press without a move (a click on the grip) changes nothing, and so saves nothing.
        if (previewWidth != before) Actions?.ResizePanel(previewWidth / start.Scale);
    }

    // The window `width` pixels wide, its outer edge (against the bar) where it was at the press.
    void PlaceWidth(NativeMethods.RECT was, int width) =>
        NativeMethods.SetWindowPos(new WindowInteropHelper(this).Handle, 0,
            dockedOn == PanelEdge.Right ? was.Right - width : was.Left, was.Top, width, was.Bottom - was.Top,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
}
