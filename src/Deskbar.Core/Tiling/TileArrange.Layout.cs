using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Tiling;

// TileArrange on the SPLIT TREE (TileLayout): every rule the Tiler applies to a stored layout. Spec:
// docs/superpowers/specs/2026-11-19-tiling-split-tree-design.md. Every rule here is the old columns-of-rows grid's
// rule restated on the tree, except where the spec changes it on purpose, and each of those says so. The grid
// versions are gone; "the grid's" in the comments below means how the rule read on it.
//
// Leaves are addressed by their index in depth-first order (TileLayout's "slot order"). A window's leaf is its slot
// in the screen's order, counted past the opening when the layout has one (LeafOf), exactly as the grid's CellOf
// counted the opening's cell.
public static partial class TileArrange
{
    // The screen-edge band's depth, a share of the inner area's width (left and right bands) or height (top and
    // bottom). Spec, "Drops": "6% is a starting number for Petre to try live; the builder keeps it in one named
    // constant."
    const double Band = 0.06;

    // The two steps of "Even out tiles" (spec, "Even out tiles"): one menu item whose label and action depend on what
    // the workspace has stored.
    public enum TileTidy { None, EvenOut, ResetGrid }

    // Where a minimized window sat, remembered when it goes (SpotOf) and used when it comes back (Returned): the
    // windows of the subtree next to it in its parent split, which side of them it was on, and its share of the pair.
    public sealed record ReturnSpot(IReadOnlyList<WindowHandle> Siblings, Zone Side, double Share);

    // The leaf of the window at `slot` (its index in the screen's order of `count` windows): one on from the slot at
    // or after the opening, when the layout has an opening for that many windows; otherwise the slot itself.
    public static int LeafOf(TileLayout layout, int slot, int count) =>
        layout.HasOpeningFor(count) && slot >= layout.Opening ? slot + 1 : slot;

    // A shape the user made, as opposed to the automatic one for the window count. The window count is the leaves
    // less the opening's, so a layout with an opening is custom: its extra leaf is no automatic shape's. (That never
    // matters to IsPlain, which refuses an opening anyway.)
    public static bool IsCustom(TileLayout layout) =>
        !layout.SameShapeAs(TileLayout.Automatic(layout.Opening is null ? layout.Count : layout.Count - 1));

    // Nothing worth storing: even splits on the automatic shape, no opening. That is what the screen is laid out as
    // with nothing stored at all, so storing it would only stop the automatic layout following the count.
    public static bool IsPlain(TileLayout layout) => layout.IsEven && !IsCustom(layout) && layout.Opening is null;

    // The stored layout brought to `count` windows, for laying out only (spec, "Arrival and minimize").
    //
    //   - An opening is kept when it is an opening for `count`. One leaver beside an opening removes only its own
    //     leaf (`leavingLeaf`, counted with the opening, LeafOf), so the space stays where it was. Anything else
    //     closes the opening first and fits what is left. All three as the grid's Refit.
    //   - A layout whose leaves match is returned as it is.
    //   - One leaf too many and a known leaver: that leaver's leaf goes, its share to its siblings (TileLayout.Remove),
    //     so nothing else moves. Petre: "on closing, i sometimes want not to move other windows".
    //   - CHANGED on purpose, no known leaver: the LAST leaves go, one by one, each as a leaver's would; and with too
    //     few leaves a new last leaf goes below the last one, once per missing window (TileLayout.AppendBelowLast: the
    //     last row of the last column, the column evened by k/(k+1) as TileSizes.Add did, or a bottom half of the last
    //     leaf when there is no column). The grid rebuilt the automatic shape of the new count instead; the tree
    //     mirrors "drop the bottom rows, rightmost column" and "a joiner goes to the bottom of the last column" without
    //     throwing a hand-made shape away.
    //
    // A leaver index the layout does not have (read from a layout that is gone) is no leaver: the last-leaves rule
    // runs, and nothing throws. Zero windows leave the lone leaf, which is TileLayout.Automatic(0).
    public static TileLayout Refit(TileLayout stored, int count, Maybe<int> leavingLeaf)
    {
        var known = leavingLeaf.HasValue && leavingLeaf.Value >= 0 && leavingLeaf.Value < stored.Count;
        if (stored.Opening is { } open)
            return stored.HasOpeningFor(count) ? stored
                : stored.Count == count + 2 && known && leavingLeaf.Value != open
                    ? stored.Remove(leavingLeaf.Value)
                    : Refit(Closed(stored), count, Maybe<int>.None);

        return stored.Count == count ? stored
            : stored.Count == count + 1 && known ? stored.Remove(leavingLeaf.Value)
            : stored.Count > count && stored.Count > 1 ? Refit(stored.Remove(stored.Count - 1), count, Maybe<int>.None)
            : stored.Count < count ? Refit(stored.AppendBelowLast(), count, Maybe<int>.None)
            : stored;
    }

