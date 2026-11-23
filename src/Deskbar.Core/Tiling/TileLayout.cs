using System.Text.Json.Serialization;
using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Tiling;

// A screen's tiling layout as a SPLIT TREE (spec: docs/superpowers/specs/2026-11-19-tiling-split-tree-design.md).
// It replaced the columns-of-rows grid (TileSizes, now read only by state.json's migration through FromGrid).
//
// Why a tree: "columns of rows" cannot say "A and C side by side on top, B full width underneath", so a drop
// beside a stacked tile had nowhere to go. A tree can: every split divides its span between children, and a child
// can be a split in the other direction.
//
//   Across: children side by side, left to right.   Down: children stacked, top to bottom.
//
// Slot order is the leaves depth-first, children in order. For an Across of Down columns (today's grid) that is
// column by column, top to bottom, which is today's slot order, so everything that maps a window to a slot keeps
// its meaning.
public enum Axis { Across, Down }

// One node of the tree. The discriminator is "kind": "leaf" or "split" in state.json.
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(TileLeaf), "leaf")]
[JsonDerivedType(typeof(TileBranch), "split")]
public abstract record TileNode;

// One cell. It holds a window, or is the layout's opening (TileLayout.Opening names which leaf).
public sealed record TileLeaf : TileNode
{
    public static TileLeaf Instance { get; } = new();
}

// A split: children laid along Axis, each taking its Share of the span. ValueList gives the record value equality.
public sealed record TileBranch(Axis Axis, ValueList<TileChild> Children) : TileNode;

// A child and the fraction of its parent's span it takes. A branch's shares sum to 1.
public sealed record TileChild(TileNode Node, double Share);

// The tree plus the opening. Equality is Root and Opening (the record's own), and TileBranch compares by content.
//
// Normal form, enforced by Normal() and so by every operation that builds an edited tree:
//   - a split has at least two children; a split left with one is replaced by that child;
//   - a split's child is never a split of the same axis: it is merged into the parent, its shares scaled by its
//     own share. Three columns are ONE Across of three, exactly as today's Columns list.
public sealed record TileLayout(TileNode Root)
{
    // Two shares closer than this are the same (also what "even" means), as in the old grid.
    const double Tolerance = 1e-9;

    // The leaf (by its index in depth-first order) that holds no window; null for none. Petre: "keep the opening for
    // the new window to jump into". The layout then describes one cell more than the screen has windows.
    public int? Opening { get; init; }

    // Number of leaves, the opening counted. Computed, so not stored: the stored JSON is the tree and the opening
    // (ruling R3).
    [JsonIgnore]
    public int Count => LeafCount(Root);

    // The automatic layout for n windows: GridLayout.Shape(n) as a tree, Across of Down columns with even shares.
    // A one-row column is a leaf; Normal turns one column into a Down root and one window into a lone leaf.
    public static TileLayout Automatic(int count) =>
        Normal(
            Split(Axis.Across, GridLayout.Shape(count)
                .Select(rows => Split(Axis.Down, Enumerable.Repeat(TileLeaf.Instance, rows).Cast<TileNode>().ToList()))
                .ToList()),
            null);

    // Migration: the stored columns-of-rows sizes as a tree, an Across of columns each a Down of its rows, in normal
    // form, the opening's slot mapped to the same leaf index (slots and leaves are numbered alike).
    public static TileLayout FromGrid(TileSizes grid) =>
        Normal(
            new TileBranch(Axis.Across, grid.Columns
                .Select((share, column) => new TileChild(
                    new TileBranch(Axis.Down, grid.Rows[column]
                        .Select(row => new TileChild(TileLeaf.Instance, row))
                        .ToValueList()),
                    share))
                .ToValueList()),
            grid.Opening);

    // The only way to build an edited tree: applies normal form. A tree with no leaf at all becomes a lone leaf.
    public static TileLayout Normal(TileNode root, int? opening) =>
        new(Flatten(root)) { Opening = opening };

    // This layout lays out `windows` windows around its opening: there is one, and exactly one cell more than windows.
    public bool HasOpeningFor(int windows) =>
        Opening is { } open && Count == windows + 1 && open >= 0 && open <= windows;

