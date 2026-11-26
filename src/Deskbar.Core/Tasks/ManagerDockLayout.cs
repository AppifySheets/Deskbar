using Deskbar.Core.Domain;

namespace Deskbar.Core.Tasks;

// THE MANAGER'S DOCK, ITS RULES (task f171bca6). Petre, once the manager's window was pinned to every desktop: "i'd
// prefer to have it docked next to the tasks or next to the bar if tasks are not available, rather than floating on top
// of things", then from the choices offered: reserve the space (other windows keep clear, as of the tasks panel), and a
// drag by the title bar snaps it back. The app's ManagerDock reserves a strip just inside the tasks panel (or inside the
// bar while the panel is closed) and keeps the manager's Windows Terminal window on it; these are the pure parts, so a
// test pins them without a window or the shell.
//
// Physical pixels for rectangles, DIPs for widths, as TasksPanelLayout and TasksPanel have them.
public static class ManagerDockLayout
{
    // Where a dock that was never resized starts. A terminal running Claude Code wants about 80 columns, which at Windows
    // Terminal's default font is a little over 600 DIP with its scroll bar; 640 leaves the tasks panel's 460 and the bar
    // beside it on Petre's 2560 DIP screens with more than half the screen to spare.
    public const double DefaultWidth = 640;

    // The narrowest it may be dragged to: about 45 columns, still enough to read the manager's replies.
    public const double MinWidth = 360;

    // Between the minimum and half the screen, the tasks panel's rule with the dock's own minimum.
    public static double Clamp(double width, double screenWidthDips) =>
        Math.Clamp(width, MinWidth, Math.Max(MinWidth, screenWidthDips / 2));

    // Where the terminal's WINDOW goes so that what is visible of it fills the strip exactly. Windows Terminal's window
    // rectangle includes an invisible resize border its visible frame (DWMWA_EXTENDED_FRAME_BOUNDS) sits inside of,
    // measured 2026-11-25 at 150%: 9 px on the left, right and bottom, none at the top. Put the window rectangle on the
    // strip and a 9 px gap shows on both sides; so the window is grown by the border the window itself has, which is
    // read from it each time (window minus frame) rather than assumed, since it changes with the scale.
    public static WindowRect TerminalWindowFor(WindowRect strip, WindowRect window, WindowRect frame) =>
        new(strip.Left - (frame.Left - window.Left),
            strip.Top - (frame.Top - window.Top),
            strip.Right + (window.Right - frame.Right),
            strip.Bottom + (window.Bottom - frame.Bottom));

    // What a drag of the docked terminal (its move or resize ending, EVENT_SYSTEM_MOVESIZEEND) means, from where its
    // visible frame ended up. A frame whose width moved away from the strip's by more than a few pixels was RESIZED, by an
    // edge: the dock takes the new width (its inner edge is the one that can move, the outer one is held by the tasks
    // panel or the bar). Anything else was a MOVE, by the title bar, and the terminal goes back onto the strip ("snaps
    // back", Petre's pick). A few pixels of slack, so a resize that ends where it started, or a rounding pixel, is a snap.
    public const int ResizeSlackPixels = 4;

    public abstract record AfterDrag
    {
        private AfterDrag() { }
        public sealed record SnapBack : AfterDrag;
        public sealed record Resize(double WidthDips) : AfterDrag;
    }

    //
    // A RESIZE ONLY WHEN THE INNER EDGE ALONE MOVED (review finding): the top, the bottom and the OUTER edge (the one
    // against the tasks panel or the bar, on the dock's `edge`) still where the strip has them, within the slack. Without
    // that, a title-bar drag that ends in a Snap (half the screen), a maximize (the whole work area) or on a screen of
    // another scale (other pixels for the same DIPs) changes the frame's width too, and was read as a new width and saved:
    // a half-screen strip reserved for good. Every such drag is a SnapBack.
    public static AfterDrag Dragged(WindowRect strip, WindowRect frame, double scale, PanelEdge edge)
    {
        bool Near(int a, int b) => Math.Abs(a - b) <= ResizeSlackPixels;
        var outerStayed = edge == PanelEdge.Right ? Near(frame.Right, strip.Right) : Near(frame.Left, strip.Left);
        var onlyInnerMoved = outerStayed && Near(frame.Top, strip.Top) && Near(frame.Bottom, strip.Bottom);
        return onlyInnerMoved && !Near(frame.Width, strip.Width) && scale > 0
            ? new AfterDrag.Resize(frame.Width / scale)
            : new AfterDrag.SnapBack();
    }
}
