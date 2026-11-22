using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Tiling;

// The arithmetic of the tiling layout: where each of n windows goes inside one screen's work area,
// given the screen's split tree (TileLayout). Pure geometry in physical pixels (Right and Bottom
// exclusive, like WindowRect everywhere), so it is testable without Windows. The Tiler calls Arrange
// once per screen and then hands the slots out to windows; the SLOT ORDER is what it relies on: the
// leaves depth-first, which for the automatic grid is column by column, top to bottom inside a column.
//
// The automatic grid's rule (from the tiling spec), what TileLayout.Automatic builds from Shape:
//   columns = ceil(sqrt(n)); column i of the remaining takes floor(remaining windows / remaining
//   columns), so the later columns are the fuller ones (3 windows: 1 + 2, 5 windows: 1 + 2 + 2).
//   Columns are equal width and rows equal height; integer remainders go to the last column and
//   the last row, so the grid always reaches the far edge of the area.
// The smallest visible frame a window will take, in physical pixels (#398). The Tiler learns it from the frame a window
// kept when it was given a smaller slot (Tiler.Minimums.cs); Windows has no way to ask another process for it, since
// WM_GETMINMAXINFO carries a pointer into the receiving process.
public sealed record TileMinimum(int Width, int Height);

public static class GridLayout
{
    // The automatic layout for `count` windows (TileLayout.Automatic: equal columns and rows in GridLayout.Shape), what
    // a screen with nothing stored is laid out as. No windows, no tiles.
    public static IReadOnlyList<WindowRect> Arrange(WindowRect area, int count, int gap) =>
        count <= 0 ? [] : Arrange(area, count, gap, TileLayout.Automatic(count));

    // The tree's cells for `count` windows: `Count - 1` rects with the opening left out when the layout has an
    // opening for this many windows, otherwise every leaf. Callers pass a layout whose Count matches.
    public static IReadOnlyList<WindowRect> Arrange(WindowRect area, int count, int gap, TileLayout layout) =>
        layout.HasOpeningFor(count)
            ? CellsOf(area, gap, layout).Where((_, slot) => slot != layout.Opening).ToList()
            : CellsOf(area, gap, layout);

    // The same, with the smallest size each WINDOW will take (#398), in the order of the rects returned (the opening
    // has none). A window that cannot go as small as its cell gets a cell its size, the room taken from the cells
    // beside it in the same split that can spare it (SplitWithMinimums); without this it kept its own size and
    // overlapped its neighbour. A null entry, or a list shorter than `count`, is a window with no known minimum. When
    // the minimums cannot all fit, a split keeps its shares, which is what the layout did before.
    public static IReadOnlyList<WindowRect> Arrange(WindowRect area, int count, int gap, TileLayout layout,
        IReadOnlyList<TileMinimum?> minimums)
    {
        var opening = layout.HasOpeningFor(count) ? layout.Opening : null;
        // From one entry per window to one per leaf: the opening's leaf takes nothing, and every other leaf takes the
        // next window's minimum in turn.
        var windows = new Queue<TileMinimum?>(minimums);
        var perLeaf = Enumerable.Range(0, layout.Count)
            .Select(leaf => leaf != opening && windows.TryDequeue(out var minimum) ? minimum : null)
            .ToList();
        var cells = CellsOf(area, gap, layout, perLeaf);
        return opening is { } open ? cells.Where((_, slot) => slot != open).ToList() : cells;
    }

    // Where the opening is, when the layout has one for `count` windows.
    public static Maybe<WindowRect> OpeningOf(WindowRect area, int count, int gap, TileLayout layout) =>
        layout.HasOpeningFor(count) && layout.Opening is { } open
            ? CellsOf(area, gap, layout)[open]
            : Maybe<WindowRect>.None;

    // Every leaf's rect in slot order, the opening included. The same steps the columns-of-rows grid took, in the same
    // order, so a migrated grid is pixel-identical: shrink by the outer gap, divide each split's span with Edges, then
    // inset each leaf on every edge that is not on the inner area's border.
    public static IReadOnlyList<WindowRect> CellsOf(WindowRect area, int gap, TileLayout layout) =>
        CellsOf(area, gap, layout, []);

