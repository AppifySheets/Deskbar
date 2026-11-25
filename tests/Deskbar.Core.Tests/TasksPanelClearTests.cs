using Deskbar.Core.Domain;
using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests;

// The tasks panel taking its strip back from the windows under it (task cafaae24, "tasks should reclaim space from other
// windows"). The shell resizes maximized windows to the new work area when the panel registers its app bar, but a
// restored window stays where it was, under the panel. TasksPanelClear.Clear says where such a window goes: moved
// sideways off the strip with its size kept when there is room, and only narrowed where there is not.
public class TasksPanelClearTests
{
    // A 3840 x 2160 screen with a 690 px panel on its right edge, just inside a 350 px bar.
    static readonly WindowRect Screen = new(0, 0, 3840, 2160);
    static readonly WindowRect RightPanel = new(2800, 0, 3490, 2160);
    static readonly WindowRect RightWork = new(0, 36, 2800, 2160);

    // The same, mirrored: bar and panel on the left edge.
    static readonly WindowRect LeftPanel = new(350, 0, 1040, 2160);
    static readonly WindowRect LeftWork = new(1040, 36, 3840, 2160);

    const int Tolerance = 8;

    static WindowRect? Right(WindowRect window, WindowRect? work = null) =>
        TasksPanelClear.Clear(window, PanelEdge.Right, Screen, RightPanel, work ?? RightWork, Tolerance);

    static WindowRect? Left(WindowRect window) =>
        TasksPanelClear.Clear(window, PanelEdge.Left, Screen, LeftPanel, LeftWork, Tolerance);

    [Fact]
    public void A_window_clear_of_the_panel_stays()
    {
        Assert.Null(Right(new WindowRect(100, 100, 2000, 1000)));
        Assert.Null(Left(new WindowRect(1500, 100, 3000, 1000)));
    }

    // Windows' invisible resize border makes a window placed flush against the panel reach a few pixels under it. That
    // is not the window being covered, and it is not moved.
    [Fact]
    public void A_window_reaching_under_only_by_its_invisible_border_stays()
    {
        Assert.Null(Right(new WindowRect(1000, 100, RightPanel.Left + 7, 1000)));
        Assert.Null(Left(new WindowRect(LeftPanel.Right - 7, 100, 3000, 1000)));
    }

    // A window with room beside it moves sideways by exactly what was under the panel, size kept: shrinking a window is a
    // bigger change than moving it.
    [Fact]
    public void A_window_with_room_moves_off_the_panel_with_its_size_kept()
    {
        Assert.Equal(new WindowRect(1700, 100, 2800, 1000), Right(new WindowRect(2000, 100, 3100, 1000)));
        Assert.Equal(new WindowRect(1040, 100, 2040, 1000), Left(new WindowRect(800, 100, 1800, 1000)));
    }

    // The screenshot's case: a terminal as wide as the screen. There is no room to move it, so it keeps its far edge and
    // ends where the panel starts.
    [Fact]
    public void A_window_too_wide_to_move_is_narrowed_to_the_panel()
    {
        Assert.Equal(new WindowRect(0, 40, 2800, 1600), Right(new WindowRect(0, 40, 3600, 1600)));
        Assert.Equal(new WindowRect(1040, 40, 3840, 1600), Left(new WindowRect(200, 40, 3840, 1600)));
    }

    // Moved as far as the work area allows, then narrowed for the rest: never pushed past the work area's far edge (a
    // taskbar or another app bar on the opposite side).
    [Fact]
    public void A_window_moves_up_to_the_far_edge_of_the_work_area_and_is_narrowed_for_the_rest()
    {
        var work = RightWork with { Left = 100 };
        Assert.Equal(new WindowRect(100, 40, 2800, 1600), Right(new WindowRect(500, 40, 3400, 1600), work));
    }

    // A window already reaching past the far edge (its own invisible border, or placed there by hand) is not pulled back
    // in: its far edge stays where it is.
    [Fact]
    public void A_window_already_past_the_far_edge_keeps_that_edge()
    {
        Assert.Equal(new WindowRect(-7, 40, 2800, 1600), Right(new WindowRect(-7, 40, 3000, 1600)));
    }

    // A window entirely under the panel comes out beside it, whole.
    [Fact]
    public void A_window_entirely_under_the_panel_comes_out_beside_it()
    {
        Assert.Equal(new WindowRect(2300, 100, 2800, 600), Right(new WindowRect(2900, 100, 3400, 600)));
    }

    // A window centred on another screen is that screen's business, whatever part of it pokes over here.
    [Fact]
    public void A_window_centred_on_another_screen_stays()
    {
        Assert.Null(Right(new WindowRect(3300, 100, 5000, 1000)));
    }

    // A work area that cannot be read right now (empty) falls back to the screen for the far edge.
    [Fact]
    public void An_unreadable_work_area_falls_back_to_the_screen()
    {
        Assert.Equal(new WindowRect(0, 40, 2800, 1600), Right(new WindowRect(300, 40, 3500, 1600), new WindowRect(0, 0, 0, 0)));
    }

    // A window above or below the panel's strip (a panel shorter than the screen) is not under it.
    [Fact]
    public void A_window_beside_the_strip_vertically_stays()
    {
        var shortPanel = RightPanel with { Top = 1000 };
        Assert.Null(TasksPanelClear.Clear(new WindowRect(2000, 100, 3100, 900), PanelEdge.Right, Screen, shortPanel, RightWork, Tolerance));
    }
}
