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
//   2. Otherwise the largest tile is split (by its share of the screen, width share times height share; on a tie the
//      one latest in slot order). It is split even when it is below the floor: a window always has to go somewhere.
//   3. The side is Right when the tile is wider than tall, otherwise Bottom.
//
// The floor compares FRACTIONS from the layout (TileLayout.ShareOf), never pixels. Pixels lose the gaps between
// tiles, and a tile of a 2x2 grid would then fail a quarter it exactly has.
//
// SKIPPED TILES: the Tiler passes the screen's MAXIMIZED tiles in `skip`, and no rule above ever chooses one, neither
// as the last-used tile nor as the largest. Since 2026-11-20 a window maximized by hand leaves the board altogether
// (Tiler.cs), so the only maximized tile still on a board is one the Tiler is un-maximizing itself, whose restore has
// not landed yet. It keeps its slot but covers the whole screen, so halving its slot would put the newcomer behind it,
// out of sight, and the maximized window would not change at all.
// Only when every tile of the screen is skipped does the rule run as if nothing were: a window still has to go
// somewhere, and the newcomer then lands in the slot the maximized window will come back to half of.
//
// AVOIDED TILES (spec 2026-11-23-split-recent-app-windows-last-design.md): the three latest windows of an app marked
// "split last" (VS Code), passed in `avoid` by the Tiler. Petre: "do it so that vscode windows are the last to be
// affected", then "maybe the last few vscode windows that were active, last 3", and of the ways offered, the middle
// one: keep splitting the tile last used (a new window lands beside what you were using), except an avoided one. So the
// tiers are: the FREE tiles (in neither set) by rules 1 and 2; only when no tile is free, the avoided ones, the OLDEST
// first by `age` (0 is the window activated most recently), so the window in use now is split last of all, the floor
// still respected; and only when those are gone too, every tile, as above. An avoided tile is never the "last used"
// choice, which is the whole point: the window you are working in does not shrink.
//
// Pure, like TileArrange: `tiles` is aligned with `order` (the slots GridLayout lays out), and the Tiler calls this.
public static class TileArrival
{
    // A split half may not be smaller than this share of the screen on either axis. The tolerance absorbs the
    // rounding of fractions that were halved or scaled.
    const double Floor = 0.25;
    const double Tolerance = 1e-9;

    // The three rules on the split tree (spec, "Arrival and minimize"). Each window's leaf comes from its slot
    // (TileArrange.LeafOf skips the opening), and its shares of the screen from TileLayout.ShareOf, where the old
    // grid read Columns[c] and Rows[c][r]: the floor and the largest tile compare those fractions, never pixels.
    //
    // Rule 3 CHANGED on purpose with the tree: the grid's rule gave Right only to a tile alone in its column, because
    // a Right split made a whole new column, which would have narrowed every tile of the stack. On the tree, Right
    // splits only that tile (TileLayout.Insert), so a wide tile in a stack is split beside itself.
    //
    // A layout that does not describe this many windows (neither one leaf per window nor an opening for them) is read
    // as the automatic layout for the count, the one such a screen is laid out on, rather than reading shares of
    // leaves that are not the windows'.
    public static Maybe<TileSplit> Choose(
        IReadOnlyList<WindowHandle> order, TileLayout layout, IReadOnlyList<WindowRect> tiles, Maybe<WindowHandle> lastUsed,
        IReadOnlySet<WindowHandle> skip, IReadOnlySet<WindowHandle>? avoid = null, Func<WindowHandle, int>? age = null)
    {
        var read = layout.Count == order.Count || layout.HasOpeningFor(order.Count) ? layout : TileLayout.Automatic(order.Count);
        var avoided = avoid ?? new HashSet<WindowHandle>();
        // Slots and leaves are counted over the whole order first, so a skipped or avoided tile still holds its place.
        var all = order
            .Select((window, slot) => (Window: window, Share: read.ShareOf(TileArrange.LeafOf(read, slot, order.Count)),
                Side: tiles[slot].Width > tiles[slot].Height ? TileArrange.Zone.Right : TileArrange.Zone.Bottom))
            .ToList();
        var free = all.Where(t => !skip.Contains(t.Window) && !avoided.Contains(t.Window)).ToList();
        var avoidedOnly = all.Where(t => !skip.Contains(t.Window) && avoided.Contains(t.Window)).ToList();
        return free.Count > 0 ? LastUsedOrLargest(free, lastUsed)
            : avoidedOnly.Count > 0 ? OldestOrLargest(avoidedOnly, age ?? (_ => int.MaxValue))
            : LastUsedOrLargest(all, lastUsed);
    }

    // Rules 1 and 2 over `candidates`: the tile last used when it passes the floor, otherwise the largest.
    static Maybe<TileSplit> LastUsedOrLargest(
        IReadOnlyList<(WindowHandle Window, (double Width, double Height) Share, TileArrange.Zone Side)> candidates,
        Maybe<WindowHandle> lastUsed)
    {
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

    // The avoided tier: the oldest tile (largest `age`) that passes the floor, the later slot winning a tie; when none
    // passes, the largest, as rule 2 would.
    static Maybe<TileSplit> OldestOrLargest(
        IReadOnlyList<(WindowHandle Window, (double Width, double Height) Share, TileArrange.Zone Side)> candidates,
        Func<WindowHandle, int> age) =>
        candidates
            .Where(t => PassesFloor(t.Share, t.Side))
            .OrderBy(t => age(t.Window))
            .Select(t => Maybe.From(new TileSplit(t.Window, t.Side)))
            .LastOrDefault(Maybe<TileSplit>.None)
            .Or(() => LastUsedOrLargest(candidates, Maybe<WindowHandle>.None));

    // Both halves keep a quarter of the screen's width share and height share, by fractions.
    static bool PassesFloor((double Width, double Height) share, TileArrange.Zone side) =>
        side == TileArrange.Zone.Right
            ? share.Width / 2 >= Floor - Tolerance && share.Height >= Floor - Tolerance
            : share.Width >= Floor - Tolerance && share.Height / 2 >= Floor - Tolerance;

    // Shares closer than the tolerance are a tie (they come from halving and rescaling, so exact equality is luck).
    static int Compare(double a, double b) => Math.Abs(a - b) < Tolerance ? 0 : a.CompareTo(b);
}
