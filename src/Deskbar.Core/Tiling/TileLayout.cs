using System.Text.Json.Serialization;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Tiling;

// A screen's tiling layout as a SPLIT TREE (spec: docs/superpowers/specs/2026-11-19-tiling-split-tree-design.md).
// It sits BESIDE TileSizes for now: nothing that reads TileSizes changes, and later tasks switch the Tiler over.
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
    // Two shares closer than this are the same (also what "even" means), as in TileSizes.
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
    // handles that before it gets here).
    public (TileLayout Layout, int Leaf) Insert(int target, TileArrange.Zone side, double share = 0.5)
    {
        if (!InRange(target)) throw OutOfRange(target);
        RequireSide(side);
        var leaf = side is TileArrange.Zone.Left or TileArrange.Zone.Top ? target : target + 1;
        return (Normal(MapLeaf(Root, target, old => SplitNode(old, TileLeaf.Instance, side, share)), OpeningAfterInsert(leaf)), leaf);
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

    // Left and Right put tiles side by side; Top and Bottom stack them.
    static Axis AxisOf(TileArrange.Zone side) =>
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

    // The tree with leaf number `leaf` replaced by f(leaf node); shares along the way are untouched.
    static TileNode MapLeaf(TileNode node, int leaf, Func<TileNode, TileNode> f)
    {
        if (node is not TileBranch b) return f(node);
        var (index, before) = ChildHolding(b, leaf);
        return b with
        {
            Children = b.Children
                .Select((c, i) => i == index ? new TileChild(MapLeaf(c.Node, leaf - before, f), c.Share) : c)
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

    static int LeafCount(TileNode node) =>
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
