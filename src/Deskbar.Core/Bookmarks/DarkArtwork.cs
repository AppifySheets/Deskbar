namespace Deskbar.Core.Bookmarks;

// DARK, COLOURLESS FAVICONS ARE DRAWN LIGHT ON THE BAR.
//
// The bar's rows are dark, and a site whose favicon is black artwork (GitHub's octocat is a black disc with the cat cut
// out of it) all but vanished on them: a resting chip is drawn at half strength, and black at half strength on a dark
// purple lane is nothing. Petre: "github too dim"; he picked option A of the mockup (task 99d92467), "dark one-colour
// artwork drawn light", over a light tile under the resting chip (B, the white backing he had turned down before) and
// a white halo (C, which smears at 20 DIP).
//
// So: when nearly every visible pixel of a favicon is dark AND colourless, the picture is drawn with its brightness
// inverted to near-white, which is what GitHub's own dark-theme favicon looks like. Its alpha, so its shape and its
// cut-outs, is left exactly as it was. A favicon with any real colour, or with real light parts (a white letter on a
// black square already reads on a dark row), is left as the site drew it: this only rescues artwork that cannot be
// seen at all, it does not restyle icons.
//
// Pixels are Pbgra32 (WPF's premultiplied blue, green, red, alpha), the format BookmarkIcons.Decode copies a favicon's
// frame into. Pure, so it is tested in Core without a WPF thread.
public static class DarkArtwork
{
    // A pixel takes part in the judgement only when it is at least half opaque, so the faint anti-aliased rim around
    // a shape, whose colour is mostly the background's, does not vote.
    const int VisibleAlpha = 0x80;

    // Dark: its brightest channel, unpremultiplied, is at most this (about 31%). The octocat's black is 0x18.
    const int DarkChannel = 0x50;

    // Colourless: its channels differ by at most this. Dark navy (0x1A, 0x2B, 0x5C) spreads 0x42 and is a colour.
    const int GreySpread = 0x20;

    // The share of visible pixels that must be dark and colourless. 90%, so a black square with a white letter on it
    // (about a fifth of its pixels light) stays as it is: it reads on a dark row already.
    const double DarkShare = 0.9;

    // The light the artwork is drawn in: near-white rather than pure white, so a lit chip is not the brightest thing
    // on the row. The mockup's option A used the same 0xF0.
    const int Light = 0xF0;

    // The pixels to draw: the same array when the artwork is not dark, a lightened copy when it is.
    public static byte[] Legible(byte[] pbgra) => IsDark(pbgra) ? Lightened(pbgra) : pbgra;

    // True when at least DarkShare of the visible pixels are dark and colourless. Artwork with no visible pixel at all
    // is not dark: there is nothing to rescue, and an empty favicon stays empty.
    public static bool IsDark(byte[] pbgra)
    {
        var visible = Pixels(pbgra).Where(p => p.A >= VisibleAlpha).ToList();
        return visible.Count > 0 && visible.Count(IsDarkGrey) >= DarkShare * visible.Count;
    }

    // Every pixel's brightness inverted and scaled to Light, alpha untouched. In premultiplied terms a channel c of a
    // pixel with alpha a becomes (a - c) scaled by Light/255: black (c = 0) becomes Light at the pixel's own opacity,
    // and a mid grey detail stays a mid grey, so the artwork's own shading survives. The result is still a valid
    // premultiplied pixel, because a - c never exceeds a. (A malformed pixel with c above a is clamped to 0, not
    // wrapped round to a bright byte.)
    public static byte[] Lightened(byte[] pbgra) =>
        pbgra.Select((value, index) => index % 4 == 3
                ? value
                : (byte)(Math.Max(0, pbgra[index - index % 4 + 3] - value) * Light / 255))
            .ToArray();

    static bool IsDarkGrey((int B, int G, int R, int A) p)
    {
        // Unpremultiplied, so a half-transparent black edge pixel is judged as the black it is.
        var channels = new[] { p.B, p.G, p.R }.Select(c => c * 255 / p.A).ToArray();
        return channels.Max() <= DarkChannel && channels.Max() - channels.Min() <= GreySpread;
    }

    static IEnumerable<(int B, int G, int R, int A)> Pixels(byte[] pbgra) =>
        Enumerable.Range(0, pbgra.Length / 4)
            .Select(i => ((int)pbgra[i * 4], (int)pbgra[i * 4 + 1], (int)pbgra[i * 4 + 2], (int)pbgra[i * 4 + 3]));
}
