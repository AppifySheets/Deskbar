using Deskbar.Core.Domain;
using Deskbar.Core.Tiling;

namespace Deskbar.Core.Tests.Tiling;

// The tiling grid's arithmetic: how many columns, how many tiles per column, where the gaps fall and
// where the integer remainders go. Pure geometry, no Windows types.
public class GridLayoutTests
{
    static readonly WindowRect Area = new(0, 0, 1000, 600);

    // The tiles grouped by their Left edge, in left-to-right order, as a count per column.
    static IReadOnlyList<int> ColumnSizes(IEnumerable<WindowRect> tiles) =>
        tiles.GroupBy(r => r.Left).OrderBy(g => g.Key).Select(g => g.Count()).ToList();

    [Fact]
    public void One_window_fills_the_area_inside_the_gap() =>
        Assert.Equal([new WindowRect(8, 8, 992, 592)], GridLayout.Arrange(Area, 1, 8));

    [Fact]
    public void Three_windows_put_one_tall_tile_left_and_two_stacked_right()
    {
        var r = GridLayout.Arrange(Area, 3, 0);
        Assert.Equal(new WindowRect(0, 0, 500, 600), r[0]);
        Assert.Equal(new WindowRect(500, 0, 1000, 300), r[1]);
        Assert.Equal(new WindowRect(500, 300, 1000, 600), r[2]);
    }

    [Fact]
    public void Five_windows_fill_columns_one_two_two() =>
        Assert.Equal([1, 2, 2], ColumnSizes(GridLayout.Arrange(Area, 5, 0)));

    // 1000/3 and 600/2 do not divide evenly, so the last column and last row take the remainder.
    [Fact]
    public void Remainders_go_to_the_last_column_and_row() =>
        Assert.Equal(1000, GridLayout.Arrange(Area, 5, 0).Max(r => r.Right));

    [Fact]
    public void Gaps_between_tiles_equal_the_gap()
    {
        var r = GridLayout.Arrange(Area, 2, 8);
        Assert.Equal(8, r[1].Left - r[0].Right);
    }

    [Fact]
    public void No_windows_no_tiles() =>
        Assert.Empty(GridLayout.Arrange(Area, 0, 8));
}
