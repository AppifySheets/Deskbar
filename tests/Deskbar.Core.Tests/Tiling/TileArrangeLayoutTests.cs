using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;
using Deskbar.Core.Tiling;
using static Deskbar.Core.Tests.Tiling.Trees;

namespace Deskbar.Core.Tests.Tiling;

// TileArrange on the split tree (TileLayout): the TileSizes cases of TileArrangeTests ported to the tree overloads,
// plus the rules the tree changes on purpose (spec: docs/superpowers/specs/2026-11-19-tiling-split-tree-design.md).
// A test whose expectation differs from its TileSizes twin says the new rule in its name.
public sealed class TileArrangeLayoutTests
{
    static WindowHandle W(int n) => new(n);

    static readonly WindowHandle A = W(1), B = W(2), Cw = W(3), D = W(4), E = W(5), X = W(9);

    static void Is(string expected, TileLayout layout) => Assert.Equal(expected, Text(layout));

    // --- drops beside a tile -------------------------------------------------------------------------------------

    // Petre's case, the reason for the tree: A over B on the left, C full height on the right, C dropped on A's right.
    // C is lifted (the stack becomes the whole screen), then A is split: A and C share the top row, B stays full width.
    [Fact]
    public void Drop_beside_the_top_of_a_stack_shares_its_row()
    {
        var (order, layout) = TileArrange.Drop([A, B, Cw], Tree(Across(C(Down(C(L, .5), C(L, .5)), .5), C(L, .5))), Cw, A, TileArrange.Zone.Right);

        Assert.Equal([A, Cw, B], order);
        Is("D[A[L 0.5, L 0.5] 0.5, L 0.5]", layout);
    }

    [Fact]
    public void Drop_beside_the_bottom_of_a_stack_shares_its_row()
    {
        var (order, layout) = TileArrange.Drop([A, B, Cw], Tree(Across(C(Down(C(L, .5), C(L, .5)), .5), C(L, .5))), Cw, B, TileArrange.Zone.Right);

        Assert.Equal([A, B, Cw], order);
        Is("D[L 0.5, A[L 0.5, L 0.5] 0.5]", layout);
    }

    // Unchanged behaviour: beside a full-height tile is still a full-height column.
    [Fact]
    public void Drop_beside_a_full_height_tile_makes_a_column()
    {
        var (order, layout) = TileArrange.Drop([A, B], TileLayout.Automatic(2), B, A, TileArrange.Zone.Left);

        Assert.Equal([B, A], order);
        Is("A[L 0.5, L 0.5]", layout);
    }

    // Ported: two side by side, the right one dropped on the bottom of the left one stacks them.
    [Fact]
    public void Dropping_below_a_tile_stacks_the_window_under_it()
    {
        var (order, layout) = TileArrange.Drop([A, B], TileLayout.Automatic(2), B, A, TileArrange.Zone.Bottom);

        Assert.Equal([A, B], order);
        Is("D[L 0.5, L 0.5]", layout);
    }

    [Fact]
    public void Dropping_above_a_tile_stacks_the_window_over_it()
    {
        var (order, layout) = TileArrange.Drop([A, B], TileLayout.Automatic(2), B, A, TileArrange.Zone.Top);

        Assert.Equal([B, A], order);
        Is("D[L 0.5, L 0.5]", layout);
    }

    // Ported: A alone in a 40% column, B over C in a 60% column. C dropped right of A takes half of A's width; the
    // stack C left becomes B alone, and the 60% keeps its width.
    [Fact]
    public void Dropping_beside_a_tile_takes_half_of_its_width()
    {
        var start = Tree(Across(C(L, .4), C(Down(C(L, .5), C(L, .5)), .6)));

        var (order, layout) = TileArrange.Drop([A, B, Cw], start, Cw, A, TileArrange.Zone.Right);

        Assert.Equal([A, Cw, B], order);
        Is("A[L 0.2, L 0.2, L 0.6]", layout);
    }

    [Fact]
    public void The_middle_changes_nothing()
    {
        var start = TileLayout.Automatic(2);
        var (order, layout) = TileArrange.Drop([A, B], start, B, A, TileArrange.Zone.Middle);

        Assert.Equal([A, B], order);
        Assert.Equal(start, layout);
    }

