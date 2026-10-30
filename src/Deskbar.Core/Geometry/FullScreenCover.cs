namespace Deskbar.Core.Geometry;

// Whether a full-screen window may take the floating bar off the top of the screen it is on.
//
// Pure, and in Core for the reason MiddleDrag is in Core: the decision is a question about
// rectangles, and it has to be testable without a window, a monitor or a mouse.
//
// MEASURED on Windows 11 build 26300, two screens (3840x2160 at 0,0 and 1920x1080 at 3840,0),
// with a video player full screen on the 4K one:
//
//   * The full-screen window's class was 'PotPlayer64' and its rectangle was EXACTLY the
//     monitor's (0,0,3840,2160). It carried WS_EX_TOPMOST and IsZoomed was FALSE, so "maximized"
//     is not the test for "full screen" -- a full-screen window is a window that covers the
//     screen, and it says so by its rectangle.
//   * A maximized window on the OTHER screen had the rectangle 3829,-11,5771,1043: a maximized
//     window hangs about 11 px over the neighbouring screen. So "which screen is this window on"
//     is the one holding MOST of it (MonitorFromWindow), never "does its rectangle touch it".
//   * SHQueryUserNotificationState is a question about the MACHINE and not about a window: it
//     stayed "busy" while another app was in front on the other screen, so it cannot say that a
//     screen is covered and must not be used.
public static class FullScreenCover
{
    // A rectangle in physical pixels on the virtual desktop. Left/Top/Right/Bottom, like
    // MiddleDrag's Screen, so the two read the same.
    public readonly record struct Rect(int Left, int Top, int Right, int Bottom);

    // A window that could cover a screen: its handle, so the caller can name it in a trace and
    // put the bar below it, and where it is.
    public readonly record struct Candidate(nint Handle, Rect Rect);

    // The window that covers the screen, or null when nothing does.
    //
    // The candidates are the windows ON THAT SCREEN, top first: EnumWindows' order is z-order, and
    // the FIRST of them decides. A small window in front of a full-screen one means the full-screen
    // one is not what is being looked at, so the screen is not covered and the bar stays where it is.
    //
    // "Covers" means CONTAINS the whole monitor rectangle. A window that reaches the screen's top
    // and both sides but stops at the taskbar's edge does not cover it, and neither does one that
    // hangs a few pixels over the neighbouring screen: a maximized window with a visible taskbar
    // never reaches the screen's bottom, so it never counts.
    //
    // The windows of this app are left out by the caller, and so are the ones on other screens.
    public static Candidate? Covering(Rect screen, IReadOnlyList<Candidate> candidates)
    {
        if (candidates.Count == 0) return null;
        var first = candidates[0];
        var rect = first.Rect;
        return rect.Left <= screen.Left && rect.Top <= screen.Top
            && rect.Right >= screen.Right && rect.Bottom >= screen.Bottom
            ? first
            : null;
    }

    // Whether the bar is OWNED BY the window that covers its screen: the step-aside setting is off,
    // the bar is floating, it is on screen, and there is a covering window.
    //
    // An owned window always stays above its owner and moves with it, so Windows itself keeps the
    // bar above a full-screen window -- there is no moment when the player is on top, which is what
    // taking the top back could never manage (measured; see the notes in FloatingBar).
    //
    // The setting on is the opposite case and is left exactly as it was: the bar steps BELOW the
    // covering window (see StepAsideIfCovered), so there is no owner to be. Docked, the shell's own
    // full-screen notice has already moved the bar out of the band. Hidden, there is no bar to own.
    public static bool OwnedByCover(bool stepAsideForFullScreen, bool docked, bool visible, bool covered) =>
        !stepAsideForFullScreen && !docked && visible && covered;
}
