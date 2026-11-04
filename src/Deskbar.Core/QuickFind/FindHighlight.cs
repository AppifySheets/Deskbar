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
        current is not null && keys.Contains(current) ? current : keys.FirstOrDefault();

    // Up and Down stop at the ends instead of wrapping: in a list that can be long, wrapping from the top
    // straight to the bottom hides where you are.
    public static string? Move(IReadOnlyList<string> keys, string? current, int step) =>
        keys.Count == 0
            ? null
            : keys[Math.Clamp(keys.ToList().IndexOf(Keep(keys, current)!) + step, 0, keys.Count - 1)];
}
