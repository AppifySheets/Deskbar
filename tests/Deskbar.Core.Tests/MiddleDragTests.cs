using Deskbar.Core.Geometry;

namespace Deskbar.Core.Tests;

// A middle drag from a 150% screen to a 100% one made the bar flicker, jump away from the cursor and end up
// back on the first screen. Measured on a 3840 x 2160 screen at 150% (DPI 144) beside a 1920 x 1080 one at
// 100% (DPI 96) at x = 3840: the bar's scale flipped 7 to 81 times per drag, its size alternating between
// 332 x 515 and 221 x 344 px, and its left edge landed up to 1600 px from the cursor.
//
// The numbers below are those. Everything is in physical pixels, which is the point: a pixel is the same
// place on both screens, a DIP is not.
public class MiddleDragTests
{
    // The bar at 150% and at 100%, as measured.
    const int WideW = 332, WideH = 515;
    const int NarrowW = 221, NarrowH = 344;

    // The second screen, where the bar has to stay once it is dropped on it.
    const int SecondLeft = 3840, SecondRight = 5760;

    [Fact]
    public void Pressing_records_where_the_cursor_is_on_the_bar() =>
        Assert.Equal(new MiddleDrag.Grab(100, 80, WideW, WideH), MiddleDrag.Press(1000, 480, 900, 400, WideW, WideH));

    // The case that must not change: one screen, or two of one scale. The bar is the same size, so it is the
    // press position plus the cursor's travel, to the pixel.
    [Fact]
    public void At_the_same_size_the_bar_follows_the_cursors_travel_exactly()
    {
        var grab = MiddleDrag.Press(1000, 480, 900, 400, WideW, WideH);

        Assert.Equal((900 + 250, 400 - 70), MiddleDrag.Target(grab, 1250, 410, WideW, WideH));
    }

    [Fact]
    public void At_the_same_size_the_bar_starts_where_it_was()
    {
        var grab = MiddleDrag.Press(1000, 480, 900, 400, WideW, WideH);

        Assert.Equal((900, 400), MiddleDrag.Target(grab, 1000, 480, WideW, WideH));
    }

    // 150% to 100%: the cursor took hold a quarter of the way along the bar and a fifth of the way down. After
    // the crossing the bar is 221 px wide, and the same quarter of it is under the cursor.
    [Fact]
    public void Crossing_to_the_smaller_scale_keeps_the_grab_point_under_the_cursor()
    {
        var grab = MiddleDrag.Press(3583, 403, 3500, 300, WideW, WideH); // 83 px in, 103 px down

        var (left, top) = MiddleDrag.Target(grab, 3900, 420, NarrowW, NarrowH);

        Assert.Equal((3845, 351), (left, top));
        Assert.InRange(3900 - left, 0.25 * NarrowW - 1, 0.25 * NarrowW + 1);
        Assert.InRange(420 - top, 0.20 * NarrowH - 1, 0.20 * NarrowH + 1);
    }

    // What the old arithmetic got wrong: once the cursor is well inside the second screen the bar is there too,
    // and a later move does not put it back on the first one.
    [Fact]
    public void A_cursor_inside_the_second_screen_puts_the_whole_bar_there()
    {
        var grab = MiddleDrag.Press(3583, 403, 3500, 300, WideW, WideH);

        foreach (var cursorX in new[] { 3990, 4200, 5000 })
        {
            var (left, _) = MiddleDrag.Target(grab, cursorX, 420, NarrowW, NarrowH);
            Assert.InRange(left, SecondLeft, SecondRight - NarrowW);
        }
    }

    // The other way, 100% to 150%: the bar is 221 px wide on the second screen and 332 once it is on the first.
    [Fact]
    public void Crossing_to_the_larger_scale_keeps_the_grab_point_under_the_cursor()
    {
        var grab = MiddleDrag.Press(4610, 370, 4500, 200, NarrowW, NarrowH); // 110 px in, 170 px down

        var (left, top) = MiddleDrag.Target(grab, 3700, 500, WideW, WideH);

        Assert.Equal((3535, 245), (left, top));
        Assert.InRange(3700 - left, 110.0 / NarrowW * WideW - 1, 110.0 / NarrowW * WideW + 1);
        Assert.InRange(500 - top, 170.0 / NarrowH * WideH - 1, 170.0 / NarrowH * WideH + 1);
    }

    // Crossing and coming back in one go: the bar is where it would have been had it never left, because the
    // position is worked out from the press and the cursor, not from the one before.
    [Fact]
    public void Crossing_and_coming_back_ends_where_it_would_have_been()
    {
        var grab = MiddleDrag.Press(3583, 403, 3500, 300, WideW, WideH);
        var direct = MiddleDrag.Target(grab, 3650, 450, WideW, WideH);

        _ = MiddleDrag.Target(grab, 4300, 450, NarrowW, NarrowH); // over on the second screen, at its size
        var back = MiddleDrag.Target(grab, 3650, 450, WideW, WideH);

        Assert.Equal(direct, back);
    }

    // A window arriving or a row wrapping changes the bar's size mid-drag with no change of scale; the grab
    // point stays under the cursor then too, which a fixed offset would not do.
    [Fact]
    public void A_bar_that_grows_during_the_drag_keeps_the_grab_point_under_the_cursor()
    {
        var grab = MiddleDrag.Press(1100, 500, 1000, 400, 200, 100); // half way along a 200 px wide bar

        var (left, top) = MiddleDrag.Target(grab, 1100, 500, 400, 100); // now 400 px wide: half way is 200 px

        Assert.Equal((900, 400), (left, top));
    }

    [Fact]
    public void A_grab_made_with_no_size_is_left_alone()
    {
        var grab = MiddleDrag.Press(10, 10, 0, 0, 0, 0);

        Assert.Equal((5, 5), MiddleDrag.Target(grab, 15, 15, 100, 100));
    }
}
