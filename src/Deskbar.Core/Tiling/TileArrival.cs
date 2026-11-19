using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Tiling;

// The tile a window splits, and on which side.
public sealed record TileSplit(WindowHandle Target, TileArrange.Zone Side);

// Which tile a window that arrives on a tiled screen splits (the design's "Change 2", predictable placement). The
// rule, in order:
//
//   1. The tile of the window last used on this screen is split, when both halves would still be at least a quarter
//      of the screen's width and a quarter of its height.
//   2. Otherwise the largest tile is split (by its share of the screen, column share times row share; on a tie the
//      one latest in slot order). It is split even when it is below the floor: a window always has to go somewhere.
//   3. The side is Right when the tile is wider than tall AND alone in its column, otherwise Bottom. A Right split
//      makes a new column, which only reads naturally beside a whole-height tile; a wide tile in a stack of rows
//      is split below instead, so the stack does not grow a column in the middle.
//
// The floor compares FRACTIONS from TileSizes, never pixels. Pixels lose the gaps between tiles, and a tile of a
// 2x2 grid would then fail a quarter it exactly has.
//
// SKIPPED TILES: the Tiler passes the screen's MAXIMIZED tiles in `skip`, and no rule above ever chooses one, neither
// as the last-used tile nor as the largest. A maximized window keeps its slot but covers the whole screen, so
// halving its slot would put the newcomer behind it, out of sight, and the maximized window would not change at all.
// Only when every tile of the screen is skipped does the rule run as if nothing were: a window still has to go
// somewhere, and the newcomer then lands in the slot the maximized window will come back to half of.
//
// Pure, like TileArrange: `tiles` is aligned with `order` (the slots GridLayout lays out), and the Tiler calls this.
public static class TileArrival
{
    // A split half may not be smaller than this share of the screen on either axis. The tolerance absorbs the
    // rounding of fractions that were halved or scaled.
    const double Floor = 0.25;
    const double Tolerance = 1e-9;

    // None only when the screen has no windows. `skip` (the maximized tiles) is honoured unless it holds every tile.
    public static Maybe<TileSplit> Choose(
        IReadOnlyList<WindowHandle> order, TileSizes sizes, IReadOnlyList<WindowRect> tiles, Maybe<WindowHandle> lastUsed,
        IReadOnlySet<WindowHandle> skip)
    {
        var everySkipped = order.All(skip.Contains);
        // Slots and cells are counted over the whole order first, so a skipped tile still holds its place in the grid.
        var candidates = order
            .Select((window, slot) => (Window: window, Slot: slot, Cell: TileArrange.CellOf(sizes, slot, order.Count)))
            .Where(t => everySkipped || !skip.Contains(t.Window))
            .Select(t => (t.Window, t.Slot, t.Cell, Side: SideOf(sizes, tiles[t.Slot], t.Cell)))
            .ToList();

        var preferred = candidates
            .Where(t => lastUsed.HasValue && t.Window == lastUsed.Value && PassesFloor(sizes, t.Cell, t.Side))
            .Select(t => Maybe.From(t))
            .FirstOrDefault(Maybe<(WindowHandle Window, int Slot, (int Column, int Row) Cell, TileArrange.Zone Side)>.None);

        // The largest tile, the later slot winning a tie (OrderBy is stable, so Last of the ascending order).
        var largest = candidates
            .OrderBy(t => sizes.Columns[t.Cell.Column] * sizes.Rows[t.Cell.Column][t.Cell.Row], Comparer<double>.Create(Compare))
            .Select(t => Maybe.From(t))
            .LastOrDefault(Maybe<(WindowHandle Window, int Slot, (int Column, int Row) Cell, TileArrange.Zone Side)>.None);

        return preferred.Or(largest).Map(t => new TileSplit(t.Window, t.Side));
    }

    // ---- the split tree (TileLayout), beside the TileSizes version above until the Tiler is switched over ----

