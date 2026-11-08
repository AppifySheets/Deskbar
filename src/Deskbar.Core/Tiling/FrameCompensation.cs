using Deskbar.Core.Domain;

namespace Deskbar.Core.Tiling;

// The tiling grid speaks in VISIBLE frames (DWMWA_EXTENDED_FRAME_BOUNDS); SetWindowPos speaks in window
// rectangles. The two differ by the invisible resize border Windows 10 and 11 draw around a sizable window:
// typically seven pixels on the left, right and bottom at 100% and none on top, but it is per window and per
// DPI, so it is MEASURED for each window (its current window rectangle against its current visible frame)
// rather than assumed (spec, "Constraints and known traps").
//
// Kept in Core, apart from the Win32 call, because it is arithmetic a test can pin and the kind that is wrong
// by a few pixels in a way nobody notices for a week.
public static class FrameCompensation
{
    // The rectangle to hand SetWindowPos so the window's visible frame lands on `wantedVisible`: each side
    // pushed out by however far that side of the window rectangle sticks out past the visible frame now.
    public static WindowRect Outer(WindowRect wantedVisible, WindowRect windowRect, WindowRect visibleFrame) =>
        new(wantedVisible.Left - (visibleFrame.Left - windowRect.Left),
            wantedVisible.Top - (visibleFrame.Top - windowRect.Top),
            wantedVisible.Right + (windowRect.Right - visibleFrame.Right),
            wantedVisible.Bottom + (windowRect.Bottom - visibleFrame.Bottom));
}
