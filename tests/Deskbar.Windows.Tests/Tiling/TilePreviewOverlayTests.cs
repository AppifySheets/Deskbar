using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Deskbar.Core.Abstractions;
using Deskbar.Core.Domain;
using Deskbar.Windows.Tiling;

namespace Deskbar.Windows.Tests.Tiling;

// The drag preview's rectangles (TilePreviewOverlay), against the real screen. The first version was solid: WPF drew
// its window opaque and ignored the layered alpha, and Petre's screen vanished under two blue rectangles ("what are
// those"). So the claim tested here is what reaches the SCREEN, read back pixel by pixel, not just the window's
// attributes.
//
// Like the other screen-capture tests, this needs an unlocked session: Windows composites nothing while it is locked.
public class TilePreviewOverlayTests
{
    static void Pump(TimeSpan span)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = span };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    // A black window the test owns, so what the preview is blended over is known.
    static Window Black()
    {
        var window = new Window
        {
            Title = "tile preview test",
            Width = 300, Height = 200, Left = 160, Top = 160,
            ShowActivated = false,
            WindowStyle = WindowStyle.None,
            Background = Brushes.Black,
            Topmost = true,
        };
        window.Show();
        return window;
    }

    // Category=Screen: reads what is really on the screen, so it needs the desktop being shown. A hidden
    // desktop (scripts/test-hidden.ps1) is never composited; CI runs it on the runner's visible desktop.
    [Trait("Category", "Screen")]
    [Fact]
    public void The_preview_is_translucent_on_screen_click_through_and_gone_when_hidden() => StaThread.Run(() =>
    {
        var black = Black();
        Pump(TimeSpan.FromMilliseconds(200));
        GetWindowRect(new WindowInteropHelper(black).Handle, out var r);
        using var overlay = new TilePreviewOverlay();

        // Where the tile lands, and the place it leaves: only the first is drawn (Petre: "no need for the blue glow").
        var frame = new WindowRect(r.Left, r.Top, r.Right, r.Bottom);
        overlay.Show([new TilePreview(frame, Dragged: true), new TilePreview(frame, Dragged: false)]);
        Pump(TimeSpan.FromMilliseconds(300));

        var hwnd = Assert.Single(overlay.Windows);
        var ex = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
        Assert.Equal(WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_LAYERED,
            ex & (WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_LAYERED));

        // An outline only: the middle is the black window underneath, untouched, and the ring is clearly blue.
        static int Blue(uint pixel) => (int)((pixel >> 16) & 0xFF);
        Assert.InRange(Blue(PixelAt((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2)), 0, 10);
        Assert.InRange(Blue(PixelAt(r.Left + 1, (r.Top + r.Bottom) / 2)), 150, 255);

        overlay.Show([]);
        Assert.Empty(overlay.Windows);
        Assert.False(IsWindow(hwnd));
        black.Close();
    });

    static uint PixelAt(int x, int y)
    {
        var dc = GetDC(0);
        try { return GetPixel(dc, x, y); }
        finally { _ = ReleaseDC(0, dc); }
    }

    const int GWL_EXSTYLE = -20;
    const long WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern long GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll")] static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")] static extern uint GetPixel(nint dc, int x, int y);
}
