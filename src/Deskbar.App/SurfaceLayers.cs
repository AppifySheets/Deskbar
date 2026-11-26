using System.Windows;
using System.Windows.Controls;

namespace Deskbar.App;

/// <summary>
/// Holds the bar's surface layers (FloatingBar.Surface.cs) and never asks the bar for any size. Its children are
/// measured as usual, so they lay out as before, but the panel's own desired size is zero, so they are drawn over
/// the whole bar without ever deciding how tall it is.
/// </summary>
// Why this exists (Deskbar task 888cd56b, "undocked bar is needlessly tall"): the bar is SizeToContent, and the layers
// sat straight in its Grid. PlaceSurfaces gives each layer an explicit margin and height after every layout, and the
// foot layer's added up to the bar's whole height. The Grid sizes to its largest child, so the layers held the bar at
// whatever height it last had: undocked, the bar kept the screen's height with an empty band above the footer, and a
// floating bar whose content shrank never shrank with it. Measured in FloatingBarSurfaceHeightTests (290 DIP of
// content, 350 DIP of bar). Inside this panel the layers still fill the Grid's cell, because the Grid arranges the
// panel at the cell's full size whatever it asked for.
public sealed class SurfaceLayers : Grid
{
    protected override Size MeasureOverride(Size constraint)
    {
        // The children are still measured (an element arranged without a measure lays out at nothing); only the
        // answer given to the bar is dropped.
        base.MeasureOverride(constraint);
        return new Size(0, 0);
    }
}