    // Where a screen's fit to `count` windows STARTS, and which leaver leaf it may use. As the grid's FitStart (final
    // review C1 and M1): the leaver counts only when windows really left (`oldCount` above `count`; a cross-screen swap
    // is no leaver); stored layouts are the start; nothing stored with a leaver starts from Automatic(oldCount), the
    // layout the leaver's leaf was read from; nothing stored without one is Automatic(count).
    public static (TileLayout Start, Maybe<int> Leaving) FitStart(Maybe<TileLayout> stored, int count, Maybe<int> leaf, int oldCount)
    {
        var leaving = oldCount > count ? leaf : Maybe<int>.None;
        var start = stored.HasValue ? stored.Value
            : leaving.HasValue ? TileLayout.Automatic(oldCount)
            : TileLayout.Automatic(count);
        return (start, leaving);
    }

    // `dragged` dropped in `zone` of `target`'s tile (spec, "Drops", step 2). The screen's opening closes first (the
    // drop makes its own room); `dragged` is lifted out of its own leaf, so the target is read in the tree that is
    // left; then the target leaf is split on `zone` (TileLayout.Insert): a sibling when its parent splits that way,
    // otherwise a new split of the leaf, halved either way and nothing else moves. The order is the leaves' windows
    // in leaf order, `dragged` at its new leaf's slot.
    //
    // That is what fixes Petre's case: in Across[Down[A, B], C], C dropped right of A is lifted (the stack becomes
    // the whole screen), and A is split, giving Down[Across[A, C], B]. The grid could only make C a new column.
    //
    // Unchanged, as on the old grid: Middle (the swap is SettledMove's), `dragged == target`, a layout that does
    // not hold one leaf per window, or a target not on this screen. A window dragged in from ANOTHER screen has
    // nothing to lift here; its own screen gives its leaf up as any leaver's.
    public static (IReadOnlyList<WindowHandle> Order, TileLayout Layout) Drop(
        IReadOnlyList<WindowHandle> order, TileLayout layout, WindowHandle dragged, WindowHandle target, Zone zone)
    {
        layout = layout.HasOpeningFor(order.Count) ? Closed(layout) : layout;
        if (zone == Zone.Middle || dragged == target || layout.Count != order.Count || !order.Contains(target))
            return (order, layout);

        var (lifted, rest) = Lifted(order, layout, dragged);
        var (next, leaf) = lifted.Insert(rest.ToList().IndexOf(target), zone);
        return (Insert(rest.ToList(), leaf, dragged), next);
    }

    // `dragged` dropped in the screen-edge band on `side` (spec, "Drops", step 1): lifted as for Drop, then a new leaf
    // along that whole side of the layout (TileLayout.InsertAtEdge), a full-height column or full-width row. Unchanged
    // for Middle (no band is Middle), a layout that does not hold one leaf per window, or a screen whose only window
    // is the dragged one: it has nowhere to go but where it is.
    public static (IReadOnlyList<WindowHandle> Order, TileLayout Layout) DropAtEdge(
        IReadOnlyList<WindowHandle> order, TileLayout layout, WindowHandle dragged, Zone side)
    {
        layout = layout.HasOpeningFor(order.Count) ? Closed(layout) : layout;
        if (side == Zone.Middle || layout.Count != order.Count || order.All(w => w == dragged))
            return (order, layout);

        var (lifted, rest) = Lifted(order, layout, dragged);
        var (next, leaf) = lifted.InsertAtEdge(side);
        return (Insert(rest.ToList(), leaf, dragged), next);
    }

