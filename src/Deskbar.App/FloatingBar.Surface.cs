using System.Windows;
using System.Windows.Media;

namespace Deskbar.App;

// WHAT THE BAR STANDS ON, band by band. Three layers lie behind the bar's content, and the Root itself is transparent:
//
//   TopSurface    from the bar's top edge to the bottom of the pinned row (Start, search, pinned apps): painted as the
//                 rows band is (flat until task d52ca36e; PaintBand).
//   RowsSurface   from there to the top of the footer, behind the workspace rows: see-through by default.
//   FootSurface   from the footer (stats, tray, clock) to the bar's bottom edge: flat.
//
// The history, because it explains the shape. The bar was flat #141518; Petre, 2026-11-22: "too black". He picked a
// grain texture, then "this and the top shouldn't be grained", so the grain went behind the rows only. Then, shown
// the bar over his own wallpaper at four strengths: "let's take that instead, 85%". So the rows band keeps 85% of the
// bar's flat colour and lets 15% of the desktop through (BarSurface.SeeThrough), and the top and the footer, which are
// the taskbar's half of the bar, stayed solid. Then, 2026-11-26, of the flat top: "i want to make this like the rest of
// the bar" (Deskbar task d52ca36e), so the top now wears the band's paint, frost and all. The footer stays solid.
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
// (their -4 margins) and are moved after every layout pass. They sit in a SurfaceLayers panel, which asks the bar for
// no size at all, because the bar is SizeToContent: a layer's desired height used to count, and the foot layer's was the
// bar's whole height, so the layers held the bar at its last height and it never shrank (Deskbar task 888cd56b, the
// undocked bar kept the screen's height). Their desired heights no longer matter, only where they are drawn.
//
// A fourth layer, FrostSurface, lies under all three: the wallpaper behind the bar, blurred (FloatingBar.Frost.cs),
// docked and, since task b971ca3f, floating too. The band then lays a lighter veil over it (BarSurface.FrostVeil)
// instead of the 85%. It takes the Root's corners (FloatingBar.xaml), so a floating bar's frost is round like the bar.
//
// The setting (AppState.SeeThrough, on by default): off, the rows band is as flat as the rest, frost or not.
public partial class FloatingBar
{
    // The pinned row's box on show: the see-through band starts under it. GroupRow puts each rebuild's new one in
    // builtPinnedRowBox, and SwapInRows makes it this one when the new rows replace the old (task c1640d1f), so a frame
    // drawn while a rebuild is under way still lays the band under the row that is actually drawn.
    FrameworkElement? pinnedRowBox;
    FrameworkElement? builtPinnedRowBox;

    // How far the layers bleed past the content, the Root's own Padding.
    const double SurfaceBleed = 4;

    // Called on every rebuild: the band follows the setting, and the frost is checked for change (FloatingBar.Frost.cs;
    // the check is coalesced to twice a second, and costs next to nothing when nothing changed).
    void ApplySurfaceSetting()
    {
        PaintBand();
        ScheduleFrost();
    }

    // The band's own paint. Flat with the setting off. With it on: over a frost (a docked bar, FloatingBar.Frost.cs)
    // the band is a light veil so the frost's colour comes through; with no frost it is 85% of the bar's colour, as
    // before. Called by the rebuild and by every frost that arrives or goes, and never schedules a check itself, or a
    // band without frost would ask again every half second for ever.
    void PaintBand()
    {
        var frosted = manager.State.SeeThrough && frostSource is not null;
        // Over a docked frost the veil eases in from the inner edge with the frost itself (BarSurface.FrostVeilFrom).
        // A floating frost has no inner edge (task b971ca3f), so its veil is the plain half all the way across.
        RowsSurface.Background = !manager.State.SeeThrough ? BarSurface.Solid
            : frostSource is { Floating: true } && frosted ? BarSurface.FrostVeil
            : frostSource is { } source && frosted ? BarSurface.FrostVeilFrom(source.FromLeft, source.Ease)
            : BarSurface.SeeThrough;
        // The top (Start, search, pinned apps) wears the band's own paint, so the bar reads as one surface from its top
        // edge down to the footer (Deskbar task d52ca36e, Petre: "i want to make this like the rest of the bar"). The
        // eased veil is relative to its box, and the top is as wide as the band, so the two ramps line up.
        TopSurface.Background = RowsSurface.Background;
        FrostSurface.Visibility = frosted ? Visibility.Visible : Visibility.Collapsed;
    }

    // Called after every layout pass (LayoutUpdated). Cheap: a few TranslatePoints, and the layers are only touched
    // when their bands actually moved, so placing them cannot start another pass that places them again.
    void PlaceSurfaces()
    {
        // The bar's Grid, which holds both the content and the SurfaceLayers panel at the same origin and size: the
        // pinned row and the footer are measured against it, and the layers, drawn in a panel of that same box, land
        // on those numbers unchanged.
        if (BarContent.Parent is not FrameworkElement surface) return;
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

    // The bar's corners: round floating, square docked. The top and foot layers follow the Root, the frost does by its
    // binding (FloatingBar.xaml). They used to keep 8 DIP corners docked too, which never showed while the top was flat
    // over a flat bar, but over a frost the top's corner would leave a sliver of unveiled picture in each top corner.
    void SetCorners(double radius)
    {
        Root.CornerRadius = new CornerRadius(radius);
        TopSurface.CornerRadius = new CornerRadius(radius, radius, 0, 0);
        FootSurface.CornerRadius = new CornerRadius(0, 0, radius, radius);
    }

    // A flat patch of "bar" for drawing with a DrawingContext (the arrange drag's gap and hint). The Root is
    // transparent now, so its Background is no longer the colour to paint with.
    static Brush BarFill => BarSurface.Solid;
}
