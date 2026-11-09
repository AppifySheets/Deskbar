using Deskbar.Core.Geometry;

namespace Deskbar.Core.Tests;

// The bar that restored off-screen on a desk of mixed scales. Measured: a 2560 x 1440 primary at 150% and a
// 1920 x 1080 screen at 100% starting at x = 3840 (1280 px of nothing between them). Saved docked to the 100%
// screen's left edge, Left 3840, Top 324, 220 DIPs wide. The window starts on the primary at 150%, and it was
// placed at 5760,0 to 6090,1548: the saved DIPs read at 150%, on no monitor. The strip was reserved right, at
// 3840 to 4060.
public class ScaleHopTests
{
    static readonly MiddleDrag.Screen Primary = new(0, 0, 2560, 1440, 144);
    static readonly MiddleDrag.Screen Secondary = new(3840, 0, 5760, 1080, 96);

    // The bar floating at 220 DIPs wide and 414 DIPs tall, at 150% and at 100%.
    const int WideW = 330, WideH = 621;
    const int NarrowW = 220, NarrowH = 414;

    // What went wrong, pinned so the explanation stays true: the saved DIPs at the WINDOW's scale are the
    // measured rectangles, docked (5760 and 330 wide) and undocked (5760,486).
    [Fact]
    public void The_saved_dips_read_at_150_percent_are_where_the_bar_was_found()
    {
        Assert.Equal(5760, ScaleHop.ToPixels(3840, Primary.Dpi));
        Assert.Equal(330, ScaleHop.ToPixels(220, Primary.Dpi));
        Assert.Equal(486, ScaleHop.ToPixels(324, Primary.Dpi));
    }

    // The fix: a window still at 150% goes to the saved spot on the 100% screen, in pixels, first...
    [Fact]
    public void A_bar_at_150_percent_restored_to_the_100_percent_screen_moves_there_first()
    {
        Assert.Equal((3840, 324), ScaleHop.Target(3840, 324, WideW, WideH, Primary.Dpi, Secondary));
    }

    // ...wholly on it, so that screen holds all of it and Windows gives it that screen's scale. After that the same
    // DIPs are that screen's pixels: left edge at 3840, 220 px wide, not 5760 and 330.
    [Fact]
    public void Once_at_the_screens_scale_the_saved_dips_land_where_they_were_saved()
    {
        var (left, top) = ScaleHop.Target(3840, 324, WideW, WideH, Primary.Dpi, Secondary)!.Value;
        Assert.True(left >= Secondary.Left && left + WideW <= Secondary.Right);
        Assert.True(top >= Secondary.Top && top + WideH <= Secondary.Bottom);

        Assert.Equal(3840, ScaleHop.ToPixels(3840, Secondary.Dpi));
        Assert.Equal(220, ScaleHop.ToPixels(220, Secondary.Dpi));
    }

    // Docked: the window as it was found, 330 x 1548 px at 5760,0, and the strip the shell granted, 3840,0 to
    // 4060,1032. Taller than the screen, so it is pinned to the screen's top, which still puts all of the screen's
    // height under it and none of any other screen.
    [Fact]
    public void A_docked_bar_found_off_screen_moves_onto_the_strips_screen()
    {
        Assert.Equal((3840, 0), ScaleHop.Target(3840, 0, 330, 1548, Primary.Dpi, Secondary));
    }

    // The mirror: saved floating on the 150% screen (Left 1500, Top 300, so pixel 2250,450) while the window is at
    // 100%. Without the move it lands at pixel 1500,300, still on the primary but not where it was put.
    [Fact]
    public void A_bar_at_100_percent_restored_to_the_150_percent_screen_moves_to_its_saved_pixel()
    {
        Assert.Equal(1500, ScaleHop.ToPixels(1500, Secondary.Dpi)); // the old landing
        Assert.Equal((2250, 450), ScaleHop.Target(1500, 300, NarrowW, NarrowH, Secondary.Dpi, Primary));
        Assert.Equal(2250, ScaleHop.ToPixels(1500, Primary.Dpi));
    }

    // A spot that would hang off the screen still moves the window wholly onto it.
    [Fact]
    public void A_spot_near_the_far_edge_still_moves_the_window_wholly_onto_the_screen()
    {
        Assert.Equal((5760 - WideW, 1080 - WideH), ScaleHop.Target(5700, 1000, WideW, WideH, Primary.Dpi, Secondary));
    }

    // The screen saved on is gone: the placement clamps the saved spot onto the screen that is left (in its DIPs,
    // 1706 x 960 at 150%), and a window at another scale is moved onto that one, never off it.
    [Fact]
    public void When_the_saved_screen_is_gone_the_window_moves_onto_the_one_that_is_left()
    {
        var at = ScaleHop.Target(1486, 324, NarrowW, NarrowH, Secondary.Dpi, Primary)!.Value;
        Assert.Equal((2229, 486), at);
        Assert.True(at.Left + NarrowW <= Primary.Right && at.Top + NarrowH <= Primary.Bottom);
    }

    // One scale, nothing to correct: every desk whose screens share a scale places the bar exactly as before.
    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    public void A_window_already_at_the_screens_scale_is_not_moved(int dpi)
    {
        Assert.Null(ScaleHop.Target(3840, 324, NarrowW, NarrowH, dpi, Secondary with { Dpi = dpi }));
    }
}