    // The layout and order without `dragged`, when it is on this screen; as they are when it comes from another.
    static (TileLayout Layout, IReadOnlyList<WindowHandle> Others) Lifted(IReadOnlyList<WindowHandle> order, TileLayout layout, WindowHandle dragged) =>
        order.Contains(dragged)
            ? (layout.Remove(order.ToList().IndexOf(dragged)), order.Where(w => w != dragged).ToList())
            : (layout, order);

    // Which screen-edge band of a screen's INNER area a point is in, if any (spec, "Drops", step 1). The band is Band
    // deep: a share of the inner width for the left and right bands, of the height for the top and bottom. In a corner
    // the nearer edge wins, and "nearer" compares SHARES, as ZoneOf does for a tile (ruling R4): at (30, 10) of a
    // 1000 x 600 area the top (1.7%) beats the left (3%). A point in the outer gap, outside the inner area, is nearer
    // than nothing (a negative share) and so in the band beside it.
    public static Maybe<Zone> ScreenBand(WindowRect inner, int x, int y)
    {
        var fx = (x - inner.Left) / (double)Math.Max(1, inner.Width);
        var fy = (y - inner.Top) / (double)Math.Max(1, inner.Height);
        var nearest = new[] { (Side: Zone.Left, Share: fx), (Side: Zone.Right, Share: 1 - fx), (Side: Zone.Top, Share: fy), (Side: Zone.Bottom, Share: 1 - fy) }
            .OrderBy(e => e.Share)
            .First();
        return nearest.Share < Band ? Maybe.From(nearest.Side) : Maybe<Zone>.None;
    }

    // --- openings ---------------------------------------------------------------------------------------------------

    // Leaf `leaf`'s OUTER edge pulled in leaves an opening there (spec, "Openings"). An edge is outer when no split
    // above the leaf has a neighbour on that side (TileLayout.SplitBeside; an inner edge is SplitMove's). The empty
    // leaf is placed exactly as a drop of a window on that side would be (TileLayout.Insert), as deep as the pull:
    // Insert's share is of the leaf's own span on that axis, so the share is the pull over the leaf's span, both as
    // shares of the inner area (TileLayout.ShareOf). Only the pulled tile gives room up.
    //
    // CHANGED on purpose: on the grid a left or right opening was a whole new column and every tile of the column
    // narrowed; here, beside a tile in a stack, only that tile does ("only the tile you touch changes").
    //
    // Kept from the grid: one opening at a time; nothing when the layout does not hold `count` windows; a pull under
    // MinOpening of the inner span is a nudge and snaps back; a tile under 2 * MinOpening on that axis cannot give
    // MinOpening up and keep as much, so it makes none, and the opening is capped so the tile keeps MinOpening. At a
    // corner, the deeper pull wins.
    public static Maybe<TileLayout> Open(TileLayout layout, int count, WindowRect area, int gap, int leaf, Edges moved, WindowRect frame)
    {
        if (layout.Opening is not null || layout.Count != count || leaf < 0 || leaf >= layout.Count)
            return Maybe<TileLayout>.None;
        var inner = GridLayout.InnerArea(area, gap);
        var path = layout.PathTo(leaf);
        var (width, height) = layout.ShareOf(leaf);

        return new[]
            {
                (Edge: Edges.Top, Side: Zone.Top, Depth: (frame.Top - inner.Top) / (double)inner.Height, Room: height),
                (Edge: Edges.Bottom, Side: Zone.Bottom, Depth: (inner.Bottom - frame.Bottom) / (double)inner.Height, Room: height),
                (Edge: Edges.Left, Side: Zone.Left, Depth: (frame.Left - inner.Left) / (double)inner.Width, Room: width),
                (Edge: Edges.Right, Side: Zone.Right, Depth: (inner.Right - frame.Right) / (double)inner.Width, Room: width),
            }
            .Where(p => moved.HasFlag(p.Edge) && TileLayout.SplitBeside(path, p.Side).HasNoValue)
            .Where(p => p.Depth >= MinOpening && p.Room >= 2 * MinOpening)
            .OrderByDescending(p => p.Depth)
            .Select(p =>
            {
                // Strictly inside (0, 1): Depth >= MinOpening > 0 and the cap keeps it MinOpening under Room.
                var (next, at) = layout.Insert(leaf, p.Side, Math.Min(p.Depth, p.Room - MinOpening) / p.Room);
                return Maybe.From(next with { Opening = at });
            })
            .FirstOrDefault(Maybe<TileLayout>.None);
    }