    // The same three rules on a split tree (spec, "Arrival and minimize"). Each window's leaf comes from its slot
    // (TileArrange.LeafOf skips the opening), and its shares of the screen from TileLayout.ShareOf, where the grid
    // read Columns[c] and Rows[c][r]: the floor and the largest tile compare those fractions, never pixels.
    //
    // Rule 3 CHANGES on purpose: the side is Right whenever the tile is wider than tall. "Alone in its column" existed
    // because a Right split made a whole new column, which would have narrowed every tile of the stack; on the tree,
    // Right splits only that tile (TileLayout.Insert), so a wide tile in a stack is split beside itself.
    //
    // A layout that does not describe this many windows (neither one leaf per window nor an opening for them) is read
    // as the automatic layout for the count, the one such a screen is laid out on, rather than reading shares of
    // leaves that are not the windows'.
    public static Maybe<TileSplit> Choose(
        IReadOnlyList<WindowHandle> order, TileLayout layout, IReadOnlyList<WindowRect> tiles, Maybe<WindowHandle> lastUsed,
        IReadOnlySet<WindowHandle> skip)
    {
        var read = layout.Count == order.Count || layout.HasOpeningFor(order.Count) ? layout : TileLayout.Automatic(order.Count);
        var everySkipped = order.All(skip.Contains);
        // Slots and leaves are counted over the whole order first, so a skipped tile still holds its place.
        var candidates = order
            .Select((window, slot) => (Window: window, Slot: slot, Share: read.ShareOf(TileArrange.LeafOf(read, slot, order.Count))))
            .Where(t => everySkipped || !skip.Contains(t.Window))
            .Select(t => (t.Window, t.Share, Side: tiles[t.Slot].Width > tiles[t.Slot].Height ? TileArrange.Zone.Right : TileArrange.Zone.Bottom))
            .ToList();

        var preferred = candidates
            .Where(t => lastUsed.HasValue && t.Window == lastUsed.Value && PassesFloor(t.Share, t.Side))
            .Select(t => Maybe.From(t))
            .FirstOrDefault(Maybe<(WindowHandle Window, (double Width, double Height) Share, TileArrange.Zone Side)>.None);

        // The largest tile by its share of the screen, the later slot winning a tie (OrderBy is stable).
        var largest = candidates
            .OrderBy(t => t.Share.Width * t.Share.Height, Comparer<double>.Create(Compare))
            .Select(t => Maybe.From(t))
            .LastOrDefault(Maybe<(WindowHandle Window, (double Width, double Height) Share, TileArrange.Zone Side)>.None);

        return preferred.Or(largest).Map(t => new TileSplit(t.Window, t.Side));
    }

    // Both halves keep a quarter of the screen's width share and height share, by fractions.
    static bool PassesFloor((double Width, double Height) share, TileArrange.Zone side) =>
        side == TileArrange.Zone.Right
            ? share.Width / 2 >= Floor - Tolerance && share.Height >= Floor - Tolerance
            : share.Width >= Floor - Tolerance && share.Height / 2 >= Floor - Tolerance;

    // Right for a tile wider than tall that is alone in its column, otherwise Bottom.
    static TileArrange.Zone SideOf(TileSizes sizes, WindowRect tile, (int Column, int Row) cell) =>
        tile.Width > tile.Height && sizes.Rows[cell.Column].Count == 1 ? TileArrange.Zone.Right : TileArrange.Zone.Bottom;

    // Both halves keep a quarter of the screen's width share and height share, by fractions.
    static bool PassesFloor(TileSizes sizes, (int Column, int Row) cell, TileArrange.Zone side)
    {
        var column = sizes.Columns[cell.Column];
        var row = sizes.Rows[cell.Column][cell.Row];
        return side == TileArrange.Zone.Right
            ? column / 2 >= Floor - Tolerance && row >= Floor - Tolerance
            : column >= Floor - Tolerance && row / 2 >= Floor - Tolerance;
    }

    // Shares closer than the tolerance are a tie (they come from halving and rescaling, so exact equality is luck).
    static int Compare(double a, double b) => Math.Abs(a - b) < Tolerance ? 0 : a.CompareTo(b);
}
