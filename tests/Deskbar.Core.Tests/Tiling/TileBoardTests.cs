using CSharpFunctionalExtensions;
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

    // --- first fill by position against a stored shape (spec, "Arrival and minimize", the first fill bullet) ------

    // Down[Across[A, C], B] on 1000 x 600 with no gap: A top left, C top right, B the full-width bottom row. Its slot
    // order is A, C, B. Sorting by Left then Top would give A, B, C and swap B and C on every restart.
    static readonly IReadOnlyList<WindowRect> HandMade =
        [new(0, 0, 500, 300), new(500, 0, 1000, 300), new(0, 300, 1000, 600)];

    static Func<int, int, Maybe<IReadOnlyList<WindowRect>>> CellsOn(IReadOnlyList<WindowRect> cells) =>
        (_, count) => count == cells.Count ? Maybe.From(cells) : Maybe<IReadOnlyList<WindowRect>>.None;

    [Fact]
    public void FirstFill_keeps_a_hand_made_shape()
    {
        var board = TileBoard.FirstFill(
        [
            (B, 1, new WindowRect(0, 300, 1000, 600)),
            (C, 1, new WindowRect(500, 0, 1000, 300)),
            (A, 1, new WindowRect(0, 0, 500, 300)),
        ], CellsOn(HandMade));

        Assert.Equal([A, C, B], board.On(1));
    }

    // No stored cells (or a count that does not match them): the sort by Left then Top, as before.
    [Fact]
    public void FirstFill_without_cells_sorts_as_before()
    {
        var windows = new[]
        {
            (C, 1, new WindowRect(500, 300, 1000, 600)),
            (A, 1, new WindowRect(0, 0, 500, 600)),
            (B, 1, new WindowRect(500, 0, 1000, 300)),
        };

        Assert.Equal([A, B, C], TileBoard.FirstFill(windows, (_, _) => Maybe<IReadOnlyList<WindowRect>>.None).On(1));
        Assert.Equal([A, B, C], TileBoard.FirstFill(windows, CellsOn([new(0, 0, 1000, 600)])).On(1));
    }

    // The largest overlap is taken first, so a window lying across two cells goes where most of it is, and the one it
    // crowded out takes what is left. C covers most of A's cell and a little of B's; A covers a little of A's cell.
    [Fact]
    public void FirstFill_takes_the_largest_overlap_first()
    {
        var board = TileBoard.FirstFill(
        [
            (A, 1, new WindowRect(0, 0, 100, 100)),
            (C, 1, new WindowRect(0, 0, 500, 320)),
            (B, 1, new WindowRect(500, 0, 1000, 300)),
        ], CellsOn(HandMade));

        // C takes A's cell (150000 px), B takes its own (150000, after C by Left then Top), A the bottom row (0 left).
        Assert.Equal([C, B, A], board.On(1));
    }

    // A window over no cell at all (off screen, or minimized to a stray place) takes a cell left over, in Left then
    // Top order among such windows, the cells in slot order.
    [Fact]
    public void FirstFill_gives_windows_over_no_cell_the_cells_left_over()
    {
        var board = TileBoard.FirstFill(
        [
            (A, 1, new WindowRect(-900, -900, -800, -800)),
            (B, 1, new WindowRect(500, 0, 1000, 300)),
            (C, 1, new WindowRect(-700, -900, -600, -800)),
        ], CellsOn(HandMade));

        Assert.Equal([A, B, C], board.On(1));
    }
}