    // True when every split's shares are 1/n.
    [JsonIgnore]
    public bool IsEven => AllBranches(Root).All(b => IsEvenShares(b.Children));

    // Same structure, every split even, opening kept.
    public TileLayout Evened() => this with { Root = EvenNode(Root) };

    // The structure is equal, shares ignored. The opening is not part of the structure (compare it separately).
    public bool SameShapeAs(TileLayout other) => SameStructure(Root, other.Root);

    // The same layout up to rounding: the same structure and opening, every share within a millionth of the other's.
    // Two routes to one layout (leaves removed in another order, Tiler.Minimize.cs's EarlierScreen) can differ in the
    // last bits of a share, which exact record equality would call a different layout; a millionth of a 4K screen is
    // far below a pixel.
    public bool Near(TileLayout other) => Opening == other.Opening && NearNode(Root, other.Root);

    static bool NearNode(TileNode a, TileNode b) =>
        (a, b) switch
        {
            (TileLeaf, TileLeaf) => true,
            (TileBranch x, TileBranch y) =>
                x.Axis == y.Axis
                && x.Children.Count == y.Children.Count
                && x.Children.Zip(y.Children, (l, r) => Math.Abs(l.Share - r.Share) < 1e-6 && NearNode(l.Node, r.Node)).All(near => near),
            _ => false
        };

    // The leaf's share of the INNER area on each axis: the product of the shares of the Across splits (width) and of
    // the Down splits (height) on its path. TileArrival's floor and largest-tile rules read these where they read
    // Columns[c] and Rows[c][r] today.
    // A leaf index outside 0..Count-1 is a caller bug and throws, rather than answering a made-up share.
    public (double Width, double Height) ShareOf(int leaf) =>
        InRange(leaf) ? SharesAlong(Root, leaf) : throw OutOfRange(leaf);

    // ---- edits -----------------------------------------------------------------------------------------------

    // Takes leaf number `leaf` out (a window left, or is being lifted for a drop). Its share goes to its siblings in
    // proportion, so the others keep their relative sizes. A split left with one child is replaced by it (Normal), and
    // that can cascade into the grandparent's axis and merge. The opening follows: removing the opening clears it, one
    // above the removed leaf moves down one. Removing the only leaf leaves a lone leaf.
    public TileLayout Remove(int leaf) =>
        InRange(leaf)
            ? Normal(RemoveLeaf(Root, leaf) ?? TileLeaf.Instance, Opening switch
            {
                { } open when open == leaf => null,
                { } open when open > leaf => open - 1,
                var open => open
            })
            : throw OutOfRange(leaf);

    // Drops a NEW leaf on `side` of leaf `target` and returns the layout with the new leaf's index.
    //   - The target's parent splits along the side's axis: the new leaf becomes the target's sibling on that side
    //     and takes `share` of the TARGET's share (the target keeps the rest, the other siblings do not change).
    //   - Otherwise the target becomes a split of that axis holding target (1 - share) and new (share), in side order.
    // Both are one operation: replace the target by a two-child split along the axis and let Normal merge it into a
    // parent of the same axis, which scales the two by the target's old share. So there is no separate "sibling" code.
    // A lone-leaf root is the second case. Side is a screen side; Middle is not (it means "swap", and the caller
    // handles that before it gets here). `share` must be strictly between 0 and 1: either end makes a leaf of no
    // size, and NaN or anything outside makes a negative one. That is a caller bug (TileArrange.Open and Returned
    // clamp before they get here), so it throws rather than storing a layout nothing can lay out.
    public (TileLayout Layout, int Leaf) Insert(int target, TileArrange.Zone side, double share = 0.5) =>
        InRange(target) ? InsertBeside(target, 1, side, share) : throw OutOfRange(target);

