using Deskbar.Core.Geometry;

namespace Deskbar.Core.Tests;

// The second half of the middle-drag fix. With the bounce gone, the owner dropped the bar straddling the border,
// its left edge just left of x = 3840 and most of it on the Full HD screen, and it jumped back wholly onto the 4K
// one, flush with that screen's right edge: rect 3484,1052 to 3840,1576 (356 x 524 px at DPI 144), saved
// Left 2322.67 DIPs. Windows had left the bar at the Full HD screen's scale (237 x 349 px at DPI 96) with its
// top-left corner on the 4K screen, and the placement code picks the screen from that corner.
//
// Two screens as measured: the 4K one at 150%, and the Full HD one at 100% to its right.
public class MiddleDragDropTests
{
    static readonly MiddleDrag.Screen Uhd = new(0, 0, 3840, 2160, 144);
    static readonly MiddleDrag.Screen FullHd = new(3840, 0, 5760, 1080, 96);
    static readonly MiddleDrag.Screen[] Screens = [Uhd, FullHd];

    // The bar at 100% and at 150%.
    const int NarrowW = 237, NarrowH = 349;
    const int WideW = 356, WideH = 524;

    static MiddleDrag.Screen[] Same(int dpi) => [Uhd with { Dpi = dpi }, FullHd with { Dpi = dpi }];

    [Fact]
    public void A_drop_mostly_on_the_100_percent_screen_ends_wholly_on_it()
    {
        // Left edge 40 px short of the border: 197 of its 237 px, and all of its height, are on the Full HD screen.
        var settled = MiddleDrag.Settle(3800, 701, NarrowW, NarrowH, Screens);

        Assert.Equal((3840, 701), settled); // flush with that screen, at the height it was dropped
    }

    [Fact]
    public void A_drop_mostly_on_the_150_percent_screen_ends_wholly_on_it()
    {
        // 240 of its 356 px are on the 4K screen. It ends flush with that screen's right edge, which is
        // where the owner's bar was found (3484), but because it was dropped there, not by being pulled.
        var settled = MiddleDrag.Settle(3600, 300, WideW, WideH, Screens);

        Assert.Equal((3840 - WideW, 300), settled);
        Assert.Equal(3484, settled!.Value.Left);
    }

    // Windows leaves a bar at the scale it had until its corner is well across, so a bar can be at 100% with most
    // of it still on the 4K screen, or at 150% with most of it on the Full HD one. What counts is where most of it is.
    [Fact]
    public void A_bar_at_100_percent_with_most_of_it_on_the_4K_screen_goes_there()
    {
        Assert.Equal((3840 - NarrowW, 600), MiddleDrag.Settle(3700, 600, NarrowW, NarrowH, Screens));
    }

    [Fact]
    public void A_bar_at_150_percent_with_most_of_it_on_the_Full_HD_screen_goes_there()
    {
        Assert.Equal((3840, 100), MiddleDrag.Settle(3700, 100, WideW, WideH, Screens));
    }

    // Wholly on the screen it overlaps, it can only be fitted in height, and only when it hangs off: a drop on
    // the Full HD screen's bottom, which is shorter than the 4K one, comes up to fit, not along.
    [Fact]
    public void The_height_is_kept_unless_the_bar_must_come_up_to_fit()
    {
        Assert.Equal((3840, 701), MiddleDrag.Settle(3800, 701, NarrowW, NarrowH, Screens));
        Assert.Equal((3840, 1080 - NarrowH), MiddleDrag.Settle(3800, 900, NarrowW, NarrowH, Screens));
    }

    [Fact]
    public void A_bar_wholly_on_one_screen_stays_put()
    {
        Assert.Null(MiddleDrag.Settle(4000, 300, NarrowW, NarrowH, Screens));
        Assert.Null(MiddleDrag.Settle(1000, 300, WideW, WideH, Screens));
    }

    // Over the outer edge of the desktop is not a border between screens: left as dropped, as before.
    [Fact]
    public void A_bar_hanging_off_the_outer_edge_stays_where_it_was_dropped()
    {
        Assert.Null(MiddleDrag.Settle(5700, 300, NarrowW, NarrowH, Screens));
    }

