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

        Assert.IsType<ManagerDockLayout.AfterDrag.SnapBack>(ManagerDockLayout.Dragged(Strip, moved, 1.5, PanelEdge.Right));
    }

    // Its inner edge dragged 150 px further in: 1110 px at 150% is a 740 DIP dock.
    [Fact]
    public void A_resize_takes_the_new_width_in_dips()
    {
        var wider = new WindowRect(-2190, 322, -1080, 2446);

        var after = Assert.IsType<ManagerDockLayout.AfterDrag.Resize>(ManagerDockLayout.Dragged(Strip, wider, 1.5, PanelEdge.Right));
        Assert.Equal(740, after.WidthDips);
    }

    // A resize that ends within a few pixels of where it started is a snap, not a new width.
    [Fact]
    public void A_few_pixels_of_width_are_not_a_resize() =>
        Assert.IsType<ManagerDockLayout.AfterDrag.SnapBack>(
            ManagerDockLayout.Dragged(Strip, Strip with { Left = Strip.Left - ManagerDockLayout.ResizeSlackPixels }, 1.5, PanelEdge.Right));

    // Review finding: a title-bar drag that ends snapped to the far half, or on another screen, changes the frame's width
    // too, but its outer edge, top or bottom moved, so it is a snap back, never a width. (A maximize, or a Snap to the half
    // beside the panel, can leave all three where a resize would; the app tells those apart by the window's own state,
    // IsZoomed and IsShellArranged, before it asks this rule. See ManagerDock.DragEnded.)
    [Theory]
    [InlineData(-3840, 322, -2460, 2446)]   // snapped to the far left half
    [InlineData(-2040, 400, -1080, 1400)]   // snapped to a quarter beside the panel: top and bottom moved
    [InlineData(100, 36, 1000, 2160)]       // dropped on the other screen
    public void A_drag_that_moved_more_than_the_inner_edge_snaps_back(int left, int top, int right, int bottom) =>
        Assert.IsType<ManagerDockLayout.AfterDrag.SnapBack>(
            ManagerDockLayout.Dragged(Strip, new WindowRect(left, top, right, bottom), 1.5, PanelEdge.Right));

    // Docked on the left edge, the outer edge is the left one, and the inner (right) edge is the one a resize moves.
    [Fact]
    public void On_the_left_edge_the_right_edge_is_the_inner_one()
    {
        var strip = new WindowRect(1080, 36, 2040, 2160);

        Assert.IsType<ManagerDockLayout.AfterDrag.Resize>(
            ManagerDockLayout.Dragged(strip, strip with { Right = 2190 }, 1.5, PanelEdge.Left));
        Assert.IsType<ManagerDockLayout.AfterDrag.SnapBack>(
            ManagerDockLayout.Dragged(strip, strip with { Left = 930 }, 1.5, PanelEdge.Left));
    }
}
