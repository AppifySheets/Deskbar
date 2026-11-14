using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;

namespace Deskbar.Core.QuickFind;

// EVERYTHING OPEN THAT QUICK FIND CAN LIST (docs/superpowers/specs/2026-11-04-quick-find-design.md, "What it
// searches"): every window Deskbar knows, on every workspace and unnamed desktop, and every tab the background
// tab reader has seen in a browser or VS Code window.
//
// Pure, so the shape of the list is tested without Windows. The App hands in the facts:
//   placeOf        where a window is, as the row says it (workspace, "unnamed desktop", "all desktops")
//   tabsOf         the last tab reading's titles, empty when there is none yet
//   isBrowser      whether a window's tabs stand in for the window itself
//   originalTitle  the title before a rename rule shortened it, so "VS" is still found by its folder
//
// One rule decides the shape: a BROWSER window that has a reading is listed through its tabs. Its window
// title is just its active tab's title plus "Microsoft Edge", so showing both would show the same thing twice
// (the plan's Review Focus, item 3). The window is still handed over, marked TabsStandIn, and FindRanking
// shows it only when a search finds none of its tabs: that is what "edge" finds, one row per Edge window.
// A browser window with no reading yet, or an installed web app (which reads no tabs), is listed as a window. A VS Code window is listed AND its tabs are: its title
// names the folder, which is what Petre types to find the window, and its tabs name files and Claude sessions.
public static class OpenCatalog
{
    public static IReadOnlyList<FindResult> Of(
        IReadOnlyList<WindowInfo> windows,
        Func<WindowHandle, FindPlace> placeOf,
        Func<WindowHandle, IReadOnlyList<string>> tabsOf,
        Func<WindowInfo, bool> isBrowser,
        Func<WindowHandle, Maybe<string>> originalTitle) =>
        windows.SelectMany(window => Entries(window, placeOf(window.Handle), tabsOf(window.Handle), isBrowser(window), originalTitle(window.Handle)))
            .ToList();

    static IEnumerable<FindResult> Entries(WindowInfo window, FindPlace place, IReadOnlyList<string> tabs, bool browser, Maybe<string> original)
    {
        var title = window.DisplayName;
        var asWindow = new FindResult(
            $"w:{window.Handle.Value:X}",
            FindKind.Window,
            title,
            place,
            Join(title, original.GetValueOrDefault(""), place.Label),
            Window: window.Handle,
            IconPath: window.ProcessPath);

        var asTabs = tabs.Select((tab, index) => new FindResult(
            $"t:{window.Handle.Value:X}:{index}:{tab}",
            FindKind.Tab,
            tab,
            place,
            Join(tab, title, place.Label),
            Window: window.Handle,
            TabIndex: index,
            Detail: title,
            IconPath: window.ProcessPath));

        return asTabs.Prepend(browser && tabs.Count > 0 ? asWindow with { TabsStandIn = true } : asWindow);
    }

    static string Join(params string[] parts) => string.Join(' ', parts.Where(p => p.Length > 0));
}