    // A dropped window, a target not on this screen and a layout for another count all change nothing, as today.
    [Fact]
    public void A_drop_that_cannot_be_read_changes_nothing()
    {
        var start = TileLayout.Automatic(2);

        Unchanged([A, B], start, TileArrange.Drop([A, B], start, A, A, TileArrange.Zone.Left));
        Unchanged([A, B], start, TileArrange.Drop([A, B], start, A, X, TileArrange.Zone.Left));
        Unchanged([A, B, Cw], start, TileArrange.Drop([A, B, Cw], start, Cw, A, TileArrange.Zone.Left));
    }

    // A window dragged in from another screen has nothing to lift here: only the insert happens.
    [Fact]
    public void A_window_from_another_screen_is_only_inserted()
    {
        var (order, layout) = TileArrange.Drop([A, B], TileLayout.Automatic(2), X, A, TileArrange.Zone.Bottom);

        Assert.Equal([A, X, B], order);
        Is("A[D[L 0.5, L 0.5] 0.5, L 0.5]", layout);
    }

    [Fact]
    public void A_drop_beside_a_tile_closes_the_opening_first()
    {
        var (order, layout) = TileArrange.Drop([A, B], OpenedAboveTheRightTile(), B, A, TileArrange.Zone.Bottom);

        Assert.Equal([A, B], order);
        Is("D[L 0.5, L 0.5]", layout);
    }

    // --- screen-edge bands ---------------------------------------------------------------------------------------

    [Fact]
    public void DropAtEdge_right_makes_a_full_height_column()
    {
        var (order, layout) = TileArrange.DropAtEdge([A, B, Cw], Tree(Down(C(L, 1 / 3.0), C(L, 1 / 3.0), C(L, 1 / 3.0))), Cw, TileArrange.Zone.Right);

        Assert.Equal([A, B, Cw], order);
        Is("A[D[L 0.5, L 0.5] 0.5, L 0.5]", layout);
    }

    // On a root of the band's axis the window becomes the outer child and takes half of the one it lands next to.
    [Fact]
    public void DropAtEdge_on_a_root_of_its_axis_halves_the_outer_child()
    {
        var thirds = Tree(Across(C(L, 1 / 3.0), C(L, 1 / 3.0), C(L, 1 / 3.0)));

        var (order, layout) = TileArrange.DropAtEdge([A, B, Cw], thirds, A, TileArrange.Zone.Right);

        Assert.Equal([B, Cw, A], order);
        Is("A[L 0.5, L 0.25, L 0.25]", layout);
    }

    [Fact]
    public void DropAtEdge_from_another_screen_wraps_the_root()
    {
        var (order, layout) = TileArrange.DropAtEdge([A, B], TileLayout.Automatic(2), X, TileArrange.Zone.Top);

        Assert.Equal([X, A, B], order);
        Is("D[L 0.5, A[L 0.5, L 0.5] 0.5]", layout);
    }

    // The only window of the screen has nowhere to go but where it is.
    [Fact]
    public void DropAtEdge_of_the_only_window_changes_nothing()
    {
        var lone = TileLayout.Automatic(1);
        Unchanged([A], lone, TileArrange.DropAtEdge([A], lone, A, TileArrange.Zone.Left));
    }

    [Fact]
    public void DropAtEdge_closes_the_opening_first()
    {
        var (order, layout) = TileArrange.DropAtEdge([A, B], OpenedAboveTheRightTile(), A, TileArrange.Zone.Bottom);

        Assert.Equal([B, A], order);
        Is("D[L 0.5, L 0.5]", layout);
    }

    // Ruling R4: distances are SHARES of the inner span, as ZoneOf reads a tile. (30, 10) is 3% from the left and
    // 1.7% from the top, so the top wins the corner.
    [Theory]
    [InlineData(59, 300, TileArrange.Zone.Left)]
    [InlineData(941, 300, TileArrange.Zone.Right)]
    [InlineData(500, 35, TileArrange.Zone.Top)]
    [InlineData(500, 565, TileArrange.Zone.Bottom)]
    [InlineData(30, 10, TileArrange.Zone.Top)]
    public void ScreenBand_is_six_percent_deep(int x, int y, TileArrange.Zone side) =>
        Assert.Equal(Maybe.From(side), TileArrange.ScreenBand(new WindowRect(0, 0, 1000, 600), x, y));

    [Theory]
    [InlineData(61, 300)]
    [InlineData(500, 300)]
    [InlineData(500, 37)]
    public void ScreenBand_is_none_outside_the_bands(int x, int y) =>
        Assert.True(TileArrange.ScreenBand(new WindowRect(0, 0, 1000, 600), x, y).HasNoValue);

