using Deskbar.Core.Domain;
using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests.Tasks;

// The manager dock's pure rules (task f171bca6, ManagerDockLayout): its width, where the terminal's window goes so its
// visible frame fills the strip, and what a drag of the docked terminal means. The numbers are the probe's, measured on
// Petre's left 4K screen at 150% on 2026-11-25.
public class ManagerDockLayoutTests
{
    // 640 DIP at 150% was granted as -2040..-1080, inside the tasks panel, the full work-area height.
    static readonly WindowRect Strip = new(-2040, 322, -1080, 2446);

    [Theory]
    [InlineData(640, 2560, 640)]   // the default fits a 2560 DIP screen
    [InlineData(100, 2560, 360)]   // never narrower than the minimum
    [InlineData(2000, 2560, 1280)] // never more than half the screen
    [InlineData(500, 600, 360)]    // a screen under twice the minimum still gets the minimum
    public void The_width_stays_between_the_minimum_and_half_the_screen(double asked, double screen, double expected) =>
        Assert.Equal(expected, ManagerDockLayout.Clamp(asked, screen));

    // Windows Terminal put on the strip had its frame 9 px inside on the left, right and bottom: window -2040..-1080,
    // frame -2031..-1089 to 2437. So the window is grown by those 9 px, and the frame then covers the strip exactly.
    [Fact]
    public void The_terminal_window_is_grown_by_its_invisible_border_so_its_frame_fills_the_strip()
    {
        var window = new WindowRect(-2040, 322, -1080, 2446);
        var frame = new WindowRect(-2031, 322, -1089, 2437);

        Assert.Equal(new WindowRect(-2049, 322, -1071, 2455), ManagerDockLayout.TerminalWindowFor(Strip, window, frame));
    }

    // Dragged by the title bar: the frame is somewhere else but as wide as the strip, so it snaps back.
    [Fact]
    public void A_move_snaps_back()
    {
        var moved = new WindowRect(-3000, 400, -2040, 2524);

        Assert.IsType<ManagerDockLayout.AfterDrag.SnapBack>(ManagerDockLayout.Dragged(Strip, moved, 1.5));
    }

    // Its inner edge dragged 150 px further in: 1110 px at 150% is a 740 DIP dock.
    [Fact]
    public void A_resize_takes_the_new_width_in_dips()
    {
        var wider = new WindowRect(-2190, 322, -1080, 2446);

        var after = Assert.IsType<ManagerDockLayout.AfterDrag.Resize>(ManagerDockLayout.Dragged(Strip, wider, 1.5));
        Assert.Equal(740, after.WidthDips);
    }

    // A resize that ends within a few pixels of where it started is a snap, not a new width.
    [Fact]
    public void A_few_pixels_of_width_are_not_a_resize() =>
        Assert.IsType<ManagerDockLayout.AfterDrag.SnapBack>(
            ManagerDockLayout.Dragged(Strip, Strip with { Left = Strip.Left - ManagerDockLayout.ResizeSlackPixels }, 1.5));
}
