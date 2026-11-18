using System.Runtime.InteropServices;
using Deskbar.Core.Abstractions;

namespace Deskbar.Windows.Tiling;

// The translucent rectangles a tile drag shows where things will land (ITileSurface.Preview, Tiler.Drag.cs). Petre:
// "can you resize, or show me how it would end up on the screen, before i let it go?", and from the mockup, a
// translucent preview for moves.
//
// PLAIN WIN32 WINDOWS, NOT WPF. The first version was a WPF HwndSource with SetLayeredWindowAttributes for the
// translucency, and it came out SOLID: WPF draws its own windows and ignored the layered alpha, so the preview hid the
// whole screen under two opaque blue rectangles (Petre, with a screenshot: "what are those"). A window class with a
// solid brush and LWA_ALPHA is composited by Windows itself; that is the one path here.
//
// Each rectangle is a window of Deskbar's own, built so it can never get in the way:
//   * WS_EX_TRANSPARENT + WS_EX_LAYERED: clicks and the drag itself go straight through it.
//   * WS_EX_NOACTIVATE, and shown with SW_SHOWNOACTIVATE: never takes focus, so the dragged window keeps its move loop.
//   * WS_EX_TOOLWINDOW: no taskbar button, so no window list sees it, the bar's included (TopLevelWindows skips
//     tool windows), and the Tiler never tiles it (it is also this process's own window).
//   * WS_EX_TOPMOST: above the tiles it is drawn over.
//
// Created when a preview is shown and DESTROYED when it is taken away, never hidden: SW_HIDE is not used anywhere in
// this codebase (NativeMethods.SW_RESTORE's comment), and a drag starts seldom enough that creating two windows costs
// nothing anyone sees. Physical pixels throughout, as CreateWindowEx and SetWindowPos both take them.
sealed class TilePreviewOverlay : IDisposable
{
    // AN OUTLINE, NOTHING ELSE. Petre, on the first translucent version: "the old rectangle, that's being left, is
    // too bright; also make the new rectangle just the border", then "no need for the blue glow". So only where the
    // dragged tile LANDS is drawn, as a ring BorderWidth wide; the place it leaves, and where a swapped tile goes,
    // get nothing (TilePreview.Dragged false is skipped). The ring is the window's REGION (SetWindowRgn), so the
    // window does not exist inside it at all: no painting code, and the tile underneath is untouched.
    internal const byte BorderAlpha = 0xD0;
    internal const int BorderWidth = 4;

    // The bar's own light blue (FloatingBar's StartBlue, #4CC2FF), as a COLORREF (0x00BBGGRR).
    const uint Tint = 0x00FFC24C;

    const string ClassName = "DeskbarTilePreview";

    readonly List<nint> windows = [];

    // The handles, for the tests.
    internal IReadOnlyList<nint> Windows => windows;

    public void Show(IReadOnlyList<TilePreview> all)
    {
        // Only the landing place is drawn (see BorderAlpha).
        var tiles = all.Where(t => t.Dragged).ToList();
        // Fewer rectangles than before: the extra ones go.
        while (windows.Count > tiles.Count)
        {
            _ = DestroyWindow(windows[^1]);
            windows.RemoveAt(windows.Count - 1);
        }
        for (var i = 0; i < tiles.Count; i++)
        {
            if (i < windows.Count) Move(windows[i], tiles[i]);
            else if (Create(tiles[i]) is var hwnd and not 0) windows.Add(hwnd);
        }
    }

    public void Dispose() => Show([]);

    static nint Create(TilePreview tile)
    {
        if (!Registered()) return 0;
        var f = tile.Frame;
        var hwnd = CreateWindowExW(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
            ClassName, "Deskbar tile preview", WS_POPUP, f.Left, f.Top, f.Width, f.Height, 0, 0, GetModuleHandleW(null), 0);
        if (hwnd == 0) return 0;
        // The alpha and the ring before the window is first shown, so it never appears filled even for a frame.
        _ = SetLayeredWindowAttributes(hwnd, 0, BorderAlpha, LWA_ALPHA);
        Ring(hwnd, f.Width, f.Height);
        _ = ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        return hwnd;
    }

    static void Move(nint hwnd, TilePreview tile)
    {
        var f = tile.Frame;
        _ = SetWindowPos(hwnd, HWND_TOPMOST, f.Left, f.Top, f.Width, f.Height, SWP_NOACTIVATE);
        Ring(hwnd, f.Width, f.Height);
    }

    // The window's region becomes a ring BorderWidth wide around its edge (window coordinates). SetWindowRgn takes
    // ownership of the region, so only the inner one, used to cut the hole, is deleted here.
    static void Ring(nint hwnd, int width, int height)
    {
        var outer = CreateRectRgn(0, 0, width, height);
        var inner = CreateRectRgn(BorderWidth, BorderWidth, Math.Max(BorderWidth, width - BorderWidth), Math.Max(BorderWidth, height - BorderWidth));
        _ = CombineRgn(outer, outer, inner, RGN_DIFF);
        _ = DeleteObject(inner);
        _ = SetWindowRgn(hwnd, outer, true);
    }

    const int RGN_DIFF = 4;

    [DllImport("gdi32.dll")]
    static extern nint CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    static extern int CombineRgn(nint destination, nint a, nint b, int mode);

    [DllImport("gdi32.dll")]
    static extern bool DeleteObject(nint obj);

    [DllImport("user32.dll")]
    static extern int SetWindowRgn(nint hwnd, nint region, bool redraw);

    // The window class, registered once per process: a solid brush in the tint, painted by DefWindowProc on
    // WM_ERASEBKGND, so the window needs no painting code of its own.
    static bool registered;

    static bool Registered()
    {
        if (registered) return true;
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc),
            hInstance = GetModuleHandleW(null),
            hbrBackground = CreateSolidBrush(Tint),
            lpszClassName = ClassName,
        };
        registered = RegisterClassExW(ref wc) != 0;
        return registered;
    }

    // CRITICAL: the window procedure delegate lives in a static field. One the GC collected while a window of the
    // class still exists would leave user32 calling freed memory.
    static readonly WndProc Proc = DefWindowProcW;

    delegate nint WndProc(nint hwnd, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    const uint WS_POPUP = 0x80000000;
    const uint WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
    const uint LWA_ALPHA = 0x2, SWP_NOACTIVATE = 0x10;
    const int SW_SHOWNOACTIVATE = 4;
    static readonly nint HWND_TOPMOST = -1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern nint CreateWindowExW(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);

    [DllImport("gdi32.dll")]
    static extern nint CreateSolidBrush(uint color);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern nint GetModuleHandleW(string? module);
}
