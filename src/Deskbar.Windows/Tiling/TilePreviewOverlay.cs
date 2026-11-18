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
    // Where the dragged tile lands is tinted more strongly than where the tile it swaps with goes; both light enough
    // that the windows underneath stay readable.
    internal const byte DraggedAlpha = 0x50;
    internal const byte SwappedAlpha = 0x28;

    // The bar's own light blue (FloatingBar's StartBlue, #4CC2FF), as a COLORREF (0x00BBGGRR).
    const uint Tint = 0x00FFC24C;

    const string ClassName = "DeskbarTilePreview";

    readonly List<nint> windows = [];

    // The handles, for the tests.
    internal IReadOnlyList<nint> Windows => windows;

    public void Show(IReadOnlyList<TilePreview> tiles)
    {
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
        // The alpha before the window is first shown, so it never appears opaque even for a frame.
        _ = SetLayeredWindowAttributes(hwnd, 0, AlphaOf(tile), LWA_ALPHA);
        _ = ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        return hwnd;
    }

    static void Move(nint hwnd, TilePreview tile)
    {
        var f = tile.Frame;
        _ = SetLayeredWindowAttributes(hwnd, 0, AlphaOf(tile), LWA_ALPHA);
        _ = SetWindowPos(hwnd, HWND_TOPMOST, f.Left, f.Top, f.Width, f.Height, SWP_NOACTIVATE);
    }

    static byte AlphaOf(TilePreview tile) => tile.Dragged ? DraggedAlpha : SwappedAlpha;

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