    // --- refit: the stored layout brought to the window count ------------------------------------------------------

    [Fact]
    public void Refit_keeps_a_layout_whose_count_matches()
    {
        var stored = Tree(Across(C(L, .7), C(L, .3)));
        Assert.Equal(stored, TileArrange.Refit(stored, 2, Maybe<int>.None));
    }

    // Ported: a leaver takes only its own leaf, on the automatic layout too.
    [Fact]
    public void A_leaver_on_the_automatic_layout_takes_only_its_own_leaf() =>
        Is("A[D[L 0.5, L 0.5] 0.5, L 0.5]", TileArrange.Refit(TileLayout.Automatic(4), 3, Maybe.From(3)));

    [Fact]
    public void A_leaver_alone_in_its_column_takes_the_column_and_the_rest_keep_their_rows() =>
        Is("D[L 0.5, L 0.5]", TileArrange.Refit(TileLayout.Automatic(3), 2, Maybe.From(0)));

    // Changed on purpose (spec, "Arrival and minimize"): with no known leaver the LAST leaves go, one by one, where
    // the grid fitted the automatic shape of the new count (two columns).
    [Fact]
    public void Refit_without_a_leaver_drops_the_last_leaves() =>
        Is("D[L 0.5, L 0.5]", TileArrange.Refit(TileLayout.Automatic(4), 2, Maybe<int>.None));

    // Changed on purpose: a joiner goes below the last leaf, once per window, where the grid fitted the automatic
    // shape of the new count (three columns for five).
    [Fact]
    public void Refit_adds_below_the_last_leaf_instead_of_regridding() =>
        Is("A[D[L 0.5, L 0.5] 0.5, D[L 0.5, L 0.25, L 0.25] 0.5]", TileArrange.Refit(TileLayout.Automatic(4), 5, Maybe<int>.None));

    // Ported: a custom shape survives a window leaving and one joining. Joining the automatic two-column layout gives
    // the automatic three-window layout, as the grid did.
    [Fact]
    public void A_custom_shape_survives_a_window_leaving_and_one_joining()
    {
        var stacked = Tree(Down(C(L, .5), C(L, .5)));
        Assert.True(TileArrange.IsCustom(stacked));

        Is("D[L 0.5, L 0.25, L 0.25]", TileArrange.Refit(stacked, 3, Maybe<int>.None));
        Is("L", TileArrange.Refit(stacked, 1, Maybe.From(1)));
        Assert.Equal(TileLayout.Automatic(3), TileArrange.Refit(TileLayout.Automatic(2), 3, Maybe<int>.None));
    }

    [Fact]
    public void A_leaver_takes_only_its_leaf_and_the_opening_stays()
    {
        var opened = OpenedAboveTheRightTile();

        // A, the left column, leaves: its leaf is 0, counted in the layout that holds the opening.
        var leaver = TileArrange.LeafOf(opened, 0, 2);
        var layout = TileArrange.Refit(opened, 1, Maybe.From(leaver));

        Assert.Equal(0, leaver);
        Assert.Equal(0, layout.Opening);
        Assert.Equal(2, layout.Count);
        Assert.Equal(Text(((TileBranch)opened.Root).Children[1].Node), Text(layout.Root));
    }

    // An opening for the count stays; any other change closes it first and then fits.
    [Fact]
    public void Any_other_change_closes_the_opening_first()
    {
        Assert.Equal(OpenedAboveTheRightTile(), TileArrange.Refit(OpenedAboveTheRightTile(), 2, Maybe<int>.None));
        Is("A[L 0.5, D[L 0.5, L 0.5] 0.5]", TileArrange.Refit(OpenedAboveTheRightTile(), 3, Maybe<int>.None));
    }

    // --- where a fit starts (FitStart, final review C1 and M1) -----------------------------------------------------

    [Fact]
    public void With_nothing_stored_one_leaver_starts_from_the_layout_it_left()
    {
        var (start, leaving) = TileArrange.FitStart(Maybe<TileLayout>.None, 2, Maybe.From(0), oldCount: 3);

        Assert.Equal(TileLayout.Automatic(3), start);
        Assert.Equal(0, leaving.GetValueOrThrow());
        Is("D[L 0.5, L 0.5]", TileArrange.Refit(start, 2, leaving));
    }

