namespace Deskbar.Core.Domain;

// HOW WIDE THE DOCKED BAR'S SOFT INNER EDGE IS: the strip along the side facing the desktop where the frost eases out
// of the sharp wallpaper (Frost.Ease) and the veil over it eases in from nothing (BarSurface.FrostVeilFrom).
//
// The history. A hard inner edge drew a line down the screen; Petre asked to "ease into it" and to "make edges less
// pronounced", so the ease shipped over 35% of the bar's width. Then "easing-in is too much; little less width for the
// easing": a fifth. Then, 2026-11-25, with a screenshot of row text standing on the half-eased wallpaper: "easing in
// from the edge in terms of transparency should be sharper, so text is unaffected". A share of the width cannot
// promise that, because the rows run right up to the bar's padding, so any share wide enough to see reaches the text.
//
// So the ease is a fixed width in DIP, the bar's own padding (the Root's Padding, FloatingBar.xaml, and
// FloatingBar.SurfaceBleed): nothing in a row stands inside it, so every caption and icon lies on the full frost and
// the full veil, and the edge is still a short ramp rather than a step.
//
// Pure and in Core, like FrostBlur, so the arithmetic is testable without a window.
public static class FrostEdge
{
    // The ease's width in DIP. Matches the bar's padding; widen it and the ramp reaches the rows' text again.
    public const double Width = 4;

    // The ease as a share of the bar's width, which is what Frost.Ease and the veil's gradient take: both cover the
    // whole bar, so one share puts the picture and the veil on the same ramp. `barWidthPixels` is the window's width
    // in screen pixels and `pixelsPerDip` its screen's scale (1.5 at 150%). A bar narrower than the ease gets the whole
    // width, never a share past 1. A nonsense input (zero, negative, NaN) gets no ease at all, which is the hard edge
    // the bar had before the ease existed: a visible line, but nothing that spreads over the text.
    public static double ShareOf(double barWidthPixels, double pixelsPerDip) =>
        barWidthPixels > 0 && pixelsPerDip > 0 && double.IsFinite(barWidthPixels) && double.IsFinite(pixelsPerDip)
            ? Math.Min(1, Width * pixelsPerDip / barWidthPixels)
            : 0;
}
