using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;
using Deskbar.Core.Tiling;
using static Deskbar.Core.Tests.Tiling.Trees;

namespace Deskbar.Core.Tests.Tiling;

// A WINDOW THAT LEAVES TAKES ITS LAYOUT CHANGE WITH IT (Tiler.Rejoin.cs). Petre, 2026-11-20: "if a window is added to
// the tiled screen and then removed, closed, minimized, we should have the same layout as was active before the new
// window appeared", and "same should happen if i minimize and restore a window".
//
// The case that broke it, read off his trace: a row of two tiles [0.474, 0.526]; a window arrives and splits the
// second one to the right, which in normal form is ONE row of three, [0.474, 0.263, 0.263]; the window closes, and
// removing its leaf spread its share over both siblings, [0.644, 0.356]. A burst of short-lived windows wore the row
// down a little more each time. These tests start from the same shape: two columns 0.4 and 0.6, so the right tile
// (1152 x 1040) is wider than tall and a newcomer splits it to the right, into the same row.
public sealed partial class TilerTests
{
    static readonly TileLayout TwoUneven = Columns([0.4, 0.6], [1.0], [1.0]);
    static readonly TileLayout ThreeAfterSplit = Columns([0.4, 0.3, 0.3], [1.0], [1.0], [1.0]);

    // W1 and W2 side by side at 0.4 and 0.6, W2 last used, then W3 opens and splits W2 to the right.
    void StartUnevenPairAndSplit()
    {
        Exists(W(1), alpha);
        Exists(W(2), alpha);
        Start(layouts: [new ScreenTileLayout(AreaKey, TwoUneven)]);
        AssertArranged([W(1), W(2)], TwoUneven);
        Uses(W(2));

        Opens(W(3), alpha);
        AssertArranged([W(1), W(2), W(3)], ThreeAfterSplit);
    }

    [Fact]
    public void A_new_window_that_closes_gives_the_screen_its_layout_back()
    {
        StartUnevenPairAndSplit();

        Closes(W(3));

        AssertArranged([W(1), W(2)], TwoUneven);
        Same(TwoUneven, StoredOnAlpha().GetValueOrThrow());
    }

    [Fact]
    public void A_new_window_that_is_minimized_gives_the_screen_its_layout_back()
    {
        StartUnevenPairAndSplit();

        Minimizes(W(3), true);

        AssertArranged([W(1), W(2)], TwoUneven);
        Same(TwoUneven, StoredOnAlpha().GetValueOrThrow());
    }

    // The second message: minimize and restore, then close. Each step shows the layout of the windows on screen at
    // that moment, as it was the last time exactly those windows were there.
    [Fact]
    public void Minimize_restore_and_close_each_show_the_layout_those_windows_had()
    {
        StartUnevenPairAndSplit();

        Minimizes(W(3), true);
        AssertArranged([W(1), W(2)], TwoUneven);

        Minimizes(W(3), false);
        AssertArranged([W(1), W(2), W(3)], ThreeAfterSplit);

        Closes(W(3));
        AssertArranged([W(1), W(2)], TwoUneven);
        Same(TwoUneven, StoredOnAlpha().GetValueOrThrow());
    }

    // A screen with nothing stored before the arrival goes back to nothing stored: the automatic layout follows the
    // count again, rather than keeping a split the newcomer made.
    [Fact]
    public void A_screen_with_nothing_stored_has_nothing_stored_again_after_the_newcomer_closes()
    {
        Exists(W(1), alpha);
        Start();
        Uses(W(1));
        Opens(W(2), alpha);
        Assert.Equal([W(1), W(2)], tiler.Board.On(1));

        Closes(W(2));

        AssertArranged([W(1)], TileLayout.Automatic(1));
        Assert.True(StoredOnAlpha().HasNoValue);
    }

    // Several newcomers unwind in reverse: W4 splits W3, W4 closes (back to the three), W3 closes (back to the pair).
    [Fact]
    public void Newcomers_that_close_in_reverse_order_unwind_one_at_a_time()
    {
        StartUnevenPairAndSplit();
        Uses(W(1));
        Opens(W(4), alpha);
        Assert.Equal(4, tiler.Board.On(1).Count);

        Closes(W(4));
        AssertArranged([W(1), W(2), W(3)], ThreeAfterSplit);

        Closes(W(3));
        AssertArranged([W(1), W(2)], TwoUneven);
    }

    // The user changed the layout after the window arrived (here as "Even out tiles" would store it). Going back to
    // the layout from before the arrival would throw that change away, so the window's leaf is taken out as before.
    [Fact]
    public void A_layout_changed_after_the_arrival_is_not_thrown_away_when_the_newcomer_closes()
    {
        StartUnevenPairAndSplit();
        var evened = Columns([0.25, 0.25, 0.5], [1.0], [1.0], [1.0]);
        Assert.True(manager.SetTileLayout(alpha.Id, AreaKey, evened).IsSuccess);
        AssertArranged([W(1), W(2), W(3)], evened);

        Closes(W(3));

        var removed = Columns([0.5, 0.5], [1.0], [1.0]);
        AssertArranged([W(1), W(2)], removed);
    }

    // Another window of the screen left while the newcomer was there: the screen is not the one it joined, so its
    // leaf is taken out as before rather than restoring a layout for windows that are not all there.
    [Fact]
    public void A_screen_that_changed_since_the_arrival_is_not_restored()
    {
        StartUnevenPairAndSplit();
        Closes(W(1));
        Assert.Equal([W(2), W(3)], tiler.Board.On(1));

        Closes(W(3));

        AssertArranged([W(2)], TileLayout.Automatic(1));
    }
}