    // The same with each LEAF's minimum window size (null for none), which only ever widens or heightens a cell.
    static IReadOnlyList<WindowRect> CellsOf(WindowRect area, int gap, TileLayout layout, IReadOnlyList<TileMinimum?> perLeaf)
    {
        var inner = InnerArea(area, gap);
        return Place(layout.Root, inner, 0, perLeaf, gap)
            .Select(cell => Inset(cell, gap,
                hasLeft: cell.Left > inner.Left, hasRight: cell.Right < inner.Right,
                hasTop: cell.Top > inner.Top, hasBottom: cell.Bottom < inner.Bottom))
            .ToList();
    }

    // The un-inset rects of a node's leaves inside `rect`, depth-first. `first` is the index of the node's first leaf,
    // so each leaf finds its own minimum in `perLeaf`.
    static IEnumerable<WindowRect> Place(TileNode node, WindowRect rect, int first, IReadOnlyList<TileMinimum?> perLeaf, int gap)
    {
        if (node is not TileBranch branch) return [rect];
        var firsts = FirstLeaves(branch, first);
        var across = branch.Axis == Axis.Across;
        var needs = branch.Children
            .Select((child, i) => Need(child.Node, firsts[i], perLeaf, gap))
            .Select(need => across ? need.Width : need.Height)
            .ToList();
        return branch.Children.Zip(ChildRects(branch, rect, needs))
            .SelectMany((pair, i) => Place(pair.First.Node, pair.Second, firsts[i], perLeaf, gap));
    }

    // The index of each child's first leaf, for a branch whose own first leaf is `first`.
    static IReadOnlyList<int> FirstLeaves(TileBranch branch, int first) =>
        branch.Children
            .Select(child => Leaves(child.Node))
            .Aggregate((Next: first, Firsts: new List<int>()), (state, leaves) =>
            {
                state.Firsts.Add(state.Next);
                return (state.Next + leaves, state.Firsts);
            })
            .Firsts;

    static int Leaves(TileNode node) => node is TileBranch branch ? branch.Children.Sum(c => Leaves(c.Node)) : 1;

    // The un-inset size a node needs so that no window in it is squeezed below its minimum. A leaf needs its window's
    // minimum plus the gap, which is at least what its inner edges take off it (HalfGaps: two halves of one gap). Side by
    // side the widths add up and the tallest decides the height; stacked, the other way round.
    static (int Width, int Height) Need(TileNode node, int first, IReadOnlyList<TileMinimum?> perLeaf, int gap)
    {
        if (node is not TileBranch branch)
            return first < perLeaf.Count && perLeaf[first] is { } minimum ? (minimum.Width + gap, minimum.Height + gap) : (0, 0);
        var firsts = FirstLeaves(branch, first);
        var children = branch.Children.Select((child, i) => Need(child.Node, firsts[i], perLeaf, gap)).ToList();
        return branch.Axis == Axis.Across
            ? (children.Sum(c => c.Width), children.Max(c => c.Height))
            : (children.Max(c => c.Width), children.Sum(c => c.Height));
    }

    // The un-inset rects of a split's children inside the split's own `rect`: its span divided by the shares with
    // Edges. SplitMove reads a split's rect through this too, so a dragged boundary is placed in the same pixels the
    // layout draws it at.
    internal static IReadOnlyList<WindowRect> ChildRects(TileBranch branch, WindowRect rect) =>
        ChildRects(branch, rect, []);

    // The same, with the span each child needs (#398, Need): a child below its need is given it and the others pay
    // for it (Fit). Empty needs is the shares exactly.
    static IReadOnlyList<WindowRect> ChildRects(TileBranch branch, WindowRect rect, IReadOnlyList<int> needs)
    {
        var across = branch.Axis == Axis.Across;
        var span = across ? rect.Width : rect.Height;
        var shared = Edges(span, branch.Children.Select(c => c.Share).ToList());
        var sizes = Fit(shared.Zip(shared.Skip(1), (from, to) => to - from).ToList(), needs, span);
        var edges = sizes.Aggregate(new List<int> { 0 }, (list, size) =>
        {
            list.Add(list[^1] + size);
            return list;
        });
        return Enumerable.Range(0, branch.Children.Count)
            .Select(i => across
                ? new WindowRect(rect.Left + edges[i], rect.Top, rect.Left + edges[i + 1], rect.Bottom)
                : new WindowRect(rect.Left, rect.Top + edges[i], rect.Right, rect.Top + edges[i + 1]))
            .ToList();
    }

