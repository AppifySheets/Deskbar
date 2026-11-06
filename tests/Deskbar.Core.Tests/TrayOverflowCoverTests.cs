using Deskbar.Core.Geometry;

namespace Deskbar.Core.Tests;

// The decision that puts the bar under the tray's hidden-icons flyout (FloatingBar.StepBelowOverflow).
public class TrayOverflowCoverTests
{
    // A bar docked on the right edge of a 3840 wide screen, and a flyout over its middle: the owner's case.
    static readonly FullScreenCover.Rect Bar = new(3500, 0, 3840, 2160);
    static readonly FullScreenCover.Rect OverBar = new(3700, 1500, 4100, 2100);

    [Fact]
    public void Overflow_in_front_and_over_the_bar_puts_the_bar_below_it() =>
        Assert.True(TrayOverflowCover.ShouldStepBelow(true, true, OverBar, Bar));

    [Fact]
    public void Overflow_that_does_not_reach_the_bar_leaves_the_bar_on_top() =>
        Assert.False(TrayOverflowCover.ShouldStepBelow(true, true, new(4000, 1500, 4400, 2100), Bar));

    [Fact]
    public void Overflow_that_only_touches_the_bars_edge_leaves_the_bar_on_top() =>
        Assert.False(TrayOverflowCover.ShouldStepBelow(true, true, new(3840, 1500, 4240, 2100), Bar));

    // A screen to the left of the main one has negative x: the bar docked on its right edge, and a flyout over that edge.
    [Fact]
    public void Overflow_over_a_bar_on_a_screen_at_negative_x_puts_the_bar_below_it() =>
        Assert.True(TrayOverflowCover.ShouldStepBelow(true, true, new(-200, 1500, 200, 2100), new(-400, 0, 0, 2160)));

    // A flyout that covers the whole bar, edges and all, still counts as over it.
    [Fact]
    public void Overflow_that_fully_contains_the_bar_puts_the_bar_below_it() =>
        Assert.True(TrayOverflowCover.ShouldStepBelow(true, true, new(3000, 0, 4500, 2160), Bar));

    [Fact]
    public void Overflow_no_longer_in_front_leaves_the_bar_on_top() =>
        Assert.False(TrayOverflowCover.ShouldStepBelow(false, true, OverBar, Bar));

    [Fact]
    public void Overflow_that_is_no_longer_visible_leaves_the_bar_on_top() =>
        Assert.False(TrayOverflowCover.ShouldStepBelow(true, false, OverBar, Bar));
}
