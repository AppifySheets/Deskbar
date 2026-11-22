using Deskbar.Core.Licensing;

namespace Deskbar.Core.Tests.Licensing;

// The daily Deskbar Pro reminder's schedule: never for a buyer, never in the first two weeks, at most once a
// calendar day, and "Not now" holds until tomorrow. ProReminder's header has the model behind it.
public class ProReminderTests
{
    static readonly DateOnly Start = new(2026, 11, 22);
    static DateOnly Day(int n) => Start.AddDays(n);

    [Fact] public void Not_before_the_start_is_stamped() => Assert.False(ProReminder.Due(false, null, null, Day(40)));
    [Fact] public void Quiet_for_the_first_two_weeks() => Assert.False(ProReminder.Due(false, Start, null, Day(13)));
    [Fact] public void Due_on_the_fifteenth_day() => Assert.True(ProReminder.Due(false, Start, null, Day(14)));
    [Fact] public void Never_for_pro() => Assert.False(ProReminder.Due(true, Start, null, Day(30)));
    [Fact] public void Not_now_holds_for_the_rest_of_the_day() => Assert.False(ProReminder.Due(false, Start, Day(20), Day(20)));
    [Fact] public void Back_the_next_day() => Assert.True(ProReminder.Due(false, Start, Day(20), Day(21)));

    // The clock set back after a dismissal: the dismissal is "in the future", and still counts as today.
    [Fact] public void A_dismissal_dated_after_today_still_holds() => Assert.False(ProReminder.Due(false, Start, Day(25), Day(20)));

    // A start date in the future (the clock was ahead when it was stamped) keeps the reminder quiet.
    [Fact] public void A_start_in_the_future_is_quiet() => Assert.False(ProReminder.Due(false, Day(5), null, Day(0)));
}
