using CSharpFunctionalExtensions;

namespace Deskbar.Core.Geometry;

// How Windows lays the wallpaper on a screen: IDesktopWallpaper's DESKTOP_WALLPAPER_POSITION, in its own order, so
// the Windows side can cast the number straight across.
public enum WallpaperFit
{
    Center = 0,
    Tile = 1,
    Stretch = 2,
    Fit = 3,
    Fill = 4,
    Span = 5,
}

// WHICH PART OF THE WALLPAPER PICTURE IS BEHIND THE DOCKED BAR, so the bar can paint that part, blurred, behind its
// idle workspace rows (the frosted band, FloatingBar.Frost.cs). Petre, shown the see-through band over his laptop's
// spring tree: "could you do transparency that's not black-ish?", then picked the frosted wallpaper from a mockup.
//
// WPF cannot blur what lies behind its own window, but a DOCKED bar is a shell app bar: the shell keeps every window
// out of its strip, so the only thing ever behind it is the wallpaper. The bar can therefore draw that itself, from
// the picture file, if it knows where on the picture its strip falls. That is this question, and it is pure
// rectangle arithmetic, so it lives in Core with tests rather than in the window.
//
// Everything is in PHYSICAL PIXELS on the virtual desktop, as the shell and GetWindowRect report them.
public static class WallpaperCrop
{
    // A rectangle in physical pixels, Left/Top/Right/Bottom like FullScreenCover.Rect.
    public readonly record struct Rect(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    // A rectangle in the PICTURE's own pixels. Fractional, because a scaled picture's pixel edges rarely fall on the
    // screen's; the caller rounds once, when it cuts.
    public readonly record struct Area(double X, double Y, double Width, double Height);

    // Within this many picture pixels of the edge still counts as on the picture: the scale's rounding, not a gap.
    const double Slack = 0.5;

    // The part of a picture `imageWidth` x `imageHeight` that Windows draws under `target` (the docked bar's
    // rectangle), when the picture is laid with `fit` on `screen`, the bar's monitor. `desktop` is every monitor's
    // rectangle together, which only Span uses: it lays one picture across all of them.
    //
    // Nothing when there is no single answer: a tiled picture repeats, and a bar over Fit's or Center's background
    // colour has no picture behind it at all. The caller then keeps the plain see-through band. Also nothing when the
    // bar is not inside its own screen, which happens for a moment while a screen wakes (FloatingBar.DockGuard.cs has
    // the measurement): better the plain band for a second than a picture from the wrong place.
    public static Maybe<Area> SourceOf(WallpaperFit fit, Rect screen, Rect desktop, int imageWidth, int imageHeight, Rect target)
    {
        if (imageWidth <= 0 || imageHeight <= 0 || target.Width <= 0 || target.Height <= 0) return Maybe<Area>.None;
        if (!Inside(target, screen) || fit == WallpaperFit.Tile) return Maybe<Area>.None;

        // The rectangle the picture is laid on: the whole desktop for Span, the bar's screen otherwise.
        var frame = fit == WallpaperFit.Span ? desktop : screen;
        if (frame.Width <= 0 || frame.Height <= 0) return Maybe<Area>.None;

        // Screen pixels per picture pixel, across and down. Fill (and Span, which is Fill over the desktop) scales
        // up until both sides are covered; Fit until one side meets the frame; Stretch each side on its own; Center
        // not at all.
        var (scaleX, scaleY) = fit switch
        {
            WallpaperFit.Stretch => ((double)frame.Width / imageWidth, (double)frame.Height / imageHeight),
            WallpaperFit.Fill or WallpaperFit.Span => Same(Math.Max((double)frame.Width / imageWidth, (double)frame.Height / imageHeight)),
            WallpaperFit.Fit => Same(Math.Min((double)frame.Width / imageWidth, (double)frame.Height / imageHeight)),
            _ => Same(1.0),
        };

        // Every fit but Stretch centres the scaled picture on the frame; Stretch fills it exactly, so its offset is 0
        // by the same sum.
        var originX = frame.Left + (frame.Width - imageWidth * scaleX) / 2;
        var originY = frame.Top + (frame.Height - imageHeight * scaleY) / 2;

        var area = new Area(
            (target.Left - originX) / scaleX,
            (target.Top - originY) / scaleY,
            target.Width / scaleX,
            target.Height / scaleY);

        // The bar must be over the picture everywhere, or part of what is behind it is background colour.
        return area.X >= -Slack && area.Y >= -Slack
            && area.X + area.Width <= imageWidth + Slack && area.Y + area.Height <= imageHeight + Slack
            ? Maybe.From(Clamp(area, imageWidth, imageHeight))
            : Maybe<Area>.None;
    }

    static (double, double) Same(double scale) => (scale, scale);

    static bool Inside(Rect inner, Rect outer) =>
        inner.Left >= outer.Left && inner.Top >= outer.Top && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;

    // Pulls the slack back onto the picture, so the caller can cut exactly this without checking it again.
    static Area Clamp(Area area, int imageWidth, int imageHeight)
    {
        var x = Math.Clamp(area.X, 0, imageWidth);
        var y = Math.Clamp(area.Y, 0, imageHeight);
        return new Area(x, y, Math.Min(area.Width, imageWidth - x), Math.Min(area.Height, imageHeight - y));
    }
}
