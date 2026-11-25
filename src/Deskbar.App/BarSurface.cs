using System.Windows.Media;

namespace Deskbar.App;

// THE BAR'S SURFACE: the backgrounds every piece of the bar's family stands on, so they cannot drift apart. Solid, the
// flat base, is the floating bar's top and footer, the detached stats window, the due card and the screen strips.
// SeeThrough lies only behind the bar's workspace rows (FloatingBar.Surface.cs), and only while the setting is on
// (AppState.SeeThrough).
//
// The history. The bar was a flat, fully opaque #141518, and docked at full screen height most of it is empty: Petre,
// 2026-11-22, "too black". A grain texture came first (#405), then went behind the rows only (#408), and then, shown
// the bar over his own wallpaper with the rows band at 85%, 70%, 55% and 40%: "let's take that instead, 85%".
// FloatingBar.xaml explains why the bar had gone fully opaque: a busy wallpaper read through behind the window titles
// at 90%. That still holds for the top and the footer, and for every row used lately, which stands on the fade's
// opaque plate (FloatingBar.Recency.cs). Only the band behind idle rows shows the desktop, and only 15% of it.
//
// Then, over his laptop's bright spring tree, 15% read as dark olive: "could you do transparency that's not
// black-ish?" The bar now paints the wallpaper behind it, blurred, under FrostVeil (FloatingBar.Frost.cs), docked and,
// since task b971ca3f, floating, and keeps SeeThrough wherever there is no frost (across the seam or two screens, a
// tiled or solid-colour wallpaper).
public static class BarSurface
{
    // The bar's flat colour: one step lighter than the old #141518, and still dark enough that the white text and
    // the icons keep the contrast they were tuned for. The fade's plates are mixed from it.
    public static readonly Color Base = Color.FromRgb(0x1B, 0x1C, 0x21);

    // The base as a brush: the bar's top and footer, the strips, the stats window, the due card, the volume knob's rim
    // (a ring that reads as a cut-out in the bar) and the flat patches drawn with a DrawingContext. Frozen, as every
    // static brush must be (CLAUDE.md, traps).
    public static readonly Brush Solid = Freeze(new SolidColorBrush(Base));

    // How much of the base the see-through band keeps: 85%, Petre's pick from the mockup over his wallpaper.
    public const double SeeThroughOpacity = 0.85;

    // The base at that opacity, for the band behind the workspace rows. 0xD9 is 85% of 0xFF, rounded.
    public static readonly Brush SeeThrough =
        Freeze(new SolidColorBrush(Color.FromArgb((byte)Math.Round(SeeThroughOpacity * 0xFF), Base.R, Base.G, Base.B)));

    // How much of the base the band keeps over the FROST (FloatingBar.Frost.cs): half. The frost behind it is the
    // wallpaper blurred until it has no edges, so the legibility argument that set 85% above (a busy picture reading
    // through the captions) no longer applies, and half is what the mockup Petre picked from ("d") showed.
    public const double FrostVeilOpacity = 0.5;

    // The base at that opacity, laid over the frost behind the workspace rows. 0x80 is 50% of 0xFF, rounded.
    public static readonly Brush FrostVeil =
        Freeze(new SolidColorBrush(Color.FromArgb((byte)Math.Round(FrostVeilOpacity * 0xFF), Base.R, Base.G, Base.B)));

    // How much of the base the veil keeps at the bar's very inner edge, where the frost is still the sharp picture
    // (Frost.Ease). None: rendered off screen over a bright sky, even 15% left a visible step where the bar began,
    // which is the line Petre asked to lose. The veil reaches the icons' side within a few pixels anyway.
    public const double FrostVeilEdgeOpacity = 0;

    // The veil easing in from the side that faces the desktop: FrostVeilEdgeOpacity at that edge, FrostVeil's half at
    // `ease` of the width in, and half from there on. Petre: "make edges less pronounced"; a uniform veil drew a dark
    // line down the screen where the bar began. The ease is a fixed few DIP (FrostEdge), so its share changes only
    // when the bar's width or scale does: one frozen brush per side, remade only then.
    public static Brush FrostVeilFrom(bool fromLeft, double ease) =>
        (fromLeft ? easedFromLeft : easedFromRight) is { } made && made.Ease == ease
            ? made.Brush
            : Remember(fromLeft, ease, EasedVeil(fromLeft, ease));

    static (double Ease, Brush Brush)? easedFromLeft, easedFromRight;

    static Brush Remember(bool fromLeft, double ease, Brush brush)
    {
        if (fromLeft) easedFromLeft = (ease, brush);
        else easedFromRight = (ease, brush);
        return brush;
    }

    // A horizontal gradient in the band's own box (relative units), so it follows the bar's width. Smoothstep-shaped
    // like the frost's ease, sampled at a few stops, so the veil and the picture arrive together.
    static Brush EasedVeil(bool fromLeft, double ease)
    {
        var stops = Enumerable.Range(0, 7)
            .Select(i => i / 6.0)
            .Select(t => new GradientStop(
                Color.FromArgb(
                    (byte)Math.Round((FrostVeilEdgeOpacity + (FrostVeilOpacity - FrostVeilEdgeOpacity) * t * t * (3 - 2 * t)) * 0xFF),
                    Base.R, Base.G, Base.B),
                fromLeft ? t * ease : 1 - t * ease));
        return Freeze(new LinearGradientBrush(new GradientStopCollection(stops), new System.Windows.Point(0, 0.5), new System.Windows.Point(1, 0.5)));
    }

    static T Freeze<T>(T freezable) where T : System.Windows.Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
