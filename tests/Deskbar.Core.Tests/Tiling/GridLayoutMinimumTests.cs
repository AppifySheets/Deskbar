using Deskbar.Core.Domain;
using Deskbar.Core.Tiling;
using static Deskbar.Core.Tests.Tiling.Trees;

namespace Deskbar.Core.Tests.Tiling;

// #398: a window that will not go as small as its cell gets a cell its size, and the room comes from the cells beside
// it in the same split that can spare it (GridLayout.Fit). The issue's own case is the first test: three columns, and
// the middle one's window (Edge) will not go narrower than 627 px.
public class GridLayoutMinimumTests
{
    const int Gap = 10;
    static readonly WindowRect Area = new(0, 0, 1536, 800);
    static readonly TileLayout ThreeColumns = Tree(Across(C(L, 1.0 / 3), C(L, 1.0 / 3), C(L, 1.0 / 3)));

    [Fact]
    public void A_column_too_narrow_for_its_window_is_widened_and_its_neighbours_pay_for_it()
    {
        var even = GridLayout.Arrange(Area, 3, Gap, ThreeColumns);
        Assert.True(even[1].Width < 627, $"the case needs a column narrower than the window: {even[1]}");

        var fitted = GridLayout.Arrange(Area, 3, Gap, ThreeColumns, [null, new TileMinimum(627, 0), null]);

        Assert.True(fitted[1].Width >= 627, $"the middle tile is still too narrow: {fitted[1]}");
        // The two neighbours give up the same amount, since they had the same to spare.
        Assert.InRange(fitted[0].Width - fitted[2].Width, -1, 1);
        // Exactly one gap between neighbours, and the outer edges where they were: nothing overlaps, nothing is lost.
        Assert.Equal(Gap, fitted[1].Left - fitted[0].Right);
        Assert.Equal(Gap, fitted[2].Left - fitted[1].Right);
        Assert.Equal(even[0].Left, fitted[0].Left);
        Assert.Equal(even[2].Right, fitted[2].Right);
    }

    // A neighbour with a minimum of its own gives only what it can spare, and the other one gives the rest.
    [Fact]
    public void A_neighbour_never_goes_below_its_own_minimum()
    {
        var fitted = GridLayout.Arrange(Area, 3, Gap, ThreeColumns, [new TileMinimum(480, 0), new TileMinimum(627, 0), null]);

        Assert.True(fitted[0].Width >= 480, $"the left tile was squeezed below its minimum: {fitted[0]}");
        Assert.True(fitted[1].Width >= 627, $"the middle tile is still too narrow: {fitted[1]}");
    }

    // Stacked tiles work the same way down a column.
    [Fact]
    public void A_row_too_short_for_its_window_is_made_taller()
    {
        var stacked = Tree(Down(C(L, 0.5), C(L, 0.5)));

        var fitted = GridLayout.Arrange(Area, 2, Gap, stacked, [new TileMinimum(0, 500), null]);

        Assert.True(fitted[0].Height >= 500, $"the top tile is still too short: {fitted[0]}");
        Assert.Equal(Gap, fitted[1].Top - fitted[0].Bottom);
    }

    // When the minimums cannot all fit, the shares stand: the overlap the window insists on, and nobody else squeezed.
    [Fact]
    public void Minimums_that_cannot_all_fit_leave_the_layout_as_it_was() =>
        Assert.Equal(
            GridLayout.Arrange(Area, 3, Gap, ThreeColumns),
            GridLayout.Arrange(Area, 3, Gap, ThreeColumns, [new TileMinimum(900, 0), new TileMinimum(900, 0), null]));

    [Fact]
    public void No_minimums_is_the_layout_exactly() =>
        Assert.Equal(
            GridLayout.Arrange(Area, 3, Gap, ThreeColumns),
            GridLayout.Arrange(Area, 3, Gap, ThreeColumns, [null, null, null]));

    // The opening holds no window, so the minimums skip it: the second minimum belongs to the window after it.
    [Fact]
    public void The_opening_takes_no_minimum()
    {
        var withOpening = ThreeColumns with { Opening = 1 };

        var fitted = GridLayout.Arrange(Area, 2, Gap, withOpening, [null, new TileMinimum(627, 0)]);

        Assert.Equal(2, fitted.Count);
        Assert.True(fitted[1].Width >= 627, $"the window after the opening did not get its minimum: {fitted[1]}");
    }
}
