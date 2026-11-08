using CSharpFunctionalExtensions;
using Deskbar.Core.Tiling;

namespace Deskbar.Core.Tests.Tiling;

// TileSizes: the fractions a screen's columns and each column's rows take, and the rules that
// reshape them when the window count changes. Fractions belong to PLACES (column 0 is leftmost,
// row 0 is top), never to windows.
public class TileSizesTests
{
    // Compares two sequences of doubles within 1e-9.
    static void AssertClose(IReadOnlyList<double> expected, IReadOnlyList<double> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        expected.Select((e, i) => (e, i)).ToList().ForEach(p => Assert.InRange(actual[p.i], p.e - 1e-9, p.e + 1e-9));
    }

    // Compares whole TileSizes values, list by list, within 1e-9.
    static void AssertClose(TileSizes expected, TileSizes actual)
    {
        AssertClose(expected.Columns, actual.Columns);
        Assert.Equal(expected.Rows.Count, actual.Rows.Count);
        expected.Rows.Select((r, i) => (r, i)).ToList().ForEach(p => AssertClose(p.r, actual.Rows[p.i]));
    }

    [Fact]
    public void Equal_sizes_for_one_then_two_rows() =>
        Assert.Equal(new TileSizes([0.5, 0.5], [[1.0], [0.5, 0.5]]), TileSizes.Equal([1, 2]));

    [Fact]
    public void A_joining_row_takes_an_equal_share() =>          // [0.7,0.3] with one row each, a 3rd window joins column 1
        Assert.Equal(new TileSizes([0.7, 0.3], [[1.0], [0.5, 0.5]]),
            new TileSizes([0.7, 0.3], [[1.0], [1.0]]).Fit([1, 2], Maybe<(int, int)>.None));

    [Fact]
    public void A_leaving_row_gives_its_share_to_the_rest_in_proportion() =>
        AssertClose(new TileSizes([1.0], [[0.2 / 0.7, 0.5 / 0.7]]),
            new TileSizes([1.0], [[0.2, 0.3, 0.5]]).Fit([2], (0, 1)));

    [Fact]
    public void A_new_column_takes_one_nth_and_appears_on_the_right() =>   // 4 windows [2,2] become 5 [1,2,2]
        AssertClose([0.7 * 2 / 3, 0.3 * 2 / 3, 1.0 / 3],
            new TileSizes([0.7, 0.3], [[0.5, 0.5], [0.5, 0.5]]).Fit([1, 2, 2], Maybe<(int, int)>.None).Columns);

    [Fact]
    public void A_column_that_goes_away_is_the_rightmost() =>
        AssertClose([0.4 / 0.6, 0.2 / 0.6],
            new TileSizes([0.4, 0.2, 0.4], [[1.0], [1.0], [1.0]]).Fit([1, 1], Maybe<(int, int)>.None).Columns);

    [Fact]
    public void Rows_that_drop_with_no_known_leaver_drop_from_the_bottom() =>
        AssertClose([1.0], new TileSizes([1.0], [[0.25, 0.75]]).Fit([1], Maybe<(int, int)>.None).Rows[0]);

    [Fact]
    public void Equality_compares_list_contents() =>
        Assert.Equal(new TileSizes([0.5, 0.5], [[1.0], [0.5, 0.5]]), new TileSizes([0.5, 0.5], [[1.0], [0.5, 0.5]]));

    [Fact]
    public void Equal_sizes_report_IsEqual_and_uneven_ones_do_not()
    {
        Assert.True(TileSizes.Equal([1, 2]).IsEqual);
        Assert.False(new TileSizes([0.7, 0.3], [[1.0], [1.0]]).IsEqual);
        Assert.False(new TileSizes([0.5, 0.5], [[1.0], [0.25, 0.75]]).IsEqual);
    }
}
