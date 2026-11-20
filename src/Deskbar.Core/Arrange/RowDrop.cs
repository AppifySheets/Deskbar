using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;

namespace Deskbar.Core.Arrange;

// The arrange mode's drop rules (docs/superpowers/specs/2026-11-20-bar-edit-mode-design.md, "What a drop does"),
// as pure functions of the state. Two questions, answered here and nowhere else:
//
//   * Plan: the pointer is over THIS row, in THIS zone of it, carrying THAT row. What would a release do, and
//     what should the bar draw while the pointer is there? The bar only measures where the pointer is within the
//     row (top quarter, bottom quarter, the middle) and asks.
//   * Applied: given an action Plan produced, what is the state afterwards? The order a drop leaves is built in
//     this one place, so the line the bar showed and the order the manager then persists cannot disagree, and a
//     drop that would change nothing is recognised by the very same arithmetic that would have performed it.
//
// No UI and no persistence here. Name validation (a taken group name) and the Persist call are the apply step's
// job; planning deliberately does not look at names.

// A row on the bar that can be dragged or dropped on: a workspace row, or a group's header.
public abstract record RowRef
{
    public sealed record Workspace(Guid Id) : RowRef;
    public sealed record Group(Guid Id) : RowRef;
}

// Where in the row under the pointer the release would land. Before and After are its top and bottom quarter,
// Onto is the middle half, End is the empty space below the last row (there is no row under the pointer then).
public enum DropZone { Before, After, Onto, End }

// What a drop would do, or Nothing. Beside is always a top-level row, or a member of a group; After says which
// side of it.
public abstract record DropAction
{
    public sealed record Nothing : DropAction;

    // Moves the workspace. It joins Beside's group if Beside is a member, and leaves its own group if Beside is
    // a top-level row (Beside = Group(id) is the whole box: before its first member, or after its last).
    public sealed record Place(Guid Workspace, RowRef Beside, bool After) : DropAction;

    // A new group of Target then Dragged, at Target's place. The name is asked for by the bar, prefilled with
    // SuggestedName.
    public sealed record GroupWith(Guid Target, Guid Dragged, string SuggestedName) : DropAction;

    // The whole box moves, members in order. Beside is a top-level row.
    public sealed record PlaceGroup(Guid Group, RowRef Beside, bool After) : DropAction;
}

// What the bar draws while the pointer is there.
public abstract record DropShow
{
    public sealed record None : DropShow;

    // An insertion line beside a row. InsideGroup: drawn short, inside the group's bracket, for a join; full
    // width for a place among the top-level rows.
    public sealed record Line(RowRef Beside, bool After, bool InsideGroup) : DropShow;

    // The target outlined. Hint reads "Group with {name}…" when the drop makes a group, null for a plain join.
    public sealed record Highlight(RowRef Target, string? Hint) : DropShow;
}

public static class RowDrop
{
    static readonly (DropAction Action, DropShow Show) NoDrop = (new DropAction.Nothing(), new DropShow.None());

    // What releasing `dragged` over `target` in `zone` does. `target` is null only for the empty space below the
    // last row (DropZone.End). Everything that is not a change comes back as (Nothing, None), including a drop
    // that is legal but would leave the order exactly as it is: the bar then shows no line, which is the honest
    // answer to "what will this do".
    public static (DropAction Action, DropShow Show) Plan(AppState state, RowRef dragged, RowRef? target, DropZone zone) =>
        Proposed(state, dragged, target, zone) is var proposal
        && proposal.Action is not DropAction.Nothing
        && !LeavesOrderAsItIs(state, proposal.Action)
            ? proposal
            : NoDrop;

    // --- the rules ---------------------------------------------------------------------------------------

    static (DropAction Action, DropShow Show) Proposed(AppState state, RowRef dragged, RowRef? target, DropZone zone) =>
        dragged switch
        {
            RowRef.Workspace d when state.Workspaces.Any(w => w.Id == d.Id) => WorkspaceDrop(state, d.Id, target, zone),
            RowRef.Group g when state.Groups.Any(x => x.Id == g.Id) => GroupDrop(state, g.Id, target, zone),
            _ => NoDrop,
        };

