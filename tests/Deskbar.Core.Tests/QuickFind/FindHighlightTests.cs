using Deskbar.Core.QuickFind;

namespace Deskbar.Core.Tests.QuickFind;

// Which row is highlighted, by the result's KEY and never its position: tab readings arrive in the background
// while the box is open, and the row Petre is about to press Enter on must not change under him (the spec's
// anti-goal; the plan's Review Focus, item 4).
public class FindHighlightTests
{
    [Fact]
    public void Highlight_follows_the_same_result_when_results_refresh() =>
        Assert.Equal("b", FindHighlight.Keep(["x", "b", "c"], "b"));

    [Fact]
    public void Highlight_falls_to_the_first_row_when_its_result_is_gone() =>
        Assert.Equal("x", FindHighlight.Keep(["x", "c"], "b"));

    [Fact]
    public void No_rows_means_no_highlight() =>
        Assert.Null(FindHighlight.Keep([], "b"));

    [Fact]
    public void Nothing_highlighted_yet_takes_the_first_row() =>
        Assert.Equal("a", FindHighlight.Keep(["a", "b"], null));

    [Theory]
    [InlineData("a", 1, "b")]
    [InlineData("b", -1, "a")]
    [InlineData("c", 1, "c")]   // stops at the last row rather than wrapping to the top
    [InlineData("a", -1, "a")]  // and at the first
    public void Arrows_move_one_row_and_stop_at_the_ends(string from, int step, string to) =>
        Assert.Equal(to, FindHighlight.Move(["a", "b", "c"], from, step));
}
