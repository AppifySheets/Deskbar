using System.Windows;

namespace Deskbar.App;

// WHERE THE GRAIN GOES. The bar stands on BarSurface's flat base, and the grain (BarSurface.Textured) sits only behind the
// workspace rows: from the bottom of the pinned row, which carries Start, search and the pinned apps, down to the top
// of the footer, which carries the stats, the tray and the clock. Petre, 2026-11-22, with a screenshot of the footer:
// "this and the top shouldn't be grained". Those two are the taskbar's half of the bar and read as controls; the grain
// belongs to the workspaces.
//
// Why a layer and not a background on the rows. The rows are a StackPanel inside the Root's 4 DIP padding, so a
// background there would leave a flat frame round the grain, and the pinned row is the first child of Rows, so the
// grain cannot simply start at Rows. GrainLayer sits behind BarContent in the same Grid, bleeds out over the Root's
// padding to both edges (its -4 side margins), and is moved to the band between the two after every layout pass.
//
// Whether there is grain at all is a setting (AppState.Grain, on by default: Petre, "make grain a default feature,
// configurable in settings"), read on every rebuild, which the state pulse of Manage's box or `deskbar grain` starts.
public partial class FloatingBar
{
    // The pinned row's box, set by GroupRow each rebuild: the grain starts under it.
    FrameworkElement? pinnedRowBox;

    // Called on every rebuild: shows or hides the grain as the setting says.
    void ApplyGrainSetting() =>
        GrainLayer.Background = manager.State.Grain ? BarSurface.Textured : null;

    // Called after every layout pass (LayoutUpdated). Cheap: two TranslatePoints, and the layer is only touched when
    // its band actually moved, so setting it cannot start another pass that sets it again.
    void PlaceGrain()
    {
        if (GrainLayer.Parent is not UIElement surface) return;
        var top = pinnedRowBox is { IsVisible: true } box && box.IsDescendantOf(surface)
            ? box.TranslatePoint(new Point(0, box.ActualHeight), surface).Y
            : 0;
        var bottom = ShellFooter.IsVisible && ShellFooter.IsDescendantOf(surface)
            ? ShellFooter.TranslatePoint(new Point(0, 0), surface).Y
            : ((FrameworkElement)surface).ActualHeight;
        var height = Math.Max(0, bottom - top);

        // Rounded to a tenth of a DIP, so layout rounding's sub-pixel jitter does not count as a move.
        top = Math.Round(top, 1);
        height = Math.Round(height, 1);
        if (Math.Abs(GrainLayer.Margin.Top - top) < 0.05 && Math.Abs(GrainLayer.Height - height) < 0.05) return;
        GrainLayer.Margin = new Thickness(GrainLayer.Margin.Left, top, GrainLayer.Margin.Right, 0);
        GrainLayer.Height = height;
    }
}
