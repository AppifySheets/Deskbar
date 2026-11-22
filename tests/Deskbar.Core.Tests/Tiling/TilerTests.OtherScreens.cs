using Deskbar.Core.Domain;
using Deskbar.Core.Tiling;
using static Deskbar.Core.Tests.Tiling.Trees;

namespace Deskbar.Core.Tests.Tiling;

// A NEW WINDOW LOOKS AT THE OTHER SCREENS BEFORE IT HALVES A TILE (spec 2026-11-22-tile-lock-and-other-screens,
// behaviour steps 3 and 4). Petre: "when opening windows, check if there's space on another screen too", and from the
// two meanings of space he picked A: an opening kept on any screen, or a screen with nothing of this workspace on it.
// Not balancing by tile size. A window dropped on a screen, or restored from minimized, still never leaves its screen.
public sealed partial class TilerTests
{
    static readonly WindowRect Area3 = new(3840, 0, 5760, 1040);

    [Fact]
    public void A_new_window_takes_an_empty_screen_before_splitting_a_tile()
    {
        surface.ScreenList.Add(FakeTileSurface.Screen(2, Area2));
        Exists(W(1), alpha, screen: 1);
        Start();
        Uses(W(1));

        Opens(W(2), alpha, screen: 1);

        AssertArranged([W(1)], TileLayout.Automatic(1));
        AssertArranged([W(2)], TileLayout.Automatic(1), Area2, screen: 2);
    }

    // Screens 2 and 3 are both empty and the window opened on 3: its own screen comes first, so it stays on 3.
    [Fact]
    public void Its_own_empty_screen_comes_before_another_empty_screen()
    {
        surface.ScreenList.Add(FakeTileSurface.Screen(2, Area2));
        surface.ScreenList.Add(FakeTileSurface.Screen(3, Area3));
        Exists(W(1), alpha, screen: 1);
        Start();
        Uses(W(1));

        Opens(W(2), alpha, screen: 3);

        Assert.Equal([W(2)], tiler.Board.On(3));
        Assert.Empty(tiler.Board.On(2));
        Assert.Equal([W(1)], tiler.Board.On(1));
    }

    [Fact]
    public void Of_several_other_empty_screens_the_lowest_numbered_is_taken()
    {
        surface.ScreenList.Add(FakeTileSurface.Screen(2, Area2));
        surface.ScreenList.Add(FakeTileSurface.Screen(3, Area3));
        Exists(W(1), alpha, screen: 1);
        Start();
        Uses(W(1));

        Opens(W(2), alpha, screen: 1);

        Assert.Equal([W(2)], tiler.Board.On(2));
        Assert.Empty(tiler.Board.On(3));
    }

    // Review focus 2. W2 is maximized on screen 2, so it is set aside and off the board, and the board says screen 2
    // is empty. It is not: a tile there would sit under or over W2. W3 splits W1 instead.
    [Fact]
    public void A_screen_showing_a_maximized_window_of_the_workspace_is_not_empty()
    {
        surface.ScreenList.Add(FakeTileSurface.Screen(2, Area2));
        Exists(W(1), alpha, screen: 1);
        Exists(W(2), alpha, screen: 2);
        surface.Maximized.Add(W(2));
        Start();
        Uses(W(1));

        Opens(W(3), alpha, screen: 1);

        AssertArranged([W(1), W(3)], Columns([0.5, 0.5], [1.0], [1.0]));
        Assert.Empty(tiler.Board.On(2));
    }

    // Screen 2: W2 | W3 with keep-space on. W3 closes and its half stays open. W4 opens on screen 1, where W1 was used
    // last: it fills screen 2's opening rather than halving W1.
    [Fact]
    public void An_opening_on_another_screen_is_filled_before_a_tile_is_split()
    {
        surface.ScreenList.Add(FakeTileSurface.Screen(2, Area2));
        Exists(W(1), alpha, screen: 1);
        new[] { 2, 3 }.ToList().ForEach(n => Exists(W(n), alpha, screen: 2));
        Start();
        Assert.True(manager.SetTileKeepsSpace(alpha.Id, true).IsSuccess);
        Closes(W(3));
        Uses(W(1));

        Opens(W(4), alpha, screen: 1);

        AssertArranged([W(1)], TileLayout.Automatic(1));
        Assert.Equal([W(2), W(4)], tiler.Board.On(2));
    }

    // "let's respect which monitor i put it to": a window dropped on screen 1 stays there with screen 2 empty.
    [Fact]
    public void A_window_dropped_on_a_screen_does_not_go_to_an_empty_one()
    {
        surface.ScreenList.Add(FakeTileSurface.Screen(2, Area2));
        Exists(W(1), alpha, screen: 1);
        Exists(W(3), beta, screen: 1);
        Start();
        Uses(W(1));

        manager.AssignWindow(W(3), alpha.Id, monitor: 1);

        AssertArranged([W(1), W(3)], Columns([0.5, 0.5], [1.0], [1.0]));
        Assert.Empty(tiler.Board.On(2));
    }

    // A window restored from minimized with no remembered place keeps the screen Windows restored it to, as the
    // existing A_window_restored_with_no_remembered_place_stays_on_its_screen says, here with screen 2 empty.
    [Fact]
    public void A_window_restored_from_minimized_does_not_go_to_an_empty_screen()
    {
        surface.ScreenList.Add(FakeTileSurface.Screen(2, Area2));
        Exists(W(1), alpha, screen: 1);
        Exists(W(3), alpha, screen: 1);
        surface.Minimized.Add(W(3));
        Start();
        Assert.False(tiler.Board.ScreenOf(W(3)).HasValue);
        Uses(W(1));

        Minimizes(W(3), minimized: false);

        AssertArranged([W(1), W(3)], Columns([0.5, 0.5], [1.0], [1.0]));
        Assert.Empty(tiler.Board.On(2));
    }

    // Review focus 1. Both open while the user is away, so they arrive in one evaluate on the way back. W2 takes the
    // empty screen 2; W3 then finds no empty screen and splits the largest tile of its own screen, W1.
    [Fact]
    public void Two_windows_in_one_evaluate_do_not_both_take_one_empty_screen()
    {
        surface.ScreenList.Add(FakeTileSurface.Screen(2, Area2));
        Exists(W(1), alpha, screen: 1);
        Start();
        Uses(W(1));

        SwitchTo(beta);
        Opens(W(2), alpha, screen: 1);
        Opens(W(3), alpha, screen: 1);
        SwitchTo(alpha);

        AssertArranged([W(1), W(3)], Columns([0.5, 0.5], [1.0], [1.0]));
        AssertArranged([W(2)], TileLayout.Automatic(1), Area2, screen: 2);
    }
}
