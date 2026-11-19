using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Deskbar.Windows.Monitoring;

namespace Deskbar.App;

// THE FOCUS RING ON A MAXIMIZED OR FULL-SCREEN WINDOW, which Windows' own border cannot show. Petre, while the focus
// ring was being built: "let's also have that outline for fullscreen windows if possible".
//
// Windows 11 draws the coloured window border (DWMWA_BORDER_COLOR, what WindowRings uses for every other window) only
// round a window with a frame that is not maximized: a maximized window's frame hangs off the screen's edges, and a
// full-screen window (a video, a browser in F11) has no frame at all. So for those this draws the line itself: a
// borderless, see-through window the size of the target's visible rectangle, with a coloured border inside its edges.
//
// It must never get in the way, so it is:
//   * click-through: WS_EX_TRANSPARENT and WS_EX_LAYERED (WPF's AllowsTransparency makes it layered), so every click
//     goes to the window under it;
//   * never activated (ShowActivated false, WS_EX_NOACTIVATE), so focus, and with it the ring, stays where it is;
//   * never on the taskbar or in Alt+Tab (ShowInTaskbar false, WS_EX_TOOLWINDOW), which also keeps it out of the
//     bar's window list and out of the z-order sweep that decides which window is in front on a screen
//     (TopLevelWindows.IsListed takes taskbar candidates only);
//   * placed DIRECTLY above its window in z-order (Place), so any other window in front of the target is in front of
//     the line too. Not topmost unless the target is: a full-screen video player is topmost (FullScreenCover's
//     measurements), and a frame below it would be hidden.
//
// The owner (WindowRings) tells it where, in what colour and above which window, and checks it every second.
sealed class RingFrame : Window
{
    // The line's thickness is given in PHYSICAL pixels (WindowRings asks for one). It was a fixed 2 DIP at first, 4 to 5 px
    // at Petre's 225%, then Windows' own border thickness (2 px there), then "can you do 1px?". Converted to DIPs at the
    // target's scale in Place, since WPF draws in DIPs, and snapped to whole pixels so 1 px stays a crisp 1 px rather
    // than a blur across two.
    readonly Border line;

    public RingFrame()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.Manual;
        // Off screen and tiny until the first Place, so the first Show draws nothing anywhere.
        Left = -32000;
        Top = -32000;
        Width = 1;
        Height = 1;
        Title = "Deskbar focus ring";
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        line = new Border { BorderThickness = new Thickness(1), Background = Brushes.Transparent, IsHitTestVisible = false, SnapsToDevicePixels = true };
        Content = line;
    }

    public nint Handle => new WindowInteropHelper(this).EnsureHandle();

    // The extended styles WPF has no property for, stamped once on the new handle: see the header for each.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE,
            NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE)
            | (nint)(NativeMethods.WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE));
    }

    const long WS_EX_TRANSPARENT = 0x00000020, WS_EX_LAYERED = 0x00080000, WS_EX_NOACTIVATE = 0x08000000;
    const uint SWP_SHOWWINDOW = 0x0040;
    const uint GW_HWNDPREV = 3;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern nint GetWindow(nint hwnd, uint command);

    // `rect` is the target's visible rectangle in physical pixels, `colour` #RRGGBB, `target` the window it outlines.
    //
    // Pixels through SetWindowPos, never Left/Top/Width/Height, which are DIPs at the frame's OWN scale: wrong exactly
    // when the target is on a screen at another scale than the one the frame was last on (ScreenStrip.PlaceAt has the
    // story). Moving onto such a screen makes Windows rescale the frame, so the rectangle is set again if it came out
    // different.
    // `pixels` is the line's thickness in physical pixels, at the scale of the target's screen.
    public void Place(NativeMethods.RECT rect, string colour, nint target, int pixels)
    {
        line.BorderBrush = BrushOf(colour);
        var dpi = NativeMethods.GetDpiForWindow(target);
        line.BorderThickness = new Thickness(pixels * 96.0 / (dpi == 0 ? 96 : dpi));
        var hwnd = Handle;

        // Into the target's band first: WPF's Topmost is the one switch between the two bands, and setting it again to
        // the same value does nothing.
        Topmost = (NativeMethods.GetWindowLongPtr(target, NativeMethods.GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;

        // Directly above the target: behind whatever is above it. When the frame already is the window above it,
        // the z-order is left alone (it is checked every second, and moving it to the front of the band would lift it
        // over windows that are in front of the target). When nothing is above it, the front of the band.
        var above = GetWindow(target, GW_HWNDPREV);
        var keepOrder = above == hwnd;
        var insertAfter = above == 0 ? (Topmost ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_TOP) : above;
        var (x, y, w, h) = (rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        NativeMethods.SetWindowPos(hwnd, insertAfter, x, y, w, h,
            NativeMethods.SWP_NOACTIVATE | SWP_SHOWWINDOW | (keepOrder ? NativeMethods.SWP_NOZORDER : 0));
        if (NativeMethods.GetWindowRect(hwnd, out var now) && (now.Left != x || now.Top != y || now.Right - now.Left != w || now.Bottom - now.Top != h))
            NativeMethods.SetWindowPos(hwnd, 0, x, y, w, h, NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER);
    }

    const long WS_EX_TOPMOST = 0x00000008;

    static readonly Dictionary<string, Brush> brushes = new(StringComparer.OrdinalIgnoreCase);

    // Frozen, because a static brush that is not takes the thread's affinity (CLAUDE.md, Traps).
    static Brush BrushOf(string colour)
    {
        if (brushes.TryGetValue(colour, out var known)) return known;
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colour));
        brush.Freeze();
        return brushes[colour] = brush;
    }
}
