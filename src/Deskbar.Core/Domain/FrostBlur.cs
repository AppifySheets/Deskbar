namespace Deskbar.Core.Domain;

// HOW BLURRED THE DOCKED BAR'S FROST IS, as one setting in screen pixels (AppState.FrostBlur, Manage's slider).
//
// The frost (FloatingBar.Frost.cs) shipped at about 28 screen pixels of blur, the mockup's CSS blur(18px) carried
// across. On his laptop's spring tree Petre: "it's too blurry", "can you make it less blurry?", "and make it a
// setting?" So the strength is a slider, and the default came down to 8: enough to take the edges out of the
// picture behind the captions, little enough that the tree still reads as a tree.
//
// The number is the blur's spread (its standard deviation) in SCREEN pixels, so it means the same on any bar width
// and any scale. The frost is worked at half size (Shrink) and blurred by three box passes; RadiusAt turns the
// setting into the box radius that gives that spread.
//
// Pure and in Core, like BarText, so the clamp and the arithmetic are testable without a window.
public static class FrostBlur
{
    public const double Default = 8;

    // 0 is a real choice: the wallpaper sharp, under the veil. 32 is a little past the strength that shipped first.
    public const double Minimum = 0;
    public const double Maximum = 32;
    public const double Step = 1;

    // How much smaller than the bar the frost is worked, in each direction. Half: small enough to be cheap (a few
    // hundred thousand pixels at most, and the box blur costs the same at any radius), large enough that the
    // sharp edge the frost eases out of (Frost.Ease) still matches the wallpaper beside the bar.
    public const int Shrink = 2;

    // Null means never configured; NaN and infinity would reach the blur as a radius. Snapped to whole pixels.
    public static double Clamp(double? value) =>
        value is not { } blur || double.IsNaN(blur) || double.IsInfinity(blur)
            ? Default
            : Math.Clamp(Math.Round(blur / Step, MidpointRounding.AwayFromZero) * Step, Minimum, Maximum);

    // The box radius whose three passes spread a point by `screenPixels` once the frost, worked at 1/Shrink, is
    // scaled back up. One box of radius r has variance r(r + 1) / 3, so three have r(r + 1); solving
    // r(r + 1) = s^2 for the spread s in frost pixels gives r = (sqrt(1 + 4 s^2) - 1) / 2.
    public static int RadiusAt(double screenPixels)
    {
        var spread = Clamp(screenPixels) / Shrink;
        return (int)Math.Round((Math.Sqrt(1 + 4 * spread * spread) - 1) / 2, MidpointRounding.AwayFromZero);
    }
}
