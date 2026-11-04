namespace Deskbar.Core.QuickFind;

// WHICH ROW IS HIGHLIGHTED, kept by the result's key rather than its position.
//
// The list refreshes while the box is open: tab readings arrive from the background reader and the catalogue
// is rebuilt. If the highlight were an index, a new row arriving above it would silently move Enter to a
// different result just as Petre pressed it, which the spec names as a failure even if everything else works.
// So the key is carried across refreshes, and only when its result is gone does the highlight fall to the
// first row.
//
// The keys are the rows in DISPLAY order (grouped, see FindGrouping), which is the order the arrows walk.
public static class FindHighlight
{
    public static string? Keep(IReadOnlyList<string> keys, string? current) =>
        current is null ? keys.FirstOrDefault()
        : keys.Contains(current) ? current
        : SameTab(keys, current) ?? keys.FirstOrDefault();

    // A TAB's key (t:<hwnd>:<index>:<title>, see FindResult) carries its title and position, and both change
    // under a live page: an unread counter ticks in the title, a tab opens to its left. Exact keys alone would
    // drop the highlight to the first row on every such change, which is the move-under-the-cursor the spec
    // forbids. So a vanished tab key is followed to the same window's tab with the same title (it moved), then
    // to the same window's tab at the same position (its title changed). Never to another window's tab.
    static string? SameTab(IReadOnlyList<string> keys, string current) =>
        Tab(current) is not { } was
            ? null
            : keys.Select(k => (Key: k, Tab: Tab(k))).Where(k => k.Tab?.Window == was.Window).ToList() is var sameWindow
                ? sameWindow.FirstOrDefault(k => k.Tab!.Value.Title == was.Title).Key
                  ?? sameWindow.FirstOrDefault(k => k.Tab!.Value.Index == was.Index).Key
                : null;

    // Split on the first three colons only: a title may hold colons of its own.
    static (string Window, string Index, string Title)? Tab(string key) =>
        key.StartsWith("t:", StringComparison.Ordinal) && key.Split(':', 4) is [_, var window, var index, var title]
            ? (window, index, title)
            : null;

    // Up and Down stop at the ends instead of wrapping: in a list that can be long, wrapping from the top
    // straight to the bottom hides where you are.
    public static string? Move(IReadOnlyList<string> keys, string? current, int step) =>
        keys.Count == 0
            ? null
            : keys[Math.Clamp(keys.ToList().IndexOf(Keep(keys, current)!) + step, 0, keys.Count - 1)];
}
