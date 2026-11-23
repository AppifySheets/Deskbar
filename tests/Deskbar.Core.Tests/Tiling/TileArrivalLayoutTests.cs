using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;
using Deskbar.Core.Tiling;
using static Deskbar.Core.Tests.Tiling.Trees;

namespace Deskbar.Core.Tests.Tiling;

// Which tile a newly arrived window splits, on the split tree. The TileArrivalTests cases ported: tiles are 1920x1040
// screens with no gap, the floor and the largest-tile rule read the leaves' shares (TileLayout.ShareOf). The side rule
// changed on purpose (spec, "Arrival and minimize"): Right for any tile wider than tall, alone in its column or not.
public sealed class TileArrivalLayoutTests
{
    static WindowHandle W(int n) => new(n);

    static IReadOnlyList<WindowHandle> Order(int count) => Enumerable.Range(1, count).Select(W).ToList();

    static IReadOnlyList<WindowRect> Tiles(int count, TileLayout layout) =>
        GridLayout.Arrange(new WindowRect(0, 0, 1920, 1040), count, 0, layout);

    static Maybe<TileSplit> Choose(int count, TileLayout layout, Maybe<WindowHandle> lastUsed, IReadOnlySet<WindowHandle>? skip = null) =>
        TileArrival.Choose(Order(count), layout, Tiles(count, layout), lastUsed, skip ?? new HashSet<WindowHandle>());

    static Maybe<TileSplit> Split(int window, TileArrange.Zone side) => Maybe.From(new TileSplit(W(window), side));

    [Fact]
    public void One_wide_tile_is_split_to_its_right() =>
        Assert.Equal(Split(1, TileArrange.Zone.Right), Choose(1, TileLayout.Automatic(1), Maybe.From(W(1))));

    [Fact]
    public void A_tall_tile_is_split_below() =>
        Assert.Equal(Split(1, TileArrange.Zone.Bottom), Choose(2, TileLayout.Automatic(2), Maybe.From(W(1))));

    // Changed on purpose: a wide tile in a stack is split to its right now. Right only splits that tile; it no longer
    // makes a full-height column, which is why the grid sent a stacked tile below.
    [Fact]
    public void Arrival_splits_a_wide_stacked_tile_to_the_right() =>
        Assert.Equal(Split(2, TileArrange.Zone.Right), Choose(2, Tree(Down(C(L, .5), C(L, .5))), Maybe.From(W(2))));

    // Ported with W3 at 20% height rather than 25%: at 25% the new side rule splits the wide W3 to its right, which
    // passes the floor (half of 50% wide, 25% high), and the case would stop testing the hand-over.
    [Fact]
    public void A_last_used_tile_below_the_floor_hands_the_split_to_the_largest() =>
        Assert.Equal(Split(1, TileArrange.Zone.Bottom),
            Choose(3, Tree(Across(C(L, .5), C(Down(C(L, .8), C(L, .2)), .5))), Maybe.From(W(3))));

    [Fact]
    public void With_no_last_used_tile_the_largest_is_split() =>
        Assert.Equal(Split(1, TileArrange.Zone.Bottom), Choose(3, TileLayout.Automatic(3), Maybe<WindowHandle>.None));

    [Fact]
    public void A_tie_goes_to_the_tile_latest_in_slot_order() =>
        Assert.Equal(Split(2, TileArrange.Zone.Bottom), Choose(2, TileLayout.Automatic(2), Maybe<WindowHandle>.None));

    // Changed on purpose: W6 (960x416, wide) is split to its right, where the grid split it below.
    [Fact]
    public void When_every_tile_is_below_the_floor_the_largest_is_still_split_wide_ones_to_the_right()
    {
        var column = Down(C(L, .3), C(L, .3), C(L, .4));
        Assert.Equal(Split(6, TileArrange.Zone.Right), Choose(6, Tree(Across(C(column, .5), C(column, .5))), Maybe<WindowHandle>.None));
    }

    [Fact]
    public void A_last_used_window_not_on_this_screen_is_ignored() =>
        Assert.Equal(Split(2, TileArrange.Zone.Bottom), Choose(2, TileLayout.Automatic(2), Maybe.From(W(9))));

    [Fact]
    public void Nothing_to_split_on_an_empty_screen() =>
        Assert.Equal(Maybe<TileSplit>.None,
            TileArrival.Choose([], TileLayout.Automatic(0), [], Maybe.From(W(1)), new HashSet<WindowHandle>()));

    [Fact]
    public void A_skipped_tile_is_never_the_largest_target() =>
        Assert.Equal(Split(1, TileArrange.Zone.Bottom),
            Choose(2, TileLayout.Automatic(2), Maybe<WindowHandle>.None, new HashSet<WindowHandle> { W(2) }));

