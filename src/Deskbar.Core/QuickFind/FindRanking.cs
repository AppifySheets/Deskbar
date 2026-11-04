namespace Deskbar.Core.QuickFind;

// THE ORDER QUICK FIND LISTS ITS MATCHES IN (docs/superpowers/specs/2026-11-04-quick-find-design.md, "Ranking").
//
//   1. Open things (windows and tabs together) before remembered programs, and those before Start menu apps.
//      Running always wins, so the top row, the one Enter takes, is never "start another copy" while a copy is
//      open. Tabs are not ranked below windows: a tab is as open as the window holding it.
//   2. Then the workspace used most recently. Petre, trying the first build: "search should show me most
//      recently used bars first". The rows he was just in lead, even over a better-matching title on a
//      workspace he has not visited for a while; a place with no workspace (an unnamed desktop, the pinned
//      row) comes after every workspace.
//   3. Inside one workspace, a title that starts with the first typed piece before one that only contains it.
//   4. Then the title, so the order is stable from one keystroke to the next.
public static class FindRanking
{
    public static IReadOnlyList<FindResult> Rank(FindQuery query, IReadOnlyList<FindResult> all, IReadOnlyList<Guid> workspacesByRecentUse) =>
        all.Where(r => query.Matches(r.SearchText))
            .OrderBy(Group)
            .ThenBy(r => Recency(r.Place.WorkspaceId, workspacesByRecentUse))
            .ThenByDescending(r => query.StartsWithFirstPiece(r.Title))
            .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

    static int Group(FindResult result) => result.Kind switch
    {
        FindKind.Window or FindKind.Tab => 0,
        FindKind.Remembered => 1,
        _ => 2,
    };

    // A workspace not in the recent list, and a place with no workspace at all, sort after every recent one.
    static int Recency(Guid? workspace, IReadOnlyList<Guid> recent) =>
        workspace is { } id && recent.ToList().IndexOf(id) is var at and >= 0 ? at : int.MaxValue;
}
