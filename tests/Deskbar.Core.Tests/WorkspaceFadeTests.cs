using Deskbar.Core.Overview;

namespace Deskbar.Core.Tests;

// How faded a workspace's row is drawn (WorkspaceFade). Petre: "let's fade windows not based on individual age,
// but the age of the workspace", and "recency has to be gradual, not 8 hours". One age per workspace, set by the
// window in it you used most recently, put through the aging icons' own curve.
public class WorkspaceFadeTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    // The newest stamp decides: one window used five minutes ago keeps the whole workspace fresh, however
    // old its other windows are, and a window with no stamp says nothing.
    [Fact]
    public void The_newest_window_sets_the_age() =>
        Assert.Equal(0, WorkspaceFade.Of([Now.AddDays(-3), Now.AddMinutes(-5), null], Now));

    [Fact]
    public void A_workspace_untouched_for_two_hours_is_fully_faded() =>
        Assert.Equal(1, WorkspaceFade.Of([Now.AddHours(-2)], Now));

    // Nothing to have used, so nothing recent: an empty workspace sits back with the idle ones.
    [Fact]
    public void A_workspace_with_no_windows_is_fully_faded() =>
        Assert.Equal(1, WorkspaceFade.Of([], Now));

    [Fact]
    public void Unstamped_windows_count_as_no_windows() =>
        Assert.Equal(1, WorkspaceFade.Of([null, null], Now));

    // A clock moved back, or a stamp restored from last-used.json that is later than now: fresh, never a
    // negative fade that would brighten a row past its own colour.
    [Fact]
    public void A_stamp_after_now_is_fresh_not_negative() =>
        Assert.Equal(0, WorkspaceFade.Of([Now.AddMinutes(10)], Now));

    // Petre, on the first curve (15 minutes to 8 hours): "it should decay much faster". Half an hour in, already a
    // third of the way; still gradual (the ^0.7 easing), with no line where a row turns idle.
    [Fact]
    public void Half_an_hour_is_a_third_of_the_way() =>
        Assert.InRange(WorkspaceFade.Of([Now.AddMinutes(-30)], Now), 0.25, 0.45);

    // Five minutes away is still fresh: a quick look at another workspace does not dim the one you came from.
    [Fact]
    public void Five_minutes_is_still_fresh() =>
        Assert.Equal(0, WorkspaceFade.Of([Now.AddMinutes(-5)], Now));

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.5, 8)]
    [InlineData(1.0, 16)]
    [InlineData(0.03, 0)]
    [InlineData(0.97, 16)]
    public void Steps_round_to_sixteenths(double fade, int step) => Assert.Equal(step, WorkspaceFade.Step(fade));
}