    [Fact]
    public void A_leaver_with_the_count_unchanged_is_ignored()
    {
        var (start, leaving) = TileArrange.FitStart(Maybe<TileLayout>.None, 2, Maybe.From(0), oldCount: 2);

        Assert.Equal(TileLayout.Automatic(2), start);
        Assert.True(leaving.HasNoValue);
        Assert.Equal(TileLayout.Automatic(2), TileArrange.Refit(start, 2, leaving));
    }

    // The leaver's leaf was read from a layout that is gone (5 is no leaf of the automatic three): Refit must not take
    // a leaf the start lacks. It drops the last leaf, which here is the automatic two, as the grid's fallback was.
    [Fact]
    public void A_leaver_whose_leaf_the_start_does_not_hold_drops_the_last_leaf()
    {
        var (start, leaving) = TileArrange.FitStart(Maybe<TileLayout>.None, 2, Maybe.From(5), oldCount: 3);

        Assert.Equal(TileLayout.Automatic(2), TileArrange.Refit(start, 2, leaving));
    }

    // Changed on purpose: two leavers at once with nothing stored. The start is the automatic four (the layout the
    // leaver's leaf was read from), and with two leaves too many the last two go, leaving the left column's stack.
    // The grid ended in the equal two columns.
    [Fact]
    public void Two_leavers_at_once_with_nothing_stored_drop_the_last_leaves()
    {
        var (start, leaving) = TileArrange.FitStart(Maybe<TileLayout>.None, 2, Maybe.From(0), oldCount: 4);

        Assert.Equal(TileLayout.Automatic(4), start);
        Is("D[L 0.5, L 0.5]", TileArrange.Refit(start, 2, leaving));
    }

    [Fact]
    public void A_stored_layout_is_the_start()
    {
        var stored = Tree(Across(C(L, .7), C(L, .3)));

        var (start, _) = TileArrange.FitStart(Maybe.From(stored), 2, Maybe.From(0), oldCount: 2);

        Assert.Equal(stored, start);
    }

    // --- custom and plain ---------------------------------------------------------------------------------------

    [Fact]
    public void Only_even_splits_on_the_automatic_layout_are_plain()
    {
        Assert.True(TileArrange.IsPlain(TileLayout.Automatic(2)));
        Assert.True(TileArrange.IsPlain(TileLayout.Automatic(5)));
        Assert.False(TileArrange.IsPlain(Tree(Down(C(L, .5), C(L, .5)))));
        Assert.False(TileArrange.IsPlain(Tree(Across(C(L, .7), C(L, .3)))));
        Assert.False(TileArrange.IsCustom(Tree(Across(C(L, .7), C(L, .3)))));
    }

    // --- openings -----------------------------------------------------------------------------------------------

    static readonly WindowRect Screen = new(0, 0, 1920, 1040);
    const int Gap = 8;

    // Two columns; the right tile's top edge pulled down to 400 of an inner area that starts at 8 and is 1024 high.
    static TileLayout OpenedAboveTheRightTile() =>
        TileArrange.Open(TileLayout.Automatic(2), 2, Screen, Gap, 1, Edges.Top, new WindowRect(964, 400, 1912, 1032)).Value;

    static TileBranch Branch(TileNode node) => (TileBranch)node;

    [Fact]
    public void Pulling_a_top_edge_in_opens_a_leaf_above_the_tile()
    {
        var layout = OpenedAboveTheRightTile();
        var column = Branch(Branch(layout.Root).Children[1].Node);

        Assert.Equal(1, layout.Opening);
        Assert.Equal(Axis.Down, column.Axis);
        Assert.Equal(392.0 / 1024, column.Children[0].Share, 9);
        Assert.Equal(1 - 392.0 / 1024, column.Children[1].Share, 9);
        Assert.True(layout.HasOpeningFor(2));
        Assert.False(TileArrange.IsPlain(layout));
    }

    // A full-height tile's left edge: the opening becomes its sibling in the root's Across, as deep as the pull.
    [Fact]
    public void Pulling_a_left_edge_in_opens_a_column_before_the_tile()
    {
        var layout = TileArrange.Open(TileLayout.Automatic(2), 2, Screen, Gap, 0, Edges.Left, new WindowRect(400, 8, 956, 1032)).Value;
        var root = Branch(layout.Root);

        Assert.Equal(0, layout.Opening);
        Assert.Equal(3, root.Children.Count);
        Assert.Equal(392.0 / 1904, root.Children[0].Share, 9);
        Assert.Equal(0.5 - 392.0 / 1904, root.Children[1].Share, 9);
        Assert.Equal(0.5, root.Children[2].Share, 9);
    }

