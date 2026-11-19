using Deskbar.Core.Domain;
using Deskbar.Core.Geometry;

namespace Deskbar.Core.Tests;

// The screen strips: the current workspace's titled window list in a 32 DIP strip along the top of
// every screen. These are the pure rules behind it. Display changes arrive in bursts with transient
// states (a moment with no usable screen among them), so the awkward cases are about what the plan
// does NOT change, and every one is testable without a second monitor.
public class StripPlanTests
{
    static readonly MonitorBounds Desk = new(0, 0, 2560, 1440);
    static readonly MonitorBounds Second = new(2560, 0, 4480, 1080);

    static IReadOnlyList<MonitorBounds> Of(params MonitorBounds[] screens) => screens;

    [Fact]
    public void Not_wanted_removes_every_strip()
    {
        var changes = StripPlan.Reconcile(false, Of(Desk, Second), Of(Desk, Second));

        Assert.Empty(changes.Add);
        Assert.Equal(Of(Desk, Second), changes.Remove);
    }

    [Fact]
    public void Wanted_adds_one_per_screen()
    {
        var changes = StripPlan.Reconcile(true, Of(Desk, Second), Of());

        Assert.Equal(Of(Desk, Second), changes.Add);
        Assert.Empty(changes.Remove);
    }

    [Fact]
    public void A_screen_that_left_loses_its_strip()
    {
        var changes = StripPlan.Reconcile(true, Of(Desk), Of(Desk, Second));

        Assert.Empty(changes.Add);
        Assert.Equal(Of(Second), changes.Remove);
    }

    // Identity is the rectangle, so a resolution change is a different screen: the old strip goes
    // and a new one is made at the new size.
    [Fact]
    public void A_screen_that_changed_size_is_a_remove_and_an_add()
    {
        var smaller = new MonitorBounds(0, 0, 1920, 1080);

        var changes = StripPlan.Reconcile(true, Of(smaller), Of(Desk));

        Assert.Equal(Of(smaller), changes.Add);
        Assert.Equal(Of(Desk), changes.Remove);
    }

    // The transient no-screen moment during a display change must not tear every strip down.
    [Fact]
    public void An_empty_screen_list_changes_nothing()
    {
        var changes = StripPlan.Reconcile(true, Of(), Of(Desk, Second));

        Assert.Empty(changes.Add);
        Assert.Empty(changes.Remove);
    }

    [Fact]
    public void Nothing_changes_when_screens_match()
    {
        var changes = StripPlan.Reconcile(true, Of(Desk, Second), Of(Second, Desk));

        Assert.Empty(changes.Add);
        Assert.Empty(changes.Remove);
    }

    [Theory]
    [InlineData(1000, 3, 150)]
    [InlineData(300, 3, 100)]
    [InlineData(300, 20, 30)]
    [InlineData(1000, 0, 150)]
    public void Tabs_share_the_room_equally_between_the_icon_only_floor_and_the_cap(double room, int tabs, double expected) =>
        Assert.Equal(expected, StripPlan.TabWidth(room, tabs));

    [Fact]
    public void A_title_shows_only_when_there_is_room_beyond_the_icon() =>
        Assert.Multiple(
            () => Assert.False(StripPlan.ShowsTitle(StripPlan.TabIconOnly)),
            () => Assert.False(StripPlan.ShowsTitle(StripPlan.TabIconOnly + 12)),
            () => Assert.True(StripPlan.ShowsTitle(StripPlan.TabIconOnly + 13)),
            () => Assert.True(StripPlan.ShowsTitle(StripPlan.TabMax)));
}
