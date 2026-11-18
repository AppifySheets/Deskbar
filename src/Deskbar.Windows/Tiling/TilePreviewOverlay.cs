using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using Deskbar.Core.Abstractions;

namespace Deskbar.Windows.Tiling;

// The translucent rectangles a tile drag shows where things will land (ITileSurface.Preview, Tiler.Drag.cs). Petre:
// "can you resize, or show me how it would end up on the screen, before i let it go?", and from the mockup, a
// translucent preview for moves.
//
// Each rectangle is a window of Deskbar's own, built so it can never get in the way:
//   * WS_EX_TRANSPARENT + WS_EX_LAYERED: clicks and the drag itself go straight through it.
//   * WS_EX_NOACTIVATE: never takes focus, so the window being dragged keeps its move loop.
//   * WS_EX_TOOLWINDOW: no taskbar button, so no window list sees it, the bar's included (TopLevelWindows skips
//     tool windows), and the Tiler never tiles it (it is also this process's own window).
//   * WS_EX_TOPMOST: above the tiles it is drawn over.
//
// Created when a preview is shown and DESTROYED when it is taken away, never hidden: SW_HIDE is not used anywhere in
// this codebase (NativeMethods.SW_RESTORE's comment), and a drag starts seldom enough that creating two windows costs
// nothing anyone sees. Physical pixels throughout, as HwndSourceParameters and SetWindowPos both take them.
sealed class TilePreviewOverlay : IDisposable
{
    // Where the dragged tile lands is filled more strongly than where the tile it swaps with goes.
    const byte DraggedAlpha = 0x60;
    const byte SwappedAlpha = 0x30;

    // The bar's own light blue (FloatingBar's StartBlue), so the preview reads as Deskbar's.
    static readonly Color Tint = Color.FromRgb(0x4C, 0xC2, 0xFF);

    readonly List<HwndSource> windows = [];

    public void Show(IReadOnlyList<TilePreview> tiles)
    {
        // Fewer rectangles than before: the extra ones go.
        while (windows.Count > tiles.Count)
        {
            windows[^1].Dispose();
            windows.RemoveAt(windows.Count - 1);
        }
        for (var i = 0; i < tiles.Count; i++)
        {
            if (i < windows.Count) Move(windows[i], tiles[i]);
            else windows.Add(Create(tiles[i]));
        }
    }

    public void Dispose() => Show([]);

    static HwndSource Create(TilePreview tile)
    {
        var f = tile.Frame;
        var source = new HwndSource(new HwndSourceParameters("Deskbar tile preview")
        {
            WindowStyle = unchecked((int)(WS_POPUP | WS_VISIBLE)),
            ExtendedWindowStyle = WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
            PositionX = f.Left,
            PositionY = f.Top,
            Width = f.Width,
            Height = f.Height,
        });
        source.CompositionTarget.BackgroundColor = Tint;
        _ = SetLayeredWindowAttributes(source.Handle, 0, tile.Dragged ? DraggedAlpha : SwappedAlpha, LWA_ALPHA);
        return source;
    }

    static void Move(HwndSource source, TilePreview tile)
    {
        var f = tile.Frame;
        _ = SetLayeredWindowAttributes(source.Handle, 0, tile.Dragged ? DraggedAlpha : SwappedAlpha, LWA_ALPHA);
        _ = SetWindowPos(source.Handle, HWND_TOPMOST, f.Left, f.Top, f.Width, f.Height, SWP_NOACTIVATE);
    }

    const uint WS_POPUP = 0x80000000, WS_VISIBLE = 0x10000000;
    const int WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
    const uint LWA_ALPHA = 0x2, SWP_NOACTIVATE = 0x10;
    static readonly nint HWND_TOPMOST = -1;

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
}
