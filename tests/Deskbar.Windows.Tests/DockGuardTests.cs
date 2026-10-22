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
}