    // Insert's general form: splits the SUBTREE spanning leaves first .. first + leaves - 1 on `side`, the new leaf
    // taking `share` of that subtree's share (a sibling when the subtree's parent splits along the side's axis,
    // otherwise a wrapper; Normal decides, exactly as for a leaf). A minimized window coming back beside a whole
    // stack needs this (TileArrange.Returned): the stack is one node, and the window goes beside all of it.
    // In normal form no split has one child, so a leaf range names at most one node; a range that names none throws.
    internal (TileLayout Layout, int Leaf) InsertBeside(int first, int leaves, TileArrange.Zone side, double share)
    {
        if (leaves < 1 || !InRange(first) || !InRange(first + leaves - 1)) throw OutOfRange(first);
        RequireSide(side);
        RequireShare(share);
        var leaf = side is TileArrange.Zone.Left or TileArrange.Zone.Top ? first : first + leaves;
        return (Normal(MapNode(Root, first, leaves, old => SplitNode(old, TileLeaf.Instance, side, share)), OpeningAfterInsert(leaf)), leaf);
    }

    // A joiner with no better place (TileArrange.Refit, no known leaver): a new LAST leaf, below the last leaf.
    //   - The last leaf's parent splits Down (a column of rows): the new leaf becomes that split's last child with
    //     1/(k+1) of it, k being its children before the add, and every other child is scaled by k/(k+1). That is the
    //     old TileSizes.Add rule, so repeated joins keep the column even: a column of two that three join is five
    //     rows of a fifth, where halving the last leaf each time left rows of 1/2, 1/4, 1/8, 1/16, 1/16 (whole-branch
    //     review, item 2: tiles 258, 125, 59 and 62 px tall).
    //   - Otherwise (the parent splits Across, or the last leaf is the whole layout) there is no column to join, so the
    //     last leaf is split at the bottom, half and half (Insert).
    // The new leaf's index is the old Count, so an opening (always below it) does not move. Normal form holds: the
    // Down split only grows, and an Insert goes through Normal.
    public TileLayout AppendBelowLast()
    {
        var last = Count - 1;
        var path = PathTo(last);
        if (path.Count == 0 || path[^1].Branch.Axis != Axis.Down)
            return Insert(last, TileArrange.Zone.Bottom).Layout;

        var column = path[^1];
        var k = column.Branch.Children.Count;
        var grown = column.Branch with
        {
            Children = column.Branch.Children
                .Select(c => c with { Share = c.Share * k / (k + 1) })
                .Append(new TileChild(TileLeaf.Instance, 1.0 / (k + 1)))
                .ToValueList()
        };
        return Normal(MapNode(Root, column.First, LeafCount(column.Branch), _ => grown), OpeningAfterInsert(Count));
    }

    // The screen-edge band: a new leaf along a whole side of the layout.
    //   - The root splits along the side's axis: the new leaf is the first (Left/Top) or last (Right/Bottom) child and
    //     takes half of the OUTER child's share, so Across[.5,.5] + Right is [.5,.25,.25] and nothing else moves.
    //   - Otherwise the root is wrapped: a split of that axis holding the old root and the new leaf, 0.5 each.
    public (TileLayout Layout, int Leaf) InsertAtEdge(TileArrange.Zone side)
    {
        RequireSide(side);
        var first = side is TileArrange.Zone.Left or TileArrange.Zone.Top;
        var leaf = first ? 0 : Count;
        var root = Root is TileBranch b && b.Axis == AxisOf(side)
            // Split just the outer child; Normal merges the pair into the root, so they share the outer child's share.
            ? b with
            {
                Children = b.Children
                    .Select((c, i) => i == (first ? 0 : b.Children.Count - 1)
                        ? new TileChild(SplitNode(c.Node, TileLeaf.Instance, side, 0.5), c.Share)
                        : c)
                    .ToValueList()
            }
            : SplitNode(Root, TileLeaf.Instance, side, 0.5);
        return (Normal(root, OpeningAfterInsert(leaf)), leaf);
    }

    // ---- walking down to a leaf (TileArrange and SplitMove read the splits an edge belongs to) ------------------

    // One step on the way from the root down to a leaf: a split, the index of its child that holds the leaf, and the
    // index of the split's own first leaf (so a child's leaves can be numbered without walking again).
    internal sealed record PathStep(TileBranch Branch, int Child, int First)
    {
        // The index of the first leaf of child `child` of this split.
        internal int FirstOf(int child) => First + Branch.Children.Take(child).Sum(c => LeafCount(c.Node));
    }