    // A dragged workspace row.
    static (DropAction Action, DropShow Show) WorkspaceDrop(AppState state, Guid dragged, RowRef? target, DropZone zone) =>
        (target, zone) switch
        {
            // The empty space below the last row: ungrouped, at the end. A pinned row is drawn in the pinned
            // block at the top, so "the end of the bar" is not a place it can go.
            (null, DropZone.End) when !IsPinned(state, dragged) && LastUnpinnedRef(state) is { } last =>
                (new DropAction.Place(dragged, last, true), new DropShow.Line(last, true, false)),

            (RowRef.Workspace t, _) when t.Id != dragged && state.Workspaces.FirstOrDefault(w => w.Id == t.Id) is { } row =>
                OntoWorkspaceRow(state, dragged, row, zone),

            (RowRef.Group h, _) when state.MembersOf(h.Id).FirstOrDefault() is { } first && !IsPinned(state, dragged) =>
                zone switch
                {
                    // The top quarter of a header is "above the whole group", at the top level.
                    DropZone.Before => (new DropAction.Place(dragged, h, false), new DropShow.Line(h, false, false)),

                    // The rest of the header joins the group at the top: the same result as the top of its
                    // first member, drawn short inside the bracket.
                    DropZone.Onto or DropZone.After =>
                        (new DropAction.Place(dragged, new RowRef.Workspace(first.Id), false),
                         new DropShow.Line(new RowRef.Workspace(first.Id), false, true)),

                    _ => NoDrop,
                },

            _ => NoDrop,
        };

    // A dragged workspace over another workspace's row: a member of a group, or a top-level row.
    static (DropAction Action, DropShow Show) OntoWorkspaceRow(AppState state, Guid dragged, Workspace row, DropZone zone)
    {
        var beside = new RowRef.Workspace(row.Id);
        var draggedPinned = IsPinned(state, dragged);

        // A member of a group. Both gaps and the middle all mean "in this group", at this row's level; only the
        // middle differs, since "onto a row of its own group" is nothing, where a stranger joins below.
        if (state.GroupOf(row.Id) is { } group)
            return draggedPinned
                ? NoDrop // a pinned row sits among the pinned rows only, and a group member is never pinned
                : zone switch
                {
                    DropZone.Before or DropZone.After =>
                        (new DropAction.Place(dragged, beside, zone == DropZone.After),
                         new DropShow.Line(beside, zone == DropZone.After, true)),
                    DropZone.Onto when state.GroupOf(dragged)?.Id != group.Id =>
                        (new DropAction.Place(dragged, beside, true), new DropShow.Highlight(beside, null)),
                    _ => NoDrop,
                };

        // A top-level row. A pinned row and an unpinned one are drawn in different blocks, so neither can go
        // beside the other, and neither can be grouped with the other: a group is never pinned.
        if (IsPinned(state, row.Id) != draggedPinned) return NoDrop;

        return zone switch
        {
            DropZone.Before or DropZone.After =>
                (new DropAction.Place(dragged, beside, zone == DropZone.After),
                 new DropShow.Line(beside, zone == DropZone.After, false)),
            DropZone.Onto when !draggedPinned =>
                (new DropAction.GroupWith(row.Id, dragged, row.Name), new DropShow.Highlight(beside, $"Group with {row.Name}…")),
            _ => NoDrop,
        };
    }

    // A dragged group header: the whole box, among the top-level rows only. Never inside a group, never onto a
    // row (that would make a group inside a group, which does not exist), never among the pinned rows.
    static (DropAction Action, DropShow Show) GroupDrop(AppState state, Guid dragged, RowRef? target, DropZone zone) =>
        (target, zone) switch
        {
            (null, DropZone.End) when LastUnpinnedRef(state) is { } last =>
                (new DropAction.PlaceGroup(dragged, last, true), new DropShow.Line(last, true, false)),

            (RowRef.Workspace t, DropZone.Before or DropZone.After)
                when state.Workspaces.FirstOrDefault(w => w.Id == t.Id) is { GroupId: null, Pinned: false } =>
                (new DropAction.PlaceGroup(dragged, t, zone == DropZone.After), new DropShow.Line(t, zone == DropZone.After, false)),

            // Another header: only its top quarter means "before that group". Its lower part would be "join" for a
            // row, and means nothing for a box.
            (RowRef.Group h, DropZone.Before) when h.Id != dragged && state.MembersOf(h.Id).Count > 0 =>
                (new DropAction.PlaceGroup(dragged, h, false), new DropShow.Line(h, false, false)),

            _ => NoDrop,
        };

    // The last top-level row in the order the bar DRAWS (pinned rows first, so the last drawn is the last
    // unpinned one): a lone workspace, or the whole box when a group is last.
    static RowRef? LastUnpinnedRef(AppState state) =>
        state.Blocks().LastOrDefault(b => !IsPinnedBlock(b)) is { } block ? RefOf(state, block) : null;

