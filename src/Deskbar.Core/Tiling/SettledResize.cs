using Deskbar.Core.Domain;

namespace Deskbar.Core.Tiling;

// Which edges of a window a resize moved. Flags so a corner drag is Right | Bottom.
[Flags]
public enum Edges { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

// Tells a RESIZE by hand from a move, for a tiled window whose frame has settled somewhere other
// than where the grid put it. Petre: "i want to be able to resize tiles by dragging the
// separation". The test (from the tile-resize plan): one or two adjacent edges moved, the edges
// opposite them within 2 px of where they were, AND a mouse button down during the change.
// Anything else is a move and keeps the existing rules (swap, put back).
public static class SettledResize
{
    // An edge that differs by no more than this is treated as not having moved: dragging one edge
    // of a window often nudges the opposite one by a pixel or two (DWM frame rounding).
    const int Tolerance = 2;

    // The moved edges for a resize, Edges.None for a move or anything that is not a resize.
    // `accepted` is where the grid put the window, `frame` where it settled.
    public static Edges Classify(WindowRect accepted, WindowRect frame, bool byHand) =>
        byHand
            ? Combine(
                OneAxis(frame.Left, accepted.Left, Edges.Left, frame.Right, accepted.Right, Edges.Right),
                OneAxis(frame.Top, accepted.Top, Edges.Top, frame.Bottom, accepted.Bottom, Edges.Bottom))
            : Edges.None;

    // A move on either axis makes the whole change a move.
    static Edges Combine(Edges? horizontal, Edges? vertical) =>
        horizontal is { } h && vertical is { } v ? h | v : Edges.None;

    // One axis: null when BOTH of its edges moved (a shift or a symmetric change, so a move),
    // otherwise the edge that moved, or Edges.None when neither did.
    static Edges? OneAxis(int nearNow, int nearThen, Edges near, int farNow, int farThen, Edges far) =>
        (Math.Abs(nearNow - nearThen) > Tolerance, Math.Abs(farNow - farThen) > Tolerance) switch
        {
            (true, true) => null,
            (true, false) => near,
            (false, true) => far,
            _ => Edges.None,
        };
}
