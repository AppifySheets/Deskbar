using Deskbar.Core.Domain;
using Deskbar.Core.Geometry;

namespace Deskbar.Core.Tests;

// #150. Petre: "when changing screen resolution, when connecting from a laptop, floating window may
// not show... maybe have its own place for each layout, as windows already does i think."
//
// The key is what makes "each layout" mean anything, so what is tested here is the two ways it could
// be wrong in opposite directions: calling one layout two things (the bar forgets where it sat) and
// calling two layouts one thing (the bar restores a position from somebody else's desk).
public class MonitorLayoutKeyTests
{
    static MonitorBounds Screen(int left, int top, int right, int bottom) => new(left, top, right, bottom);

    static readonly MonitorBounds Desk = Screen(0, 0, 2560, 1440);
    static readonly MonitorBounds Second = Screen(2560, 0, 4480, 1080);

    // The one that matters most, because EnumDisplayMonitors makes no promise about order: the same
    // two screens handed back the other way round have to be the same desk.
    [Fact]
    public void Enumeration_order_does_not_change_the_key() =>
        Assert.Equal(
            MonitorLayoutKey.Of([Desk, Second]),
            MonitorLayoutKey.Of([Second, Desk]));

    [Fact]
    public void Two_screens_are_a_different_layout_from_one() =>
        Assert.NotEqual(
            MonitorLayoutKey.Of([Desk]),
            MonitorLayoutKey.Of([Desk, Second]));

    // A resolution change moves every edge the bar could be parked against, so the position chosen at
    // 4K is not the position wanted at 1080p. Same screen, different layout, on purpose.
    [Fact]
    public void A_resolution_change_is_a_different_layout() =>
        Assert.NotEqual(
            MonitorLayoutKey.Of([Screen(0, 0, 3840, 2160)]),
            MonitorLayoutKey.Of([Screen(0, 0, 1920, 1080)]));

    // The laptop-on-the-left arrangement Petre actually has: negative coordinates must survive into
    // the key rather than being lost to formatting.
    [Fact]
    public void A_screen_left_of_the_primary_one_keeps_its_negative_origin() =>
        Assert.Contains("-1920,0", MonitorLayoutKey.Of([Screen(-1920, 0, 0, 1080), Desk]));

    // Moving a screen in the arrangement is a different layout too, and this is the case sorting
    // could quietly swallow: the same two rectangles' SIZES with different origins.
    [Fact]
    public void Rearranging_the_same_two_screens_is_a_different_layout() =>
        Assert.NotEqual(
            MonitorLayoutKey.Of([Screen(0, 0, 1920, 1080), Screen(1920, 0, 3840, 1080)]),
            MonitorLayoutKey.Of([Screen(0, 0, 1920, 1080), Screen(-1920, 0, 0, 1080)]));

    [Fact]
    public void The_same_layout_twice_gives_the_same_key() =>
        Assert.Equal(MonitorLayoutKey.Of([Desk, Second]), MonitorLayoutKey.Of([Desk, Second]));

    // No monitors and no answer are both Unknown, which callers read as "do not remember, and do not
    // look up". Storing under an empty key would let two different unknown layouts share one entry
    // and hand each other's position back.
    [Fact]
    public void No_monitors_is_unknown() =>
        Assert.Equal(MonitorLayoutKey.Unknown, MonitorLayoutKey.Of([]));

    [Fact]
    public void Null_is_unknown() =>
        Assert.Equal(MonitorLayoutKey.Unknown, MonitorLayoutKey.Of(null));

    [Fact]
    public void Unknown_is_not_known() => Assert.False(MonitorLayoutKey.IsKnown(MonitorLayoutKey.Unknown));

    [Fact]
    public void A_real_layout_is_known() => Assert.True(MonitorLayoutKey.IsKnown(MonitorLayoutKey.Of([Desk])));

    // --- the same desk under a different key -------------------------------------------------------
    //
    // Petre's desk, measured from his state.json on 2026-10-25: one arrangement, a 4K screen to the left
    // of a 4K primary, saved under four keys because the left screen came back 229, 232, 246 and 248 px
    // down on different reconnects. The exact key missed every time, so the bar never found the spot it
    // had been parked in at that desk.

    static readonly MonitorBounds Primary4K = Screen(0, 0, 3840, 2160);

    [Fact]
    public void A_screen_that_came_back_a_few_pixels_lower_is_the_same_desk() =>
        Assert.True(MonitorLayoutKey.SameDesk(
            MonitorLayoutKey.Of([Screen(-3840, 229, 0, 2389), Primary4K]),
            MonitorLayoutKey.Of([Screen(-3840, 246, 0, 2406), Primary4K])));

    [Fact]
    public void The_same_key_is_the_same_desk() =>
        Assert.True(MonitorLayoutKey.SameDesk(MonitorLayoutKey.Of([Desk, Second]), MonitorLayoutKey.Of([Desk, Second])));

    // The other direction, and the reason the side is part of it: the screen on the other side of the
    // primary puts every edge the bar could dock against somewhere else.
    [Fact]
    public void The_same_screens_on_the_other_side_are_another_desk() =>
        Assert.False(MonitorLayoutKey.SameDesk(
            MonitorLayoutKey.Of([Screen(-3840, 0, 0, 2160), Primary4K]),
            MonitorLayoutKey.Of([Primary4K, Screen(3840, 0, 7680, 2160)])));

    [Fact]
    public void A_screen_above_is_not_a_screen_beside() =>
        Assert.False(MonitorLayoutKey.SameDesk(
            MonitorLayoutKey.Of([Screen(0, -2160, 3840, 0), Primary4K]),
            MonitorLayoutKey.Of([Screen(-3840, 0, 0, 2160), Primary4K])));

    // An RDP session that keeps the arrangement but not the resolution is still another desk.
    [Fact]
    public void Another_resolution_is_another_desk() =>
        Assert.False(MonitorLayoutKey.SameDesk(
            MonitorLayoutKey.Of([Primary4K, Screen(3840, 0, 7680, 2160)]),
            MonitorLayoutKey.Of([Primary4K, Screen(3840, 0, 5760, 1080)])));

    [Fact]
    public void Another_number_of_screens_is_another_desk() =>
        Assert.False(MonitorLayoutKey.SameDesk(MonitorLayoutKey.Of([Primary4K]), MonitorLayoutKey.Of([Screen(-3840, 0, 0, 2160), Primary4K])));

    [Fact]
    public void Unknown_is_never_the_same_desk_as_anything() =>
        Assert.False(MonitorLayoutKey.SameDesk(MonitorLayoutKey.Unknown, MonitorLayoutKey.Unknown));

    // A key nobody could have written (a hand-edited state.json) only matches itself.
    [Fact]
    public void An_unreadable_key_only_matches_itself()
    {
        Assert.True(MonitorLayoutKey.SameDesk("garbage", "garbage"));
        Assert.False(MonitorLayoutKey.SameDesk("garbage", MonitorLayoutKey.Of([Primary4K])));
    }
}
