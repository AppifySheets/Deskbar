using System.Windows;
using System.Windows.Media;

namespace Deskbar.App;

// WHAT THE BAR STANDS ON, band by band. Three layers lie behind the bar's content, and the Root itself is transparent:
//
//   TopSurface    from the bar's top edge to the bottom of the pinned row (Start, search, pinned apps): flat.
//   RowsSurface   from there to the top of the footer, behind the workspace rows: see-through by default.
//   FootSurface   from the footer (stats, tray, clock) to the bar's bottom edge: flat.
//
// The history, because it explains the shape. The bar was flat #141518; Petre, 2026-11-22: "too black". He picked a
// grain texture, then "this and the top shouldn't be grained", so the grain went behind the rows only. Then, shown
// the bar over his own wallpaper at four strengths: "let's take that instead, 85%". So the rows band keeps 85% of the
// bar's flat colour and lets 15% of the desktop through (BarSurface.SeeThrough), and the top and the footer, which are
// the taskbar's half of the bar, stay solid.
//
// Active rows stay solid through the fade's plate (FloatingBar.Recency.cs): a workspace row used lately covers the
// band with an opaque plate of BarSurface.Base that wears away as the row ages, so an idle row turns see-through
// gradually. The legibility argument that made the bar opaque (FloatingBar.xaml) is why it is 85% and not less: at
// 70% the grey captions of idle rows started to sink into a bright sky in the mockup. Every row with recent use is
// solid at any strength.
//
// Why layers, measured from the layout, and not backgrounds on panels. The rows are a StackPanel inside the Root's
// 4 DIP padding, so a background there would leave a frame of something else round them, and the pinned row is the
// first child of Rows, so the band cannot simply start at Rows. The layers bleed over the padding to the bar's edges
// (their -4 margins) and are moved after every layout pass. Each one's DESIRED height stays inside the content's own
// height (the -4 margins pay for the bleed), because the bar is SizeToContent: a layer that asked for 4 DIP more
// would grow the bar, which would grow the layer, every pass.
//
// The setting (AppState.SeeThrough, on by default): off, the rows band is as flat as the rest.
public partial class FloatingBar
{
    // The pinned row's box, set by GroupRow each rebuild: the see-through band starts under it.
    FrameworkElement? pinnedRowBox;

    // How far the layers bleed past the content, the Root's own Padding.
    const double SurfaceBleed = 4;

    // Called on every rebuild: the band follows the setting.
    void ApplySurfaceSetting() =>
        RowsSurface.Background = manager.State.SeeThrough ? BarSurface.SeeThrough : BarSurface.Solid;

    // Called after every layout pass (LayoutUpdated). Cheap: a few TranslatePoints, and the layers are only touched
    // when their bands actually moved, so placing them cannot start another pass that places them again.
    void PlaceSurfaces()
    {
        if (RowsSurface.Parent is not FrameworkElement surface) return;
        var total = surface.ActualHeight;
        var top = pinnedRowBox is { IsVisible: true } box && box.IsDescendantOf(surface)
            ? box.TranslatePoint(new Point(0, box.ActualHeight), surface).Y
            : 0;
        var bottom = ShellFooter.IsVisible && ShellFooter.IsDescendantOf(surface)
            ? ShellFooter.TranslatePoint(new Point(0, 0), surface).Y
            : total;

        // Rounded to a tenth of a DIP, so layout rounding's sub-pixel jitter does not count as a move.
        top = Math.Round(Math.Clamp(top, 0, total), 1);
        bottom = Math.Round(Math.Clamp(bottom, top, total), 1);
        total = Math.Round(total, 1);

        // Top: from the bar's edge (-4) down to the band. Desired height -4 + (top + 4) = top.
        Place(TopSurface, new Thickness(-SurfaceBleed, -SurfaceBleed, -SurfaceBleed, 0), top + SurfaceBleed);
        // Band: exactly between. Desired height bottom.
        Place(RowsSurface, new Thickness(-SurfaceBleed, top, -SurfaceBleed, 0), bottom - top);
        // Foot: from the footer to the bar's edge. Desired height bottom + (total - bottom + 4) - 4 = total.
        Place(FootSurface, new Thickness(-SurfaceBleed, bottom, -SurfaceBleed, -SurfaceBleed), total - bottom + SurfaceBleed);
    }

    static void Place(FrameworkElement layer, Thickness margin, double height)
    {
        if (layer.Margin == margin && Math.Abs(layer.Height - height) < 0.05) return;
        layer.Margin = margin;
        layer.Height = height;
    }

    // A flat patch of "bar" for drawing with a DrawingContext (the arrange drag's gap and hint). The Root is
    // transparent now, so its Background is no longer the colour to paint with.
    static Brush BarFill => BarSurface.Solid;
}
