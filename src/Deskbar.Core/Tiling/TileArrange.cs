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
    public static bool IsPlain(TileSizes sizes) => sizes.IsEqual && !IsCustom(sizes) && sizes.Opening is null;

    // Stored sizes brought to `count` windows. The shape survives windows leaving on every shape, the automatic one
    // included: a window that leaves takes its own cell with it (a column left empty goes too). On the automatic
    // shape anything else is TileSizes.Fit, as before. A custom shape also survives a window joining: one that joins goes to the bottom of the LAST column, which is where the board puts a joining
    // window (TileBoard.Join appends, and the order fills columns left to right). Anything else falls back to Fit.
    //
    // An OPENING stays while other windows leave: `leaving` is then the leaver's CELL (CellOf below counts the opening),
    // and only that cell goes. Any other change closes the opening first and fits what is left. A window that joins
    // never reaches here with an opening open: Tiler.Keep puts it into the opening (Filled) before anything is laid out.
    public static TileSizes Refit(TileSizes stored, int count, Maybe<(int Column, int Row)> leaving)
    {
        if (stored.Opening is not null)
            return stored.HasOpeningFor(count) ? stored
                : stored.Shape.Sum() == count + 2 && leaving.HasValue && Holds(stored, leaving.Value) && Slot(stored, leaving.Value) != stored.Opening
                    ? RemoveCell(stored, leaving.Value)
                    : Refit(Closed(stored), count, Maybe<(int Column, int Row)>.None);

        var have = stored.Shape.Sum();
        if (have == count) return stored;
        // A leaver takes only its own cell, on EVERY shape (the automatic one too). Petre: "on closing, i sometimes
        // want not to move other windows". Rebuilding the automatic grid from the new count moved tiles between
        // columns (a 2x2 losing its bottom-right tile became 1+2 and the bottom-left tile jumped across).
        if (have == count + 1 && leaving.HasValue && Holds(stored, leaving.Value))
            return RemoveCell(stored, leaving.Value);
        if (!IsCustom(stored)) return stored.Fit(GridLayout.Shape(count), leaving);
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
        // A rearrangement closes the screen's opening: the drop makes its own room.
        sizes = sizes.HasOpeningFor(order.Count) ? Closed(sizes) : sizes;
        var shape = sizes.Shape;
        if (zone == Zone.Middle || dragged == target || shape.Sum() != order.Count || !order.Contains(target))
            return (order, sizes);

        // Out of its own place first, so the target's cell is read in the shape that is left. A window coming from
        // ANOTHER screen (dragged across, Petre: "on the right it only highlighted the right circle") has no place
        // here to leave; its own screen gives its cell up as any leaver's.
        var lifted = order.Contains(dragged) ? RemoveCell(sizes, GridLayout.CellOf(order.ToList().IndexOf(dragged), shape)) : sizes;
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
    // no rows goes, and its share goes to the other columns in proportion. An opening after the cell moves up one
    // slot with it; taking out the opening's own cell closes it.
    public static TileSizes RemoveCell(TileSizes sizes, (int Column, int Row) cell)
    {
        var slot = Slot(sizes, cell);
        var opening = sizes.Opening is { } open && open != slot ? open > slot ? open - 1 : open : (int?)null;
        var rows = sizes.Rows[cell.Column];
        return rows.Count > 1
            ? sizes with { Rows = sizes.Rows.Select((r, c) => c == cell.Column ? Without(r, cell.Row) : r).ToList(), Opening = opening }
            : new TileSizes(Without(sizes.Columns, cell.Column), sizes.Rows.Where((_, c) => c != cell.Column).ToList()) { Opening = opening };
    }

    // --- openings: room kept for the next window (TileSizes.Opening) ---------------------------------------------

    // No opening is made narrower than this share of the screen's inner span, and the tile that makes it keeps at
    // least as much: a pull smaller than this is a nudge and snaps back, as an outer edge always did.
    const double MinOpening = 0.1;

    // The cell of the window at `slot` (its index in the screen's order, `count` windows) in these sizes: the opening,
    // when there is one, holds a cell of its own that no window's slot counts.
    public static (int Column, int Row) CellOf(TileSizes sizes, int slot, int count) =>
        sizes.HasOpeningFor(count)
            ? GridLayout.CellOf(slot >= sizes.Opening ? slot + 1 : slot, sizes.Shape)
            : GridLayout.CellOf(slot, ShapeFor(Maybe.From(sizes), count));

    // A tile at `cell` whose OUTER edge was pulled in (one that faces the screen's edge, so SplitMove has no split to
    // move) leaves an opening there: a new row above or below it in its column for the top or bottom edge, a new
    // column beside its column for the left or right edge, as deep as the pull. None when the screen has an opening
    // already (one at a time), or the pull is under MinOpening (a nudge). With two outer edges pulled (a corner), the
    // deeper pull wins.
    public static Maybe<TileSizes> Open(TileSizes sizes, int count, WindowRect area, int gap, (int Column, int Row) cell, Edges moved, WindowRect frame)
    {
        if (sizes.Opening is not null || sizes.Shape.Sum() != count) return Maybe<TileSizes>.None;
        var inner = GridLayout.InnerArea(area, gap);
        var shape = sizes.Shape;
        var columnStart = shape.Take(cell.Column).Sum();
        var row = sizes.Rows[cell.Column][cell.Row];
        var column = sizes.Columns[cell.Column];

        // Per outer edge that was pulled: its depth as a share of the inner span, the share the tile has on that axis
        // (rows are shares of the column's height, columns of the width), and how to make the opening that deep.
        var pulls = new List<(double Depth, double Room, Func<double, TileSizes> Make)>();
        if (moved.HasFlag(Edges.Top) && cell.Row == 0)
            pulls.Add(((frame.Top - inner.Top) / (double)inner.Height, row, d =>
                WithRows(sizes, cell.Column, Carve(sizes.Rows[cell.Column], cell.Row, d, after: false)) with { Opening = columnStart + cell.Row }));
        if (moved.HasFlag(Edges.Bottom) && cell.Row == shape[cell.Column] - 1)
            pulls.Add(((inner.Bottom - frame.Bottom) / (double)inner.Height, row, d =>
                WithRows(sizes, cell.Column, Carve(sizes.Rows[cell.Column], cell.Row, d, after: true)) with { Opening = columnStart + cell.Row + 1 }));
        if (moved.HasFlag(Edges.Left) && cell.Column == 0)
            pulls.Add(((frame.Left - inner.Left) / (double)inner.Width, column, d =>
                WithColumn(sizes, cell.Column, d, right: false) with { Opening = columnStart }));
        if (moved.HasFlag(Edges.Right) && cell.Column == shape.Count - 1)
            pulls.Add(((inner.Right - frame.Right) / (double)inner.Width, column, d =>
                WithColumn(sizes, cell.Column, d, right: true) with { Opening = columnStart + shape[cell.Column] }));

        // The tile keeps at least MinOpening of its own share; a tile too small to give that much up makes none.
        return pulls
            .Where(p => p.Depth >= MinOpening && p.Room >= 2 * MinOpening)
            .OrderByDescending(p => p.Depth)
            .Select(p => Maybe.From(p.Make(Math.Min(p.Depth, p.Room - MinOpening))))
            .FirstOrDefault(Maybe<TileSizes>.None);
    }

    // KEEP THE SPACE (tiling-predictable-placement design, change 3). With the workspace's switch on, a tile that
    // CLOSES does not take its cell with it: the cell stays, empty, as the screen's opening, and the next window to
    // arrive goes into it (Tiler.Arrival.cs, step 2). The sizes are otherwise untouched, so every other tile is laid
    // out exactly where it was: the slots behind the leaver move up one, and CellOf maps a slot at or after the
    // opening one cell on, which is the cell each of those windows already had.
    //
    // `sizes` describe the grid as it was before the close, one cell per window then, and `cell` is the leaver's cell
    // in them. A cell the sizes do not have (it cannot happen from the Tiler, which reads the cell off these same
    // sizes, but a wrong slot must never become an opening no grid can lay out) keeps nothing: the sizes come back
    // as they were. An opening is never plain (IsPlain), so SetTileSizes stores it even on an otherwise even grid.
    public static TileSizes KeptOpen(TileSizes sizes, (int Column, int Row) cell) =>
        Holds(sizes, cell) ? sizes with { Opening = Slot(sizes, cell) } : sizes;

    // The screen's opening closed: its cell goes, and its share goes to the tiles around it in proportion.
    public static TileSizes Closed(TileSizes sizes) =>
        sizes.Opening is { } open && open >= 0 && open < sizes.Shape.Sum()
            ? RemoveCell(sizes, GridLayout.CellOf(open, sizes.Shape))
            : sizes with { Opening = null };

    // True when the tile at `cell` was pulled back over the opening next to it: its edge facing the opening let go
    // within a quarter of the opening's depth from the opening's far side. That gives the room back, the opening
    // closes. Left and right only close an opening that is a whole column: beside a column of several tiles the edge
    // faces the column, not the opening.
    public static bool Closes(TileSizes sizes, int count, WindowRect area, int gap, (int Column, int Row) cell, Edges moved, WindowRect frame)
    {
        if (sizes.Opening is not { } open || !GridLayout.OpeningOf(area, count, gap, sizes).TryGetValue(out var hole))
            return false;
        var at = GridLayout.CellOf(open, sizes.Shape);
        var wholeColumn = sizes.Shape[at.Column] == 1;
        return (moved.HasFlag(Edges.Top) && at.Column == cell.Column && at.Row == cell.Row - 1 && frame.Top <= hole.Top + hole.Height / 4)
            || (moved.HasFlag(Edges.Bottom) && at.Column == cell.Column && at.Row == cell.Row + 1 && frame.Bottom >= hole.Bottom - hole.Height / 4)
            || (moved.HasFlag(Edges.Left) && wholeColumn && at.Column == cell.Column - 1 && frame.Left <= hole.Left + hole.Width / 4)
            || (moved.HasFlag(Edges.Right) && wholeColumn && at.Column == cell.Column + 1 && frame.Right >= hole.Right - hole.Width / 4);
    }

    // `newcomer` joins a screen whose windows are `order`: into the opening when the sizes have one for `order`, and
    // the opening is then filled (gone); otherwise at the end, with the sizes as they were.
    public static (IReadOnlyList<WindowHandle> Order, TileSizes Sizes) Filled(IReadOnlyList<WindowHandle> order, TileSizes sizes, WindowHandle newcomer) =>
        sizes.HasOpeningFor(order.Count) && sizes.Opening is { } open
            ? (Insert(order.ToList(), open, newcomer), sizes with { Opening = null })
            : (order.Append(newcomer).ToList(), sizes);

    // A tile of this screen dragged into its opening: it leaves its own cell (the tiles around share that) and fills
    // the opening. Unchanged when there is no opening or the window is not on the screen.
    public static (IReadOnlyList<WindowHandle> Order, TileSizes Sizes) IntoOpening(IReadOnlyList<WindowHandle> order, TileSizes sizes, WindowHandle window) =>
        sizes.HasOpeningFor(order.Count) && order.Contains(window)
            ? Filled(order.Where(w => w != window).ToList(), RemoveCell(sizes, CellOf(sizes, order.ToList().IndexOf(window), order.Count)), window)
            : (order, sizes);

    static bool Holds(TileSizes sizes, (int Column, int Row) cell) =>
        cell.Column >= 0 && cell.Column < sizes.Rows.Count && cell.Row >= 0 && cell.Row < sizes.Rows[cell.Column].Count;

    // The slot (column by column) of a cell.
    static int Slot(TileSizes sizes, (int Column, int Row) cell) =>
        sizes.Shape.Take(cell.Column).Sum() + cell.Row;

    static TileSizes WithRows(TileSizes sizes, int column, IReadOnlyList<double> rows) =>
        sizes with { Rows = sizes.Rows.Select((r, c) => c == column ? rows : r).ToList() };

    // A new single-row column of `depth` beside `column`, which gives that much of its width up.
    static TileSizes WithColumn(TileSizes sizes, int column, double depth, bool right)
    {
        var columns = sizes.Columns.ToList();
        columns[column] -= depth;
        columns.Insert(right ? column + 1 : column, depth);
        var rows = sizes.Rows.ToList();
        rows.Insert(right ? column + 1 : column, [1.0]);
        return sizes with { Columns = columns, Rows = rows };
    }

    // Entry `index` gives `depth` up to a new entry after or before it.
    static IReadOnlyList<double> Carve(IReadOnlyList<double> list, int index, double depth, bool after)
    {
        var copy = list.ToList();
        copy[index] -= depth;
        copy.Insert(after ? index + 1 : index, depth);
        return copy;
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