    // Changed on purpose (spec, "Openings"): the right column is a stack, and its top tile's right edge is pulled 200
    // px in on a 1000 px inner width. Only that tile gives the room up; the grid made a whole new column and narrowed
    // the bottom tile too.
    [Fact]
    public void Open_beside_a_stacked_tile_splits_only_that_tile()
    {
        var layout = TileArrange.Open(TileLayout.Automatic(3), 3, new WindowRect(0, 0, 1000, 600), 0, 1, Edges.Right, new WindowRect(500, 0, 800, 300)).Value;

        Is("A[L 0.5, D[A[L 0.6, L 0.4] 0.5, L 0.5] 0.5] open 2", layout);
        Assert.Equal(new WindowRect(500, 300, 1000, 600), GridLayout.Arrange(new WindowRect(0, 0, 1000, 600), 3, 0, layout)[2]);
    }

    // An inner edge is SplitMove's: it has a neighbour, so it opens nothing.
    [Fact]
    public void An_inner_edge_opens_nothing()
    {
        var area = new WindowRect(0, 0, 1000, 600);
        Assert.True(TileArrange.Open(TileLayout.Automatic(3), 3, area, 0, 0, Edges.Right, new WindowRect(0, 0, 300, 600)).HasNoValue);
        Assert.True(TileArrange.Open(TileLayout.Automatic(3), 3, area, 0, 1, Edges.Left, new WindowRect(700, 0, 1000, 300)).HasNoValue);
        Assert.True(TileArrange.Open(TileLayout.Automatic(3), 3, area, 0, 1, Edges.Bottom, new WindowRect(500, 0, 1000, 100)).HasNoValue);
    }

    // Two outer edges pulled at a corner: the deeper pull wins.
    [Fact]
    public void At_a_corner_the_deeper_pull_opens()
    {
        var layout = TileArrange.Open(TileLayout.Automatic(2), 2, new WindowRect(0, 0, 1000, 600), 0, 1, Edges.Top | Edges.Right, new WindowRect(500, 120, 700, 600)).Value;

        Is("A[L 0.5, L 0.2, L 0.3] open 2", layout);
    }

    [Fact]
    public void A_nudge_a_second_opening_a_narrow_tile_or_another_count_opens_nothing()
    {
        Assert.True(TileArrange.Open(TileLayout.Automatic(2), 2, Screen, Gap, 1, Edges.Top, new WindowRect(964, 40, 1912, 1032)).HasNoValue);
        Assert.True(TileArrange.Open(OpenedAboveTheRightTile(), 2, Screen, Gap, 0, Edges.Top, new WindowRect(8, 400, 956, 1032)).HasNoValue);
        // 15% wide: it cannot give up 10% and keep 10%.
        Assert.True(TileArrange.Open(Tree(Across(C(L, .85), C(L, .15))), 2, new WindowRect(0, 0, 1000, 600), 0, 1, Edges.Right, new WindowRect(850, 0, 880, 600)).HasNoValue);
        Assert.True(TileArrange.Open(TileLayout.Automatic(2), 3, Screen, Gap, 1, Edges.Top, new WindowRect(964, 400, 1912, 1032)).HasNoValue);
    }

    [Fact]
    public void The_layout_leaves_the_opening_empty()
    {
        var tiles = GridLayout.Arrange(Screen, 2, Gap, OpenedAboveTheRightTile());

        Assert.Equal([new WindowRect(8, 8, 956, 1032), new WindowRect(964, 404, 1912, 1032)], tiles);
        Assert.Equal(new WindowRect(964, 8, 1912, 396), GridLayout.OpeningOf(Screen, 2, Gap, OpenedAboveTheRightTile()).Value);
    }

    [Fact]
    public void A_window_after_the_opening_is_counted_past_it()
    {
        Assert.Equal(0, TileArrange.LeafOf(OpenedAboveTheRightTile(), 0, 2));
        Assert.Equal(2, TileArrange.LeafOf(OpenedAboveTheRightTile(), 1, 2));
        // Without an opening for the count, a slot is its leaf.
        Assert.Equal(1, TileArrange.LeafOf(OpenedAboveTheRightTile(), 1, 3));
        Assert.Equal(1, TileArrange.LeafOf(TileLayout.Automatic(2), 1, 2));
    }

