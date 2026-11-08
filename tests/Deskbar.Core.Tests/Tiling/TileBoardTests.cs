using Deskbar.Core.Domain;
using Deskbar.Core.Tiling;

namespace Deskbar.Core.Tests.Tiling;

// The slot board: which windows hold which slot, per screen. Slot order is list order, and it must
// match GridLayout.Arrange's order (column by column, top to bottom inside a column).
public class TileBoardTests
{
    static readonly WindowHandle A = new(1);
    static readonly WindowHandle B = new(2);
    static readonly WindowHandle C = new(3);

    [Fact]
    public void Join_appends_to_the_end()
    {
        var board = TileBoard.Empty.Join(1, A).Join(1, B);
        Assert.Equal([A, B], board.On(1));
        Assert.Equal(1, board.ScreenOf(B).Value);
    }

    [Fact]
    public void Join_on_another_screen_leaves_the_first()
    {
        var board = TileBoard.Empty.Join(1, A).Join(1, B).Join(2, A);
        Assert.Equal([B], board.On(1));
        Assert.Equal([A], board.On(2));
        Assert.Equal(2, board.ScreenOf(A).Value);
    }

    // Regression: Join used to read the screen's list before the leave took effect, so a window joining
    // the screen it was already on was listed twice and the Tiler's slot dictionary threw.
    [Fact]
    public void Joining_the_screen_it_is_already_on_moves_it_to_the_end_once()
    {
        var board = TileBoard.Empty.Join(1, A).Join(1, B).Join(1, A);
        Assert.Equal([B, A], board.On(1));
    }

    [Fact]
    public void Joining_the_screen_it_is_already_on_never_duplicates()
    {
        var board = TileBoard.Empty.Join(1, A).Join(1, B).Join(1, A);
        Assert.Equal(1, board.Screens.Values.SelectMany(w => w).Count(w => w == A));
    }

    [Fact]
    public void Leave_closes_the_gap()
    {
        var board = TileBoard.Empty.Join(1, A).Join(1, B).Join(1, C).Leave(B);
        Assert.Equal([A, C], board.On(1));
        Assert.True(board.ScreenOf(B).HasNoValue);
    }

    [Fact]
    public void Swap_exchanges_two_slots()
    {
        var board = TileBoard.Empty.Join(1, A).Join(1, B).Join(1, C).Swap(A, C);
        Assert.Equal([C, B, A], board.On(1));
    }

    [Fact]
    public void Swap_across_screens_changes_nothing()
    {
        var board = TileBoard.Empty.Join(1, A).Join(2, B);
        var swapped = board.Swap(A, B);
        Assert.Equal([A], swapped.On(1));
        Assert.Equal([B], swapped.On(2));
    }

    [Fact]
    public void First_fill_orders_left_to_right_then_top_to_bottom()
    {
        var board = TileBoard.FirstFill(
        [
            (C, 1, new WindowRect(500, 300, 1000, 600)),
            (A, 1, new WindowRect(0, 0, 500, 600)),
            (B, 1, new WindowRect(500, 0, 1000, 300)),
        ]);
        Assert.Equal([A, B, C], board.On(1));
    }
}
