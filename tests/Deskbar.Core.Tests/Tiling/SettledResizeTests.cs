using Deskbar.Core.Domain;
using Deskbar.Core.Tiling;

namespace Deskbar.Core.Tests.Tiling;

// SettledResize: telling a resize by hand (an edge dragged, a button down) from a move.
public class SettledResizeTests
{
    static readonly WindowRect Accepted = new(0, 0, 500, 600);

    [Fact] public void A_right_edge_drag_by_hand_is_a_resize() => Assert.Equal(Edges.Right, SettledResize.Classify(Accepted, new(0, 0, 700, 600), byHand: true));
    [Fact] public void The_same_change_without_a_button_is_not() => Assert.Equal(Edges.None, SettledResize.Classify(Accepted, new(0, 0, 700, 600), byHand: false));
    [Fact] public void A_shift_is_a_move() => Assert.Equal(Edges.None, SettledResize.Classify(Accepted, new(100, 0, 600, 600), byHand: true));
    [Fact] public void A_corner_drag_moves_both_splits() => Assert.Equal(Edges.Right | Edges.Bottom, SettledResize.Classify(Accepted, new(0, 0, 700, 400), byHand: true));
    [Fact] public void Two_pixels_of_drift_on_the_opposite_edge_still_count() => Assert.Equal(Edges.Right, SettledResize.Classify(Accepted, new(2, 1, 700, 599), byHand: true));
    [Fact] public void Both_sides_of_an_axis_moving_is_a_move() => Assert.Equal(Edges.None, SettledResize.Classify(Accepted, new(-50, 0, 700, 600), byHand: true));
    [Fact] public void An_unchanged_frame_is_not_a_resize() => Assert.Equal(Edges.None, SettledResize.Classify(Accepted, Accepted, byHand: true));
}
