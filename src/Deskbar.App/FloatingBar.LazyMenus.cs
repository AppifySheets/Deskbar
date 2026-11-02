using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace Deskbar.App;

// A CONTEXT MENU IS FILLED THE FIRST TIME IT OPENS, not on every rebuild. Petre: "when activating a bar in
// deskbar, it takes a little while to activate", "can you make it instantaneous?"
//
// The bar rebuilds every row from scratch on every pulse, twice per workspace switch, and each rebuild used
// to build a ContextMenu for every icon and every row with it: a dozen MenuItems each, click handlers wired,
// glyphs made, for menus that are almost never opened. Measured on his bar with the rebuild's cost ledger
// (FloatingBar.RebuildPhases.cs): 21-36ms for the icon menus and 29-45ms for the row menus, of a draw of
// 124-159ms. The harness confirms the share: a rebuild with no real windows behind it draws in 33ms, and
// the menus are 24ms of that.
//
// So a row or icon is given an EMPTY menu and a filler. The menu object itself has to exist from the start,
// and not because of the tests: WPF decides which menu a right-click opens through a class handler that
// runs on each element as ContextMenuOpening bubbles up, BEFORE any handler added here, and it takes the
// first element that has a menu at all. An element with no menu until the handler ran would lose the click
// to its row, or to the bar's own Root. The empty menu costs nothing to speak of; the items are the cost.
// The filler runs on ContextMenuOpening, which WPF raises before it opens the menu, and the menu it fills is
// the very object WPF is about to show, so what the builders attach to it (the fade hold, IconMenu's own
// Opened handler) is attached to the right menu.
//
// MenuOf is the one way to ask, so a test asks the same way WPF does rather than reading a menu that is
// still empty. It is also safe to call twice: once filled, the menu stays.
public partial class FloatingBar
{
    static readonly ConditionalWeakTable<FrameworkElement, Action<ContextMenu>> menuFillers = [];

    // Gives the element a menu that stays empty until it is first opened.
    static void LazyMenu(FrameworkElement element, Action<ContextMenu> fill)
    {
        element.ContextMenu = new ContextMenu();
        menuFillers.AddOrUpdate(element, fill);
        element.ContextMenuOpening += (_, _) => MenuOf(element);
    }

    // The element's menu, filled now if it was only promised. Null for an element that was never given one.
    internal static ContextMenu? MenuOf(FrameworkElement element)
    {
        if (element.ContextMenu is not { } menu) return null;
        if (!menuFillers.TryGetValue(element, out var fill)) return menu;
        menuFillers.Remove(element);
        fill(menu);
        return menu;
    }
}
