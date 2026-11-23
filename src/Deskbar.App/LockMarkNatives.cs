using System.Runtime.InteropServices;
using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;
using Deskbar.Core.Tiling;
using Deskbar.Windows.Monitoring;

namespace Deskbar.App;

// The few reads TileLockMarks makes of a tile, for LockMarks (Core) to work from. Every one of them is a read that
// never waits on the tile's own thread (GetWindowRect, IsIconic, IsZoomed, GetDpiForWindow, DwmGetWindowAttribute):
// nothing here sends the tile a message, so a hung app cannot stall the bar's UI thread on its 100 ms tick.
static class LockMarkNatives
{
    static readonly int RectSize = Marshal.SizeOf<NativeMethods.RECT>();

    // The tile's window rectangle, visible frame, caption buttons and scale. None when the window is gone or Windows
    // will not give its rectangles; the caption buttons are None on their own when DWM refuses just that one.
    public static Maybe<LockMarkGeometry> GeometryOf(WindowHandle window)
    {
        var hwnd = window.Value;
        if (!NativeMethods.GetWindowRect(hwnd, out var w)) return Maybe<LockMarkGeometry>.None;
        if (NativeMethods.DwmGetWindowAttributeRect(hwnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out var v, RectSize) != 0)
            return Maybe<LockMarkGeometry>.None;
        var buttons = NativeMethods.DwmGetWindowAttributeRect(hwnd, NativeMethods.DWMWA_CAPTION_BUTTON_BOUNDS, out var b, RectSize) == 0
            ? Maybe.From(Rect(b))
            : Maybe<WindowRect>.None;
        var dpi = (int)NativeMethods.GetDpiForWindow(hwnd);
        return new LockMarkGeometry(Rect(w), Rect(v), buttons, dpi == 0 ? 96 : dpi);
    }

    // Can a mark be drawn on this tile now? Not when it is gone, minimized (its rectangle is the iconic one off in the
    // corner), maximized (it is set aside, not a tile, and a mark over it would be in the wrong place for a moment), or
    // cloaked (on another desktop: in the moment of a switch, before the Tiler's board follows).
    public static bool Drawable(WindowHandle window)
    {
        var hwnd = window.Value;
        return NativeMethods.IsWindow(hwnd)
               && !NativeMethods.IsIconic(hwnd)
               && !NativeMethods.IsZoomed(hwnd)
               && !(NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0);
    }

    // Where the mouse pointer is, in physical pixels (the app is per-monitor aware). None if Windows will not say.
    public static Maybe<(int X, int Y)> Pointer() =>
        NativeMethods.GetCursorPos(out var p) ? (p.X, p.Y) : Maybe<(int X, int Y)>.None;

    static WindowRect Rect(NativeMethods.RECT r) => new(r.Left, r.Top, r.Right, r.Bottom);
}
