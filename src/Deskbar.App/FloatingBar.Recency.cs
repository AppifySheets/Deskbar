using System.Collections.Concurrent;
using System.Windows.Media;
using Deskbar.Core.Domain;
using Deskbar.Core.Overview;

namespace Deskbar.App;

// RECENT WORKSPACES KEEP THEIR COLOUR. Petre: "i get lost in so many workspaces; how can i have workspaces that i've
// been working on active while others are hidden or dimmed?" From the mockups he picked "b gradual", then: "let's
// fade windows not based on individual age, but the age of the workspace", "recency has to be gradual, not 8 hours",
// "more time goes by, less pronounced a bar and all its icons become", and "let's also fade the rails on the left,
// so i don't get tempted to look at them". Design: docs/superpowers/specs/2026-11-22-recent-workspaces-design.md.
//
// Every workspace row has ONE fade, from 0 (used in the last 15 minutes) to 1 (untouched for 8 hours), worked out by
// WorkspaceFade from the newest last-used stamp among its windows. That one number drives the whole row:
//
//   rail     the workspace colour, draining to grey and thinning to about 19% opacity (alpha 0x30), so an idle
//            rail barely shows. Not to nothing: the rails also mark where one row ends and the next begins.
//   tint     a faint wash of the workspace colour, a third of the current row's 0x38 when fresh, gone when idle.
//            Before this only the row you are on was tinted ("try b"); a fresh row now carries a trace of it.
//   name     today's resting white (0xA8) dimming to a floor of 0x6B, never lower, so every name stays readable.
//   icons    the grey overlay of the aging icons (FloatingBar.Aging.cs), now at the WORKSPACE's fade rather than
//            each window's own, so every icon in a row is one shade.
//
// The row you are on is drawn exactly as before: being on it is using it. So is an expanded row's bright spine, and
// a row asking for you keeps its pulsing caption. Rows that are not workspaces (the pinned row, unbound desktops,
// Unplaced) are left as they were, icons aging per window.
//
// The rail, tint and name are drawn from the fade rounded to one of WorkspaceFade.Steps (16) steps, which keeps the
// brush caches below at 16 entries per colour, and lets the aging timer ask for a rebuild only when a workspace's
// step has moved (RefreshFades), a few times an hour. The icons fade continuously in place, as they always have.
public partial class FloatingBar
{
    // Where a rail ends up: a cool grey a touch lighter than the bar, at 0x30, so it is a trace and not a stripe.
    static readonly Color IdleRail = Color.FromRgb(0x4A, 0x4E, 0x58);
    const byte IdleRailAlpha = 0x30;

    // A third of LaneTint's 0x38, the current row's tint.
    const byte RecentTintAlpha = 0x13;

    // The resting caption's alpha when fresh (RestingRowForeground's) and its floor when fully faded.
    const byte FreshCaptionAlpha = 0xA8;
    const byte FadedCaptionAlpha = 0x6B;

    // Keyed by hex and step, or by step alone. Concurrent because the tests build bars on several STA threads at once
    // and these are static; the brushes themselves are frozen, so any thread may use them.
    static readonly ConcurrentDictionary<(string Hex, int Step), Brush> fadedAccents = new();
    static readonly ConcurrentDictionary<int, Brush> restingCaptions = new();
    static readonly ConcurrentDictionary<int, Brush> groupHeaders = new();

    // --- which workspace rows were drawn at which step ----------------------------------------------------------

    // Per workspace row drawn in this rebuild: how to work its fade out again (the windows it was drawn with, and
    // whether it was the row you are on), and the step it was drawn at. Emptied at the start of every rebuild.
    readonly Dictionary<Guid, (Func<double> Fade, int Step)> drawnFades = [];

    void ForgetDrawnFades() => drawnFades.Clear();

