using Deskbar.Core.Domain;

namespace Deskbar.Core.Tiling;

// Where a tile goes when it is dropped on an EDGE of another tile, and the layout that follows. Petre, testing
// tiling live: "i still can't drag below another window". Two windows were always two columns, because the grid
// worked its shape out from the window count (GridLayout.Shape) and nothing could ask for another. He picked drop
// zones from a mockup:
//
//   * the top or bottom quarter of a tile stacks the dropped window above or below it;
//   * the left or right quarter puts it beside that tile;
//   * the middle swaps, as before (SettledMove).
//
// The layout a drop makes is KEPT, stored per screen as a split tree (TileLayout, Workspace.TileLayouts); "Even out
// tiles" evens its splits and keeps it, "Reset to grid" forgets it. The rules on the tree live in TileArrange.Layout.cs
// (spec: docs/superpowers/specs/2026-11-19-tiling-split-tree-design.md). This file holds the parts that are about a
// tile's rectangle rather than the tree: the zones and the shared helpers. The columns-of-rows versions of every rule
// (on TileSizes) were here until the Tiler moved to the tree; TileSizes is now only read by state.json's migration.
//
// Pure, like GridLayout: the Tiler calls it, and the tests reach every rule without Windows.
public static partial class TileArrange
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

    // --- openings: room kept for the next window (TileLayout.Opening) --------------------------------------------

    // No opening is made narrower than this share of the screen's inner span, and the tile that makes it keeps at
    // least as much: a pull smaller than this is a nudge and snaps back, as an outer edge always did.
    const double MinOpening = 0.1;

    static IReadOnlyList<WindowHandle> Insert(List<WindowHandle> list, int index, WindowHandle w)
    {
        var copy = list.ToList();
        copy.Insert(index, w);
        return copy;
    }
}
