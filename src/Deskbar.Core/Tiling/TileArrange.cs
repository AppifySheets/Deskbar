using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Tiling;

// Where a tile goes when it is dropped on an EDGE of another tile, and the grid SHAPE that follows. Petre, testing
// tiling live: "i still can't drag below another window". Two windows were always two columns, because the grid
// worked its shape out from the window count (GridLayout.Shape) and nothing could ask for another. He picked drop
// zones from a mockup:
//
//   * the top or bottom quarter of a tile stacks the dropped window above or below it, in that tile's column;
//   * the left or right quarter makes a new column for it beside that tile's column;
//   * the middle swaps, as before (SettledMove).
//
// The shape a drop makes is KEPT, in the sizes it is stored with (TileSizes.Rows holds each column's rows, so the
// sizes already are a shape). A screen whose stored shape matches its window count uses it; "Even out tiles" forgets
// it with the sizes. Fractions follow the place: the tile dropped next to gives up half of its own height (stacked)
// or width (new column), and the place the dropped window left is shared by its neighbours in proportion, so every
// other tile keeps the size it had.
//
// Pure, like GridLayout and TileSizes: the Tiler calls it, and the tests reach every rule without Windows.
public static class TileArrange
{
    // Which part of a tile a point is over. The edge zones are a quarter of the tile deep; in a corner, the nearer
    // edge wins.
    public enum Zone { Middle, Top, Bottom, Left, Right }

    const double Edge = 0.25;

    public static Zone ZoneOf(WindowRect tile, int x, int y)
    {
        var fx = (x - tile.Left) / (double)Math.Max(1, tile.Width);
        var fy = (y - tile.Top) / (double)Math.Max(1, tile.Height);
        // Distance to each edge as a share of the tile, the nearest decides.
        var nearest = new[] { (Zone.Left, fx), (Zone.Right, 1 - fx), (Zone.Top, fy), (Zone.Bottom, 1 - fy) }
            .OrderBy(e => e.Item2)
            .First();
        return nearest.Item2 < Edge ? nearest.Item1 : Zone.Middle;
    }

    // The shape a screen with `count` windows uses: the stored one when it describes exactly that many windows,
    // otherwise the automatic one.
    public static IReadOnlyList<int> ShapeFor(Maybe<TileSizes> stored, int count) =>
        stored.HasValue && stored.Value.Shape.Sum() == count ? stored.Value.Shape : GridLayout.Shape(count);

    // A shape the user made by a drop, as opposed to the one the window count gives.
    public static bool IsCustom(TileSizes sizes) =>
        !sizes.Shape.SequenceEqual(GridLayout.Shape(sizes.Shape.Sum()));

    // Nothing worth storing: even sizes on the automatic shape (what the grid is with nothing stored at all).
    public static bool IsPlain(TileSizes sizes) => sizes.IsEqual && !IsCustom(sizes);

    // Stored sizes brought to `count` windows. On the automatic shape this is TileSizes.Fit, as before. A custom
    // shape survives windows coming and going: a window that leaves takes its own cell with it (a column left empty
    // goes too), and one that joins goes to the bottom of the LAST column, which is where the board puts a joining
    // window (TileBoard.Join appends, and the order fills columns left to right). Anything else falls back to Fit.
    public static TileSizes Refit(TileSizes stored, int count, Maybe<(int Column, int Row)> leaving)
    {
        var have = stored.Shape.Sum();
        if (have == count) return stored;
        if (!IsCustom(stored)) return stored.Fit(GridLayout.Shape(count), leaving);
        if (have == count + 1 && leaving.HasValue && leaving.Value.Column < stored.Rows.Count
            && leaving.Value.Row < stored.Rows[leaving.Value.Column].Count)
            return RemoveCell(stored, leaving.Value);
        if (have < count)
        {
            var shape = stored.Shape.ToList();
            shape[^1] += count - have;
            return stored.Fit(shape, Maybe<(int Column, int Row)>.None);
        }
        return stored.Fit(GridLayout.Shape(count), leaving);
    }

