namespace Deskbar.Core.Geometry;

// Pure maths of a middle-button drag of the bar, and unlike EdgeSnap and WorkAreaClamp it is in
// PHYSICAL pixels, because that is the whole point of it.
//
// The drag used to hold the window's position in DIPs and add the cursor's travel to it. A DIP is
// not a fixed place on the desktop once the monitors differ in scale: at 150% a Left of 1500 is
// pixel 2250, at 100% the same 1500 is pixel 1500. So when the bar crossed from a 150% screen to a
// 100% one and WPF rescaled it, the very next write put it on the wrong screen, which rescaled it
// again, and so on. Measured on a 4K 150% screen beside a Full HD 100% one: the bar's scale flipped
// 7 to 81 times per drag and its left edge ended up to 1600 px from the cursor.
//
// A pixel IS the same place on every monitor, so the drag holds the cursor and the window in
// pixels and never converts them.
//
// The second half of the problem is the size. Crossing into a monitor of another scale resizes the
// bar (332 px wide becomes 221), so "the window's position plus the cursor's travel" would leave the
// bar sliding away from the cursor by the difference. What stays fixed is WHERE on the bar the
// cursor took hold, as a share of its size, and the window is placed from the size it has NOW.
public static class MiddleDrag
{
    // Where the cursor took hold: its offset from the window's top-left corner, and the window's
    // size when it did, which is what turns that offset into a share of the size.
    public readonly record struct Grab(int OffsetX, int OffsetY, int Width, int Height);

    public static Grab Press(int cursorX, int cursorY, int left, int top, int width, int height) =>
        new(cursorX - left, cursorY - top, width, height);

    // The top-left corner that keeps the grab point under the cursor for a window that is
    // width x height NOW. At the size it was grabbed at the share is exactly one, so this is the
    // old "position plus travel" to the pixel and a single monitor, or two of one scale, behave as
    // they always did.
    public static (int Left, int Top) Target(Grab grab, int cursorX, int cursorY, int width, int height) =>
        (cursorX - Share(grab.OffsetX, grab.Width, width), cursorY - Share(grab.OffsetY, grab.Height, height));

    static int Share(int offset, int grabbedAt, int now) =>
        grabbedAt <= 0 || grabbedAt == now ? offset : (int)Math.Round(offset * (double)now / grabbedAt);

    // A monitor in physical pixels, with its scale as an effective DPI.
    public readonly record struct Screen(int Left, int Top, int Right, int Bottom, int Dpi);

    // Where a bar dropped across the border between screens of DIFFERENT scale should rest: wholly on the
    // screen that holds most of it, as pixels, or null when it should stay where it is.
    //
    // Windows keeps a window at the scale it last chose, and it does not switch the moment more than half
    // of the window is on the other screen (measured: a 332 px bar went to 100% when its left edge reached
    // x = 3730 beside a border at 3840, and came back to 150% only below 3674). So a bar dropped in
    // between rests with its top-left corner on one screen and most of its area on the other, and the app's
    // own placement (MonitorBounds, which picks the screen from the corner) sent it to the corner's screen:
    // the owner dropped one mostly on his Full HD screen and it jumped back onto the 4K one, flush with its
    // right edge. Judged by where most of the bar IS, it goes where the owner put it.
    //
    // Only across different scales. Two screens of one scale have no scale to disagree about, and a bar
    // resting across their border stays there as it always has. Nor does a bar over only one screen move: one
    // that hangs off the outer edge of the desktop is left as dropped.
    public static (int Left, int Top)? Settle(int left, int top, int width, int height, IReadOnlyList<Screen> screens)
    {
        Screen? home = null;
        long best = 0;
        var overlapped = 0;
        var scales = new HashSet<int>();
        foreach (var screen in screens)
        {
            var wide = Math.Min(left + width, screen.Right) - Math.Max(left, screen.Left);
            var high = Math.Min(top + height, screen.Bottom) - Math.Max(top, screen.Top);
            if (wide <= 0 || high <= 0) continue;
            overlapped++;
            scales.Add(screen.Dpi);
            // The first of two equal shares wins, which is the screen listed first.
            if ((long)wide * high > best) (home, best) = (screen, (long)wide * high);
        }
        if (home is not { } target || overlapped < 2 || scales.Count < 2) return null;

        var settled = WorkAreaClamp.Clamp(left, top, width, height, target.Left, target.Top, target.Right, target.Bottom);
        var (settledLeft, settledTop) = ((int)settled.Left, (int)settled.Top);
        return settledLeft == left && settledTop == top ? null : (settledLeft, settledTop);
    }
}
