using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Tiling;

// What a split move made: the new layout, and which old leaf now sits in each leaf place. Order[newLeaf] is the old
// leaf; it is the identity unless the move regrouped the tree at a crossing (SplitMove.Regrouped), which changes the
// depth-first order of the leaves it touched. The screen's windows are kept in leaf order, so a caller that stores
// Layout must put its windows in the new order too (Windows), or they trade places on the next layout. The opening, if
// any, is already remapped in Layout.
public sealed record SplitMoved(TileLayout Layout, IReadOnlyList<int> Order)
{
    // The new leaf index of old leaf `oldLeaf`.
    public int LeafOf(int oldLeaf) => Order.ToList().IndexOf(oldLeaf);

    // `windows`, the screen's windows in slot order for `before`, in slot order for Layout. Slots map to leaves past
    // the opening (TileArrange.LeafOf), so the opening's leaf has no window and is skipped.
    public IReadOnlyList<WindowHandle> Windows(IReadOnlyList<WindowHandle> windows, TileLayout before)
    {
        var byLeaf = windows
            .Select((window, slot) => (Leaf: TileArrange.LeafOf(before, slot, windows.Count), Window: window))
            .ToDictionary(p => p.Leaf, p => p.Window);
        return Order.Where(byLeaf.ContainsKey).Select(old => byLeaf[old]).ToList();
    }
}

// Moves a split of the tiling layout when the user drags a tile's edge. Petre: "i want to be able
// to resize tiles by dragging the separation". Pure arithmetic on the split tree (TileLayout), no Windows.
//
// The edge a tile's neighbour shares is a SPLIT: dragging the right edge of the left tile and the left
// edge of the right one move the same split. Edges on the outside of the layout have no split; a drag of
// one alone returns None and the caller snaps the window back (or opens a space, TileArrange.Open).
//
// One exception, at a "+" CROSSING (docs/superpowers/specs/2026-11-23-tile-local-split-design.md). Petre, looking at
// four tiles in a 2x2: "all windows should let me resize only the other one being resized, not the entire row", and
// "the split could be between a and c, which keeps b and d as they are". Where the dragged line is crossed straight
// through by a line of the other direction, the line is in pieces and only the piece beside the dragged tile moves.
// The tree cannot say "half a split", so the two children either side of the dragged boundary are first REGROUPED
// into bands at the crossings around the tile (Regrouped), and then the band's own split moves as any other. At a T
// (one line ending against another) there is no way to move half a line without tiles overlapping, so the whole line
// moves, as before.
public static class SplitMove
{
    // No child of a split may be narrower than this share of that split's own span. TileArrange.Returned clamps a
    // returning window's share to it too.
    internal const double MinShare = 0.1;

    // Two boundaries this close on screen (pixels, the units of the work area) are one crossing: the same tolerance
    // SettledResize allows an edge that held. Shares rounded to pixels can put two lines that look straight a pixel
    // apart, and the user sees one line.
    const int CrossingTolerance = 2;

