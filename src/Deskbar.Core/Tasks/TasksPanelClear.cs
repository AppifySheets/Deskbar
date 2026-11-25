using Deskbar.Core.Domain;

namespace Deskbar.Core.Tasks;

// WHERE A WINDOW UNDER THE TASKS PANEL GOES (task cafaae24, "tasks should reclaim space from other windows").
//
// The panel registers an app bar, and the shell resizes MAXIMIZED windows to the new work area, but it leaves a restored
// window exactly where it was, with its edge under the panel: the strip set measured the same for its top band
// (StripPlan.Clear, probe notes 2026-11-19, section 1), and Petre's screenshot showed a terminal's lines cut off by the
// panel. So the panel moves such windows out of its strip itself, once, when it takes the strip (TasksPanel.Dock).
//
// THE RULE. A window counts when its centre is on the panel's screen (a window centred on another screen is that screen's
// business), it overlaps the panel's strip vertically, and it reaches under the strip by more than `tolerancePx`, which is
// there for Windows' invisible resize border: a window placed flush against the panel reaches a few pixels under it
// without anything of it being covered. Such a window moves sideways, away from the panel, by exactly what was under it,
// size kept, because shrinking a window is a bigger change than moving it. Only when the far edge of the work area stops
// it (a window as wide as the screen, like the screenshot's terminal) does it stop there and end at the panel's edge,
// narrower. A window whose far edge already reaches past the work area's (its own invisible border, or placed there by
// hand) keeps that edge; it is never pulled further in than the panel requires.
//
// All in physical pixels, like the shell's rectangles. `workArea` supplies only the FAR edge, the side away from the
// panel, which the panel's own registration does not move, so a work area read a moment before the shell has caught up
// still answers it right; an empty one (the screen cannot be read right now) falls back to the screen's edge.
//
// Which windows to ask about at all (restored: not minimized, maximized, snapped or in a tile) is the caller's, for the
// reasons ScreenStrips.ClearUnder gives.
public static class TasksPanelClear
{
    public static WindowRect? Clear(WindowRect window, PanelEdge edge, WindowRect screen, WindowRect strip, WindowRect workArea, int tolerancePx)
    {
        var centreX = window.Left + window.Width / 2;
        var centreY = window.Top + window.Height / 2;
        var onScreen = centreX >= screen.Left && centreX < screen.Right && centreY >= screen.Top && centreY < screen.Bottom;
        var besideVertically = window.Bottom > strip.Top && window.Top < strip.Bottom;
        if (!onScreen || !besideVertically) return null;

        var readable = workArea.Width > 0 && workArea.Height > 0;
        return edge == PanelEdge.Right
            ? FromRight(window, strip, readable ? workArea.Left : screen.Left, tolerancePx)
            : FromLeft(window, strip, readable ? workArea.Right : screen.Right, tolerancePx);
    }

    // Panel on the right edge: the window moves left, at most to `farLeft`, and ends at the strip's left edge.
    static WindowRect? FromRight(WindowRect window, WindowRect strip, int farLeft, int tolerancePx)
    {
        var under = window.Right - strip.Left;
        if (under <= tolerancePx || window.Left >= strip.Right) return null;
        var left = Math.Max(Math.Min(window.Left, farLeft), window.Left - under);
        return window with { Left = left, Right = strip.Left };
    }

    // Panel on the left edge: the mirror image, the window moves right, at most to `farRight`.
    static WindowRect? FromLeft(WindowRect window, WindowRect strip, int farRight, int tolerancePx)
    {
        var under = strip.Right - window.Left;
        if (under <= tolerancePx || window.Right <= strip.Left) return null;
        var right = Math.Min(Math.Max(window.Right, farRight), window.Right + under);
        return window with { Left = strip.Right, Right = right };
    }
}