    // The splits from the root down to leaf `leaf`, outermost first. Empty for a lone-leaf root.
    internal IReadOnlyList<PathStep> PathTo(int leaf) =>
        InRange(leaf) ? Steps(Root, leaf, 0).ToList() : throw OutOfRange(leaf);

    static IEnumerable<PathStep> Steps(TileNode node, int leaf, int first)
    {
        if (node is not TileBranch b) return [];
        var (index, before) = ChildHolding(b, leaf);
        return Steps(b.Children[index].Node, leaf - before, first + before).Prepend(new PathStep(b, index, first));
    }

    // The step (an index into `path`) whose split an edge on `side` of the path's leaf belongs to: walking up from the
    // leaf, the nearest split of the side's axis in which the leaf's subtree has a neighbour on that side (spec,
    // "Dragging a separation"). None means the edge is OUTER: nothing of the layout lies beyond it, which is where an
    // opening can be pulled (spec, "Openings"). The neighbour is the child at Child - 1 (Left, Top) or Child + 1.
    internal static Maybe<int> SplitBeside(IReadOnlyList<PathStep> path, TileArrange.Zone side)
    {
        var before = side is TileArrange.Zone.Left or TileArrange.Zone.Top;
        return Enumerable.Range(0, path.Count)
            .Reverse()
            .Where(i => path[i].Branch.Axis == AxisOf(side)
                && (before ? path[i].Child > 0 : path[i].Child < path[i].Branch.Children.Count - 1))
            .Select(i => Maybe.From(i))
            .FirstOrDefault(Maybe<int>.None);
    }

    // ---- internals -------------------------------------------------------------------------------------------

    static TileNode Split(Axis axis, IReadOnlyList<TileNode> nodes) =>
        new TileBranch(axis, nodes.Select(n => new TileChild(n, 1.0 / nodes.Count)).ToValueList());

    bool InRange(int leaf) => leaf >= 0 && leaf < Count;

    static ArgumentOutOfRangeException OutOfRange(int leaf) =>
        new(nameof(leaf), leaf, "There is no such leaf in this layout.");

    // An insert at index i pushes an opening at i or above up one.
    int? OpeningAfterInsert(int leaf) => Opening is { } open && open >= leaf ? open + 1 : Opening;

    static void RequireSide(TileArrange.Zone side)
    {
        if (side == TileArrange.Zone.Middle)
            throw new ArgumentException("Middle is not a side; only Left, Right, Top and Bottom place a new leaf.", nameof(side));
    }

    // Written as a negation so NaN fails too (every comparison with NaN is false).
    static void RequireShare(double share)
    {
        if (!(share > 0 && share < 1))
            throw new ArgumentOutOfRangeException(nameof(share), share, "A new leaf's share must be strictly between 0 and 1.");
    }

    // Left and Right put tiles side by side; Top and Bottom stack them.
    internal static Axis AxisOf(TileArrange.Zone side) =>
        side is TileArrange.Zone.Left or TileArrange.Zone.Right ? Axis.Across : Axis.Down;

    // Split THIS NODE on `side`: a two-child split along the side's axis holding `node` (1 - share) and `added`
    // (share), in side order (Left/Top puts `added` first). Works on a leaf or a whole subtree: a leaver coming back
    // as a subtree needs the same operation. The result is not normal until a caller passes it through Normal.
    static TileNode SplitNode(TileNode node, TileNode added, TileArrange.Zone side, double share) =>
        new TileBranch(AxisOf(side),
            (side is TileArrange.Zone.Left or TileArrange.Zone.Top
                ? new[] { new TileChild(added, share), new TileChild(node, 1 - share) }
                : [new TileChild(node, 1 - share), new TileChild(added, share)])
            .ToValueList());

    // The child of `b` holding leaf number `leaf` (counted inside b) and how many leaves come before that child.
    static (int Index, int Before) ChildHolding(TileBranch b, int leaf)
    {
        var counts = b.Children.Select(c => LeafCount(c.Node)).ToList();
        var index = Enumerable.Range(0, counts.Count).First(i => leaf < counts.Take(i + 1).Sum());
        return (index, counts.Take(index).Sum());
    }