    [Fact]
    public void Keeping_a_leaf_open_makes_it_the_opening()
    {
        var kept = TileArrange.KeptOpen(TileLayout.Automatic(4), 3);

        Assert.Equal(TileLayout.Automatic(4) with { Opening = 3 }, kept);
        Assert.True(kept.HasOpeningFor(3));
        Assert.False(TileArrange.IsPlain(kept));
    }

    [Fact]
    public void Keeping_a_leaf_the_layout_does_not_have_changes_nothing()
    {
        Assert.Equal(TileLayout.Automatic(4), TileArrange.KeptOpen(TileLayout.Automatic(4), 4));
        Assert.Equal(TileLayout.Automatic(4), TileArrange.KeptOpen(TileLayout.Automatic(4), -1));
    }

    [Fact]
    public void Closing_gives_the_room_back() =>
        Assert.Equal(TileLayout.Automatic(2), TileArrange.Closed(OpenedAboveTheRightTile()));

    [Fact]
    public void A_newcomer_fills_the_opening_and_it_is_gone()
    {
        var (order, layout) = TileArrange.Filled([A, B], OpenedAboveTheRightTile(), Cw);

        Assert.Equal([A, Cw, B], order);
        Assert.Null(layout.Opening);
        Assert.Equal(3, layout.Count);
    }

    [Fact]
    public void Without_an_opening_a_newcomer_joins_at_the_end()
    {
        var (order, layout) = TileArrange.Filled([A, B], TileLayout.Automatic(2), Cw);

        Assert.Equal([A, B, Cw], order);
        Assert.Equal(TileLayout.Automatic(2), layout);
    }

    [Fact]
    public void A_tile_moved_into_the_opening_leaves_its_own_place()
    {
        var (order, layout) = TileArrange.IntoOpening([A, B], OpenedAboveTheRightTile(), A);

        Assert.Equal([A, B], order);
        Assert.Null(layout.Opening);
        Assert.Equal(Axis.Down, Branch(layout.Root).Axis);
        Assert.Equal(2, layout.Count);
    }

    [Fact]
    public void Into_an_opening_that_is_not_there_changes_nothing()
    {
        var (order, layout) = TileArrange.IntoOpening([A, B], TileLayout.Automatic(2), A);

        Assert.Equal([A, B], order);
        Assert.Equal(TileLayout.Automatic(2), layout);
    }

    // --- closing an opening by pulling the tile back over it ------------------------------------------------------

    static readonly WindowRect Small = new(0, 0, 1000, 600);

    // The stacked case above: leaf 1 (top right) has its opening as its right sibling, at (800, 0, 1000, 300).
    static TileLayout OpenedBesideTheTopRightTile() =>
        TileArrange.Open(TileLayout.Automatic(3), 3, Small, 0, 1, Edges.Right, new WindowRect(500, 0, 800, 300)).Value;

    [Fact]
    public void Closes_when_pulled_back_over_its_sibling_opening()
    {
        var opened = OpenedBesideTheTopRightTile();

        Assert.True(TileArrange.Closes(opened, 3, Small, 0, 1, Edges.Right, new WindowRect(500, 0, 960, 300)));
        // Not far enough: within a quarter of the hole's depth (50 px) from its far side is what counts.
        Assert.False(TileArrange.Closes(opened, 3, Small, 0, 1, Edges.Right, new WindowRect(500, 0, 900, 300)));
        // The bottom tile's top edge faces the top row, not the opening.
        Assert.False(TileArrange.Closes(opened, 3, Small, 0, 3, Edges.Top, new WindowRect(500, 0, 1000, 600)));
        // The left tile's right edge faces the whole right column.
        Assert.False(TileArrange.Closes(opened, 3, Small, 0, 0, Edges.Right, new WindowRect(0, 0, 1000, 600)));
    }

    // Ported: the opening above the right tile closes when that tile's top edge is let go near the screen's top.
    [Fact]
    public void Closes_when_the_top_edge_is_pulled_back_up()
    {
        Assert.True(TileArrange.Closes(OpenedAboveTheRightTile(), 2, Screen, Gap, 2, Edges.Top, new WindowRect(964, 50, 1912, 1032)));
        Assert.False(TileArrange.Closes(OpenedAboveTheRightTile(), 2, Screen, Gap, 2, Edges.Top, new WindowRect(964, 300, 1912, 1032)));
    }

