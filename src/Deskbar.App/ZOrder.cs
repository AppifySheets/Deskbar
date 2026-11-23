using Deskbar.Windows.Monitoring;

namespace Deskbar.App;

// "DIRECTLY ABOVE THAT WINDOW", for the two windows of Deskbar's own that are drawn over another app's window without
// being owned by it: the focus ring's frame (RingFrame) and a tile's lock mark (LockMarkWindow). Neither may be owned by
// its target: an owner or parent across two processes joins the two threads' input, so a hung app would freeze Deskbar
// (spec 2026-11-22-tile-lock-and-other-screens-design.md, "Following the window"). Being placed directly above the
// target instead means anything in front of the target (a dialog, a floating window) is in front of the overlay too.
static class ZOrder
{
    const uint SWP_SHOWWINDOW = 0x0040;
    const uint GW_HWNDPREV = 3;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern nint GetWindow(nint hwnd, uint command);

    // Puts `hwnd` on the rectangle (physical pixels), shown and not activated, directly above `target`: behind whatever
    // is above the target. When `hwnd` already is the window above the target, the z-order is left alone, because both
    // callers re-check on a timer and moving to the front of the band would lift the overlay over windows in front of
    // the target. With nothing above the target, the front of the band (`topmost` says which band; the caller sets the
    // overlay's own Topmost first, as WPF's Topmost is the one switch between the bands).
    //
    // Pixels through SetWindowPos, never WPF's Left/Top/Width/Height, which are DIPs at the overlay's OWN scale: wrong
    // exactly when the target is on a screen at another scale than the one the overlay was last on (ScreenStrip.PlaceAt
    // has the story). Moving onto such a screen makes Windows rescale the overlay, so the rectangle is set again if it
    // came out different.
    public static void PlaceDirectlyAbove(nint hwnd, nint target, int x, int y, int width, int height, bool topmost = false)
    {
        var above = GetWindow(target, GW_HWNDPREV);
        var keepOrder = above == hwnd;
        var insertAfter = above == 0 ? (topmost ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_TOP) : above;
        NativeMethods.SetWindowPos(hwnd, insertAfter, x, y, width, height,
            NativeMethods.SWP_NOACTIVATE | SWP_SHOWWINDOW | (keepOrder ? NativeMethods.SWP_NOZORDER : 0));
        if (NativeMethods.GetWindowRect(hwnd, out var now) && (now.Left != x || now.Top != y || now.Right - now.Left != width || now.Bottom - now.Top != height))
            NativeMethods.SetWindowPos(hwnd, 0, x, y, width, height, NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER);
    }
}