    // Two screens of one scale are exactly as before: a bar left across their border stays across it.
    [Fact]
    public void A_bar_across_screens_of_the_same_scale_stays_where_it_was_dropped()
    {
        Assert.Null(MiddleDrag.Settle(3700, 300, WideW, WideH, Same(144)));
        Assert.Null(MiddleDrag.Settle(3700, 300, NarrowW, NarrowH, Same(96)));
    }

    [Fact]
    public void Equal_shares_go_to_the_screen_listed_first()
    {
        // An even width, 118 px on each side of the border.
        Assert.Equal((3840 - 236, 300), MiddleDrag.Settle(3840 - 118, 300, 236, NarrowH, Screens));
    }

    // --- which step moved the owner's bar -----------------------------------------------------------------------

    // SnapToEdges: it snaps to an edge within 20 DIPs and never clamps, and read from the top-left corner it looks
    // at the 4K screen's edges, in that screen's DIPs (0 to 2560 across). The bar at Left 3800 is far from them.
    [Fact]
    public void Snapping_does_not_move_a_straddling_bar()
    {
        Assert.Equal((3800.0, 701.0), EdgeSnap.Snap(3800, 701, NarrowW, NarrowH, 0, 0, 3840 / 1.5, 2160 / 1.5));
    }

    // PositionFrom, which the readjustment after the change of scale runs from the stored position, as arithmetic
    // (the real one asks Windows for the screen): the screen under the stored top-left, taken as pixels; the stored
    // right edge less the bar's width; clamped into that screen in ITS DIPs. The width is the bar's at the time it
    // runs, 356 px as DIPs at the 4K screen's 150%.
    static (double Left, double Top) Restore(double left, double top, double right, double width, double height)
    {
        var screen = Screens.FirstOrDefault(s => left >= s.Left && left < s.Right && top >= s.Top && top < s.Bottom, Uhd);
        var scale = screen.Dpi / 96.0;
        return WorkAreaClamp.Clamp(right - width, top, width, height,
            screen.Left / scale, screen.Top / scale, screen.Right / scale, screen.Bottom / scale);
    }

    const double WideWDips = WideW / 1.5, WideHDips = WideH / 1.5;

    [Fact]
    public void The_readjustment_pulled_the_straddling_bar_onto_the_4K_screen()
    {
        // As stored by the drop: Left 3800, Right 3800 + 237.
        var (left, _) = Restore(3800, 701, 3800 + NarrowW, WideWDips, WideHDips);

        Assert.Equal(2322.67, left, 2); // the Left the owner found saved
    }

    // After a restart, or after the readjustment, the settled bar is where it was put: the stored position
    // (DIPs at the scale it rests at) restores to the same place, on the same screen.
    [Fact]
    public void A_settled_bar_on_the_100_percent_screen_is_restored_to_the_same_place()
    {
        var (left, top) = MiddleDrag.Settle(3800, 701, NarrowW, NarrowH, Screens)!.Value;

        // Restored at the Full HD screen's scale, and also while the window still has the 4K screen's, as at startup.
        Assert.Equal((3840.0, 701.0), Restore(left, top, left + NarrowW, NarrowW, NarrowH));
        var startup = Restore(left, top, left + NarrowW, WideWDips, WideHDips);
        Assert.Equal(3840.0, startup.Left, 0);
        Assert.Equal(701.0, startup.Top);
    }

    [Fact]
    public void A_settled_bar_on_the_150_percent_screen_is_restored_to_the_same_place()
    {
        var (left, top) = MiddleDrag.Settle(3600, 300, WideW, WideH, Screens)!.Value;

        // Stored in DIPs at 150%.
        var (storedLeft, storedTop) = (left / 1.5, top / 1.5);
        var (restoredLeft, restoredTop) = Restore(storedLeft, storedTop, storedLeft + WideWDips, WideWDips, WideHDips);

        Assert.Equal(storedLeft, restoredLeft, 2);
        Assert.Equal(storedTop, restoredTop, 2);
        Assert.Equal(3484, restoredLeft * 1.5, 1);
    }
}
