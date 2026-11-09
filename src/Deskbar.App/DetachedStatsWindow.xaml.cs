using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Deskbar.Core;
using Deskbar.Core.Domain;
using Deskbar.Core.Geometry;
using Deskbar.Core.Persistence;
using Deskbar.Windows.Monitoring;
using Deskbar.Windows.Performance;

namespace Deskbar.App;

// THE DETACHED STATS WINDOW: the bar's stats line (CPU, memory, disk, GPU with their sparklines) in a small window of
// its own, always on top, that the user drags anywhere. The owner wanted the numbers while a game or another
// full-screen window covers the bar, or simply somewhere else on the screen. Right-clicking the bar's stats line
// detaches it (the bar then hides its own line); right-clicking this window puts it back. The choice and the
// position are kept in AppState.DetachedStats, so a restart brings it back where it was.
//
// It draws with the same StatsStrip as the bar and subscribes to the SAME ISystemStats instance the bar does
// (SystemStats shares one timer between subscribers), so detaching costs no second set of counters and the two can
// never disagree.
//
// KNOWN LIMIT, BY DESIGN: a game in EXCLUSIVE full-screen mode draws past the desktop, and no window can show over
// it. Borderless-windowed games, which is what most modern games use by default, are fine, and so is any other
// full-screen window (a video, a browser). It is not worked around.
//
// Left press and move drags; a left click without movement opens Task Manager, like the bar's line. One gesture is
// never both: a press that dragged is not a click.
public partial class DetachedStatsWindow : Window
{
    const string TaskManagerTip = "Click for Task Manager";

    // The monitor lookups' flags: NULL for "is the point on a monitor at all", PRIMARY for the fallback.
    const uint MonitorDefaultToNull = 0, MonitorDefaultToPrimary = 1;

    // Clear of the screen's edge when the window has to be put somewhere without a remembered position.
    const double FallbackMargin = 12;

    readonly WorkspaceManager manager;
    readonly Action openTaskManager;
    readonly TextBlock tip = new();
    // Four cells of 70 DIP.
    readonly StatsStrip Strip = new() { Width = 280 };
    readonly IDisposable subscription;

    // Set only when the app is going away. Until then a close request (Alt+F4, a taskbar close) only hides the
    // window: a closed WPF window can never be shown again, and the next detach would throw and take the app down
    // (the same rule, and the same story, as QuickFindWindow).
    bool closingForGood;

    // A left press is in progress, and where it began (in this window, so it is not thrown off by the window
    // moving under the pointer). `dragging` is true while DragMove's native loop owns the window.
    Point? pressAt;
    bool dragging;

    public DetachedStatsWindow(WorkspaceManager manager, ISystemStats stats, Action openTaskManager)
    {
        this.manager = manager;
        this.openTaskManager = openTaskManager;
        InitializeComponent();
        Root.Child = Strip;

        Root.ToolTip = tip;
        System.Windows.Automation.AutomationProperties.SetName(this, "System stats");
        Strip.ApplyTextSize(TextSize());

        var menu = new ContextMenu();
        var back = new MenuItem { Header = "Put stats back on the bar" };
        back.Click += (_, _) => ReturnRequested?.Invoke();
        menu.Items.Add(back);
        ContextMenu = menu;

        subscription = stats.Samples.Subscribe(sample => Dispatcher.BeginInvoke(() => OnSample(sample)));
        Closed += (_, _) => subscription.Dispose();
    }

    // The user asked for the stats back on the bar. App hides this window and records it; this window does not
    // decide that, it only says so.
    internal event Action? ReturnRequested;

    // The window's handle, created before it is ever shown, so App can hand it to WindowMonitor.Ignore first: the
    // monitor does not skip our own process, and an unignored window would list itself in the bar (same reason as
    // QuickFind.EnsureHandle).
    internal nint EnsureHandle() => new WindowInteropHelper(this).EnsureHandle();

    // Shows the window at the remembered place, kept inside the screen that holds that point, or at the primary
    // screen's top right when the point is on no screen (a monitor that is gone). The size is only known once it
    // is up, so it is clamped straight after Show.
    internal void ShowAt(DetachedStatsState state)
    {
        Left = state.Left;
        Top = state.Top;
        Show();
        UpdateLayout();

        if (WorkAreaAt(state.Left, state.Top, MonitorDefaultToNull) is { } work)
            (Left, Top) = WorkAreaClamp.Clamp(state.Left, state.Top, ActualWidth, ActualHeight, work.Left, work.Top, work.Right, work.Bottom);
        else if (WorkAreaAt(0, 0, MonitorDefaultToPrimary) is { } primary)
            (Left, Top) = WorkAreaClamp.Clamp(primary.Right - ActualWidth - FallbackMargin, primary.Top + FallbackMargin,
                ActualWidth, ActualHeight, primary.Left, primary.Top, primary.Right, primary.Bottom);
    }

    // The work area (the screen minus the taskbar) of the monitor holding the point, in DIPs, or null when Windows
    // has none to give. The DPI comes from GetDpiForMonitor on the same monitor, the way FloatingBar.MonitorBounds
    // does it, because a window-scoped DPI can still be stale just after a Show.
    static (double Left, double Top, double Right, double Bottom)? WorkAreaAt(double x, double y, uint whenNone)
    {
        var monitor = NativeMethods.MonitorFromPoint(new NativeMethods.POINT { X = (int)x, Y = (int)y }, whenNone);
        if (monitor == nint.Zero) return null;
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return null;
        NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out var dpiY);
        var scaleX = dpiX / 96.0;
        var scaleY = dpiY / 96.0;
        return (info.rcWork.Left / scaleX, info.rcWork.Top / scaleY, info.rcWork.Right / scaleX, info.rcWork.Bottom / scaleY);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!closingForGood)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    internal void CloseForGood()
    {
        closingForGood = true;
        Close();
    }

    double TextSize() => BarText.Size(7, BarText.Clamp(manager.State.BarTextOffset));

    void OnSample(SystemSample sample)
    {
        Strip.Paint(sample, holdFourColumns: false);
        tip.Text = StatsStrip.TipText(sample, TaskManagerTip);
        // Cheap, and once a second is enough for a change of the text size on Manage to reach it.
        Strip.ApplyTextSize(TextSize());
        ReclaimTopmost();
    }

    // Topmost is a band, not a rank: the taskbar and every other topmost window climb over this one, so it
    // re-asserts HWND_TOPMOST, the way the bar's ReclaimTopmost does, once a second (the rate samples arrive at).
    // SWP_NOACTIVATE is the load-bearing flag: without it this would take focus from whatever was just clicked.
    // Not while hidden, and not mid-drag, where a SetWindowPos would fight DragMove's native loop.
    void ReclaimTopmost()
    {
        if (!IsVisible || dragging) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == nint.Zero) return;
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        pressAt = e.GetPosition(this);
        // Captured so the move that crosses the threshold is seen even if the pointer has already left the window.
        CaptureMouse();
    }

    void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || pressAt is not { } start) return;
        var now = e.GetPosition(this);
        if (Math.Abs(now.X - start.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(now.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        // It is a drag now, and so no longer a click: the release DragMove's loop consumes never reaches the
        // handler below, and pressAt is cleared in case it did.
        pressAt = null;
        ReleaseMouseCapture();
        dragging = true;
        try
        {
            DragMove(); // blocks until the mouse button is released
        }
        finally
        {
            dragging = false;
        }
        manager.SaveDetachedStats(new DetachedStatsState(true, Left, Top));
    }

    void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        // A press that never moved past the threshold is a click.
        if (pressAt is null) return;
        pressAt = null;
        openTaskManager();
    }
}
