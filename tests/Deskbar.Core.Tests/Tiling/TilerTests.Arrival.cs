using Deskbar.Core.Domain;
using Deskbar.Core.Tiling;

namespace Deskbar.Core.Tests.Tiling;

// A NEW WINDOW SPLITS THE TILE YOU LAST USED (tiling-predictable-placement design, change 2). A window that arrives
// on a tiled workspace no longer joins the end of its screen and rebuilds the grid from the count: it takes half of
// the tile the user last activated, on that tile's screen (TileArrival.Choose picks the tile and the side). These
// tests drive the whole Tiler through the shared TilerTests harness (TilerTests.cs).
public sealed partial class TilerTests
{
    // The key the first fake screen's sizes are stored under (its bounds, "L,T,R,B").
    const string AreaKey = "0,0,1920,1040";

    // The user activates a window: input happened first, so the tick moves on.
    void Uses(WindowHandle h)
    {
        surface.InputTick++;
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Activated, Info(h)));
    }

    // Windows activates a window by itself (after a close or minimize): no input since.
    void HandedFocus(WindowHandle h) =>
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Activated, Info(h)));

    // The board order of a screen and every window's frame, against the grid laid out with these sizes.
    void AssertArranged(IReadOnlyList<WindowHandle> order, TileSizes sizes, WindowRect? area = null, int screen = 1)
    {
        Assert.Equal(order, tiler.Board.On(screen));
        Assert.Equal(GridLayout.Arrange(area ?? Area, order.Count, Gap, sizes), order.Select(w => surface.Frames[w]).ToList());
    }

    // Two columns, W1 last used: W1 is taller than wide, so W3 goes below it and W2 keeps its whole column.
    [Fact]
    public void A_new_window_splits_the_tile_last_used()
    {
        new[] { 1, 2 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        Uses(W(1));

        Opens(W(3), alpha);

        AssertArranged([W(1), W(3), W(2)], new TileSizes([0.5, 0.5], [[0.5, 0.5], [1.0]]));
    }

    // Review focus 1: the window opened on screen 2, but the tile last used is on screen 1, so it goes there.
    [Fact]
    public void A_new_window_lands_on_the_screen_of_the_tile_last_used()
    {
        surface.ScreenList.Add(FakeTileSurface.Screen(2, Area2));
        Exists(W(1), alpha, screen: 1);
        Exists(W(2), alpha, screen: 2);
        Start();
        Uses(W(1));

        Opens(W(3), alpha, screen: 2);

        // W1 had the whole of screen 1, wider than tall and alone in its column: a new column to its right.
        AssertArranged([W(1), W(3)], new TileSizes([0.5, 0.5], [[1.0], [1.0]]));
        AssertArranged([W(2)], TileSizes.Equal([1]), Area2, screen: 2);
    }

    // The newcomer is the last-used tile as soon as the user activates it, so the next window splits it.
    [Fact]
    public void The_newcomer_is_the_next_target_once_it_has_joined()
    {
        Exists(W(1), alpha);
        Start();
        Uses(W(1));

        Opens(W(2), alpha);
        AssertArranged([W(1), W(2)], new TileSizes([0.5, 0.5], [[1.0], [1.0]]));

        Uses(W(2));
        Opens(W(3), alpha);
        AssertArranged([W(1), W(2), W(3)], new TileSizes([0.5, 0.5], [[1.0], [0.5, 0.5]]));
    }

    // Review focus 2: closing W3 makes Windows activate another window (here W1) with no input in between. That
    // activation is not the user's, so the target stays W2, the last tile the user chose. Had it counted, W4 would
    // be below W1: [W1, W4, W2].
    [Fact]
    public void Focus_Windows_hands_out_after_a_close_does_not_count_when_it_comes_after()
    {
        new[] { 1, 2, 3 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        Assert.Equal([W(1), W(2), W(3)], tiler.Board.On(1));
        Uses(W(2));
        Uses(W(3));

        surface.InputTick++; // the click on W3's close button
        Closes(W(3));
        HandedFocus(W(1));
        Opens(W(4), alpha);

        AssertArranged([W(1), W(2), W(4)], new TileSizes([0.5, 0.5], [[1.0], [0.5, 0.5]]));
    }

    // The same when the hand-out activation arrives before the window's Disappeared.
    [Fact]
    public void Focus_Windows_hands_out_after_a_close_does_not_count_when_it_comes_before()
    {
        new[] { 1, 2, 3 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        Uses(W(2));
        Uses(W(3));

        surface.InputTick++; // the click on W3's close button
        HandedFocus(W(1));
        Closes(W(3));
        Opens(W(4), alpha);

        AssertArranged([W(1), W(2), W(4)], new TileSizes([0.5, 0.5], [[1.0], [0.5, 0.5]]));
    }

    // A window that is not a tile (here one that cannot tile) leaves the target as it was.
    [Fact]
    public void Activating_a_floating_window_keeps_the_target()
    {
        new[] { 1, 2, 9 }.ToList().ForEach(n => Exists(W(n), alpha));
        surface.CannotTile.Add(W(9));
        Start();
        Assert.DoesNotContain(W(9), tiler.Board.On(1));
        Uses(W(1));
        Uses(W(9));

        Opens(W(3), alpha);

        AssertArranged([W(1), W(3), W(2)], new TileSizes([0.5, 0.5], [[0.5, 0.5], [1.0]]));
    }

    // Review focus 3: a window that opens maximized is restored, and lands in the half it split off.
    [Fact]
    public void A_window_opening_maximized_is_restored_into_its_split()
    {
        Exists(W(1), alpha);
        Start();
        Uses(W(1));

        surface.Maximized.Add(W(2));
        Opens(W(2), alpha);

        Assert.Contains(W(2), surface.Restored);
        Assert.Equal([W(1), W(2)], tiler.Board.On(1));
        // Windows restores it to wherever it was before it was maximized; the Tiler puts it into its slot.
        Settles(W(2), new WindowRect(400, 300, 900, 700));
        AssertArranged([W(1), W(2)], new TileSizes([0.5, 0.5], [[1.0], [1.0]]));
        Assert.Equal(GridLayout.Arrange(Area, 2, Gap)[1], surface.Frames[W(2)]);
    }

    // An opening comes before any split: W3 fills the room kept bottom right, and W1, the tile last used, keeps its
    // whole column.
    [Fact]
    public void A_new_window_goes_into_an_opening_before_any_split()
    {
        new[] { 1, 2 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        var opened = new TileSizes([0.5, 0.5], [[1.0], [0.5, 0.5]]) { Opening = 2 };
        Assert.True(manager.SetTileSizes(alpha.Id, AreaKey, opened).IsSuccess);
        var w1Before = surface.Frames[W(1)];
        Uses(W(1));

        Opens(W(3), alpha);

        var (order, filled) = TileArrange.Filled([W(1), W(2)], opened, W(3));
        AssertArranged(order, filled);
        Assert.Equal([W(1), W(2), W(3)], order);
        Assert.Equal(w1Before, surface.Frames[W(1)]);
    }

    // Two windows opened on Alpha while the user was on Beta: the first splits the tile last used (W1, sideways,
    // since it has the whole screen), the second the largest tile. The two halves tie, and the later in slot order
    // (W2) wins; it is taller than wide, so W3 goes below it.
    [Fact]
    public void Several_arrivals_at_once_split_the_target_then_the_largest()
    {
        Exists(W(1), alpha);
        Start();
        Uses(W(1));

        SwitchTo(beta);
        Opens(W(2), alpha);
        Opens(W(3), alpha);
        // Another desktop's windows are never moved: nothing is placed until the user comes back.
        Assert.DoesNotContain(W(2), PlacedWindows);
        Assert.DoesNotContain(W(3), PlacedWindows);
        SwitchTo(alpha);

        AssertArranged([W(1), W(2), W(3)], new TileSizes([0.5, 0.5], [[1.0], [0.5, 0.5]]));
    }

    // A window the bar (or the pipe's `move`) brings over from another workspace splits the tile last used too.
    [Fact]
    public void A_window_brought_from_another_workspace_splits_the_tile_last_used()
    {
        new[] { 1, 2 }.ToList().ForEach(n => Exists(W(n), alpha));
        Exists(W(3), beta);
        Start();
        Uses(W(1));

        Assert.True(manager.MoveToDesktop(W(3), alpha.DesktopId!.Value).IsSuccess);

        AssertArranged([W(1), W(3), W(2)], new TileSizes([0.5, 0.5], [[0.5, 0.5], [1.0]]));
    }
}
