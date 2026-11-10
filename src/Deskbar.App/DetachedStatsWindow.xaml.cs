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
// The MIDDLE button moves the window, like the bar (the owner: "make it draggable only with middle mouse click like
// deskbar is"); a left click opens Task Manager, like the bar's line. The left button never drags, so a click can
// never be mistaken for a move and a move never opens anything. The middle drag is the bar's (FloatingBar's
// MiddleDragging has the long reasoning), kept in physical pixels through MiddleDrag.
public partial class DetachedStatsWindow : Window
{
    const string TaskManagerTip = "Click for Task Manager";

    // The monitor lookup's flag for the fallback; the lookup for a stored point is MONITOR_DEFAULTTONEAREST.
    const uint MonitorDefaultToPrimary = 1;

    // A stored position beyond this (in either direction) is not a position, it is damage. WPF throws for a Left or
    // Top outside the Int32 range, so nothing past it may ever be assigned; a real desktop is nowhere near it.
    const double MostPlausible = 1e6;

    // Clear of the screen's edge when the window has to be put somewhere without a remembered position.
    const double FallbackMargin = 12;

    readonly WorkspaceManager manager;
    readonly Action openTaskManager;
    readonly TextBlock tip = new();
    // Four cells of 70 DIP.
    // The window has the room for the name beside the value (StatsStrip has why the bar does not).
    readonly StatsStrip Strip = new(namesBesideValues: true) { Width = 280 };
    readonly IDisposable subscription;

    // Set only when the app is going away. Until then a close request (Alt+F4, a taskbar close) is turned into a
    // request to put the stats back on the bar: a closed WPF window can never be shown again, and the next detach
    // would throw and take the app down (the same rule, and the same story, as QuickFindWindow).
    bool closingForGood;

    // A left press of ours is in progress: its release ON the window is a click. A release without one (a menu
    // closing, the end of a middle drag's capture) is not, and nor is a release after the pointer has left the
    // window, which is what a button does: the press is forgotten on MouseLeave (constructor).
    bool leftPressed;

    // The middle drag, as the bar keeps it (FloatingBar.MiddleDragging): the cursor at the press in physical pixels,
    // where on the window it took hold, and whether the threshold has been crossed.
    Point? middleStart;
    MiddleDrag.Grab middleGrab;
    bool middleDragging;

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

        // Press, slide off, release: not a click, the way a button reads it.
        MouseLeave += (_, _) => leftPressed = false;

        // A drag whose capture is taken away (Alt+Tab, a menu opening, another window grabbing the mouse) never
        // sees its middle release. Without this it stayed "dragging": the next middle press skipped the threshold,
        // and where the window had been left was not saved. The ordinary release clears the flag before it lets go
        // of the capture, so this does nothing then.
        LostMouseCapture += (_, _) =>
        {
            if (!middleDragging) return;
            middleDragging = false;
            middleStart = null;
            manager.SaveDetachedStats(new DetachedStatsState(true, Left, Top));
        };

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

    // Shows the window at the remembered place, kept inside the screen that holds that point (the nearest screen,
    // like the bar, so a corner dragged just past an edge stays on that edge's screen), or at the primary screen's
    // top right when there is no usable position: none stored, or one that is not a number or absurdly far off (a
    // hand-edited state file). The size is only known once it is up, so it is clamped straight after Show. Left and
    // Top are only ever assigned a value that has been checked, because WPF throws for one outside the Int32 range.
    internal void ShowAt(DetachedStatsState state)
    {
        var usable = IsPlausible(state.Left) && IsPlausible(state.Top);
        if (usable)
        {
            Left = state.Left;
            Top = state.Top;
        }
        else if (MonitorAt(0, 0, MonitorDefaultToPrimary) is { } first)
        {
            // Not clamped yet, the size is unknown: the strip's own width stands in for it until the Show below.
            Left = first.Right - Strip.Width - FallbackMargin;
            Top = first.Top + FallbackMargin;
        }

        Show();
        UpdateLayout();

        if (usable && MonitorAt(state.Left, state.Top, NativeMethods.MONITOR_DEFAULTTONEAREST) is { } screen)
            (Left, Top) = WorkAreaClamp.Clamp(state.Left, state.Top, ActualWidth, ActualHeight, screen.Left, screen.Top, screen.Right, screen.Bottom);
        else if (MonitorAt(0, 0, MonitorDefaultToPrimary) is { } primary)
            (Left, Top) = WorkAreaClamp.Clamp(primary.Right - ActualWidth - FallbackMargin, primary.Top + FallbackMargin,
                ActualWidth, ActualHeight, primary.Left, primary.Top, primary.Right, primary.Bottom);
    }

    static bool IsPlausible(double coordinate) => double.IsFinite(coordinate) && Math.Abs(coordinate) <= MostPlausible;

