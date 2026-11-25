using Deskbar.Core.Domain;

namespace Deskbar.Core.Tests;

// The docked bar's soft inner edge is a fixed few DIP (Petre, 2026-11-25: "easing in from the edge in terms of
// transparency should be sharper, so text is unaffected"), turned into a share of the bar's width for Frost.Ease and
// the veil's gradient.
public class FrostEdgeTests
{
    // The ease stays inside the bar's 4 DIP padding, where no row content stands.
    [Fact]
    public void The_ease_is_no_wider_than_the_bars_padding() => Assert.Equal(4, FrostEdge.Width);

    // A 210 DIP bar at 100%: 4 of 210.
    [Fact]
    public void At_one_hundred_percent_the_share_is_the_ease_over_the_width() =>
        Assert.Equal(4.0 / 210, FrostEdge.ShareOf(210, 1.0), 9);

    // The same bar at 150% is 315 screen pixels wide and the ease 6 of them: the same share, the same DIP.
    [Fact]
    public void The_share_is_the_same_at_any_scale() =>
        Assert.Equal(FrostEdge.ShareOf(210, 1.0), FrostEdge.ShareOf(315, 1.5), 9);

    // Far narrower than the fifth it replaced, which spread over about 40 DIP of a 210 DIP bar.
    [Fact]
    public void The_ease_is_a_small_fraction_of_a_normal_bar() =>
        Assert.InRange(FrostEdge.ShareOf(210, 1.0), 0.01, 0.03);

    [Fact]
    public void A_bar_narrower_than_the_ease_eases_over_its_whole_width() =>
        Assert.Equal(1, FrostEdge.ShareOf(3, 1.0));

    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(-10, 1.0)]
    [InlineData(210, 0)]
    [InlineData(double.NaN, 1.0)]
    [InlineData(210, double.PositiveInfinity)]
    public void A_nonsense_width_or_scale_gives_no_ease(double width, double scale) =>
        Assert.Equal(0, FrostEdge.ShareOf(width, scale));
}
