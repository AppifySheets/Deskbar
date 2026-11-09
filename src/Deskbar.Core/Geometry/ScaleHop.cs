namespace Deskbar.Core.Geometry;

// Placing the bar on a screen whose scale is not the window's own. Like MiddleDrag, the move it computes is in
// PHYSICAL pixels, because that is the only unit that means the same place on every screen.
//
// The bar is placed by writing Left, Top, Width and Height, which are DIPs, and WPF turns them into pixels at the
// WINDOW's current scale. The placement code works those DIPs out from the TARGET screen's own DPI (MonitorBounds,
// DockTo), which is right while the window is already at that screen's scale and wrong by the ratio of the two
// scales when it is not. Measured on a 2560 x 1440 primary at 150% with a 1920 x 1080 screen at 100% starting at
// x = 3840: the bar was saved docked to the 100% screen's left edge, Left 3840. The window starts out on the
// primary at 150%, so Left 3840 became pixel 5760 and the 220 DIP strip 330 px, the window at 5760,0 to
// 6090,1548: exactly one screen-width past the 100% screen's right edge, on no monitor at all. The strip itself
// was reserved right, at 3840 to 4060, because the shell is handed pixels. And it never healed: Windows rescales
// a window only when it lands on a screen of another scale, this one landed on none, so it stayed at 150% and
// every later dock, undock and restore made the same mistake again (undocked it went to 5760,486, which is
// 3840,324 times 1.5).
//
// So before such a placement the window is moved, in pixels, onto the screen it is going to. Windows rescales it
// to that screen's DPI on the spot, and from then on the screen's DIPs and the window's are one unit again, so
// the placement that follows lands where it was meant to, unchanged.
public static class ScaleHop
{
    // The pixel a coordinate in a screen's DIPs stands for at that screen's DPI. The placement code's DIPs are the
    // target screen's pixels divided by its scale (see MonitorBounds), so this is that division undone.
    public static int ToPixels(double dips, int dpi) => (int)Math.Round(dips * dpi / 96.0);

    // Where to move a window that is widthPx x heightPx at windowDpi NOW, so that it takes the scale of `screen`
    // before (left, top), in that screen's DIPs, is written. Kept wholly on the screen, or as much of it as fits,
    // so that screen holds most of the window and Windows gives it that screen's scale; a position off every
    // screen is what started this. Null when the window is at the screen's scale already, which is every desk
    // whose screens share one scale: nothing to correct, and the placement goes on exactly as it always has.
    public static (int Left, int Top)? Target(double left, double top, int widthPx, int heightPx, int windowDpi, MiddleDrag.Screen screen)
    {
        if (windowDpi == screen.Dpi) return null;
        var (x, y) = WorkAreaClamp.Clamp(ToPixels(left, screen.Dpi), ToPixels(top, screen.Dpi), widthPx, heightPx,
            screen.Left, screen.Top, screen.Right, screen.Bottom);
        return ((int)x, (int)y);
    }
}
