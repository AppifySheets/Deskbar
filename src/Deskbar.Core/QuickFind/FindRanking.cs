namespace Deskbar.Core.QuickFind;

// THE ORDER QUICK FIND LISTS ITS MATCHES IN (docs/superpowers/specs/2026-11-04-quick-find-design.md, "Ranking").
//
//   1. Open things (windows and tabs together) before remembered programs, and those before Start menu apps.
//      Running wins, so the top row, the one Enter takes, is not "start another copy" while a copy is open,
//      with the one exception described below. Tabs are not ranked below windows: a tab is as open as the
//      window holding it.
//   2. Then the workspace used most recently. Petre, trying the first build: "search should show me most
//      recently used bars first". The rows he was just in lead, even over a better-matching title on a
//      workspace he has not visited for a while; a place with no workspace (an unnamed desktop, the pinned
//      row) comes after every workspace.
//   3. Inside one workspace, a title that starts with the first typed piece before one that only contains it.
//   4. Then the title, so the order is stable from one keystroke to the next.
//
// Three rules were added after Petre typed "code" with sixteen VS Code windows open and got "a bunch of
// nonsense", when what he wanted was a new VS Code window:
//
//   * A TAB IS FOUND BY ITS OWN TITLE OR ITS WORKSPACE, not through its window's title alone. A tab is searched
//     together with the window holding it, so "deskbar app" finds app.cs in the Deskbar window; but that window's
//     title ends in "Visual Studio Code", and "code" therefore matched every tab of every VS Code window.
//   * A BROWSER WINDOW STANDS BEHIND ITS TABS (FindResult.TabsStandIn): it is listed only when none of its tabs
//     is found. "edge" lists each Edge window once, where it used to list every tab in all of them.
//   * A NEW WINDOW ROW LEADS when what was typed NAMES a program (FindQuery.Names) that has several windows
//     open. With several there is no one window "the program" means, so going to the most recent of them was
//     an arbitrary answer; opening another is what the Start menu and a taskbar middle click do for the same
//     word. With one window open the window still leads, which keeps the spec's promise that Enter does not
//     start a second copy by accident wherever "go to it" has an answer. Otherwise the row sits just below
//     everything open, above programs that are not running.
public static class FindRanking
{
    // At most this many rows of things that would have to be started. Open things are never capped. The Start
    // menu alone has about five hundred programs on Petre's machine, so a broad query would otherwise draw
    // hundreds of rows on every keystroke and pull an icon out of every exe, which froze the UI thread for
    // seconds (final review, I6). Fifteen is more than a launcher's top matches need.
    public const int MaxLaunchRows = 15;

    public static IReadOnlyList<FindResult> Rank(FindQuery query, IReadOnlyList<FindResult> all, IReadOnlyList<Guid> workspacesByRecentUse)
    {
        var found = all.Where(r => query.Matches(r.SearchText) && FoundItself(query, r)).ToList();
        // Windows that have a found tab: a browser window standing behind its tabs is dropped for these.
        var windowsWithFoundTab = found.Where(r => r.Kind == FindKind.Tab).Select(r => r.Window).OfType<Domain.WindowHandle>().ToHashSet();
        var ranked = found
            .Where(r => !(r.TabsStandIn && r.Window is { } window && windowsWithFoundTab.Contains(window)))
            .OrderBy(r => Group(query, r))
            .ThenBy(r => Recency(r.Place.WorkspaceId, workspacesByRecentUse))
            .ThenByDescending(r => query.StartsWithFirstPiece(r.Title))
            .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        // The cap keeps each row where ranking put it: a leading new window row stays first, and counts.
        var allowed = ranked.Where(r => !r.IsOpen).Take(MaxLaunchRows).Select(r => r.Key).ToHashSet();
        return ranked.Where(r => r.IsOpen || allowed.Contains(r.Key)).ToList();
    }

    // A tab needs something typed to be in its own title or its workspace's name; everything else is found by
    // its whole search text.
    static bool FoundItself(FindQuery query, FindResult result) =>
        result.Kind != FindKind.Tab || query.MatchesAny($"{result.Title} {result.Place.Label}");

    static int Group(FindQuery query, FindResult result) => result.Kind switch
    {
        FindKind.NewWindow when result.OpenWindows > 1 && query.Names(result.SearchText) => 0,
        FindKind.Window or FindKind.Tab => 1,
        FindKind.NewWindow => 2,
        FindKind.Remembered => 3,
        _ => 4,
    };

    // A workspace not in the recent list, and a place with no workspace at all, sort after every recent one.
    static int Recency(Guid? workspace, IReadOnlyList<Guid> recent) =>
        workspace is { } id && recent.ToList().IndexOf(id) is var at and >= 0 ? at : int.MaxValue;
}
