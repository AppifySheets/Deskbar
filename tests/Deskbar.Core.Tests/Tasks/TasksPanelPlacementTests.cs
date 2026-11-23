using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;
using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests.Tasks;

// Which screen edge the tasks panel docks on (TasksPanelPlacement), one test per rule of the spec's "Where it docks".
public class TasksPanelPlacementTests
{
    static readonly WindowRect Left = new(0, 0, 1920, 1080);
    static readonly WindowRect Right = new(1920, 0, 3840, 1080);

    [Fact]
    public void A_bar_docked_left_puts_the_panel_on_the_left_of_the_bars_screen() =>
        Assert.Equal(new PanelPlacement(PanelEdge.Left, Left),
            TasksPanelPlacement.For("left", Left, Maybe<WindowRect>.None, 0));

    [Fact]
    public void A_bar_docked_right_puts_the_panel_on_the_right_of_the_bars_screen() =>
        Assert.Equal(new PanelPlacement(PanelEdge.Right, Left),
            TasksPanelPlacement.For("right", Left, Maybe<WindowRect>.None, 0));

    [Fact]
    public void A_bar_across_the_seam_puts_the_panel_on_the_left_of_the_right_hand_screen() =>
        Assert.Equal(new PanelPlacement(PanelEdge.Left, Right),
            TasksPanelPlacement.For("seam", Left, Maybe.From(Right), 1920));

    [Fact]
    public void A_seam_bar_without_a_right_hand_screen_falls_back_to_the_bars_screen() =>
        Assert.Equal(new PanelPlacement(PanelEdge.Left, Left),
            TasksPanelPlacement.For("seam", Left, Maybe<WindowRect>.None, 1920));

    [Fact]
    public void A_floating_bar_nearer_the_left_edge_docks_the_panel_left() =>
        Assert.Equal(new PanelPlacement(PanelEdge.Left, Left),
            TasksPanelPlacement.For(null, Left, Maybe<WindowRect>.None, 400));

    [Fact]
    public void A_floating_bar_nearer_the_right_edge_docks_the_panel_right() =>
        Assert.Equal(new PanelPlacement(PanelEdge.Right, Left),
            TasksPanelPlacement.For(null, Left, Maybe<WindowRect>.None, 1500));

    [Fact]
    public void A_floating_bar_exactly_in_the_middle_docks_the_panel_right() =>
        Assert.Equal(new PanelPlacement(PanelEdge.Right, Left),
            TasksPanelPlacement.For(null, Left, Maybe<WindowRect>.None, 960));

    // An edge string nobody wrote (a hand-edited state.json) must not throw: it is treated as floating.
    [Fact]
    public void An_unrecognised_edge_is_treated_like_a_floating_bar() =>
        Assert.Equal(new PanelPlacement(PanelEdge.Left, Left),
            TasksPanelPlacement.For("top", Left, Maybe<WindowRect>.None, 400));
}
