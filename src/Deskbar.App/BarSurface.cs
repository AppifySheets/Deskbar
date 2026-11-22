using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Deskbar.App;

// THE BAR'S SURFACE: the backgrounds every piece of the bar's family stands on, so they cannot drift apart. Solid, the
// flat base, is the floating bar's Root, its top and footer, the detached stats window, the due card and the screen
// strips. Textured, the grain, lies only behind the bar's workspace rows (FloatingBar.Grain.cs), and only while the
// setting is on (AppState.Grain).
//
// Why a texture. The bar was a flat, fully opaque #141518, and docked at full screen height most of it is empty:
// Petre, 2026-11-22, "too black". He was shown lighter flat colours, raised row cards, zones, acrylic, mica and grain
// side by side, and picked grain ("texture"). Transparency stays out: FloatingBar.xaml explains why the bar went
// opaque (a busy wallpaper read through behind the window titles), and grain changes nothing about that, because
// every pixel of the texture is still fully opaque.
//
// What it is. A 128 x 128 tile of monochrome noise baked onto a slightly lifted base (#1B1C21 rather than #141518),
// repeated across the surface. Baking the noise into opaque pixels, instead of layering a translucent noise over a
// solid colour, keeps it to ONE brush: a Border has one Background, and nothing in the bar's layout has to change.
// The tile is 128 DIP at 96 dpi, so at 125% or 150% WPF scales it up with bilinear filtering, which softens single
// pixels into the fine, slightly cloudy grain the mockup showed. The noise comes from a fixed seed, so the texture
// is the same on every start and never "moves" when the app restarts.
public static class BarSurface
{
    // The base the noise sits on: one step lighter than the old #141518, and still dark enough that the white
    // text and the icons keep the contrast they were tuned for.
    public static readonly Color Base = Color.FromRgb(0x1B, 0x1C, 0x21);

    // The base without texture, for the few places that must paint a flat patch of "bar": the volume knob's rim
    // (a ring that reads as a cut-out in the bar) and anything drawn with a DrawingContext where a tiled brush has
    // no meaningful phase. Frozen, as every static brush must be (CLAUDE.md, traps).
    public static readonly Brush Solid = Freeze(new SolidColorBrush(Base));

    // The textured surface itself. Tiled in absolute DIPs so the grain keeps one size however tall the bar grows;
    // a relative viewport would stretch the noise with the window.
    public static readonly Brush Textured = Freeze(new ImageBrush(NoiseTile())
    {
        TileMode = TileMode.Tile,
        ViewportUnits = BrushMappingMode.Absolute,
        Viewport = new System.Windows.Rect(0, 0, TileSize, TileSize),
        Stretch = Stretch.None,
    });

    const int TileSize = 128;

    // How far a pixel may stray from the base, in 8-bit levels. Summing two uniform draws gives a triangular
    // distribution: most pixels sit near the base and only a few reach the edge, which reads as grain rather than
    // as static. Measured, not guessed (BarSurfaceTests): 8 gave a standard deviation of 3.3 levels at 96 dpi and
    // only 2.1 at 150%, where bilinear scaling smooths it, and Petre could not see it ("i can't see texture").
    // 24 lands at about 6.5 at 150% and 200%. The base is 27 levels and the spread stays under it, so nothing clamps
    // and the average stays on the base.
    const int Spread = 24;

    // Fixed seed: the same tile on every start, so the bar looks identical across restarts and screenshots.
    const int Seed = 2026_11_22;

    // One Bgr32 tile: the same offset on all three channels, so the noise is grey and never tints the base.
    static BitmapSource NoiseTile()
    {
        var random = new Random(Seed);
        var pixels = Enumerable.Range(0, TileSize * TileSize)
            .Select(_ => (int)Math.Round((random.NextDouble() + random.NextDouble() - 1) * Spread))
            .SelectMany(offset => new[]
            {
                Channel(Base.B, offset),
                Channel(Base.G, offset),
                Channel(Base.R, offset),
                (byte)0xFF,
            })
            .ToArray();
        var tile = BitmapSource.Create(TileSize, TileSize, 96, 96, PixelFormats.Bgr32, null, pixels, TileSize * 4);
        tile.Freeze();
        return tile;
    }

    static byte Channel(byte value, int offset) => (byte)Math.Clamp(value + offset, 0, 255);

    static T Freeze<T>(T freezable) where T : System.Windows.Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
