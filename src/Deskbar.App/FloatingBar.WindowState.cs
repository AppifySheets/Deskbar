using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Deskbar.Core.Domain;
using Deskbar.Core.Overview;
using Deskbar.Windows.Monitoring;

namespace Deskbar.App;

// WHETHER A WINDOW IS MAXIMIZED OR IN A TILE, on its icon. Petre: "show a sign next to each window that will tell me
// if it's maximized or part of a tile". From three mockups (a glyph at the end of a strip tab, a badge on the icon, a
// MAX/TILE word) he took the badge, "b": the one that works on the bar's icons as well as the strips' tabs, since it
// sits on the icon and needs no title.
//
// A small dark square on the icon's BOTTOM-LEFT corner, the last free one: the pin badge holds the top-left, the tab
// count and the attention dot the top-right. The family band runs along the bottom edge, so the badge covers its left
// end; the band is still read from the rest of its length. Dark with a light glyph for the reason the pin badge and the
// attention dot are: it has to hold on artwork nobody chose. The glyphs are Windows' own: the maximize button's square
// for a maximized window, a two-by-two grid for a window in a tile. A window that is neither gets no badge.
//
// Maximized wins over tiled: a maximized window in a tiled workspace covers its screen whatever its slot says.
//
// Overlaid, never laid out, like the pin badge: it hangs past the corner on a negative margin, so the icon's cell
// measures what the icon alone measures and no row grows (the sound ring's lesson, FloatingBar.Activity.Dressed).
public partial class FloatingBar
{
    internal enum WindowFit { None, Maximized, Tiled }

    internal const string WindowFitBadgeTag = "window-fit-badge";

    // Whether a window is maximized. A seam for the tests, which have no real windows; IsZoomed is a cheap call, and
    // it answers for a window on another virtual desktop too.
    internal Func<WindowHandle, bool> IsMaximized { get; init; } = handle => NativeMethods.IsZoomed(handle.Value);

    // Whether a window is in a tile. The Tiler lives in App and is built after the bar, so App passes a lambda that
    // reads it when asked (its Board: the current workspace's tiles). Windows of a tiled workspace you are not on are
    // not on that board, so they show no tile badge until you are there.
    internal Func<WindowHandle, bool> IsTiled { get; init; } = _ => false;

    internal WindowFit FitOf(WindowHandle handle) =>
        IsMaximized(handle) ? WindowFit.Maximized
        : IsTiled(handle) ? WindowFit.Tiled
        : WindowFit.None;

    // What each badge drawn by the last rebuild said, so the 1 s tick can tell when one is out of date. A window
    // maximized by its own title bar, or a tile formed by the Tiler, raises nothing the bar rebuilds on by itself, so
    // without this the badge would wait for an unrelated rebuild (RefreshWindowFits).
    readonly Dictionary<WindowHandle, WindowFit> fitsShown = [];

    void ForgetWindowFits() => fitsShown.Clear();

    UIElement WithWindowFit(UIElement icon, WindowRow row)
    {
        var fit = FitOf(row.Window.Handle);
        fitsShown[row.Window.Handle] = fit;
        return fit == WindowFit.None ? icon : WithFitBadge(icon, fit);
    }

    internal static UIElement WithFitBadge(UIElement icon, WindowFit fit)
    {
        var cell = new Grid();
        cell.Children.Add(icon);

        var badge = new Grid
        {
            Tag = WindowFitBadgeTag,
            Width = 10,
            Height = 10,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(-3, 0, 0, -3),
            IsHitTestVisible = false,
            ToolTip = null,
        };
        badge.Children.Add(new Rectangle { Fill = PinBadgeFill, RadiusX = 2, RadiusY = 2 });
        badge.Children.Add(fit == WindowFit.Maximized
            ? new Path { Data = MaximizedShape, Stroke = PinBadgeInk, StrokeThickness = 1 }
            : new Path { Data = TiledShape, Fill = PinBadgeInk });
        cell.Children.Add(badge);
        return cell;
    }

    // On the badge's 10 unit square. The maximize button's square, outlined; and four panes, filled, with a one unit
    // gap between them, which is what reads as a grid at this size.
    static readonly Geometry MaximizedShape = FrozenGeometry("M2.5,2.5 H7.5 V7.5 H2.5 Z");
    static readonly Geometry TiledShape = FrozenGeometry("M2,2 H4.5 V4.5 H2 Z M5.5,2 H8 V4.5 H5.5 Z M2,5.5 H4.5 V8 H2 Z M5.5,5.5 H8 V8 H5.5 Z");

    // Called on App's 1 s tick: when any window the last rebuild badged now fits differently, rebuild, so the badge
    // follows a maximize, a restore or a tile within a second. Nothing happens while every badge is still right, which
    // is nearly every tick.
    internal void RefreshWindowFits()
    {
        if (fitsShown.Count == 0 || rebuilding) return;
        if (fitsShown.Any(shown => FitOf(shown.Key) != shown.Value)) RequestRebuild();
    }
}