    // `dragged` dropped in `zone` of `target`'s tile, on a screen whose windows are `order` (slot order, column by
    // column) laid out with `sizes`. Returns the new order and the sizes, whose shape is the new shape. A Middle
    // zone, or a window not on this screen, changes nothing (the swap is SettledMove's).
    public static (IReadOnlyList<WindowHandle> Order, TileSizes Sizes) Drop(
        IReadOnlyList<WindowHandle> order, TileSizes sizes, WindowHandle dragged, WindowHandle target, Zone zone)
    {
        var shape = sizes.Shape;
        if (zone == Zone.Middle || dragged == target || shape.Sum() != order.Count
            || !order.Contains(dragged) || !order.Contains(target))
            return (order, sizes);

        // Out of its own place first, so the target's cell is read in the shape that is left.
        var lifted = RemoveCell(sizes, GridLayout.CellOf(order.ToList().IndexOf(dragged), shape));
        var rest = order.Where(w => w != dragged).ToList();
        var at = GridLayout.CellOf(rest.IndexOf(target), lifted.Shape);
        var columnStart = lifted.Shape.Take(at.Column).Sum();

        return zone switch
        {
            Zone.Top => (Insert(rest, rest.IndexOf(target), dragged), InsertRow(lifted, at.Column, at.Row, below: false)),
            Zone.Bottom => (Insert(rest, rest.IndexOf(target) + 1, dragged), InsertRow(lifted, at.Column, at.Row, below: true)),
            Zone.Left => (Insert(rest, columnStart, dragged), InsertColumn(lifted, at.Column, right: false)),
            _ => (Insert(rest, columnStart + lifted.Shape[at.Column], dragged), InsertColumn(lifted, at.Column, right: true)),
        };
    }

    static IReadOnlyList<WindowHandle> Insert(List<WindowHandle> list, int index, WindowHandle w)
    {
        var copy = list.ToList();
        copy.Insert(index, w);
        return copy;
    }

    // Takes one cell out. Its row's share goes to the other rows of its column in proportion; a column left with
    // no rows goes, and its share goes to the other columns in proportion.
    public static TileSizes RemoveCell(TileSizes sizes, (int Column, int Row) cell)
    {
        var rows = sizes.Rows[cell.Column];
        if (rows.Count > 1)
            return sizes with { Rows = sizes.Rows.Select((r, c) => c == cell.Column ? Without(r, cell.Row) : r).ToList() };
        return new TileSizes(
            Without(sizes.Columns, cell.Column),
            sizes.Rows.Where((_, c) => c != cell.Column).ToList());
    }

    // A new row beside `row` in `column`, taking half of that row's height.
    static TileSizes InsertRow(TileSizes sizes, int column, int row, bool below) =>
        sizes with { Rows = sizes.Rows.Select((r, c) => c == column ? Split(r, row, after: below) : r).ToList() };

    // A new single-row column beside `column`, taking half of that column's width.
    static TileSizes InsertColumn(TileSizes sizes, int column, bool right)
    {
        var rows = sizes.Rows.ToList();
        rows.Insert(right ? column + 1 : column, [1.0]);
        return new TileSizes(Split(sizes.Columns, column, after: right), rows);
    }

    // Halves entry `index`, the new half going after or before it.
    static IReadOnlyList<double> Split(IReadOnlyList<double> list, int index, bool after)
    {
        var copy = list.ToList();
        var half = copy[index] / 2;
        copy[index] = half;
        copy.Insert(after ? index + 1 : index, half);
        return copy;
    }

    // Drops entry `index` and scales the rest back up to a whole, keeping their proportions.
    static IReadOnlyList<double> Without(IReadOnlyList<double> list, int index)
    {
        var rest = list.Where((_, i) => i != index).ToList();
        var total = rest.Sum();
        return total <= 0 ? rest.Select(_ => 1.0 / rest.Count).ToList() : rest.Select(f => f / total).ToList();
    }
}