    // True when leaf `leaf` was pulled back over the opening next to it, so the room goes back and the opening closes
    // (spec, "Openings": "a tile pulled back over the opening next to it in the same split closes it, by the same
    // quarter rule"). The moved edge's split is found as SplitMove finds it (TileLayout.SplitBeside), and its
    // neighbour child on the moved side must be the opening's own leaf; the edge must be let go within a quarter of
    // the hole's depth from the hole's far side.
    //
    // This keeps the grid's "whole column" rule without naming columns: an opening that is a whole-height column is
    // the root's neighbour of every tile of the stack beside it, so any of them closes it; an opening inside a stack
    // is not the next column's neighbour (the stack is), so a tile there does not.
    public static bool Closes(TileLayout layout, int count, WindowRect area, int gap, int leaf, Edges moved, WindowRect frame)
    {
        if (layout.Opening is not { } open || leaf < 0 || leaf >= layout.Count
            || !GridLayout.OpeningOf(area, count, gap, layout).TryGetValue(out var hole))
            return false;
        var path = layout.PathTo(leaf);

        bool Faces(Zone side) =>
            TileLayout.SplitBeside(path, side)
                .Map(i =>
                {
                    var step = path[i];
                    var neighbour = side is Zone.Left or Zone.Top ? step.Child - 1 : step.Child + 1;
                    return step.Branch.Children[neighbour].Node is TileLeaf && step.FirstOf(neighbour) == open;
                })
                .GetValueOrDefault(false);

        return (moved.HasFlag(Edges.Top) && Faces(Zone.Top) && frame.Top <= hole.Top + hole.Height / 4)
            || (moved.HasFlag(Edges.Bottom) && Faces(Zone.Bottom) && frame.Bottom >= hole.Bottom - hole.Height / 4)
            || (moved.HasFlag(Edges.Left) && Faces(Zone.Left) && frame.Left <= hole.Left + hole.Width / 4)
            || (moved.HasFlag(Edges.Right) && Faces(Zone.Right) && frame.Right >= hole.Right - hole.Width / 4);
    }

    // KEEP THE SPACE: the closing window's leaf stays, marked empty, and nothing else changes (spec, "Openings").
    // `leaf` is read from the layout as it was before the close. A leaf the layout does not have keeps nothing.
    public static TileLayout KeptOpen(TileLayout layout, int leaf) =>
        leaf >= 0 && leaf < layout.Count ? layout with { Opening = leaf } : layout;

    // The opening closed: its leaf is removed like any leaver's, and its neighbours grow (TileLayout.Remove).
    public static TileLayout Closed(TileLayout layout) =>
        layout.Opening is { } open && open >= 0 && open < layout.Count
            ? layout.Remove(open)
            : layout with { Opening = null };

    // `newcomer` joins: into the opening when the layout has one for `order` (the opening is then filled, gone);
    // otherwise at the end, the layout as it was. Unchanged from the grid.
    public static (IReadOnlyList<WindowHandle> Order, TileLayout Layout) Filled(IReadOnlyList<WindowHandle> order, TileLayout layout, WindowHandle newcomer) =>
        layout.HasOpeningFor(order.Count) && layout.Opening is { } open
            ? (Insert(order.ToList(), open, newcomer), layout with { Opening = null })
            : (order.Append(newcomer).ToList(), layout);

    // A tile of this screen dragged into its opening: its own leaf goes (the tiles around it share the room) and it
    // fills the opening. Unchanged when there is no opening or the window is not on the screen.
    public static (IReadOnlyList<WindowHandle> Order, TileLayout Layout) IntoOpening(IReadOnlyList<WindowHandle> order, TileLayout layout, WindowHandle window) =>
        layout.HasOpeningFor(order.Count) && order.Contains(window)
            ? Filled(order.Where(w => w != window).ToList(), layout.Remove(LeafOf(layout, order.ToList().IndexOf(window), order.Count)), window)
            : (order, layout);