    static RowRef RefOf(AppState state, IReadOnlyList<Workspace> block) =>
        block[0].GroupId is { } id && state.Groups.Any(g => g.Id == id)
            ? new RowRef.Group(id)
            : new RowRef.Workspace(block[0].Id);

    // Only an ungrouped workspace is pinned (WorkspaceManager.SetWorkspacePinned refuses a member).
    static bool IsPinned(AppState state, Guid id) =>
        state.Workspaces.Any(w => w.Id == id && w.Pinned && w.GroupId is null);

    static bool IsPinnedBlock(IReadOnlyList<Workspace> block) => block is [{ Pinned: true, GroupId: null }];

    // A drop that leaves every workspace in its place and every membership as it was is not a drop. Asked of
    // Applied, not of a second set of rules, so "nothing would change" and "what would change" cannot disagree.
    static bool LeavesOrderAsItIs(AppState state, DropAction action) =>
        Applied(state, action, action is DropAction.GroupWith g ? g.SuggestedName : null) is var after
        && after.Groups.Count == state.Groups.Count
        && after.Workspaces.Select(w => (w.Id, w.GroupId)).SequenceEqual(state.Workspaces.Select(w => (w.Id, w.GroupId)));

    // --- the order a drop leaves ---------------------------------------------------------------------------

    // The state after the drop. Does NO validation of the group name (the apply step refuses a taken one) and NO
    // persistence. `groupName` is read only by GroupWith, trimmed, and falls back to the suggestion when null.
    // An action that does not fit the state (a row that is gone, a Beside that is not on the dragged row's side of
    // the bar) comes back as the state unchanged, never as a half-done drop.
    public static AppState Applied(AppState state, DropAction action, string? groupName) =>
        action switch
        {
            DropAction.Place p => PlacedWorkspace(state, p),
            DropAction.PlaceGroup p => PlacedGroup(state, p),
            DropAction.GroupWith g => Grouped(state, g, groupName),
            _ => state,
        };

    static AppState PlacedWorkspace(AppState state, DropAction.Place place)
    {
        if (state.Workspaces.FirstOrDefault(w => w.Id == place.Workspace) is not { } dragged) return state;

        // Beside the dragged row itself is a drop on its own place.
        if (place.Beside is RowRef.Workspace { Id: var self } && self == dragged.Id) return state;

        // The workspace Beside points at, as a row to measure from. For a whole box it is the member at the end
        // being dropped beside, MINUS the dragged row: when the dragged row is leaving that very box, the box
        // is measured as it will be once it has left.
        var anchor = place.Beside switch
        {
            RowRef.Workspace w => (Guid?)w.Id,
            RowRef.Group g => state.MembersOf(g.Id).Where(m => m.Id != dragged.Id)
                .Select(m => (Guid?)m.Id)
                .Aggregate((Guid?)null, (first, m) => place.After ? m : first ?? m),
            _ => null,
        };
        if (anchor is not { } anchorId) return state;

        // Reordering INSIDE its own group is the one move that does not leave it. Everything else does, through
        // the same rule as "move out of the group" (a group left with one member is dissolved), done first so the
        // bar never holds a half-moved state.
        var staysInGroup = place.Beside is RowRef.Workspace
            && dragged.GroupId is { } own
            && state.Workspaces.Any(w => w.Id == anchorId && w.GroupId == own);
        var left = staysInGroup ? state : WorkspaceManager.WithoutMember(state, dragged.Id);
        var moved = left.Workspaces.Single(w => w.Id == dragged.Id);

        // Beside a member of a group means joining that group; beside a top-level row or a whole box, standing alone.
        var joins = place.Beside is RowRef.Workspace ? left.GroupOf(anchorId)?.Id : null;

        return Rearranged(left, IsPinned(state, dragged.Id), side =>
            WithoutWorkspace(side, moved.Id) is var rest && rest.FindIndex(b => b.Exists(w => w.Id == anchorId)) is var at and >= 0
                ? joins is { } group
                    ? rest.Select((b, i) => i == at
                            ? InsertedAt(b, b.FindIndex(w => w.Id == anchorId) + (place.After ? 1 : 0), moved with { GroupId = group })
                            : b)
                        .ToList()
                    : InsertedAt(rest, at + (place.After ? 1 : 0), [moved with { GroupId = null }])
                : null)
            is { } order
                ? left with { Workspaces = order }
                : state;
    }