    // A whole-height opening closes from either tile of the stack beside it: the walk up from either reaches the root
    // Across, where the opening is the neighbour on the left. An opening inside a stack does not close from the next
    // column, whose left neighbour is the stack (the grid's "whole column" rule, kept).
    [Fact]
    public void A_whole_column_opening_closes_from_the_stack_beside_it_and_a_stacked_one_does_not()
    {
        var column = Tree(Across(C(L, .2), C(Down(C(L, .5), C(L, .5)), .8)), opening: 0);
        Assert.True(TileArrange.Closes(column, 2, Small, 0, 1, Edges.Left, new WindowRect(30, 0, 1000, 300)));
        Assert.True(TileArrange.Closes(column, 2, Small, 0, 2, Edges.Left, new WindowRect(30, 300, 1000, 600)));

        var stacked = Tree(Across(C(Down(C(L, .5), C(L, .5)), .5), C(L, .5)), opening: 0);
        Assert.False(TileArrange.Closes(stacked, 2, Small, 0, 2, Edges.Left, new WindowRect(0, 0, 1000, 600)));
    }

    // --- a minimized tile coming back (SpotOf when it goes, Returned when it comes back) ---------------------------

    static readonly TileLayout StackAndColumn = Tree(Across(C(Down(C(L, .5), C(L, .5)), .5), C(L, .5)));

    [Fact]
    public void The_spot_of_a_full_height_column_is_the_whole_stack_beside_it()
    {
        var spot = TileArrange.SpotOf([A, B, Cw], StackAndColumn, Cw).Value;

        Assert.Equal([A, B], spot.Siblings);
        Assert.Equal(TileArrange.Zone.Right, spot.Side);
        Assert.Equal(0.5, spot.Share, 9);
    }

    // The sibling is the child before, else the one after; the share is pairwise, own over own plus sibling's.
    [Fact]
    public void The_spot_share_is_pairwise()
    {
        var thirds = Tree(Across(C(L, .2), C(L, .3), C(L, .5)));

        var b = TileArrange.SpotOf([A, B, Cw], thirds, B).Value;
        Assert.Equal([A], b.Siblings);
        Assert.Equal(TileArrange.Zone.Right, b.Side);
        Assert.Equal(0.6, b.Share, 9);

        var a = TileArrange.SpotOf([A, B, Cw], thirds, A).Value;
        Assert.Equal([B], a.Siblings);
        Assert.Equal(TileArrange.Zone.Left, a.Side);
        Assert.Equal(0.4, a.Share, 9);
    }

    // The opening is never a sibling: B's child before is the opening, so its sibling is C after it.
    [Fact]
    public void The_opening_is_never_a_sibling()
    {
        var spot = TileArrange.SpotOf([B, Cw], Tree(Across(C(L, .25), C(L, .25), C(L, .5)), opening: 0), B).Value;

        Assert.Equal([Cw], spot.Siblings);
        Assert.Equal(TileArrange.Zone.Left, spot.Side);
        Assert.Equal(1 / 3.0, spot.Share, 9);
    }

    [Fact]
    public void A_lone_tile_or_a_window_not_on_the_screen_has_no_spot()
    {
        Assert.True(TileArrange.SpotOf([A], TileLayout.Automatic(1), A).HasNoValue);
        Assert.True(TileArrange.SpotOf([A, B], TileLayout.Automatic(2), X).HasNoValue);
    }

    // Changed on purpose (spec, "Arrival and minimize"): the stack is still a stack, so C comes back full height
    // beside it, where the grid rule ("old column gone") also made a column but by index.
    [Fact]
    public void Returned_brings_a_full_height_column_back_full_height()
    {
        var spot = TileArrange.SpotOf([A, B, Cw], StackAndColumn, Cw).Value;

        var (order, layout) = TileArrange.Returned([A, B], Tree(Down(C(L, .5), C(L, .5))), Cw, spot).Value;

        Assert.Equal([A, B, Cw], order);
        Is("A[D[L 0.5, L 0.5] 0.5, L 0.5]", layout);
    }

