namespace Deskbar.Core.QuickFind;

// LAYOUT B: results under one header per place. Petre picked it from three mockups ("b"): rows grouped under
// the workspace's name in its lane colour, the way the bar groups windows into rows.
//
// The ranking (FindRanking) still decides what comes first. Groups appear in the order of their best-ranked
// result and rows keep their ranked order inside a group, so the first row on screen is always the top
// result, the one Enter takes when nothing has been moved.
//
// A place is its workspace when it has one and its label otherwise ("Desktop 1", "all desktops"). Things that
// would have to be started get a group of their own even on the same workspace, headed "<name>, would start
// here", so an open window and a program that is not running never sit under one header looking alike.
public sealed record FindGroup(string Header, FindPlace Place, bool Open, IReadOnlyList<FindResult> Results);

public static class FindGrouping
{
    public static IReadOnlyList<FindGroup> Group(IReadOnlyList<FindResult> ranked) =>
        ranked.GroupBy(r => (Place: r.Place.WorkspaceId?.ToString() ?? r.Place.Label, r.IsOpen))
            .Select(g => new FindGroup(
                g.Key.IsOpen ? g.First().Place.Label : $"{g.First().Place.Label}, would start here",
                g.First().Place,
                g.Key.IsOpen,
                g.ToList()))
            .ToList();
}
