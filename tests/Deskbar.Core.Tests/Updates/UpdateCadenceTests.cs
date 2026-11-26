using Deskbar.Core.Updates;
using Xunit;

namespace Deskbar.Core.Tests.Updates;

// Task 5e1e1a12: ordinary releases at most once a day, critical ones and opted-in copies at once.
// The marker these releases carry is written by scripts/ReleaseNotes.psm1 (Format-ImportanceNotes), and the
// strings here are copied from what that function writes, so a change to one side shows up here.
public class UpdateCadenceTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 26, 9, 0, 0, TimeSpan.FromHours(4));

    static ReleaseInfo Release(string version, string? criticalSince = null) =>
        new(version, "https://github.com/AppifySheets/Deskbar/releases/tag/v" + version, null, null, criticalSince);

    [Fact]
    public void An_ordinary_release_waits_until_a_day_has_passed_since_the_last_offer()
    {
        Assert.Equal(UpdateCadence.Verdict.Wait,
            UpdateCadence.Decide("2.6.30", Release("2.6.31"), everyRelease: false, Now.AddHours(-23), lastOfferedVersion: null, Now));
        Assert.Equal(UpdateCadence.Verdict.Offer,
            UpdateCadence.Decide("2.6.30", Release("2.6.31"), everyRelease: false, Now.AddHours(-24), lastOfferedVersion: null, Now));
    }

    [Fact]
    public void A_copy_that_asked_for_every_release_is_offered_at_once()
    {
        Assert.Equal(UpdateCadence.Verdict.Offer,
            UpdateCadence.Decide("2.6.30", Release("2.6.31"), everyRelease: true, Now.AddMinutes(-5), lastOfferedVersion: null, Now));
        Assert.Equal(UpdateCadence.Verdict.Offer,
            UpdateCadence.Decide("2.6.30", Release("2.6.31"), everyRelease: true, lastOffer: null, lastOfferedVersion: null, Now));
    }

    [Fact]
    public void A_critical_floor_above_the_running_version_skips_the_wait()
    {
        // The critical release came earlier and an ordinary one followed: the floor is carried forward.
        Assert.Equal(UpdateCadence.Verdict.Offer,
            UpdateCadence.Decide("2.6.30", Release("2.6.33", criticalSince: "2.6.32"), everyRelease: false, Now.AddMinutes(-5), lastOfferedVersion: null, Now));
        Assert.Equal(UpdateCadence.Verdict.Offer,
            UpdateCadence.Decide("2.6.30", Release("2.6.33", criticalSince: "2.6.32"), everyRelease: false, lastOffer: null, lastOfferedVersion: null, Now));
    }

    [Fact]
    public void A_critical_release_is_offered_once_and_later_releases_carrying_its_floor_wait_for_the_day()
    {
        // 2.6.32 was critical and was offered an hour ago; the user said not now. 2.6.33 and on copy the floor.
        Assert.Equal(UpdateCadence.Verdict.Wait,
            UpdateCadence.Decide("2.6.30", Release("2.6.33", criticalSince: "2.6.32"), everyRelease: false,
                Now.AddHours(-1), lastOfferedVersion: "2.6.32", Now));
        // An offer of something newer than the critical release covered it too.
        Assert.Equal(UpdateCadence.Verdict.Wait,
            UpdateCadence.Decide("2.6.30", Release("2.6.35", criticalSince: "2.6.32"), everyRelease: false,
                Now.AddHours(-1), lastOfferedVersion: "2.6.34", Now));
        // A NEWER critical release than the one offered is news again.
        Assert.Equal(UpdateCadence.Verdict.Offer,
            UpdateCadence.Decide("2.6.30", Release("2.6.36", criticalSince: "2.6.36"), everyRelease: false,
                Now.AddHours(-1), lastOfferedVersion: "2.6.32", Now));
        // An offer from before the critical release does not cover it.
        Assert.Equal(UpdateCadence.Verdict.Offer,
            UpdateCadence.Decide("2.6.30", Release("2.6.33", criticalSince: "2.6.32"), everyRelease: false,
                Now.AddHours(-1), lastOfferedVersion: "2.6.31", Now));
    }

    [Fact]
    public void A_critical_floor_the_running_version_already_has_is_ordinary()
    {
        Assert.Equal(UpdateCadence.Verdict.Wait,
            UpdateCadence.Decide("2.6.32", Release("2.6.33", criticalSince: "2.6.32"), everyRelease: false, Now.AddMinutes(-5), lastOfferedVersion: null, Now));
    }

    [Fact]
    public void A_copy_that_never_offered_starts_the_clock_instead_of_offering()
    {
        Assert.Equal(UpdateCadence.Verdict.StartClock,
            UpdateCadence.Decide("2.6.30", Release("2.6.31"), everyRelease: false, lastOffer: null, lastOfferedVersion: null, Now));
    }

    [Fact]
    public void A_clock_stamped_far_in_the_future_does_not_hold_updates_back()
    {
        Assert.Equal(UpdateCadence.Verdict.Offer,
            UpdateCadence.Decide("2.6.30", Release("2.6.31"), everyRelease: false, Now.AddDays(30), lastOfferedVersion: null, Now));
    }

    [Fact]
    public void The_release_body_marker_is_read_into_the_release()
    {
        var release = UpdateCheck.ReadRelease("""
            {
              "tag_name": "v2.6.33",
              "html_url": "https://github.com/AppifySheets/Deskbar/releases/tag/v2.6.33",
              "body": "<!-- deskbar-critical-since: 2.6.32 -->\nFixed a thing.",
              "assets": []
            }
            """).Value;
        Assert.Equal("2.6.32", release.CriticalSince);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("Just notes.", null)]
    [InlineData("<!-- deskbar-critical-since: 2.6.40 -->", "2.6.40")]
    [InlineData("**Critical update.** Deskbar offers it straight away rather than with the daily update.\n\n<!-- deskbar-critical-since: v2.6.40 -->\nnotes", "2.6.40")]
    [InlineData("<!--deskbar-critical-since:2.6.40-->", "2.6.40")]
    [InlineData("<!-- deskbar-critical-since: soon -->", null)]
    [InlineData("deskbar-critical-since: 2.6.40", null)]
    public void The_marker_is_found_only_in_its_own_shape(string? body, string? expected) =>
        Assert.Equal(expected, UpdateCheck.CriticalSinceIn(body));
}