    [Fact]
    public void When_every_tile_is_skipped_the_rule_ignores_skip() =>
        Assert.Equal(Split(1, TileArrange.Zone.Right),
            Choose(1, TileLayout.Automatic(1), Maybe.From(W(1)), new HashSet<WindowHandle> { W(1) }));

    // AVOIDED TILES (spec 2026-11-23-split-recent-app-windows-last-design.md): the three latest windows of an app
    // marked "split last" (VS Code). Petre: "do it so that vscode windows are the last to be affected", and of the two
    // ways offered, the middle one: the tile last used is still split, unless it is avoided.
    static Maybe<TileSplit> ChooseAvoiding(int count, TileLayout layout, Maybe<WindowHandle> lastUsed,
        IReadOnlySet<WindowHandle> avoid, int[]? ages = null, IReadOnlySet<WindowHandle>? skip = null) =>
        TileArrival.Choose(Order(count), layout, Tiles(count, layout), lastUsed, skip ?? new HashSet<WindowHandle>(), avoid,
            w => ages is null ? int.MaxValue : ages[(int)w.Value - 1]);

    // W1 | W3 over W2 with W3 at 20% height: W1 is 50% x 100%, W2 50% x 80%, W3 50% x 20% (too short to halve).
    static readonly TileLayout TallShortPair = Tree(Across(C(L, .5), C(Down(C(L, .8), C(L, .2)), .5)));

    [Fact]
    public void An_avoided_last_used_tile_hands_the_split_to_the_largest_free_one() =>
        Assert.Equal(Split(2, TileArrange.Zone.Bottom),
            ChooseAvoiding(2, TileLayout.Automatic(2), Maybe.From(W(1)), new HashSet<WindowHandle> { W(1) }));

    // Every tile avoided: the oldest one (the largest age) is split, not the tile last used.
    [Fact]
    public void Avoided_tiles_are_split_only_when_no_tile_is_free() =>
        Assert.Equal(Split(2, TileArrange.Zone.Bottom),
            ChooseAvoiding(2, TileLayout.Automatic(2), Maybe.From(W(1)), new HashSet<WindowHandle> { W(1), W(2) }, [0, 1]));

    // W3 is the oldest but too short to halve, so the next oldest, W2, is split (960x832, wider than tall: to its right).
    [Fact]
    public void Among_avoided_tiles_the_oldest_that_passes_the_floor_is_split() =>
        Assert.Equal(Split(2, TileArrange.Zone.Right),
            ChooseAvoiding(3, TallShortPair, Maybe<WindowHandle>.None, new HashSet<WindowHandle> { W(1), W(2), W(3) }, [0, 1, 2]));

    [Fact]
    public void Skipped_tiles_still_come_after_avoided_ones() =>
        Assert.Equal(Split(1, TileArrange.Zone.Bottom),
            ChooseAvoiding(2, TileLayout.Automatic(2), Maybe<WindowHandle>.None, new HashSet<WindowHandle> { W(1) },
                skip: new HashSet<WindowHandle> { W(2) }));

    // Plan review focus 5: the only free tile is too small to halve; it is still the one split (wide, so to its right),
    // never an avoided tile.
    [Fact]
    public void When_every_free_tile_fails_the_floor_the_largest_free_one_is_split_not_an_avoided_one() =>
        Assert.Equal(Split(3, TileArrange.Zone.Right),
            ChooseAvoiding(3, TallShortPair, Maybe<WindowHandle>.None, new HashSet<WindowHandle> { W(1), W(2) }));

    // The leaves' shares come from the tree: in Down[Across[W1, W3], W2] the top pair's tiles are a quarter of the
    // screen each, W2 half, so W2 is the largest.
    [Fact]
    public void Shares_are_read_from_nested_splits() =>
        Assert.Equal(Split(2, TileArrange.Zone.Right),
            TileArrival.Choose([W(1), W(3), W(2)], Tree(Down(C(Across(C(L, .5), C(L, .5)), .5), C(L, .5))),
                Tiles(3, Tree(Down(C(Across(C(L, .5), C(L, .5)), .5), C(L, .5)))), Maybe<WindowHandle>.None, new HashSet<WindowHandle>()));

    // With an opening the windows' slots skip its leaf: W2 sits in leaf 2 (52.5% of the screen) and wins over W1
    // (30%). Read as leaf 1, the opening's 17.5%, it would lose.
    [Fact]
    public void An_opening_is_skipped_when_slots_are_read()
    {
        var opened = Tree(Across(C(L, .3), C(Down(C(L, .25), C(L, .75)), .7)), opening: 1);
        Assert.Equal(Split(2, TileArrange.Zone.Right), Choose(2, opened, Maybe<WindowHandle>.None));
    }
}
