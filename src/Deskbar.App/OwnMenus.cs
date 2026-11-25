using System.Windows;
using System.Windows.Controls;

namespace Deskbar.App;

// WHETHER ONE OF DESKBAR'S OWN MENUS IS OPEN, so the windows that climb back on top every second (the bar, the tasks
// panel, the screen strips: their ReclaimTopmost) leave it alone while it is.
//
// Petre, 2026-11-24, a screenshot: a task line's menu in the tasks panel, its right half gone under the panel. Measured
// on the hidden desktop before this was written (a throwaway probe): the popup WPF opens for a ContextMenu has NO owner
// window (GetWindow(GW_OWNER) is 0), so nothing keeps it above the window it was opened from. It opens topmost and on
// top; the next SetWindowPos(HWND_TOPMOST) of the panel, a second later at most, puts the panel above it, and the bar's
// does the same wherever the bar overlaps it. Ranks from the probe, smaller is higher: menu 0, bar 3, panel 10 when
// opened; after the bar's re-assertion menu 3, bar 2; after the panel's menu 4, panel 0.
//
// Only menus are counted, not every popup: a tooltip or a popup that stays open for a long time would otherwise stop the
// bar re-asserting itself, and the taskbar would climb over it (CLAUDE.md, "Topmost is a shared band"). A menu is open
// for seconds and always closes, and ContextMenu raises Closed every time it does, so the count cannot stick. Tooltips
// have the same missing owner and are handled the other way round: the climb goes ahead and lifts them back above
// itself straight after (OwnToolTips, task d4695a97).
//
// Per thread, because the class handlers run on the thread that owns the menu, and so does each window's re-assertion:
// in the app that is the one UI thread, and in the tests each test's own STA thread, so a menu one test opens does not
// hold another test's window back.
internal static class OwnMenus
{
    [ThreadStatic] static int open;

    // Every ContextMenu in the process, whichever window opened it. Registered once, before any menu can open: the App
    // calls Start at startup, and AnyOpen calls it too, so a window that asks first is covered as well.
    static OwnMenus()
    {
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((_, _) => open++));
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.ClosedEvent, new RoutedEventHandler((_, _) => open = Math.Max(0, open - 1)));
    }

    // Touches the class so its static constructor has registered the handlers.
    public static void Start() { }

    public static bool AnyOpen => open > 0;

    // A MENU OPENED AT THE POINTER STILL NEEDS A PLACEMENT TARGET, FOR ITS SCREEN'S SCALING (task 4b6cc9a9).
    //
    // Petre, 2026-11-25, two screenshots from a friend's PC with two screens: a task line's menu on the bar with text
    // and rows about one and a half times too large, and the bar's own background menu at the normal size beside it.
    // The difference between the two is not their styling. The background menu hangs off an element of the bar
    // (Root.ContextMenu), so it has a placement target. The task line's menu was a bare
    // `new ContextMenu { Placement = MousePoint }` with none.
    //
    // WPF's Popup creates its window on the monitor of its placement target, so the window is born with that monitor's
    // DPI (Popup.cs, PopupInitialPlacementHelper.GetPlacementOrigin). With no target the origin is (0,0): the window is
    // created on the PRIMARY monitor at the primary's scaling and then shown wherever the pointer is, and a visible
    // popup ignores the DPI change that follows (Popup.OnDpiChanged). So on a 100% screen beside a 150% primary the menu
    // is drawn at 150%. On a machine whose screens all share one scaling (Petre's: both 144 DPI) nothing shows, which
    // is why it was only ever seen elsewhere.
    //
    // So every menu opened at the pointer names the window it belongs to as its target. Placement stays MousePoint,
    // which ignores the target's rectangle: the target only says which screen. A window that opens its own menu passes
    // itself (`AtPointer(this)`). The two callers with no window at hand (a screen strip's menu, which the bar opens
    // for whichever strip was clicked, and ClaudeTaskActions' folder list) use the overload that asks which of the
    // app's windows is under the pointer. The window, never the clicked element: a target that a rebuild deletes takes
    // its popup's placement with it (the hover card's note in FloatingBar.xaml.cs).
    public static ContextMenu AtPointer(UIElement? owner) =>
        new() { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint, PlacementTarget = owner };

    public static ContextMenu AtPointer() => AtPointer(WindowUnderPointer());

    // The app's window the pointer is over, or null (none of ours, or a thread that does not own it): the menu then
    // opens bare, as it did before. FromHwnd throws on 0, hence the guard.
    internal static UIElement? WindowUnderPointer() =>
        Deskbar.Windows.Monitoring.NativeMethods.GetCursorPos(out var cursor)
        && Deskbar.Windows.Monitoring.NativeMethods.GetAncestor(WindowFromPoint(cursor), Deskbar.Windows.Monitoring.NativeMethods.GA_ROOT) is var root and not 0
        && System.Windows.Interop.HwndSource.FromHwnd(root) is { RootVisual: UIElement visual }
            ? visual
            : null;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern nint WindowFromPoint(Deskbar.Windows.Monitoring.NativeMethods.POINT point);
}
