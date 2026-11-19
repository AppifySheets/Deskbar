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

    // Where the opening is, when the layout has one for `count` windows.
    public static Maybe<WindowRect> OpeningOf(WindowRect area, int count, int gap, TileLayout layout) =>
        layout.HasOpeningFor(count) && layout.Opening is { } open
            ? CellsOf(area, gap, layout)[open]
            : Maybe<WindowRect>.None;

    // Every leaf's rect in slot order, the opening included. The same steps the columns-of-rows grid took, in the same
    // order, so a migrated grid is pixel-identical: shrink by the outer gap, divide each split's span with Edges, then
    // inset each leaf on every edge that is not on the inner area's border.
    public static IReadOnlyList<WindowRect> CellsOf(WindowRect area, int gap, TileLayout layout)
    {
        var inner = InnerArea(area, gap);
        return Place(layout.Root, inner)
            .Select(cell => Inset(cell, gap,
                hasLeft: cell.Left > inner.Left, hasRight: cell.Right < inner.Right,
                hasTop: cell.Top > inner.Top, hasBottom: cell.Bottom < inner.Bottom))
            .ToList();
    }

    // The un-inset rects of a node's leaves inside `rect`, depth-first.
    static IEnumerable<WindowRect> Place(TileNode node, WindowRect rect) =>
        node is TileBranch branch
            ? branch.Children.Zip(ChildRects(branch, rect)).SelectMany(pair => Place(pair.First.Node, pair.Second))
            : [rect];

    // The un-inset rects of a split's children inside the split's own `rect`: its span divided by the shares with
    // Edges. SplitMove reads a split's rect through this too, so a dragged boundary is placed in the same pixels the
    // layout draws it at.
    internal static IReadOnlyList<WindowRect> ChildRects(TileBranch branch, WindowRect rect)
    {
        var across = branch.Axis == Axis.Across;
        var edges = Edges(across ? rect.Width : rect.Height, branch.Children.Select(c => c.Share).ToList());
        return Enumerable.Range(0, branch.Children.Count)
            .Select(i => across
                ? new WindowRect(rect.Left + edges[i], rect.Top, rect.Left + edges[i + 1], rect.Bottom)
                : new WindowRect(rect.Left, rect.Top + edges[i], rect.Right, rect.Top + edges[i + 1]))
            .ToList();
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
