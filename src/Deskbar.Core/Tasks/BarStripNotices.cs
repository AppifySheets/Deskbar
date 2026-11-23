using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Tasks;

// Something that happened to the bar's strip, as FloatingBar sees it, for BarStripNotices.Next to judge. Three kinds,
// one per place the bar decides today:
//   Docked    a dock finished (DockTo, DockAcrossSeam): the edge and screens it docked on, the window's middle, and
//             whether its app bar registered afresh on this dock (AppBar.RegisteredOnLastDock; for a seam dock, either
//             of its two halves).
//   Floating  the bar is on screen, not minimized and not docked (UndockFromEdge, LetGoOfStrip, ShowBar). Its screen
//             and middle as read from the window just now, None for either that could not be read.
//   Gone      the bar went away: minimized behind its taskbar button, or closing.
public abstract record BarStripEvent
{
    BarStripEvent() { }

    public sealed record Docked(string Edge, WindowRect Screen, Maybe<WindowRect> SeamRight, int MiddleX, bool RegisteredAfresh)
        : BarStripEvent;

    public sealed record Floating(Maybe<WindowRect> Screen, Maybe<int> MiddleX) : BarStripEvent;

    public sealed record Gone : BarStripEvent
    {
        public static readonly Gone Instance = new();
    }
}

// WHEN THE BAR ANNOUNCES ITS STRIP, the rule the whole tasks panel rests on (spec 2026-11-23-tasks-panel-design.md,
// "Keeping the bar outside"; final review of the tasks panel branch, Important 2). The shell places a NEWER app bar
// registration inside an older one on the same edge, so the panel stays inside the bar only while it registers after
// every registration of the bar's. A notice the bar fails to send leaves the bar INSIDE the panel; a notice it sends
// for nothing makes the panel re-register, which sends every app bar on the edge ABN_POSCHANGED, the bar re-docks in
// answer, and if that re-dock were announced too the two would re-register each other for ever, moving every maximized
// window on the screen each time. So the decision is pure and lives here, with a test for each case, and FloatingBar
// only reads the answer: a notice to send, or None for nothing to say.
//
// `last` is the last notice sent, null before the first one. The rules, each pinned in BarStripNoticesTests:
//   * Docked announces when its app bar registered afresh, wherever it is (the dock guard, Explorer restarting, a dock
//     after undock or minimize): the panel must register behind it again.
//   * Docked without a fresh registration announces only when the edge, the screen or the seam's right-hand screen
//     differs from the last notice's: a display change can move the docked bar while its registration stands. At the
//     same place it says NOTHING, and that is the loop breaker: the shell's re-ask (OnShellMovedStrips) and the end of
//     a width drag re-dock in place without registering. The middle is left out of "same place" on purpose, since a
//     width drag moves it.
//   * Floating announces unless the last notice was already a floating one. The screen is the one read from the window,
//     the last notice's when it could not be read, and nothing at all when there is neither (no place to give the panel).
//     The middle likewise, then the screen's own middle.
//   * Gone announces unless the last notice was already a gone one, WHATEVER the last edge: a floating bar going away
//     must take a panel docked by the floating rule with it, as much as a docked bar. Built from the last notice, never
//     from the window, whose handle may be going. Nothing when nothing was ever announced: the panel has no place yet.
public static class BarStripNotices
{
    public static Maybe<BarStrip> Next(BarStrip? last, BarStripEvent happened) =>
        happened switch
        {
            BarStripEvent.Docked docked => AfterDock(last, docked),
            BarStripEvent.Floating floating => WhileFloating(last, floating),
            BarStripEvent.Gone => last is { Gone: false } shown
                ? shown with { Edge = null, SeamRight = Maybe<WindowRect>.None, Gone = true }
                : Maybe<BarStrip>.None,
            _ => throw new ArgumentOutOfRangeException(nameof(happened), happened, "a kind of strip event nobody handles"),
        };

    static Maybe<BarStrip> AfterDock(BarStrip? last, BarStripEvent.Docked docked)
    {
        var next = new BarStrip(docked.Edge, docked.Screen, docked.SeamRight, docked.MiddleX, Gone: false);
        return !docked.RegisteredAfresh && last is { } before && SamePlace(before, next) ? Maybe<BarStrip>.None : next;
    }

    static Maybe<BarStrip> WhileFloating(BarStrip? last, BarStripEvent.Floating floating)
    {
        if (last is { Edge: null, Gone: false }) return Maybe<BarStrip>.None;
        var screen = floating.Screen.HasValue ? floating.Screen.Value : last?.Screen;
        if (screen is null) return Maybe<BarStrip>.None;
        var middle = floating.MiddleX.GetValueOrDefault(last?.MiddleX ?? (screen.Left + screen.Right) / 2);
        return new BarStrip(null, screen, Maybe<WindowRect>.None, middle, Gone: false);
    }

    // Same edge and same screens; the middle is left out on purpose, since a width drag moves it without registering.
    static bool SamePlace(BarStrip a, BarStrip b) => a.Edge == b.Edge && a.Screen == b.Screen && a.SeamRight.Equals(b.SeamRight);
}