    // Notes a workspace row about to be drawn and returns the step to draw it at. The current workspace is always 0.
    int NoteFade(WorkspaceGroup g)
    {
        var windows = g.Running.Select(r => r.Window.Handle).ToList();
        Func<double> fade = g.IsCurrent ? () => 0 : () => WorkspaceFade.Of(windows.Select(manager.LastUsed), manager.Now);
        var step = WorkspaceFade.Step(fade());
        drawnFades[g.Workspace.Id] = (fade, step);
        return step;
    }

    // The step a workspace was noted at in this rebuild; 0 for one that was not.
    int DrawnStepOf(Guid workspace) => drawnFades.TryGetValue(workspace, out var drawn) ? drawn.Step : 0;

    // An icon's fade: its workspace row's, continuous rather than stepped, or, off a workspace row, the window's own age.
    double FadeOf(Guid? workspace, WindowHandle window) =>
        workspace is { } id && drawnFades.TryGetValue(id, out var drawn) ? drawn.Fade() : AgeOf(window);

    // Whether any workspace's step has moved since it was drawn.
    internal bool FadesMoved() => drawnFades.Values.Any(d => WorkspaceFade.Step(d.Fade()) != d.Step);

    // From the aging timer: a rebuild when a step has moved, which redraws the rails, tints and names at the new step.
    // Rebuild holds itself back while a button is down, a menu is open or a drag is in flight (FlushIfIdle), and
    // RequestRebuild, the gate every timer-driven trigger goes through, skips it while nothing would be on screen.
    void RefreshFades()
    {
        if (FadesMoved()) RequestRebuild();
    }

    // --- the faded brushes ----------------------------------------------------------------------------------------

    static double FractionOf(int step) => Math.Clamp(step, 0, WorkspaceFade.Steps) / (double)WorkspaceFade.Steps;

    static byte Between(byte from, byte to, double fraction) => (byte)Math.Round(from + (to - from) * fraction);

    // The rail at a step: the palette colour blended toward IdleRail, its alpha falling from opaque to IdleRailAlpha by
    // the same fraction. Null for a row that has opted out of colour, as LaneAccent is, and for an unreadable hex.
    internal static Brush? FadedAccent(string? color, int index, int step)
    {
        if (WorkspacePalette.IsNone(color)) return null;
        var hex = WorkspacePalette.For(color, index < 0 ? 0 : index);
        if (fadedAccents.TryGetValue((hex, step), out var cached)) return cached;
        try
        {
            var solid = (Color)ColorConverter.ConvertFromString(hex);
            var at = FractionOf(step);
            var brush = Frozen(
                Between(0xFF, IdleRailAlpha, at),
                Between(solid.R, IdleRail.R, at),
                Between(solid.G, IdleRail.G, at),
                Between(solid.B, IdleRail.B, at));
            return fadedAccents.GetOrAdd((hex, step), brush);
        }
        catch (FormatException)
        {
            // A hand-edited state.json can hold anything; no rail beats a crash on every rebuild (as Tint does).
            return null;
        }
    }

    // The row's wash at a step: RecentTintAlpha when fresh, nothing once it has faded away. Null means "no tint", which
    // every row already handles.
    internal static Brush? FadedTint(string? color, int index, int step) =>
        Between(RecentTintAlpha, 0, FractionOf(step)) is var alpha && alpha > 0 ? Lane(color, index, alpha) : null;

    // A resting workspace name at a step.
    internal static Brush RestingCaption(int step) =>
        restingCaptions.GetOrAdd(step, s => Frozen(Between(FreshCaptionAlpha, FadedCaptionAlpha, FractionOf(s)), 0xFF, 0xFF, 0xFF));

    // A group's name at a step: GroupHeaderForeground dimmed by the same ratio a workspace name is.
    internal static Brush GroupHeaderAt(int step) =>
        groupHeaders.GetOrAdd(step, s =>
        {
            var header = ((SolidColorBrush)GroupHeaderForeground).Color;
            var ratio = Between(FreshCaptionAlpha, FadedCaptionAlpha, FractionOf(s)) / (double)FreshCaptionAlpha;
            return Frozen((byte)Math.Round(header.A * ratio), header.R, header.G, header.B);
        });
}