    // --- a minimized window coming back to a screen that changed (spec, "Arrival and minimize") --------------------

    // Where `window` sits, remembered when it is minimized. Its sibling is the child next to it in its parent split:
    // the one before it, else the one after, never the opening's leaf (an empty leaf is nothing to come back beside).
    // `Side` is where the window is relative to that sibling (Right or Bottom when the sibling is before it, along the
    // parent's axis), `Siblings` the windows of the sibling's subtree, and `Share` the window's share of the PAIR,
    // own / (own + sibling's): so Returned, re-splitting the sibling, gives the window the proportion of the pair it
    // had, whatever else the parent held. (Pairwise is the brief's rule; the spec says "its own fraction in that
    // parent split", which for a split of two is the same number.)
    //
    // WALKING UP past the opening (task 5 review): when the window's only sibling in its parent is the opening's leaf
    // (A in Down[Across[A, open], B]), the parent split is read instead, one level up, with the window's whole subtree
    // as "own": A remembers B below the Across it sat in, and the Across's share of that pair, so it comes back above
    // B. In normal form one level is always enough (a split has two children or more, and there is one opening), but
    // the walk goes on up for as long as it has to.
    //
    // None for a lone leaf (nothing to come back beside), a lone window beside the opening (nothing but the opening on
    // the screen), a window not on the screen, or a layout that does not describe the screen's windows.
    public static Maybe<ReturnSpot> SpotOf(IReadOnlyList<WindowHandle> order, TileLayout layout, WindowHandle window)
    {
        var slot = order.ToList().IndexOf(window);
        if (slot < 0 || !Describes(order, layout))
            return Maybe<ReturnSpot>.None;
        var path = layout.PathTo(LeafOf(layout, slot, order.Count));
        var opening = layout.HasOpeningFor(order.Count) ? layout.Opening : null;

        // From the window's own split up to the root: the first with a sibling that is not the opening.
        return Enumerable.Range(0, path.Count).Reverse()
            .Select(level => SiblingIn(path[level], opening).Map(i => SpotIn(order, layout, path[level], i)))
            .FirstOrDefault(spot => spot.HasValue, Maybe<ReturnSpot>.None);
    }

    // The child of `parent` next to the one on the window's path: nearest before first, then nearest after, skipping
    // the opening's leaf.
    static Maybe<int> SiblingIn(TileLayout.PathStep parent, int? opening) =>
        Enumerable.Range(0, parent.Child).Reverse()
            .Concat(Enumerable.Range(parent.Child + 1, parent.Branch.Children.Count - parent.Child - 1))
            .Where(i => !(parent.Branch.Children[i].Node is TileLeaf && parent.FirstOf(i) == opening))
            .Select(i => Maybe.From(i))
            .FirstOrDefault(Maybe<int>.None);

    // The spot beside child `sibling` of `parent`, for the window whose subtree is the parent's child on the path.
    static ReturnSpot SpotIn(IReadOnlyList<WindowHandle> order, TileLayout layout, TileLayout.PathStep parent, int sibling)
    {
        var children = parent.Branch.Children;
        var across = parent.Branch.Axis == Axis.Across;
        var side = sibling < parent.Child ? across ? Zone.Right : Zone.Bottom : across ? Zone.Left : Zone.Top;
        var siblings = Enumerable.Range(parent.FirstOf(sibling), TileLayout.LeafCount(children[sibling].Node))
            .Select(l => WindowIn(order, layout, l))
            .Where(w => w.HasValue)
            .Select(w => w.Value)
            .ToList();
        var own = children[parent.Child].Share;
        return new ReturnSpot(siblings, side, own / (own + children[sibling].Share));
    }

