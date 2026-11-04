using Deskbar.Core.QuickFind;

namespace Deskbar.Core.Tests.QuickFind;

// Quick find's matching (docs/superpowers/specs/2026-11-04-quick-find-design.md, "Matching"). Petre: "inv 10
// should find that", of a tab titled "Invoice 2026-10": every typed piece must appear, in any order and case.
public class FindQueryTests
{
    [Theory]
    [InlineData("inv 10")]
    [InlineData("10 inv")]
    [InlineData("INV 10")]
    public void Every_piece_must_appear_in_any_order_and_case(string typed) =>
        Assert.True(FindQuery.Parse(typed).Matches("Invoice 2026-10 - Microsoft Edge"));

    [Fact]
    public void A_missing_piece_fails_the_match() =>
        Assert.False(FindQuery.Parse("inv 11").Matches("Invoice 2026-10"));

    [Fact]
    public void Spaces_alone_are_an_empty_query()
    {
        Assert.True(FindQuery.Parse("   ").IsEmpty);
        Assert.Equal(["inv", "10"], FindQuery.Parse("  inv   10 ").Pieces);
    }

    [Fact]
    public void An_empty_query_matches_nothing() =>
        Assert.False(FindQuery.Parse("").Matches("anything"));

    [Fact]
    public void Prefix_is_judged_on_the_first_piece_only() =>
        Assert.True(FindQuery.Parse("inv 10").StartsWithFirstPiece("Invoice 2026-10"));

    [Fact]
    public void A_title_containing_but_not_starting_with_the_first_piece_is_no_prefix() =>
        Assert.False(FindQuery.Parse("inv").StartsWithFirstPiece("My invoices"));
}