    static AppState PlacedGroup(AppState state, DropAction.PlaceGroup place)
    {
        if (state.MembersOf(place.Group).Count == 0) return state;

        // The block Beside names. Beside the group itself, or one of its members, is a drop on its own place.
        Func<IReadOnlyList<Workspace>, bool> isBeside = place.Beside switch
        {
            RowRef.Workspace w => b => b.Any(x => x.Id == w.Id),
            RowRef.Group g => b => b.Any(x => x.GroupId == g.Id),
            _ => _ => false,
        };
        if (state.Blocks().Any(b => b.Any(x => x.GroupId == place.Group) && isBeside(b))) return state;

        // A group is never pinned, so it moves among the unpinned rows only.
        return Rearranged(state, pinnedSide: false, side =>
            side.FindIndex(b => b.Exists(x => x.GroupId == place.Group)) is var mine and >= 0
            && side.Where((_, i) => i != mine).ToList() is var rest
            && rest.FindIndex(b => isBeside(b)) is var at and >= 0
                ? InsertedAt(rest, at + (place.After ? 1 : 0), side[mine])
                : null)
            is { } order
                ? state with { Workspaces = order }
                : state;
    }

    static AppState Grouped(AppState state, DropAction.GroupWith grouping, string? groupName)
    {
        if (grouping.Target == grouping.Dragged
            || state.Workspaces.FirstOrDefault(w => w.Id == grouping.Target) is not { GroupId: null } target
            || state.Workspaces.All(w => w.Id != grouping.Dragged)
            || IsPinned(state, grouping.Dragged))
            return state;

        // The dragged row leaves its group first, as in Place, so leaving a group of two dissolves it.
        var left = WorkspaceManager.WithoutMember(state, grouping.Dragged);
        var moved = left.Workspaces.Single(w => w.Id == grouping.Dragged);

        // The group takes the colour its first workspace was wearing, as CreateGroup does, so making a group
        // does not recolour the row it was made from.
        var group = new Group(Guid.NewGuid(), (groupName ?? grouping.SuggestedName).Trim()) { Color = target.Color };

        return Rearranged(left, pinnedSide: false, side =>
            WithoutWorkspace(side, moved.Id) is var rest && rest.FindIndex(b => b.Exists(w => w.Id == target.Id)) is var at and >= 0
                ? rest.Select((b, i) => i == at ? new List<Workspace> { target with { GroupId = group.Id }, moved with { GroupId = group.Id } } : b)
                    .ToList()
                : null)
            is { } order
                ? left with { Workspaces = order, Groups = [.. left.Groups, group] }
                : state;
    }

    // Edits the blocks drawn on ONE side of the bar (the pinned rows, or every other block) and writes the edited
    // sequence back into the slots that side already held in the stored list, leaving the other side's blocks
    // where they were. That is ReorderedOnItsSide's rule (WorkspaceManager), and it is what lets an unpinned row
    // come back where it was after being pinned. Null from `edit` means "does not fit", and passes through.
    //
    // If the edit changed how many blocks the side has (a row left a group to stand alone, or two rows became
    // one box), the sequence still goes into the side's slots in order: extra blocks follow the side's last slot,
    // and a shortfall drops the side's trailing slots. The order within the side is always the edited one, and
    // the other side keeps its relative order, which is all the bar draws.
    static List<Workspace>? Rearranged(AppState state, bool pinnedSide, Func<List<List<Workspace>>, List<List<Workspace>>?> edit)
    {
        var blocks = state.Blocks().Select(b => b.ToList()).ToList();
        var slots = Enumerable.Range(0, blocks.Count).Where(i => IsPinnedBlock(blocks[i]) == pinnedSide).ToList();

        return edit(slots.Select(i => blocks[i]).ToList()) is { } edited
            ? blocks.SelectMany((b, i) => Filled(b, slots.IndexOf(i), slots.Count, edited)).SelectMany(b => b).ToList()
            : null;
    }

    // What block `b` of the stored list becomes: itself when it is on the other side, else the edited block that
    // takes over its slot (plus every extra block when it is the side's last slot).
    static IEnumerable<List<Workspace>> Filled(List<Workspace> b, int slot, int slotCount, List<List<Workspace>> edited) =>
        slot < 0
            ? [b]
            : edited.Skip(slot).Take(1).Concat(slot == slotCount - 1 ? edited.Skip(slot + 1) : []);

    // The blocks with one workspace taken out, a block left empty gone. A group left short keeps its remaining
    // members as one block: the dissolve rule has already run on the state by the time this is called.
    static List<List<Workspace>> WithoutWorkspace(List<List<Workspace>> blocks, Guid id) =>
        blocks.Select(b => b.Where(w => w.Id != id).ToList()).Where(b => b.Count > 0).ToList();

    static List<T> InsertedAt<T>(IEnumerable<T> items, int index, T item) =>
        items.Take(index).Append(item).Concat(items.Skip(index)).ToList();
}
