using Deskbar.Core.Licensing;
using Deskbar.Core.Time;

namespace Deskbar.Core.Tests.Licensing;

// The daily Deskbar Pro reminder's schedule: never for a buyer, never in the first two weeks, never before enough
// active use, at most once a calendar day, and "Not now" holds until tomorrow. ProReminder's header has the model.
public class ProReminderTests
{
    static readonly DateOnly Start = new(2026, 11, 22);
    static DateOnly Day(int n) => Start.AddDays(n);
    static readonly TimeSpan Enough = ProReminder.QuietHours;
    static readonly TimeSpan Little = ProReminder.QuietHours - TimeSpan.FromMinutes(1);

    [Fact] public void Not_before_the_start_is_stamped() => Assert.False(ProReminder.Due(false, null, null, Day(40), Enough));
    [Fact] public void Quiet_for_the_first_two_weeks() => Assert.False(ProReminder.Due(false, Start, null, Day(13), Enough));
    [Fact] public void Due_on_the_fifteenth_day_with_enough_use() => Assert.True(ProReminder.Due(false, Start, null, Day(14), Enough));
    [Fact] public void Never_for_pro() => Assert.False(ProReminder.Due(true, Start, null, Day(30), Enough));
    [Fact] public void Not_now_holds_for_the_rest_of_the_day() => Assert.False(ProReminder.Due(false, Start, Day(20), Day(20), Enough));
    [Fact] public void Back_the_next_day() => Assert.True(ProReminder.Due(false, Start, Day(20), Day(21), Enough));

    // Two weeks alone are not enough: someone who installed it and barely used it is not asked to pay.
    [Fact] public void Quiet_until_enough_active_use_however_long_ago() => Assert.False(ProReminder.Due(false, Start, null, Day(200), Little));

    // The clock set back after a dismissal: the dismissal is "in the future", and still counts as today.
    [Fact] public void A_dismissal_dated_after_today_still_holds() => Assert.False(ProReminder.Due(false, Start, Day(25), Day(20), Enough));

    // A start date in the future (the clock was ahead when it was stamped) keeps the reminder quiet.
    [Fact] public void A_start_in_the_future_is_quiet() => Assert.False(ProReminder.Due(false, Day(5), null, Day(0), Enough));

    // The use is counted from the start date on, across every workspace; time before it does not count, so an
    // existing copy with months of history still gets its quiet start.
    [Fact]
    public void Active_time_counts_from_the_start_across_workspaces()
    {
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var time = WorkspaceTime.Empty
            .Credit(a, Day(-30), TimeSpan.FromHours(100))
            .Credit(a, Day(1), TimeSpan.FromHours(3))
            .Credit(b, Day(2), TimeSpan.FromHours(4));
        Assert.Equal(TimeSpan.FromHours(7), time.TotalSince(Start));
    }
}