    // The new layout after leaf `leaf` was dragged by `moved` and settled at `frame`, or None when no moved edge
    // belongs to a split (spec, "Dragging a separation"). Per axis, the moved edge's split is found by walking up from
    // the leaf to the nearest split of that edge's axis in which the leaf's subtree has a neighbour on that side
    // (TileLayout.SplitBeside). In Down[Across[A, C], B], A's bottom edge is therefore the ROOT's boundary between
    // the top pair and B, and C's bottom moves with it: that is a T, so on screen they are one line and one split.
    //
    // When exactly ONE axis has an inner moved edge, and its line is crossed at a "+", the tree is regrouped first
    // (Regrouped) so that the piece beside the tile is a split of its own. A corner whose two edges are both inner
    // moves the crossing point itself, which moves every tile meeting it, so it is never regrouped.
    //
    // The boundary goes to the released edge plus `far` (right, bottom) or minus `near` (left, top), so laying the
    // layout out again leaves exactly `gap` between the two tiles (GridLayout.HalfGaps). It is stored as a fraction
    // of THAT split's rect, laid out with the same Edges arithmetic the layout uses (GridLayout.ChildRects). Only the
    // two children either side of it change, and neither goes under MinShare of that split. A corner moves one split
    // per axis; the two cannot disturb each other, because moving an Across boundary never changes any split's height
    // and moving a Down boundary never changes any width. A moved outer edge alongside an inner one is ignored. The
    // opening, if any, is untouched (apart from following a regroup). A leaf the layout does not have is a caller bug
    // and throws (TileLayout.PathTo).
    //
    // `regroup` false turns crossings off: the caller knows the tiles on screen are not where this layout alone puts
    // them (a window that will not shrink stretched its tile, Tiler.Unstretched), so a "+" in the layout may not be
    // one on screen, and only the whole-line move is safe.
    public static Maybe<SplitMoved> Apply(TileLayout layout, WindowRect area, int gap, int leaf, Edges moved, WindowRect frame,
        bool regroup = true)
    {
        var inner = GridLayout.InnerArea(area, gap);
        var path = layout.PathTo(leaf);

        // The inner side moved on each axis, the far side winning when both are flagged and inner (as Moved decides).
        Maybe<TileArrange.Zone> InnerSide(Edges far, TileArrange.Zone farSide, Edges near, TileArrange.Zone nearSide) =>
            moved.HasFlag(far) && TileLayout.SplitBeside(path, farSide).HasValue ? farSide
            : moved.HasFlag(near) && TileLayout.SplitBeside(path, nearSide).HasValue ? nearSide
            : Maybe<TileArrange.Zone>.None;
        var sides = new[]
        {
            InnerSide(Edges.Right, TileArrange.Zone.Right, Edges.Left, TileArrange.Zone.Left),
            InnerSide(Edges.Bottom, TileArrange.Zone.Bottom, Edges.Top, TileArrange.Zone.Top),
        }.Where(s => s.HasValue).Select(s => s.Value).ToList();

        var identity = (IReadOnlyList<int>)Enumerable.Range(0, layout.Count).ToList();
        var (start, order) = regroup && sides.Count == 1
            ? Regrouped(layout, inner, leaf, sides[0]).GetValueOrDefault((layout, identity))
            : (layout, identity);

        return Moved(start, inner, gap, order.ToList().IndexOf(leaf), moved, frame)
            .Map(next => new SplitMoved(next, order));
    }

    // Today's move, on `layout` (regrouped or not): one split per axis that has an inner moved edge.
    static Maybe<TileLayout> Moved(TileLayout layout, WindowRect inner, int gap, int leaf, Edges moved, WindowRect frame)
    {
        var (near, far) = GridLayout.HalfGaps(gap);
        var path = layout.PathTo(leaf);
        var rects = RectsAlong(path, inner);

        // (the step whose split moves, the boundary index in it, the boundary's position as a fraction of that split),
        // for an edge that belongs to a split.
        Maybe<(int Step, int Index, double Position)> Boundary(TileArrange.Zone side, int released) =>
            TileLayout.SplitBeside(path, side).Map(i =>
            {
                var rect = rects[i];
                var across = path[i].Branch.Axis == Axis.Across;
                var start = across ? rect.Left : rect.Top;
                var span = across ? rect.Width : rect.Height;
                var before = side is TileArrange.Zone.Left or TileArrange.Zone.Top;
                return (i, before ? path[i].Child - 1 : path[i].Child, (released - start) / (double)Math.Max(1, span));
            });

        // The far edge wins when both edges of an axis are flagged and both are inner.
        var across = moved.HasFlag(Edges.Right) && Boundary(TileArrange.Zone.Right, frame.Right + far) is { HasValue: true } right ? right
            : moved.HasFlag(Edges.Left) ? Boundary(TileArrange.Zone.Left, frame.Left - near)
            : Maybe<(int Step, int Index, double Position)>.None;
        var down = moved.HasFlag(Edges.Bottom) && Boundary(TileArrange.Zone.Bottom, frame.Bottom + far) is { HasValue: true } bottom ? bottom
            : moved.HasFlag(Edges.Top) ? Boundary(TileArrange.Zone.Top, frame.Top - near)
            : Maybe<(int Step, int Index, double Position)>.None;

        var boundaries = new[] { across, down }.Where(b => b.HasValue).Select(b => b.Value).ToList();
        return boundaries.Count == 0
            ? Maybe<TileLayout>.None
            : layout with
            {
                Root = boundaries.Aggregate(layout.Root, (root, b) =>
                    Rebuilt(root, path.Take(b.Step).Select(s => s.Child).ToList(), branch => branch with
                    {
                        Children = branch.Children
                            .Zip(Split(branch.Children.Select(c => c.Share).ToList(), b.Index, b.Position))
                            .Select(pair => pair.First with { Share = pair.Second })
                            .ToValueList()
                    }))
            };
    }

