using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;
using Deskbar.Core.Tiling;
using static Deskbar.Core.Tests.Tiling.Trees;

namespace Deskbar.Core.Tests.Tiling;

// LOCKED TILES (docs/superpowers/specs/2026-11-22-tile-lock-and-other-screens-design.md). Petre: "i want to pin the
// window so that it's not moving". A locked tile is never the one a new window splits: not as the tile last used,
// not as the largest tile. The lock covers being split and nothing else (spec, non-goals). These tests drive the
// whole Tiler through the shared harness (TilerTests.cs) and the arrival helpers (TilerTests.Arrival.cs).
public sealed partial class TilerTests
{
    // W1 | W2, W1 used and locked: W3 does not halve W1, it splits W2 (the largest unlocked tile). W2 is taller
    // than wide, so W3 goes below it, and W1 keeps its whole column.
    [Fact]
    public void A_locked_tile_last_used_is_not_split()
    {
        new[] { 1, 2 }.ToList().ForEach(n => Exists(W(n), alpha));
        Start();
        Uses(W(1));
        Assert.True(tiler.SetTileLocked(W(1), true).IsSuccess);

        Opens(W(3), alpha);

        AssertArranged([W(1), W(2), W(3)], Columns([0.5, 0.5], [1.0], [0.5, 0.5]));
    }

    // Screen 1 has only a locked tile, screen 2 an unlocked one: the window goes to screen 2 and splits W2 there,
    // rather than halving the locked W1 or ignoring the lock.
    [Fact]
    public void When_every_tile_on_the_screen_is_locked_the_window_splits_an_unlocked_tile_on_another_screen()
    {
        surface.ScreenList.Add(FakeTileSurface.Screen(2, Area2));
        Exists(W(1), alpha, screen: 1);
        Exists(W(2), alpha, screen: 2);
        Start();
        Uses(W(1));
        Assert.True(tiler.SetTileLocked(W(1), true).IsSuccess);

        Opens(W(3), alpha, screen: 1);

        AssertArranged([W(1)], TileLayout.Automatic(1));
        AssertArranged([W(2), W(3)], Columns([0.5, 0.5], [1.0], [1.0]), Area2, screen: 2);
    }

    // Final review, M2. Screen 1 holds a locked W1 and W2, a maximized window the Tiler is un-maximizing itself (tiling
    // was just turned on and the restore has not landed), which keeps its slot but can never be split. Every tile there
    // is locked or restoring, so the new window goes to screen 2's unlocked W3 rather than halving the locked W1.
    [Fact]
    public void A_screen_of_locked_and_restoring_tiles_sends_the_window_to_an_unlocked_tile_elsewhere()
    {
        surface.ScreenList.Add(FakeTileSurface.Screen(2, Area2));
        surface.RestoreLandsLater = true;
        Exists(W(1), alpha, screen: 1);
        Exists(W(2), alpha, screen: 1);
        Exists(W(3), alpha, screen: 2);
        surface.Maximized.Add(W(2));
        Start(alphaTiled: false);
        Assert.True(manager.SetWorkspaceTiled(alpha.Id, true).IsSuccess);
        Assert.Contains(W(2), surface.Restored);
        Assert.Equal(2, tiler.Board.On(1).Count);
        Uses(W(1));
        Assert.True(tiler.SetTileLocked(W(1), true).IsSuccess);

        Opens(W(4), alpha, screen: 1);

        Assert.Equal([W(3), W(4)], tiler.Board.On(2));
        Assert.DoesNotContain(W(4), tiler.Board.On(1));
    }

    // Nothing anywhere is unlocked: a window still has to go somewhere, so the lock is ignored for this one window.
    [Fact]
    public void When_every_tile_everywhere_is_locked_the_lock_is_ignored_for_that_window()
    {
        Exists(W(1), alpha);
        Start();
        Uses(W(1));
        Assert.True(tiler.SetTileLocked(W(1), true).IsSuccess);

        Opens(W(2), alpha);

        AssertArranged([W(1), W(2)], Columns([0.5, 0.5], [1.0], [1.0]));
    }

    // Review focus 4. A window dropped on a screen stays there ("let's respect which monitor i put it to"), even when
    // every tile on that screen is locked and another screen has an unlocked one: it splits the locked W1.
    [Fact]
    public void A_window_dropped_on_a_screen_of_locked_tiles_stays_there()
    {
        surface.ScreenList.Add(FakeTileSurface.Screen(2, Area2));
        Exists(W(1), alpha, screen: 1);
        Exists(W(2), alpha, screen: 2);
        Exists(W(3), beta, screen: 2);
        Start();
        Uses(W(1));
        Assert.True(tiler.SetTileLocked(W(1), true).IsSuccess);

        manager.AssignWindow(W(3), alpha.Id, monitor: 1);

        AssertArranged([W(1), W(3)], Columns([0.5, 0.5], [1.0], [1.0]));
        AssertArranged([W(2)], TileLayout.Automatic(1), Area2, screen: 2);
    }

    // Only a tile can be locked. The menu offers the item only on tiles, and the pipe gets the reason.
    [Fact]
    public void Locking_a_window_that_is_not_a_tile_is_refused()
    {
        Exists(W(1), alpha);
        Start();

        var result = tiler.SetTileLocked(W(9), true);

        Assert.True(result.IsFailure);
        Assert.Equal("That window is not in a tile.", result.Error);
        Assert.True(tiler.TileLockOf(W(9)).HasNoValue);
    }

    // Review focus 3. Windows reuses handles, so a closed window's lock must go with it: a new window that happens
    // to get the same handle starts unlocked.
    [Fact]
    public void A_closed_window_is_forgotten_so_a_reused_handle_comes_back_unlocked()
    {
        Exists(W(1), alpha);
        Start();
        Assert.True(tiler.SetTileLocked(W(1), true).IsSuccess);
        Assert.Equal(Maybe.From(true), tiler.TileLockOf(W(1)));

        Closes(W(1));
        monitor.Dead.Remove(W(1));
        Opens(W(1), alpha);

        Assert.Equal(Maybe.From(false), tiler.TileLockOf(W(1)));
    }
}
