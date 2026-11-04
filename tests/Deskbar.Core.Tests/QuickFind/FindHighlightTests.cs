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

    // A tab's key carries its title and position, which change under a live page (an unread counter, a tab
    // opened to its left). The highlight follows it to the same window's tab with the same title, then to the
    // same window's tab at the same position, before falling to the first row (final review, finding 5).
    [Fact]
    public void A_tab_that_moved_keeps_the_highlight_by_its_title() =>
        Assert.Equal("t:1F:3:Inbox", FindHighlight.Keep(["w:9", "t:1F:3:Inbox"], "t:1F:2:Inbox"));

    [Fact]
    public void A_tab_whose_title_changed_keeps_the_highlight_by_its_place() =>
        Assert.Equal("t:1F:2:Inbox (3)", FindHighlight.Keep(["w:9", "t:1F:2:Inbox (3)"], "t:1F:2:Inbox"));

    [Fact]
    public void A_title_with_colons_still_matches_its_tab() =>
        Assert.Equal("t:1F:4:a: b", FindHighlight.Keep(["w:9", "t:1F:4:a: b"], "t:1F:1:a: b"));

    [Fact]
    public void Another_windows_tab_does_not_take_the_highlight() =>
        Assert.Equal("w:9", FindHighlight.Keep(["w:9", "t:2A:2:Inbox"], "t:1F:2:Inbox"));

    [Theory]
    [InlineData("a", 1, "b")]
    [InlineData("b", -1, "a")]
    [InlineData("c", 1, "c")]   // stops at the last row rather than wrapping to the top
    [InlineData("a", -1, "a")]  // and at the first
    public void Arrows_move_one_row_and_stop_at_the_ends(string from, int step, string to) =>
        Assert.Equal(to, FindHighlight.Move(["a", "b", "c"], from, step));
}
