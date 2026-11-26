using System.Reactive.Linq;
using System.Windows;
using System.Windows.Interop;
using CSharpFunctionalExtensions;
using Deskbar.Core;
using Deskbar.Core.Domain;
using Deskbar.Core.Tasks;
using Deskbar.Windows.Monitoring;

namespace Deskbar.App;

// THE MANAGER'S WINDOW, DOCKED (task f171bca6). Petre, once the manager's terminal was pinned to every desktop: "i'd
// prefer to have it docked next to the tasks or next to the bar if tasks are not available, rather than floating on top
// of things", then from the choices offered: reserve the space, and a drag by the title bar snaps it back.
//
// HOW. The terminal is Windows Terminal's window, not Deskbar's, so Deskbar cannot make it an app bar itself (an app bar
// registered on another process's window would outlive a Deskbar crash, and its notices would go to a window that does
// not read them). Instead a second TasksPanel holds the strip, on a placeholder window of Deskbar's own: borderless,
// fully transparent and click-through, so it is never seen and never in the way. The terminal is then kept exactly on
// the strip the shell granted (TasksPanel.Granted), grown by its own invisible resize border so its visible frame fills
// the strip (ManagerDockLayout.TerminalWindowFor).
//
// THE ORDER. Bar outermost, then the tasks panel, then the manager. The shell puts a NEWER registration inside an older one
// on the same edge, so the dock registers afresh after every registration of the bar's (its notices, FloatingBar.Strip)
// AND every fresh registration of the panel's (TasksPanel.Registered): the panel opening, the panel answering the bar,
// the panel's guard. With the panel closed only the bar's notices come, and the dock sits just inside the bar. The panel
// answers a bar notice before the dock does when both are queued (it subscribed first), and when it does not, its own
// Registered notice that follows queues the dock again, so the dock ends up inside either way. The panel giving its strip
// back (closed) moves the dock outward by the shell's own position notice, which TasksPanel answers with an in-place
// re-ask.
//
// THE TERMINAL. Followed from the moment the App has pinned it (App.PinManagerWindow calls Follow), and given up when it
// closes or stops being the manager's (a second tab took its title): the strip is then given back, so no empty column is
// left. A drag of it by the title bar puts it back; a drag of its edge takes the new width (ManagerDockLayout.Dragged),
// saved in AppState.ManagerDockWidth. While a drag is under way nothing moves it, and the 1 s Keep puts it back if
// something else did (Windows Terminal resizing itself, a DPI change). A minimized or maximized terminal is left alone:
// both are the user's own doing, and a maximized window's rectangle is the work area's anyway.
//
// THREADS. Everything here runs on the UI thread: the window events are raised on it, the dock's Granted is said from
// the dispatcher, and the App's 1 s timer ticks on it.
internal sealed class ManagerDock : IDisposable
{
    readonly TasksPanel strip;
    readonly WorkspaceManager manager;
    readonly IDisposable grants;

    // The manager's terminal while it is docked, and whether the user is dragging it right now.
    Maybe<WindowHandle> terminal = Maybe<WindowHandle>.None;
    bool dragging;

    // The strip the shell granted, while one is held.
    Maybe<WindowRect> granted = Maybe<WindowRect>.None;

    /// <param name="placeholder">The transparent window holding the app bar (made by <see cref="Placeholder"/>).</param>
    /// <param name="barStrip">The bar's strip notices (FloatingBar.Strip).</param>
    /// <param name="panel">The tasks panel, whose fresh registrations the dock follows to stay inside it.</param>
    /// <param name="restoredWindows">The windows a newly taken strip moves aside (the panel's list, the terminal left out).</param>
    /// <param name="nudge">Moves one of those without raising it (ScreenLayout.Nudge).</param>
    public ManagerDock(Window placeholder, IObservable<BarStrip> barStrip, TasksPanel panel, WorkspaceManager manager,
        Func<IReadOnlyList<(WindowHandle Window, WindowRect Rect)>> restoredWindows, Func<WindowHandle, WindowRect, Result> nudge)
    {
        this.manager = manager;
        strip = new TasksPanel(placeholder, barStrip.Merge(panel.Registered))
        {
            Name = "manager dock",
            ClampWidth = ManagerDockLayout.Clamp,
            OnTop = false,
            RestoredWindows = restoredWindows,
            Nudge = nudge,
        };
        grants = strip.Granted.Subscribe(g =>
        {
            granted = g;
            Place();
        });
    }

    // The width the dock is held at: the user's last drag, else the default.
    double Width => manager.State.ManagerDockWidth ?? ManagerDockLayout.DefaultWidth;

    // Click-through and never activated (the extended styles' values from winuser.h).
    const long WsExTransparent = 0x00000020, WsExNoActivate = 0x08000000;

