using Deskbar.Core.Domain;

namespace Deskbar.Core.Geometry;

// What a strip along the top of a screen must do to follow the screens, as a plan the caller applies.
// The strip shows the current workspace's titled window list, one per screen (spec
// docs/superpowers/specs/2026-11-19-screen-strips-design.md). Pure and in Core, like MonitorLayoutKey,
// because every awkward case here is a question about WHAT CHANGES, answerable without a second monitor.
//
// A screen's identity is its MonitorBounds record value: the rectangle, not a display number or device
// name, for the reasons MonitorLayoutKey gives. A screen whose size changed is therefore a different
// screen: its old strip is removed and a new one added, rather than a strip resized in place.
public sealed record StripChanges(IReadOnlyList<MonitorBounds> Add, IReadOnlyList<MonitorBounds> Remove);

public static class StripPlan
{
    // A tab is at most this wide, however much room there is, so one window in a wide strip is not a
    // 1000 DIP slab.
    public const double TabMax = 150;

    // The floor: icon only. Tabs narrow equally from TabMax down to here and then stop; once the
    // strip is full the rest is the caller's problem (the strip stays one line, spec "The look").
    public const double TabIconOnly = 30;

    // Which strips to create and which to remove, so the strips on screen equal the screens wanted.
    //
    // Not wanted: every existing strip goes, and none is added.
    //
    // No screens at all: nothing changes. Display changes arrive in bursts with transient states, and
    // one of them is a moment when Windows reports no usable screen (spec "Constraints"). Reading it
    // as "every screen left" would tear all strips down just to rebuild them a moment later, so the
    // existing strips are kept until a real arrangement arrives.
    public static StripChanges Reconcile(bool wanted, IReadOnlyList<MonitorBounds> screens, IReadOnlyList<MonitorBounds> existing) =>
        !wanted ? new StripChanges([], existing.Distinct().ToList())
        : screens.Count == 0 ? new StripChanges([], [])
        : new StripChanges(
            screens.Distinct().Where(s => !existing.Contains(s)).ToList(),
            existing.Distinct().Where(e => !screens.Contains(e)).ToList());

    // The width of each tab when `tabs` windows share `room` DIPs: an equal share, never above
    // TabMax and never below TabIconOnly. No tabs answers TabMax, which is what a first tab would get.
    public static double TabWidth(double room, int tabs) =>
        tabs <= 0 ? TabMax : Math.Clamp(room / tabs, TabIconOnly, TabMax);

    // Whether a tab this wide has room for its title after the icon. Twelve DIPs beyond the
    // icon-only width is the least that shows a letter and an ellipsis rather than a clipped sliver.
    public static bool ShowsTitle(double tabWidth) => tabWidth > TabIconOnly + 12;

    // Where a window must go so its title bar is not under a new strip, or null when it can stay where it is.
    //
    // The shell does NOT do this. It resizes maximized windows to the new work area, but a restored window whose top
    // is under a new top-edge app bar stays there, its title bar (the only handle to drag it by) unreachable: measured,
    // a window at [450,6,1350,456] stayed put when a 48 px top bar took [0,0,3840,48] (probe notes 2026-11-19,
    // section 1). So the strip set moves such windows down itself, once, when it creates a strip.
    //
    // `stripPx` is how far below the screen's top edge the strip ends, in physical pixels like everything here. A
    // window counts when its top is above that line and its centre is on this screen: a window centred on another
    // screen is that screen's strip's business, whatever part of it pokes over here. The answer is the same rectangle
    // moved down to start exactly at the strip's bottom edge, size kept. Kept even when that pushes the bottom past
    // the screen: shrinking a window is a bigger change than moving it, and 32 DIP off the bottom is far better than a
    // title bar nobody can reach.
    //
    // Which windows to ask about at all (restored: not minimized, maximized or snapped) is the caller's: a minimized
    // window's rectangle is the -32000 nonsense (CLAUDE.md, traps), and a maximized or snapped one is the shell's to size.
    public static WindowRect? Clear(WindowRect window, MonitorBounds screen, int stripPx)
    {
        var line = screen.Top + stripPx;
        var centreX = window.Left + window.Width / 2;
        var centreY = window.Top + window.Height / 2;
        var onScreen = centreX >= screen.Left && centreX < screen.Right && centreY >= screen.Top && centreY < screen.Bottom;
        return onScreen && window.Top < line
            ? window with { Top = line, Bottom = line + window.Height }
            : null;
    }
}
