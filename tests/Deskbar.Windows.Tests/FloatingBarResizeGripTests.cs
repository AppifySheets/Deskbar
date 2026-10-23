using Deskbar.App;

namespace Deskbar.Windows.Tests;

// The bar's width is dragged from a 4 DIP strip at either edge. Docked, the edge on the screen's side is
// pinned by the reservation, so a grip there offers a resize that cannot happen. Petre, docked left: "when i
// move the mouse to the left edge, it changes to resize; shouldn't when docked". The other edge keeps its
// grip, because a docked bar's width may still be dragged and the reservation follows.
public class FloatingBarResizeGripTests
{
    const double Width = 160;
    const double Height = 400;

    [Fact]
    public void Floating_both_edges_offer_a_resize()
    {
        Assert.Equal(FloatingBar.ResizeSide.Left, FloatingBar.GripAt(2, 100, Width, Height, dockedEdge: null));
        Assert.Equal(FloatingBar.ResizeSide.Right, FloatingBar.GripAt(Width - 2, 100, Width, Height, dockedEdge: null));
        Assert.Null(FloatingBar.GripAt(Width / 2, 100, Width, Height, dockedEdge: null));
    }

    [Fact]
    public void Docked_left_only_the_right_edge_offers_a_resize()
    {
        Assert.Null(FloatingBar.GripAt(2, 100, Width, Height, dockedEdge: "left"));
        Assert.Equal(FloatingBar.ResizeSide.Right, FloatingBar.GripAt(Width - 2, 100, Width, Height, dockedEdge: "left"));
    }

    [Fact]
    public void Docked_right_only_the_left_edge_offers_a_resize()
    {
        Assert.Equal(FloatingBar.ResizeSide.Left, FloatingBar.GripAt(2, 100, Width, Height, dockedEdge: "right"));
        Assert.Null(FloatingBar.GripAt(Width - 2, 100, Width, Height, dockedEdge: "right"));
    }

    // Across the seam both edges sit inside a screen, so both grips stay.
    [Fact]
    public void Docked_across_the_seam_both_edges_offer_a_resize()
    {
        Assert.Equal(FloatingBar.ResizeSide.Left, FloatingBar.GripAt(2, 100, Width, Height, dockedEdge: "seam"));
        Assert.Equal(FloatingBar.ResizeSide.Right, FloatingBar.GripAt(Width - 2, 100, Width, Height, dockedEdge: "seam"));
    }

    [Fact]
    public void Outside_the_bars_height_there_is_no_grip()
    {
        Assert.Null(FloatingBar.GripAt(2, -1, Width, Height, dockedEdge: null));
        Assert.Null(FloatingBar.GripAt(2, Height + 1, Width, Height, dockedEdge: null));
    }
}