    // THE PLACEHOLDER: a window nobody sees or clicks. Borderless and fully transparent (a layered window whose pixels are
    // all transparent is skipped by hit-testing), WS_EX_TRANSPARENT as well so a click always falls through to the
    // terminal over it, a tool window so it is on no taskbar or Alt+Tab, and never activated.
    public static Window Placeholder()
    {
        var window = new Window
        {
            Title = "Deskbar manager dock",
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = false,
            Width = 1,
            Height = 1,
        };
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE,
                new nint(style | WsExTransparent | NativeMethods.WS_EX_TOOLWINDOW | WsExNoActivate));
        };
        return window;
    }

    /// <summary>The manager's terminal, pinned: dock it (the first time) and put it on the strip.</summary>
    public void Follow(WindowHandle window)
    {
        if (terminal.Map(t => t == window).GetValueOrDefault(false))
        {
            Place();
            return;
        }
        terminal = window;
        ClickTrace.Write($"manager dock: following 0x{window.Value:X}");
        if (strip.IsShown) Place();
        else strip.Show(Width);
    }

    /// <summary>A window closed, or was retitled so it is no longer the manager's: when it was the docked terminal, the strip goes back.</summary>
    public void Lost(WindowHandle window)
    {
        if (!terminal.Map(t => t == window).GetValueOrDefault(false)) return;
        terminal = Maybe<WindowHandle>.None;
        dragging = false;
        ClickTrace.Write($"manager dock: 0x{window.Value:X} gone, strip given back");
        strip.Hide();
    }

    /// <summary>A move or resize of some window began (EVENT_SYSTEM_MOVESIZESTART).</summary>
    public void DragStarted(WindowHandle window)
    {
        if (terminal.Map(t => t == window).GetValueOrDefault(false)) dragging = true;
    }

    /// <summary>A move or resize ended (EVENT_SYSTEM_MOVESIZEEND): the terminal goes back, or the dock takes its new width.</summary>
    public void DragEnded(WindowHandle window)
    {
        if (!terminal.Map(t => t == window).GetValueOrDefault(false)) return;
        dragging = false;
        if (granted is not { HasValue: true } held || FrameOf(window) is not { } frame) return;
        var scale = NativeMethods.GetDpiForWindow(window.Value) / 96.0;
        switch (ManagerDockLayout.Dragged(held.Value, frame, scale))
        {
            case ManagerDockLayout.AfterDrag.Resize resize:
                var width = ManagerDockLayout.Clamp(resize.WidthDips, double.MaxValue);
                ClickTrace.Write($"manager dock: resized to {width:0} DIP");
                manager.SaveManagerDockWidth(width);
                // Re-docks in place at the new width; Granted then puts the terminal on the new strip.
                strip.SetWidth(width);
                Place();
                break;
            default:
                ClickTrace.Write("manager dock: moved by hand, put back");
                Place();
                break;
        }
    }

    /// <summary>The 1 s tick: the strip's guard, and the terminal put back where something else moved it.</summary>
    public void Keep()
    {
        strip.Keep();
        if (!dragging && granted.HasValue && terminal is { HasValue: true } t && FrameOf(t.Value) is { } frame
            && !Same(frame, granted.Value))
            Place();
    }

    public void Shutdown()
    {
        grants.Dispose();
        strip.Shutdown();
    }

    public void Dispose() => Shutdown();

    // Puts the terminal on the strip, its visible frame exactly over it. Twice when needed: a move onto a screen of another
    // scale makes Windows Terminal rescale itself on arrival, which changes its border and size, so the second pass
    // measures again and lands it exactly. Not while dragged, minimized or maximized (see the header).
    void Place()
    {
        if (dragging || terminal is not { HasValue: true } t || granted is not { HasValue: true } g) return;
        var hwnd = t.Value.Value;
        if (!NativeMethods.IsWindow(hwnd) || NativeMethods.IsIconic(hwnd) || NativeMethods.IsZoomed(hwnd)) return;
        Enumerable.Range(0, 2).ToList().ForEach(_ =>
        {
            if (WindowOf(t.Value) is not { } window || FrameOf(t.Value) is not { } frame || Same(frame, g.Value)) return;
            var target = ManagerDockLayout.TerminalWindowFor(g.Value, window, frame);
            NativeMethods.SetWindowPos(hwnd, 0, target.Left, target.Top, target.Width, target.Height,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        });
    }

    // A pixel of slack either way, for the rounding of a scaled border.
    static bool Same(WindowRect a, WindowRect b) =>
        Math.Abs(a.Left - b.Left) <= 1 && Math.Abs(a.Top - b.Top) <= 1 && Math.Abs(a.Right - b.Right) <= 1 && Math.Abs(a.Bottom - b.Bottom) <= 1;

    static WindowRect? WindowOf(WindowHandle window) =>
        NativeMethods.GetWindowRect(window.Value, out var r) ? new WindowRect(r.Left, r.Top, r.Right, r.Bottom) : null;

    // The visible frame (DWMWA_EXTENDED_FRAME_BOUNDS), physical pixels.
    static WindowRect? FrameOf(WindowHandle window) =>
        NativeMethods.DwmGetWindowAttributeRect(window.Value, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out var r,
            System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.RECT>()) == 0
            ? new WindowRect(r.Left, r.Top, r.Right, r.Bottom)
            : null;
}
