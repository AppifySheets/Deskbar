using Deskbar.Core.Domain;
using Deskbar.Core.Tiling;

namespace Deskbar.Core.Tests.Tiling;

// Where a window goes when a drag or resize settles. Screen 1 holds A on the left and B on the right.
public class SettledMoveTests
{
    static readonly WindowHandle A = new(1);
    static readonly WindowHandle B = new(2);

    static readonly TileBoard Board = TileBoard.Empty.Join(1, A).Join(1, B);

    static readonly IReadOnlyDictionary<WindowHandle, WindowRect> Slots = new Dictionary<WindowHandle, WindowRect>
    {
        [A] = new(0, 0, 500, 600),
        [B] = new(500, 0, 1000, 600),
    };

    static MoveOutcome Decide(WindowHandle moved, WindowRect frame, int screenNow) =>
        SettledMove.Decide(moved, frame, screenNow, Board, Slots);

    [Fact]
    public void Centre_over_another_tile_swaps() =>
        Assert.Equal(new MoveOutcome.Swap(B), Decide(A, new(450, 100, 950, 500), 1));

    [Fact]
    public void Centre_over_its_own_slot_goes_back() =>
        Assert.IsType<MoveOutcome.BackToSlot>(Decide(A, new(10, 10, 400, 500), 1));

    // A resize that left the centre (x=-250) outside every tile.
    [Fact]
    public void Centre_in_no_tile_goes_back() =>
        Assert.IsType<MoveOutcome.BackToSlot>(Decide(A, new(-400, 0, -100, 600), 1));

    [Fact]
    public void Another_screen_joins_it() =>
        Assert.Equal(new MoveOutcome.JoinScreen(2), Decide(A, new(2000, 0, 2400, 600), 2));
}
