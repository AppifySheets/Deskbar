using Deskbar.App;
using Deskbar.Windows.Monitoring;

namespace Deskbar.Windows.Tests;

// FloatingBar.KeepDockedStrip takes the strip back when the shell stops holding it. The rule it judges by, with
// the numbers from the RDP session's dump that found the problem: the bar docked left at 0..258 on a 1920 wide
// screen whose work area still started at 0.
public class DockGuardTests
{
    static NativeMethods.RECT R(int left, int top, int right, int bottom) => new() { Left = left, Top = top, Right = right, Bottom = bottom };

    [Theory]
    [InlineData("left", 0, 258, 0, 1920, false)]       // the dump: the work area reaches under the strip
    [InlineData("left", 0, 258, 258, 1920, true)]      // held
    [InlineData("left", 0, 258, 306, 1920, true)]      // held, with something else docked further in
    [InlineData("right", 1662, 1920, 0, 1920, false)]  // the same failure on the other edge
    [InlineData("right", 1662, 1920, 0, 1662, true)]
    public void The_strip_is_held_only_when_the_work_area_keeps_clear_of_it(string edge, int barLeft, int barRight, int workLeft, int workRight, bool held) =>
        Assert.Equal(held, FloatingBar.StripHeld(edge, R(barLeft, 0, barRight, 1200), R(workLeft, 0, workRight, 1150)));

    // Petre: "after a few restarts and unlocks, bar ended up on another screen, docked to the edge". Measured at
    // 00:23:13 on 2026-11-01: the bar docked right on the LEFT screen at -395..0, judged against the work area
    // 0..3840 of the primary, because the left screen had dropped out for a moment on unlock and Windows called the
    // primary the nearest. The guard took that for a lost strip, registered again, and the shell gave it the
    // primary's right edge. A screen the bar is not on says nothing about the bar's strip, so there is no verdict.
    [Fact]
    public void A_screen_the_bar_is_not_on_gives_no_verdict() =>
        Assert.Null(FloatingBar.StripHeld("right", R(-395, 246, 0, 2406), monitor: R(0, 0, 3840, 2160), work: R(0, 0, 3840, 2160)));

    // The same bar measured against its own screen is judged as before: held when the work area stops at the strip.
    [Theory]
    [InlineData(-395, true)]
    [InlineData(0, false)]
    public void The_bars_own_screen_gives_the_usual_verdict(int workRight, bool held) =>
        Assert.Equal(held, FloatingBar.StripHeld("right", R(-395, 246, 0, 2406), monitor: R(-3840, 246, 0, 2406), work: R(-3840, 246, workRight, 2406)));
}