    // D joined the stack meanwhile, so no subtree holds only C's siblings except the leaves A and B. The first in leaf
    // order (A) is split; the Down keeps its thirds.
    [Fact]
    public void Returned_goes_beside_the_first_remaining_sibling_when_the_stack_was_split()
    {
        var spot = TileArrange.SpotOf([A, B, Cw], StackAndColumn, Cw).Value;

        var (order, layout) = TileArrange.Returned([A, B, D], Tree(Down(C(L, 1 / 3.0), C(L, 1 / 3.0), C(L, 1 / 3.0))), Cw, spot).Value;

        Assert.Equal([A, Cw, B, D], order);
        Is("D[A[L 0.5, L 0.5] 0.333333, L 0.333333, L 0.333333]", layout);
    }

    // The largest subtree of siblings wins over a single sibling leaf elsewhere, and as a child of an Across it
    // becomes a sibling there (taking its share of the stack's share), not a wrapper.
    [Fact]
    public void Returned_splits_the_largest_subtree_of_siblings()
    {
        var spot = new TileArrange.ReturnSpot([A, B, D], TileArrange.Zone.Right, 0.5);
        var layout = Tree(Across(C(Down(C(L, .5), C(L, .5)), .5), C(Down(C(L, .5), C(L, .5)), .5)));

        var (order, returned) = TileArrange.Returned([A, B, D, E], layout, Cw, spot).Value;

        Assert.Equal([A, B, Cw, D, E], order);
        Is("A[D[L 0.5, L 0.5] 0.25, L 0.25, D[L 0.5, L 0.5] 0.5]", returned);
    }

    [Fact]
    public void Returned_is_none_without_siblings() =>
        Assert.True(TileArrange.Returned([D, E], TileLayout.Automatic(2), Cw, new TileArrange.ReturnSpot([A, B], TileArrange.Zone.Right, 0.5)).HasNoValue);

    // The remembered share is clamped to SplitMove's minimum, so a sliver comes back as a usable tile.
    [Fact]
    public void Returned_clamps_the_share_to_the_minimum()
    {
        var (_, layout) = TileArrange.Returned([A], TileLayout.Automatic(1), B, new TileArrange.ReturnSpot([A], TileArrange.Zone.Right, 0.01)).Value;

        Is("A[L 0.9, L 0.1]", layout);
    }

    // An opening after the returning leaf moves on one with it, and stays open.
    [Fact]
    public void A_returning_tile_before_the_opening_moves_the_opening_on()
    {
        var kept = Tree(Across(C(Down(C(L, .5), C(L, .5)), .5), C(L, .5)), opening: 2);

        var (order, layout) = TileArrange.Returned([B, Cw], kept, A, new TileArrange.ReturnSpot([B], TileArrange.Zone.Top, 0.5)).Value;

        Assert.Equal([A, B, Cw], order);
        Is("A[D[L 0.25, L 0.25, L 0.5] 0.5, L 0.5] open 3", layout);
        Assert.True(layout.HasOpeningFor(3));
    }

    // An opening before the returning leaf keeps its index, and the window's place in the order skips it.
    [Fact]
    public void A_returning_tile_after_the_opening_is_counted_past_it()
    {
        var kept = Tree(Across(C(L, .5), C(Down(C(L, .5), C(L, .5)), .5)), opening: 0);

        var (order, layout) = TileArrange.Returned([B, Cw], kept, A, new TileArrange.ReturnSpot([Cw], TileArrange.Zone.Bottom, 0.5)).Value;

        Assert.Equal([B, Cw, A], order);
        Is("A[L 0.5, D[L 0.5, L 0.25, L 0.25] 0.5] open 0", layout);
    }

    // --- even out tiles, two steps ------------------------------------------------------------------------------

    [Fact]
    public void TidyStep_evens_out_while_a_split_is_uneven() =>
        Assert.Equal(TileArrange.TileTidy.EvenOut, TileArrange.TidyStep([Tree(Down(C(L, .5), C(L, .5))), Tree(Across(C(L, .7), C(L, .3)))]));

    [Fact]
    public void TidyStep_resets_to_the_grid_once_every_stored_layout_is_even() =>
        Assert.Equal(TileArrange.TileTidy.ResetGrid, TileArrange.TidyStep([Tree(Down(C(L, .5), C(L, .5)))]));

    [Fact]
    public void TidyStep_offers_nothing_with_nothing_stored() =>
        Assert.Equal(TileArrange.TileTidy.None, TileArrange.TidyStep([]));

    static void Unchanged(IReadOnlyList<WindowHandle> order, TileLayout layout, (IReadOnlyList<WindowHandle> Order, TileLayout Layout) result)
    {
        Assert.Equal(order, result.Order);
        Assert.Equal(layout, result.Layout);
    }
}