    // Sizes along one split, each raised to its need when it falls short (#398). What the raised ones gain is taken from
    // the ones above their own need, each giving in proportion to what it can spare, so a neighbour never goes below
    // its own need either. The sizes still add up to the span. When the needs together exceed the span there is no
    // answer that satisfies them all, and the shares stand as they are: the old behaviour, an overlap, rather than a
    // squeeze that would push some other window below ITS minimum.
    static IReadOnlyList<int> Fit(IReadOnlyList<int> sizes, IReadOnlyList<int> needs, int span)
    {
        int NeedOf(int i) => i < needs.Count ? needs[i] : 0;
        var indexes = Enumerable.Range(0, sizes.Count).ToList();
        var deficit = indexes.Sum(i => Math.Max(0, NeedOf(i) - sizes[i]));
        if (deficit == 0 || indexes.Sum(NeedOf) > span) return sizes;

        var spare = indexes.Select(i => Math.Max(0, sizes[i] - NeedOf(i))).ToList();
        var totalSpare = spare.Sum();
        // Rounded down, so nobody gives more than its share; the pixels that leaves over are taken one at a time from
        // the first ones that still have some to spare. totalSpare >= deficit, because the needs fit in the span.
        var taken = spare.Select(s => (int)((long)deficit * s / totalSpare)).ToList();
        var left = deficit - taken.Sum();
        var extra = indexes.Select(i =>
        {
            var more = Math.Min(left, spare[i] - taken[i]);
            left -= more;
            return more;
        }).ToList();
        return indexes.Select(i => Math.Max(sizes[i], NeedOf(i)) - taken[i] - extra[i]).ToList();
    }

    // How many tiles each column holds for n windows: columns = ceil(sqrt(n)), filled left to
    // right from what is still unplaced, so the later columns are the fuller ones.
    public static IReadOnlyList<int> Shape(int count)
    {
        if (count <= 0)
            return [];

        var columns = (int)Math.Ceiling(Math.Sqrt(count));
        return Enumerable.Range(0, columns)
            .Aggregate(
                (Remaining: count, Sizes: new List<int>()),
                (state, i) =>
                {
                    var take = state.Remaining / (columns - i);
                    state.Sizes.Add(take);
                    return (state.Remaining - take, state.Sizes);
                })
            .Sizes;
    }

    // The n+1 boundaries (offsets from the start of the span) of n entries with the given fractions.
    // Even fractions use floor(span / n) per entry, exactly as the equal grid always did; uneven
    // ones round the running total. The last boundary is always the span's end, so the integer
    // remainder lands in the last entry.
    internal static IReadOnlyList<int> Edges(int span, IReadOnlyList<double> fractions)
    {
        var n = fractions.Count;
        var even = fractions.All(f => Math.Abs(f - 1.0 / n) < 1e-9);
        var running = 0.0;
        var inner = fractions.Take(n - 1).Select((f, i) =>
        {
            running += f;
            return even ? (i + 1) * (span / n) : (int)Math.Round(running * span);
        });
        return new[] { 0 }.Concat(inner).Append(span).ToList();
    }

    // The area inside the outer gap. SplitMove uses it too, so both agree on where fractions start.
    public static WindowRect InnerArea(WindowRect area, int gap) =>
        new(area.Left + gap, area.Top + gap, area.Right - gap, area.Bottom - gap);

    // How a shared edge's gap is split: the tile on the left/top takes Near, the one on the
    // right/bottom takes Far, so for an odd gap the odd pixel goes to Far and two neighbours are
    // always exactly `gap` apart. SplitMove uses it too, so the exact-gap guarantee lives in one place.
    public static (int Near, int Far) HalfGaps(int gap) =>
        (gap / 2, gap - gap / 2);

    // Shrinks a cell on its INNER edges only (the outer edges already carry the full gap from the
    // shrunk area). Each side of a shared edge takes half; for an odd gap the odd pixel goes to the
    // tile on the left/top of the edge, so two neighbours are always exactly `gap` apart.
    static WindowRect Inset(WindowRect cell, int gap, bool hasLeft, bool hasRight, bool hasTop, bool hasBottom)
    {
        var (near, far) = HalfGaps(gap);
        return new WindowRect(
            cell.Left + (hasLeft ? near : 0),
            cell.Top + (hasTop ? near : 0),
            cell.Right - (hasRight ? far : 0),
            cell.Bottom - (hasBottom ? far : 0));
    }
}
