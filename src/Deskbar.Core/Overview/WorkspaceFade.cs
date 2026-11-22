namespace Deskbar.Core.Overview;

// HOW FADED A WORKSPACE'S ROW IS. Petre: "i get lost in so many workspaces", then, from the mockups, "b gradual":
// the workspaces you have been working in keep their colour, and the rest sit back. Then "let's fade windows not
// based on individual age, but the age of the workspace", "recency has to be gradual, not 8 hours", and "more time
// goes by, less pronounced a bar and all its icons become". Design: docs/superpowers/specs/2026-11-22-recent-workspaces-design.md.
//
// ONE AGE PER WORKSPACE: the time since you last used ANY of its windows, so the window you used most recently
// sets it. That is the answer to "have I been working here?": one window used five minutes ago means yes, however
// many others in it have sat untouched since yesterday. The stamps are the ones WorkspaceManager.LastUsed already
// keeps for the aging icons (stamped whenever a window comes to the front, never merely for opening, and saved
// across restarts in last-used.json), so there is no second clock to keep in step.
//
// A workspace with no windows, or none with a stamp, is fully faded: there is nothing in it to have used.
//
// The CURVE is the one the aging icons have used since they shipped (FloatingBar.Aging.cs, which now asks this
// class): full colour up to 5 minutes, then a fade eased by ^0.7 so most of it happens early, and fully faded at
// 2 hours. The first guess was 15 minutes to 8 hours; on the bar that left every row looking fresh all afternoon,
// and Petre: "it should decay much faster" (2026-11-22). Half an hour away is now a third of the way down.
//
// STEPS: the bar draws a row's rail, tint and name from the fade rounded to one of 16 steps. Sixteen is finer than
// the eye can tell apart on a 3 DIP rail, it keeps the cache of faded brushes small (16 per colour), and it lets
// the bar redraw only when a workspace's step has actually moved (16 times over the 2 hours a row takes to fade, so
// every seven minutes or so per fading workspace), instead of every minute.
public static class WorkspaceFade
{
    public static readonly TimeSpan Starts = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Full = TimeSpan.FromHours(2);
    public const int Steps = 16;

    // 0 for fresh, rising to 1 when fully faded. A negative span (a clock moved back, a restored stamp later than
    // now) falls under Starts and is fresh, never a negative fade.
    public static double Curve(TimeSpan unused) =>
        unused <= Starts ? 0 : Math.Pow(Math.Min(1, (unused - Starts) / (Full - Starts)), 0.7);

    // The fade of a workspace whose windows were last used at these times (null: a window with no stamp).
    public static double Of(IEnumerable<DateTimeOffset?> windowsLastUsed, DateTimeOffset now) =>
        // Max over nullables skips the nulls and is null when nothing is left.
        windowsLastUsed.Max() is { } newest ? Curve(now - newest) : 1;

    // 0..Steps, the step the bar draws a fade at.
    public static int Step(double fade) => (int)Math.Round(Math.Clamp(fade, 0, 1) * Steps);
}