    // The tree with the node spanning leaves first .. first + leaves - 1 replaced by f(node); shares along the way are
    // untouched. The OUTERMOST node with that span is the one meant (a root spanning every leaf is wrapped whole).
    static TileNode MapNode(TileNode node, int first, int leaves, Func<TileNode, TileNode> f)
    {
        if (first == 0 && LeafCount(node) == leaves) return f(node);
        if (node is not TileBranch b)
            throw new ArgumentException("No node of this layout spans exactly those leaves.", nameof(leaves));
        var (index, before) = ChildHolding(b, first);
        return b with
        {
            Children = b.Children
                .Select((c, i) => i == index ? new TileChild(MapNode(c.Node, first - before, leaves, f), c.Share) : c)
                .ToValueList()
        };
    }

    // The tree without leaf number `leaf`, or null when that leaf was the whole node. When a child vanishes the
    // others are rescaled to sum to 1 again, which is "its share goes to its siblings in proportion".
    static TileNode? RemoveLeaf(TileNode node, int leaf)
    {
        if (node is not TileBranch b) return null;
        var (index, before) = ChildHolding(b, leaf);
        var inner = RemoveLeaf(b.Children[index].Node, leaf - before);
        var kept = b.Children
            .Select((c, i) => i == index ? (inner is null ? null : new TileChild(inner, c.Share)) : c)
            .OfType<TileChild>()
            .ToList();
        if (kept.Count == 0) return null;
        // Only a vanished child changes the sum; otherwise leave every share exactly as it was.
        var total = kept.Sum(c => c.Share);
        return b with
        {
            Children = (inner is null ? kept.Select(c => c with { Share = c.Share / total }) : kept).ToValueList()
        };
    }

    internal static int LeafCount(TileNode node) =>
        node is TileBranch b ? b.Children.Sum(c => LeafCount(c.Node)) : 1;

    static IEnumerable<TileBranch> AllBranches(TileNode node) =>
        node is TileBranch b ? b.Children.SelectMany(c => AllBranches(c.Node)).Prepend(b) : [];

    static bool IsEvenShares(IReadOnlyList<TileChild> children) =>
        children.All(c => Math.Abs(c.Share - 1.0 / children.Count) < Tolerance);

    static TileNode EvenNode(TileNode node) =>
        node is TileBranch b
            ? b with { Children = b.Children.Select(c => new TileChild(EvenNode(c.Node), 1.0 / b.Children.Count)).ToValueList() }
            : node;

    static bool SameStructure(TileNode a, TileNode b) =>
        (a, b) switch
        {
            (TileLeaf, TileLeaf) => true,
            (TileBranch x, TileBranch y) =>
                x.Axis == y.Axis
                && x.Children.Count == y.Children.Count
                && x.Children.Zip(y.Children, (l, r) => SameStructure(l.Node, r.Node)).All(same => same),
            _ => false
        };

    // Bottom-up normal form. Children are normalized first; then a child that came out as a split of this split's
    // axis (it was one, or it collapsed to one) is merged in with its shares scaled by its own share; then a split
    // with one child is that child. A single-child collapse returns the child with ITS shares untouched, which is
    // what "takes the split's whole area" means.
    static TileNode Flatten(TileNode node) =>
        node is not TileBranch branch
            ? node
            : branch.Children
                .Select(c => new TileChild(Flatten(c.Node), c.Share))
                .SelectMany(c => c.Node is TileBranch inner && inner.Axis == branch.Axis
                    ? inner.Children.Select(g => new TileChild(g.Node, g.Share * c.Share))
                    : [c])
                .ToList() switch
            {
                [] => TileLeaf.Instance,
                [var only] => only.Node,
                var many => new TileBranch(branch.Axis, many.ToValueList())
            };

    // Width and height shares of leaf number `leaf` inside `node`. An index that is not in there throws, never (1,1).
    static (double Width, double Height) SharesAlong(TileNode node, int leaf)
    {
        if (node is not TileBranch b)
            return leaf == 0 ? (1.0, 1.0) : throw OutOfRange(leaf);

        // Descend into the child holding `leaf`.
        var (index, before) = ChildHolding(b, leaf);
        var child = b.Children[index];
        var (w, h) = SharesAlong(child.Node, leaf - before);
        return b.Axis == Axis.Across ? (w * child.Share, h) : (w, h * child.Share);
    }
}
