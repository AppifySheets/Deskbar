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
// for seconds and always closes, and ContextMenu raises Closed every time it does, so the count cannot stick.
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
}
