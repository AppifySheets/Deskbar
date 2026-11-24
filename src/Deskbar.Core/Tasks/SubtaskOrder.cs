using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// SUBTASKS UNDER THEIR PARENT. Follow-up to docs/superpowers/specs/2026-11-24-subtasks-design.md, after Petre's
// screenshot of "↳ test1" and "↳ test" at the top of quadrant IV, four lines above the parent they were added from.
// The first build kept every line where its own flags and rank put it; a subtask read as lost. Now a subtask is drawn
// right after its parent, in the parent's place, wherever its own flags would have put it.
//
// One rule for the bar and the tasks panel, applied to the lines a surface actually shows (after its filters), so a
// parent that is filtered out (assigned to Claude and so not on the bar, on another workspace in the panel) leaves its
// subtasks where they were. Pure and generic over the line type, since the bar orders TaskLines and the panel TaskItems.
//
// What stays where it was:
//   * a line the caller says may not move (`movable`): a subtask WAITING ON YOU keeps its place in the pinned block,
//     since that block is what makes a question findable;
//   * a subtask whose parent is not in the list;
//   * a task naming itself, or a chain from a hand-edited file (A under B under C): only a parent that is not itself
//     being moved takes children, so nothing is lost or drawn twice. TaskBoard keeps one level for anything made here.
// Siblings keep the order they had in the list. The bar's ranks, flags and `task list` are untouched: this is drawing.
public static class SubtaskOrder
{
    /// <summary>
    /// <paramref name="items"/> with every movable subtask moved to just after its parent (when the parent is in the list),
    /// siblings in their original order; everything else as it was. A duplicate id (a hand-edited tasks.json; review of
    /// #467, finding 5) neither throws nor draws children twice: the first line with an id stands for it, and the
    /// children follow that one only.
    /// </summary>
    public static IReadOnlyList<T> UnderParents<T>(IReadOnlyList<T> items, Func<T, Guid> id, Func<T, Maybe<Guid>> parent, Func<T, bool> movable)
    {
        var first = FirstOf(items, id);
        bool Moves(T t) => MovesUnder(t, first, id, parent, movable);
        var children = items.Where(Moves).ToLookup(t => parent(t).Value);
        return items.Select((t, i) => (Item: t, Index: i))
            .Where(x => !Moves(x.Item))
            .SelectMany(x => first[id(x.Item)].Index == x.Index ? children[id(x.Item)].Prepend(x.Item) : [x.Item])
            .ToArray();
    }

    /// <summary>Whether a line is drawn nested under its parent in <paramref name="items"/> (UnderParents moved it).</summary>
    public static IReadOnlySet<Guid> Nested<T>(IReadOnlyList<T> items, Func<T, Guid> id, Func<T, Maybe<Guid>> parent, Func<T, bool> movable)
    {
        var first = FirstOf(items, id);
        return items.Where(t => MovesUnder(t, first, id, parent, movable)).Select(id).ToHashSet();
    }

    // The first line with each id, and where it is.
    static IReadOnlyDictionary<Guid, (T Item, int Index)> FirstOf<T>(IReadOnlyList<T> items, Func<T, Guid> id) =>
        items.Select((t, i) => (Item: t, Index: i)).GroupBy(x => id(x.Item)).ToDictionary(g => g.Key, g => g.First());

    // Wants to move: movable, names a parent other than itself, and that parent is in the list. Moves: a candidate whose
    // parent stays put, so the parent is emitted at the top level and takes it along.
    static bool MovesUnder<T>(T t, IReadOnlyDictionary<Guid, (T Item, int Index)> first, Func<T, Guid> id, Func<T, Maybe<Guid>> parent, Func<T, bool> movable)
    {
        bool Candidate(T x) => movable(x) && parent(x) is { HasValue: true } p && p.Value != id(x) && first.ContainsKey(p.Value);
        return Candidate(t) && !Candidate(first[parent(t).Value].Item);
    }
}