    // The bounds of the monitor holding the point, in DIPs, or null when Windows has none to give (with NEAREST, only
    // a failed call). The whole monitor, rcMonitor, NOT the work area: the window may be parked over the taskbar, as
    // the bar may (FloatingBar.MonitorBounds has the story: clamping to rcWork evicts a window dropped on a taskbar).
    // The DPI comes from GetDpiForMonitor on the same monitor, the way FloatingBar.MonitorBounds does it, because a
    // window-scoped DPI can still be stale just after a Show.
    static (double Left, double Top, double Right, double Bottom)? MonitorAt(double x, double y, uint whenNone)
    {
        var monitor = NativeMethods.MonitorFromPoint(new NativeMethods.POINT { X = (int)x, Y = (int)y }, whenNone);
        if (monitor == nint.Zero) return null;
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return null;
        NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out var dpiY);
        var scaleX = dpiX / 96.0;
        var scaleY = dpiY / 96.0;
        return (info.rcMonitor.Left / scaleX, info.rcMonitor.Top / scaleY, info.rcMonitor.Right / scaleX, info.rcMonitor.Bottom / scaleY);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!closingForGood)
        {
            // Alt+F4 reaches this window once a click or a drag has activated it. Hiding it here would leave the
            // state saying "shown", the bar's own line hidden and the window unreachable until a restart. So the
            // request becomes the menu's: App hides the window and records that the stats are back on the bar.
            e.Cancel = true;
            ReturnRequested?.Invoke();
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
    }

    // NO RE-CLAIMING OF THE TOP. Topmost is a band, not a rank, and the bar re-asserts HWND_TOPMOST once a second
    // (FloatingBar.ReclaimTopmost) because the taskbar climbs over it. This window did the same at first, and the
    // owner's first live test found the cost: parked over the system tray, the window and the taskbar took turns on
    // top, once a second, because the shell re-raises the taskbar inside the band too, and overlapping the bar it
    // would have fought the bar's own re-claim the same way. One window per band may insist; this one is not it.
    // So it is an ordinary topmost window: above every normal window, which is what it is for, and below whichever
    // topmost window was raised last, which is what a user expects of a small gadget dropped on the tray.

    void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Not captured: a release that happens elsewhere should not be a click, and MouseLeave forgets the press.
        leftPressed = true;
    }

    void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!leftPressed) return;
        leftPressed = false;
        openTaskManager();
    }

    // THE MIDDLE BUTTON MOVES THE WINDOW. DragMove cannot do it: it drives Windows' own move loop, which is defined
    // in terms of the LEFT button (called with the left button up it throws). So the window is moved by hand, and
    // the bar's two traps apply unchanged. The cursor comes from GetCursorPos, not from e.GetPosition, which is
    // measured against the window this code is moving, so the travel would shrink to nothing as the window caught
    // up. And the whole drag is in PHYSICAL pixels, moved with SetWindowPos, never by writing Left and Top: a DIP is
    // not a fixed place once the screens differ in scale, and the window flickered between two screens that way
    // (MiddleDrag has the numbers).
    void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        NativeMethods.GetCursorPos(out var cursor);
        GrabWindowAt(cursor.X, cursor.Y);
    }

    void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.MiddleButton != MouseButtonState.Pressed || middleStart is not { } start) return;

        NativeMethods.GetCursorPos(out var cursor);
        if (!middleDragging)
        {
            if (Math.Abs(cursor.X - start.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(cursor.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            middleDragging = true;
            // A left press held through this drag is no longer a click: its release over the window, which the
            // capture keeps the pointer "on", would open Task Manager at the end of a move.
            leftPressed = false;
            // So the moves keep arriving when the pointer outruns the window.
            CaptureMouse();
        }

        MoveWindowTo(cursor.X, cursor.Y);
    }

    void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        middleStart = null;
        if (!middleDragging) return;
        middleDragging = false;
        ReleaseMouseCapture();
        // Left and Top follow a move WPF did not make itself, so they are where the window landed.
        manager.SaveDetachedStats(new DetachedStatsState(true, Left, Top));
    }

    // The window's size is read FRESH on every move: the move that carries it onto a screen of another scale makes
    // WPF resize it, and the next move places it from its new size (MiddleDrag.Target). Internal so a test can drive
    // it without a real mouse, like the bar's.
    internal void MoveWindowTo(int cursorX, int cursorY)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return;
        var (left, top) = MiddleDrag.Target(middleGrab, cursorX, cursorY, rect.Right - rect.Left, rect.Bottom - rect.Top);
        NativeMethods.SetWindowPos(hwnd, nint.Zero, left, top, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    // Records where the cursor took hold of the window. False when Windows will not give its rectangle, which leaves
    // no drag armed.
    internal bool GrabWindowAt(int cursorX, int cursorY)
    {
        middleStart = null;
        if (!NativeMethods.GetWindowRect(new WindowInteropHelper(this).Handle, out var rect)) return false;
        middleStart = new Point(cursorX, cursorY);
        middleGrab = MiddleDrag.Press(cursorX, cursorY, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        return true;
    }
}
