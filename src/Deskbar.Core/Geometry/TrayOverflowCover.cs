namespace Deskbar.Core.Geometry;

// Whether the bar must sit BELOW the tray's hidden-icons flyout (the "overflow") instead of on top of it.
//
// Pure, and in Core for the reason FullScreenCover is: it is a question about rectangles and two facts, and it
// has to be testable without a window. See FloatingBar.StepBelowOverflow for why the bar needs it.
public static class TrayOverflowCover
{
    // Only when the overflow is in front, is visible, and its rectangle overlaps the bar's. A flyout somewhere else
    // on the screen is none of the bar's business, and the bar keeps its place at the top of the band.
    public static bool ShouldStepBelow(bool overflowInFront, bool overflowVisible, FullScreenCover.Rect overflow, FullScreenCover.Rect bar) =>
        overflowInFront && overflowVisible && Intersects(overflow, bar);

    // Rectangles that only touch along an edge do not intersect: Right and Bottom are exclusive, as GetWindowRect's are.
    static bool Intersects(FullScreenCover.Rect a, FullScreenCover.Rect b) =>
        a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;
}
