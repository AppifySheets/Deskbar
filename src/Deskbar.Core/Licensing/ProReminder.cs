namespace Deskbar.Core.Licensing;

// Whether the bar shows its one-line Deskbar Pro reminder today.
//
// The model (decided with Petre on 2026-11-22): Deskbar is free for personal use, every feature works in
// every copy, and Deskbar Pro is a one-time US$18.99 add-on in the Microsoft Store "with priority support",
// asked of anyone who uses Deskbar for work. A copy without Pro gets a gentle nudge: one line at the bottom
// of the bar, at most once a calendar day, never in the first two weeks. "Not now" puts it away until the next
// day. Corporate licensing (invoices, seats) is parked in Deskbar #384.
//
// Pure, so every rule is a unit test in Deskbar.Core.Tests; the App feeds it the Pro verdict (Entitlement),
// the two dates from AppState and today's local date.
public static class ProReminder
{
    // The quiet start: the day the reminder was first possible plus this many days, so it can appear from the
    // fifteenth day on. Counted from ProReminderSince, which the app stamps on the first start of a build
    // that has the reminder, so a copy that was already installed gets the same two weeks as a new one.
    public const int QuietDays = 14;

    // The rules, all of which must hold:
    //   - not Pro (a buyer never sees it; a Store licence that could not be read counts as Pro, see
    //     ProEntitlement);
    //   - the start date is known (it is null only before the first stamp, and then nothing is shown);
    //   - at least QuietDays have passed since it;
    //   - not dismissed today. A dismissal dated AFTER today (the clock was set back) also counts as today,
    //     so changing the clock cannot make the line come back sooner, and setting it forward only brings
    //     tomorrow's line early.
    public static bool Due(bool isPro, DateOnly? since, DateOnly? dismissedOn, DateOnly today) =>
        !isPro
        && since is { } start
        && today.DayNumber - start.DayNumber >= QuietDays
        && (dismissedOn is not { } dismissed || dismissed < today);
}