    // Each split's own (un-inset) rect along `path`: the inner area for the root, then the child rect holding the leaf.
    static IReadOnlyList<WindowRect> RectsAlong(IReadOnlyList<TileLayout.PathStep> path, WindowRect inner) =>
        path.Aggregate((IReadOnlyList<WindowRect>)[inner],
            (acc, step) => [.. acc, GridLayout.ChildRects(step.Branch, acc[^1])[step.Child]]);

    // One side of the dragged boundary seen across the line: its children along the OTHER axis (a lone child when it
    // is not split that way) and the pixel position of each inner boundary between them. Boundary j sits between
    // child j - 1 and child j, so the list holds j = 1 .. n - 1.
    sealed record Side(IReadOnlyList<TileChild> Children, IReadOnlyList<(int Index, int Position)> Boundaries)
    {
        internal static Side Of(TileNode node, WindowRect rect, Axis other) =>
            node is TileBranch b && b.Axis == other
                ? new(b.Children, GridLayout.ChildRects(b, rect)
                    .Select((r, j) => (j, other == Axis.Across ? r.Left : r.Top))
                    .Skip(1)
                    .ToList())
                : new([new TileChild(node, 1)], []);

        // The run of children [from, to) as one node: the child itself, or a split of the run with its shares made
        // to sum to 1 again. And the run's share of this side.
        internal (TileNode Node, double Share) Run(int from, int to, Axis other)
        {
            var run = Children.Skip(from).Take(to - from).ToList();
            var share = run.Sum(c => c.Share);
            return run.Count == 1
                ? (run[0].Node, share)
                : (new TileBranch(other, run.Select(c => c with { Share = c.Share / share }).ToValueList()), share);
        }

        internal int LeavesOf(int child) => TileLayout.LeafCount(Children[child].Node);
    }

