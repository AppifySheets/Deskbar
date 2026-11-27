using Deskbar.Core.Bookmarks;

namespace Deskbar.Core.Tests.Bookmarks;

// Dark, colourless favicons (GitHub's black octocat) are drawn near-white so they show on the dark row (task 99d92467);
// anything with colour or real light parts is left exactly as the site drew it.
public sealed class DarkArtworkTests
{
    // One Pbgra32 pixel, premultiplied from straight colour, as WPF's converter writes it.
    static byte[] Pixel(byte r, byte g, byte b, byte a) =>
        [(byte)(b * a / 255), (byte)(g * a / 255), (byte)(r * a / 255), a];

    // `count` copies of a pixel, as a flat Pbgra32 buffer.
    static byte[] Many(byte[] pixel, int count) => Enumerable.Repeat(pixel, count).SelectMany(p => p).ToArray();

    static readonly byte[] Black = Pixel(0x18, 0x17, 0x17, 0xFF);
    static readonly byte[] Clear = Pixel(0, 0, 0, 0);
    static readonly byte[] White = Pixel(0xFF, 0xFF, 0xFF, 0xFF);

    // The octocat's shape: a black disc with a transparent cat cut out, here 70 black pixels and 30 transparent ones.
    static byte[] Octocat => [.. Many(Black, 70), .. Many(Clear, 30)];

    [Fact]
    public void A_black_favicon_with_transparent_cut_outs_is_dark() =>
        Assert.True(DarkArtwork.IsDark(Octocat));

    [Fact]
    public void A_dark_favicon_is_drawn_near_white_and_keeps_its_shape()
    {
        var drawn = DarkArtwork.Legible(Octocat);

        // The black disc is now near-white (0xF0 less the octocat's own 0x17), fully opaque as before.
        Assert.All(Enumerable.Range(0, 70), i => Assert.InRange(drawn[i * 4], (byte)0xD0, (byte)0xF0));
        Assert.All(Enumerable.Range(0, 70), i => Assert.Equal(0xFF, drawn[i * 4 + 3]));
        // The cut-out stays transparent: alpha and colour both 0, still a valid premultiplied pixel.
        Assert.All(Enumerable.Range(70, 30), i => Assert.Equal(Clear, drawn.Skip(i * 4).Take(4).ToArray()));
    }

    [Fact]
    public void A_half_transparent_black_edge_becomes_half_transparent_light()
    {
        var edge = Pixel(0, 0, 0, 0x80);
        var drawn = DarkArtwork.Legible([.. Many(Black, 10), .. edge]).Skip(40).ToArray();

        Assert.Equal(0x80, drawn[3]);
        // Premultiplied: 0xF0 at half opacity, and never above its own alpha.
        Assert.InRange(drawn[0], (byte)0x70, (byte)0x80);
    }

    [Fact]
    public void A_coloured_favicon_is_left_as_it_is()
    {
        var navy = Pixel(0x1A, 0x2B, 0x5C, 0xFF);
        var icon = Many(navy, 50);

        Assert.False(DarkArtwork.IsDark(icon));
        Assert.Same(icon, DarkArtwork.Legible(icon));
    }

    [Fact]
    public void A_black_square_with_a_white_letter_is_left_as_it_is()
    {
        // About a fifth light: it reads on a dark row already.
        var icon = Many(Black, 80).Concat(Many(White, 20)).ToArray();

        Assert.Same(icon, DarkArtwork.Legible(icon));
    }

    [Fact]
    public void A_mostly_black_favicon_with_a_small_light_detail_is_still_dark() =>
        Assert.True(DarkArtwork.IsDark([.. Many(Black, 95), .. Many(White, 5)]));

    [Fact]
    public void A_fully_transparent_favicon_is_not_dark() =>
        Assert.False(DarkArtwork.IsDark(Many(Clear, 16)));

    [Fact]
    public void A_faint_rim_does_not_vote()
    {
        // Barely visible light pixels (alpha under half) around a black shape do not stop it being dark.
        var faintWhite = Pixel(0xFF, 0xFF, 0xFF, 0x20);

        Assert.True(DarkArtwork.IsDark([.. Many(Black, 50), .. Many(faintWhite, 50)]));
    }
}
