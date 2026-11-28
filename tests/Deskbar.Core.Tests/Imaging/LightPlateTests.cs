using Deskbar.Core.Imaging;

namespace Deskbar.Core.Tests.Imaging;

// An app logo drawn on an opaque light square (Outlook's, from Edge) has the square made transparent; a dark square
// (GitHub's), a see-through border, and dark artwork that needs the white are all left alone (task 61a62df7).
public sealed class LightPlateTests
{
    const int Size = 12;

    // One Pbgra32 pixel, premultiplied from straight colour, as WPF's converter writes it.
    static byte[] Pixel(byte r, byte g, byte b, byte a) =>
        [(byte)(b * a / 255), (byte)(g * a / 255), (byte)(r * a / 255), a];

    static readonly byte[] White = Pixel(0xFF, 0xFF, 0xFF, 0xFF);
    static readonly byte[] Blue = Pixel(0x1A, 0x6E, 0xE0, 0xFF);
    static readonly byte[] Black = Pixel(0x18, 0x17, 0x17, 0xFF);
    static readonly byte[] DarkPlate = Pixel(0x22, 0x1E, 0x20, 0xFF);
    static readonly byte[] Clear = Pixel(0, 0, 0, 0);

    // A Size x Size picture: `inside` on the square from 3 to 8 (a 6 x 6 block of artwork), `plate` everywhere else.
    static byte[] Logo(byte[] plate, byte[] inside) =>
        Enumerable.Range(0, Size * Size)
            .SelectMany(i => InArtwork(i) ? inside : plate)
            .ToArray();

    static bool InArtwork(int index) => index % Size is >= 3 and <= 8 && index / Size is >= 3 and <= 8;

    static byte[] At(byte[] pixels, int x, int y) => pixels.Skip((y * Size + x) * 4).Take(4).ToArray();

    static byte[] With(byte[] pixels, int x, int y, byte[] pixel)
    {
        var copy = (byte[])pixels.Clone();
        pixel.CopyTo(copy, (y * Size + x) * 4);
        return copy;
    }

    [Fact]
    public void A_white_plate_becomes_transparent_and_the_artwork_stays()
    {
        var drawn = LightPlate.Lifted(Logo(White, Blue), Size, Size);

        Assert.Equal(Clear, At(drawn, 0, 0));
        Assert.Equal(Clear, At(drawn, 11, 6));
        Assert.Equal(Blue, At(drawn, 5, 5));
    }

    [Fact]
    public void White_enclosed_by_the_artwork_stays_white()
    {
        // The O on Outlook's badge: white, but not connected to the border, so the flood never reaches it.
        var drawn = LightPlate.Lifted(With(Logo(White, Blue), 5, 5, White), Size, Size);

        Assert.Equal(White, At(drawn, 5, 5));
    }

    [Fact]
    public void An_anti_aliased_edge_keeps_its_colour_at_part_opacity()
    {
        // A third of the blue laid over white, as the site's renderer anti-aliased the artwork's outline.
        var blend = Pixel((0x1A + 2 * 0xFF) / 3, (0x6E + 2 * 0xFF) / 3, (0xE0 + 2 * 0xFF) / 3, 0xFF);
        var drawn = At(LightPlate.Lifted(With(Logo(White, Blue), 2, 5, blend), Size, Size), 2, 5);

        // About a third opaque, and its colour (unpremultiplied) is a strong blue again, not the pale mix. Colour to
        // alpha takes the channel furthest from the plate all the way (red to 0), so it is close to the blue, not exact.
        Assert.InRange(drawn[3], (byte)0x40, (byte)0x60);
        Assert.InRange(drawn[2] * 255 / drawn[3], 0x00, 0x24);
        Assert.InRange(drawn[1] * 255 / drawn[3], 0x50, 0x78);
        Assert.InRange(drawn[0] * 255 / drawn[3], 0xC8, 0xF0);
    }

    [Fact]
    public void A_dark_plate_is_left_as_it_is()
    {
        // GitHub's logo: a dark square with the light cat on it reads on the bar already.
        var logo = Logo(DarkPlate, White);

        Assert.Same(logo, LightPlate.Lifted(logo, Size, Size));
    }

    [Fact]
    public void A_logo_with_a_see_through_border_is_left_as_it_is()
    {
        var logo = Logo(Clear, Blue);

        Assert.Same(logo, LightPlate.Lifted(logo, Size, Size));
    }

    [Fact]
    public void A_border_of_mixed_colours_is_not_a_plate()
    {
        // Artwork running to the edge: every other border pixel blue.
        var logo = Enumerable.Range(0, Size * Size).SelectMany(i => i % 2 == 0 ? White : Blue).ToArray();

        Assert.Same(logo, LightPlate.Lifted(logo, Size, Size));
    }

    [Fact]
    public void Dark_artwork_keeps_its_white_plate()
    {
        // Black on white: without the plate it would vanish on the dark row, so the plate is what makes it legible.
        var logo = Logo(White, Black);

        Assert.Same(logo, LightPlate.Lifted(logo, Size, Size));
    }

    [Fact]
    public void A_nearly_opaque_plate_is_lifted_too()
    {
        // Alpha 0xF8 counts as opaque, and its premultiplied bytes (F8) are still white in straight colour (review).
        var drawn = LightPlate.Lifted(Logo(Pixel(0xFF, 0xFF, 0xFF, 0xF8), Blue), Size, Size);

        Assert.Equal(Clear, At(drawn, 0, 0));
        Assert.Equal(Blue, At(drawn, 5, 5));
    }

    [Fact]
    public void A_tile_with_rounded_corners_is_the_apps_own_shape_and_stays()
    {
        // Each corner pixel see-through, as a 1 px radius leaves it: still 90% of the border opaque, but not a square.
        var logo = new[] { (0, 0), (Size - 1, 0), (0, Size - 1), (Size - 1, Size - 1) }
            .Aggregate(Logo(White, Blue), (pixels, corner) => With(pixels, corner.Item1, corner.Item2, Clear));

        Assert.Same(logo, LightPlate.Lifted(logo, Size, Size));
    }

    [Fact]
    public void Mostly_dark_artwork_with_a_coloured_accent_keeps_its_white_plate()
    {
        // Black with a red stripe, a sixth of the artwork: not colourless, but the black would vanish on the dark row.
        var red = Pixel(0xE0, 0x20, 0x20, 0xFF);
        var logo = Enumerable.Range(3, 6)
            .Aggregate(Logo(White, Black), (pixels, x) => With(pixels, x, 8, red));

        Assert.Same(logo, LightPlate.Lifted(logo, Size, Size));
    }

    [Fact]
    public void A_plate_with_almost_nothing_on_it_is_kept()
    {
        var logo = With(Logo(White, White), 5, 5, Blue);

        Assert.Same(logo, LightPlate.Lifted(logo, Size, Size));
    }

    [Fact]
    public void A_buffer_that_does_not_match_its_size_comes_back_untouched()
    {
        var logo = Logo(White, Blue);

        Assert.Same(logo, LightPlate.Lifted(logo, Size + 1, Size));
    }
}