    // The tree regrouped at the crossings around the dragged tile, and the leaf order that results (Order[new] = old),
    // or None when the dragged line has no "+" crossing beside the tile (a T, or no line across it at all).
    //
    // S is the split the dragged edge belongs to (axis X), P and Q its children either side of the boundary. P's and
    // Q's own boundaries along the other axis Y, where they line up within CrossingTolerance, are the crossings. The
    // nearest crossing above the tile and the nearest below it (either may be missing: the end of the line) cut P and Q
    // into up to three bands each, as runs of their Y children. P and Q are replaced in S by ONE child, Y[ X[P band,
    // Q band] per band ], with P's and Q's combined share; inside each band P and Q keep their proportions of that
    // width, and the bands take the dragged side's heights, so nothing on screen moves (the other side by at most the
    // tolerance). Normal form then merges whatever it can: a band split of the same axis as its child, a single-child
    // S. Flattening keeps depth-first order, so the order computed here holds after it.
    //
    //   Across[Down[A, C], Down[B, D]], A's right edge:  Down[Across[A, B], Across[C, D]], order A B C D (was A C B D).
    static Maybe<(TileLayout Layout, IReadOnlyList<int> Order)> Regrouped(TileLayout layout, WindowRect inner, int leaf, TileArrange.Zone side)
    {
        var path = layout.PathTo(leaf);
        var rects = RectsAlong(path, inner);
        return TileLayout.SplitBeside(path, side).Bind(i =>
        {
            var step = path[i];
            var s = step.Branch;
            var k = side is TileArrange.Zone.Left or TileArrange.Zone.Top ? step.Child - 1 : step.Child;
            var other = s.Axis == Axis.Across ? Axis.Down : Axis.Across;
            var childRects = GridLayout.ChildRects(s, rects[i]);
            var p = Side.Of(s.Children[k].Node, childRects[k], other);
            var q = Side.Of(s.Children[k + 1].Node, childRects[k + 1], other);

            // Crossings as (P's boundary index, Q's boundary index), top to bottom (or left to right).
            var crossings = p.Boundaries
                .SelectMany(a => q.Boundaries
                    .Where(b => Math.Abs(a.Position - b.Position) <= CrossingTolerance)
                    .Select(b => (P: a.Index, Q: b.Index)))
                .ToList();
            if (crossings.Count == 0) return Maybe<(TileLayout, IReadOnlyList<int>)>.None;

            // The dragged tile's child on its own side. That side has boundaries (there are crossings), so it is a split
            // along Y and the next step down the path is it.
            var inP = step.Child == k;
            var tile = path[i + 1].Child;
            int Mine((int P, int Q) c) => inP ? c.P : c.Q;
            var cuts = new[] { (P: 0, Q: 0) }
                .Concat(crossings.Where(c => Mine(c) <= tile).TakeLast(1))
                .Concat(crossings.Where(c => Mine(c) > tile).Take(1))
                .Append((P: p.Children.Count, Q: q.Children.Count))
                .ToList();
            var bands = cuts.Zip(cuts.Skip(1), (from, to) => (From: from, To: to)).ToList();

            var sP = s.Children[k].Share;
            var sQ = s.Children[k + 1].Share;
            var merged = new TileBranch(other, bands
                .Select(band =>
                {
                    var (pNode, pShare) = p.Run(band.From.P, band.To.P, other);
                    var (qNode, qShare) = q.Run(band.From.Q, band.To.Q, other);
                    return new TileChild(
                        new TileBranch(s.Axis, new[] { new TileChild(pNode, sP / (sP + sQ)), new TileChild(qNode, sQ / (sP + sQ)) }.ToValueList()),
                        inP ? pShare : qShare);
                })
                .ToValueList());
            var regrouped = s with
            {
                Children = s.Children.Take(k)
                    .Append(new TileChild(merged, sP + sQ))
                    .Concat(s.Children.Skip(k + 2))
                    .ToValueList()
            };

            // The leaves of P's child j start at first + (P's leaves before j); Q's follow all of P's.
            var first = step.FirstOf(k);
            var qFirst = first + Enumerable.Range(0, p.Children.Count).Sum(p.LeavesOf);
            IEnumerable<int> LeavesOf(Side of, int start, int from, int to) =>
                Enumerable.Range(from, to - from).SelectMany(j =>
                    Enumerable.Range(start + Enumerable.Range(0, j).Sum(of.LeavesOf), of.LeavesOf(j)));
            var end = qFirst + Enumerable.Range(0, q.Children.Count).Sum(q.LeavesOf);
            var order = Enumerable.Range(0, first)
                .Concat(bands.SelectMany(band =>
                    LeavesOf(p, first, band.From.P, band.To.P).Concat(LeavesOf(q, qFirst, band.From.Q, band.To.Q))))
                .Concat(Enumerable.Range(end, layout.Count - end))
                .ToList();

            var root = Rebuilt(layout.Root, path.Take(i).Select(c => c.Child).ToList(), _ => regrouped);
            var opening = layout.Opening is { } open ? order.IndexOf(open) : (int?)null;
            return Maybe.From((TileLayout.Normal(root, opening), (IReadOnlyList<int>)order));
        });
    }

    // The tree with the split reached by following `route` (child indexes from the root) replaced by f(split).
    static TileNode Rebuilt(TileNode node, IReadOnlyList<int> route, Func<TileBranch, TileNode> f) =>
        node is not TileBranch branch ? node
        : route.Count == 0 ? f(branch)
        : branch with
        {
            Children = branch.Children
                .Select((c, i) => i == route[0] ? c with { Node = Rebuilt(c.Node, route.Skip(1).ToList(), f) } : c)
                .ToValueList()
        };

    // Puts the boundary between entries `index` and `index + 1` at `position` (a fraction of the
    // span the entries divide, from its start: the split's own). Only those two entries change; they keep their combined share and neither goes below MinShare.
    static IReadOnlyList<double> Split(IReadOnlyList<double> fractions, int index, double position)
    {
        var before = fractions.Take(index).Sum();
        var pair = fractions[index] + fractions[index + 1];
        // A pair already narrower than two minimums splits evenly rather than throwing.
        var first = pair < 2 * MinShare ? pair / 2 : Math.Clamp(position - before, MinShare, pair - MinShare);
        return fractions.Select((f, i) => i == index ? first : i == index + 1 ? pair - first : f).ToList();
    }
}
