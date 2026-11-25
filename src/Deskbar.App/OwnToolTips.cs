using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Deskbar.Windows.Monitoring;

namespace Deskbar.App;

// DESKBAR'S OWN OPEN TOOLTIPS, so the windows that climb back on top every second (the bar, the tasks panel, the
// screen strips: their ReclaimTopmost) can lift them back above themselves straight after.
//
// Task d4695a97, Petre's screenshot of 2026-11-25: a task line's hover text in the tasks panel, its right half gone
// under the bar. It is the menu problem again (OwnMenus has the measurement): the popup WPF opens for a ToolTip has no
// owner window either, so nothing keeps it above the window it belongs to. It opens on top, and the next
// SetWindowPos(HWND_TOPMOST) of the bar puts the bar over it wherever the two overlap, as the panel's and a strip's do.
//
// Menus are handled by holding the climb back while one is open. Tooltips are not, on purpose (the OwnMenus comment
// already said so): a tooltip opens on every hover, and with the pointer resting on the bar one can stay up for as long
// as WPF's ShowDuration, so holding the bar back for it would let the taskbar or the Start menu climb over the bar in
// that time (CLAUDE.md, "Topmost is a shared band"). So the climb goes ahead and every open tooltip is put back on top
// of the band right after it, in the same tick: the bar stays above everything else and the hover text above the bar.
// Nothing is drawn in between, because both moves happen before the thread returns to its message loop.
//
// Per thread, for the reason OwnMenus is: the class handlers run on the thread that owns the tooltip, and so does each
// window's re-assertion, the one UI thread in the app and each test's own STA thread in the tests.
internal static class OwnToolTips
{
    // The tooltips open on this thread, in the order they opened. ToolTip raises Closed every time it closes, so an
    // entry cannot stick.
    [ThreadStatic] static ImmutableList<ToolTip>? open;

    static ImmutableList<ToolTip> Open => open ?? [];

    // Every ToolTip in the process, whichever window it belongs to. Registered once, before any tooltip can open: the
    // App calls Start at startup, and Lift and AnyOpen touch the class too.
    static OwnToolTips()
    {
        EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent,
            new RoutedEventHandler((tip, _) => open = Open.Remove((ToolTip)tip).Add((ToolTip)tip)));
        EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.ClosedEvent,
            new RoutedEventHandler((tip, _) => open = Open.Remove((ToolTip)tip)));
    }

    // Touches the class so its static constructor has registered the handlers.
    public static void Start() { }

    public static bool AnyOpen => !Open.IsEmpty;

    // Puts every open tooltip back on top of the topmost band, oldest first so the newest ends on top. Called by each
    // ReclaimTopmost after its own climb. SWP_NOACTIVATE for the reason the climbs carry it: the focus stays where the
    // user put it. A tooltip whose popup has no window (yet, or any more) is skipped.
    public static void Lift() =>
        Open
            .Select(tip => PresentationSource.FromVisual(tip) is HwndSource source ? source.Handle : 0)
            .Where(hwnd => hwnd != 0)
            .ToList()
            .ForEach(hwnd => NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE));
}