    // `window` comes back to a screen whose windows are `order`, laid out with `layout`, beside what it was next to.
    // The node split is the LARGEST subtree (most leaves; a tie goes to the first in leaf order) whose leaves are all
    // windows of `spot.Siblings` (at least one, the opening in none of them). It is split on `spot.Side` with
    // `spot.Share` (TileLayout.InsertBeside: a sibling when its parent splits that way, otherwise a wrapper), clamped to
    // SplitMove.MinShare as the spec says, so a sliver comes back usable. A full-height column beside a stack comes back
    // full height while the stack is still a stack, and beside the first of its old neighbours when the stack was
    // broken up. Only that node changes size.
    //
    // CHANGED on purpose: this replaces the grid's "old column still there" rule and keeps its intent.
    //
    // None when no sibling is on the screen (the caller then arrives the window as a new one), when the window is on
    // the screen already, or when the layout does not describe `order`. An opening after the new leaf moves on one and
    // stays open; the window's place in the order skips an opening before it.
    //
    // A STALE opening (task 5 review: one leaf per window and an Opening still named, so the "opening" is a window's
    // leaf; HasOpeningFor says no) is no opening: the mark is dropped first. Kept, it shifted the window's place in the
    // order by one (C put between A and B while its leaf was the last), and a later Refit would read it as an opening
    // to close and take a window's leaf with it.
    public static Maybe<(IReadOnlyList<WindowHandle> Order, TileLayout Layout)> Returned(
        IReadOnlyList<WindowHandle> order, TileLayout layout, WindowHandle window, ReturnSpot spot)
    {
        layout = layout.Opening is not null && !layout.HasOpeningFor(order.Count) ? layout with { Opening = null } : layout;
        if (order.Contains(window) || !Describes(order, layout))
            return Maybe<(IReadOnlyList<WindowHandle>, TileLayout)>.None;
        var open = layout.HasOpeningFor(order.Count);

        return Subtrees(layout.Root, 0)
            .Where(t => Enumerable.Range(t.First, t.Leaves)
                .All(l => WindowIn(order, layout, l).Map(w => spot.Siblings.Contains(w)).GetValueOrDefault(false)))
            .OrderByDescending(t => t.Leaves)
            .ThenBy(t => t.First)
            .Select(t => Maybe.From(t))
            .FirstOrDefault(Maybe<(int First, int Leaves)>.None)
            .Map(t =>
            {
                var (next, leaf) = layout.InsertBeside(t.First, t.Leaves, spot.Side,
                    Math.Clamp(spot.Share, SplitMove.MinShare, 1 - SplitMove.MinShare));
                var slot = open && next.Opening < leaf ? leaf - 1 : leaf;
                return (Insert(order.ToList(), slot, window), next);
            });
    }

    // The window in leaf `leaf`: None for the opening's leaf (when the layout has one for `order`) or a leaf past the
    // windows.
    static Maybe<WindowHandle> WindowIn(IReadOnlyList<WindowHandle> order, TileLayout layout, int leaf)
    {
        var slot = layout.HasOpeningFor(order.Count) && layout.Opening is { } open
            ? leaf == open ? -1 : leaf > open ? leaf - 1 : leaf
            : leaf;
        return slot >= 0 && slot < order.Count ? Maybe.From(order[slot]) : Maybe<WindowHandle>.None;
    }

    // The layout lays out exactly these windows: one leaf each, or one each plus an opening.
    static bool Describes(IReadOnlyList<WindowHandle> order, TileLayout layout) =>
        layout.Count == order.Count || layout.HasOpeningFor(order.Count);

    // Every node of the tree as (its first leaf, its leaf count), the node itself before its children.
    static IEnumerable<(int First, int Leaves)> Subtrees(TileNode node, int first) =>
        node is TileBranch b
            ? b.Children
                .Select((c, i) => (c.Node, First: first + b.Children.Take(i).Sum(k => TileLayout.LeafCount(k.Node))))
                .SelectMany(c => Subtrees(c.Node, c.First))
                .Prepend((first, TileLayout.LeafCount(node)))
            : [(first, 1)];

    // --- even out tiles ---------------------------------------------------------------------------------------------

    // Which step the one menu item offers for a workspace's stored layouts (spec, "Even out tiles"): while any split is
    // uneven, "Even out tiles" (equal splits, shape kept); once all are even but something is still stored, a
    // hand-made shape remains, so "Reset to grid" (clear the stored layouts); with nothing stored, nothing.
    public static TileTidy TidyStep(IEnumerable<TileLayout> stored)
    {
        var layouts = stored.ToList();
        return layouts.Any(l => !l.IsEven) ? TileTidy.EvenOut
            : layouts.Count > 0 ? TileTidy.ResetGrid
            : TileTidy.None;
    }
}
