namespace Deskbar.Core.Imaging;

// AN APP LOGO'S LIGHT PLATE IS MADE TRANSPARENT ON THE BAR (task 61a62df7, option A of the mockup Petre picked).
//
// The shell hands the bar an installed app's logo (ITaskbarPins.ImageOfApp) for bookmark chips, taskbar pins, media
// players and store-app windows. For a web app installed from Edge, that logo is whatever picture the site's manifest
// gave for the size asked, and some are artwork on an opaque WHITE SQUARE. Measured on Petre's machine: Outlook's
// Square44x44Logo.targetsize-48.png is its blue envelope on FFFFFF corner to corner, while its 32 px window icon is
// transparent, so the chip was a glaring white tile next to a running Outlook window that had none. Petre: "white
// background on email is bad, but it's good on github". GitHub's logo is a DARK square (221E20) with the white cat on
// it, and it already sits well on the dark row.
//
// So: when the whole border of a logo, corners included, is one opaque LIGHT colour, that plate is lifted. Only what is connected to the
// border is touched (a flood from the edge), so white INSIDE the artwork, the O on Outlook's badge, stays white. The
// pixels at the artwork's edge, which the site anti-aliased against the white, are un-blended from it ("colour to
// alpha"), so the artwork keeps a clean outline instead of a pale fringe.
//
// What is left alone, each on purpose:
//   * a dark plate (GitHub): it reads on the bar already, and Petre likes it;
//   * a logo whose border is not opaque (YouTube Music's red disc): there is no square to lift;
//   * a tile with rounded corners, even slightly: the app drew that tile as its shape (a brand's white tile), so it
//     is part of the logo. Pins and store-app windows come through here too, not only chips, so this guard matters;
//   * a border of mixed colours: that is artwork running to the edge, not a plate;
//   * a logo that would come out mostly DARK, any hue (black on white, or black with a red accent): on the dark row
//     the white tile is what makes it visible, which is exactly the GitHub case the other way round;
//   * a logo that would come out nearly empty (a white square with almost nothing on it).
//
// Pixels are Pbgra32 (WPF's premultiplied blue, green, red, alpha), top row first, the format PinImage carries. Pure,
// so it is tested in Core without a WPF thread. Plain loops, as in Frost: a flood fill over a flat array is clearer as
// a queue and a loop than as LINQ.
public static class LightPlate
{
    // A border pixel counts as opaque from this alpha on.
    const int OpaqueAlpha = 0xF0;

    // The share of border pixels that must be opaque, and the share of those that must match the plate's colour.
    // 90%, so a few stray pixels of artwork touching the edge do not save the plate, but a rounded tile (whose corners
    // are see-through, a third of a 48 px border at a typical radius) is not taken for a square.
    const double BorderShare = 0.9;

    // A pixel is the plate's colour when no channel differs from it by more than this. The flood spreads only through
    // such pixels.
    const int Near = 24;

    // Pixels a little further from the plate than Near, reached at the flood's edge, are the anti-aliased rim: they are
    // un-blended from the plate but the flood does not pass through them. Beyond this they are artwork and kept.
    const int Fringe = 96;

    // A plate is light when every channel is at least this. White and the near-white greys sites use.
    const int LightChannel = 0xD0;

    // At least this share of the picture must still be visible (alpha at least half) once the plate is gone.
    const double KeptShare = 0.1;
    const int VisibleAlpha = 0x80;

    // A visible pixel left after the lift is dark when its brightest straight channel is at most this, DarkArtwork's
    // threshold (about 31%). From DarkShare of the visible pixels on, the plate stays.
    const int DarkChannel = 0x50;
    const double DarkShare = 0.5;

    // The pixels to draw: the same array when there is no light plate to lift, a copy with the plate transparent when
    // there is. Never throws on odd input; a buffer that does not match its size comes back untouched.
    public static byte[] Lifted(byte[] pbgra, int width, int height)
    {
        if (width < 3 || height < 3 || pbgra.Length != width * height * 4) return pbgra;

        var border = BorderIndices(width, height).ToList();
        var opaque = border.Where(i => pbgra[i * 4 + 3] >= OpaqueAlpha).ToList();
        if (opaque.Count < BorderShare * border.Count) return pbgra;
        // A SQUARE plate only: all four corners opaque. A tile with rounded corners, however small the radius, is a
        // shape the app chose (a brand's own white tile), not a background a size of the logo was padded with.
        if (Corners(width, height).Any(i => pbgra[i * 4 + 3] < OpaqueAlpha)) return pbgra;

        // The plate's colour: the median of each channel over the opaque border, so a few artwork pixels at the edge do
        // not tint it. In STRAIGHT colour, which is what every comparison below uses: "opaque" starts at 0xF0, and a
        // plate at alpha 0xF8 has premultiplied bytes of 0xF8, which would never match its own straight 0xFF (review).
        var straightBorder = opaque.Select(i => Straight(pbgra, i)).ToList();
        var plate = Enumerable.Range(0, 3)
            .Select(c => straightBorder.Select(colour => colour[c]).OrderBy(v => v).ElementAt(straightBorder.Count / 2))
            .ToArray();
        if (plate.Min() < LightChannel) return pbgra;
        if (straightBorder.Count(colour => Distance(colour, plate) <= Near) < BorderShare * straightBorder.Count)
            return pbgra;

        var lifted = Flood(pbgra, width, height, border, plate);

        var visible = Enumerable.Range(0, width * height).Where(i => lifted[i * 4 + 3] >= VisibleAlpha).ToList();
        if (visible.Count < KeptShare * width * height) return pbgra;
        // Dark artwork keeps its plate: on the dark row the light square is what makes it visible. Judged on what is
        // left, any hue (black with a red accent, or dark navy, would vanish as surely as plain black), and from half
        // the artwork on, since that half would be lost.
        return visible.Count(i => Straight(lifted, i).Max() <= DarkChannel) >= DarkShare * visible.Count
            ? pbgra
            : lifted;
    }

    // Every pixel reached from the border through plate-coloured pixels has the plate taken out of it.
    static byte[] Flood(byte[] pbgra, int width, int height, IEnumerable<int> border, int[] plate)
    {
        var result = (byte[])pbgra.Clone();
        var seen = new bool[width * height];
        var queue = new Queue<int>(border);
        while (queue.Count > 0)
        {
            var index = queue.Dequeue();
            if (seen[index]) continue;
            seen[index] = true;

            var alpha = pbgra[index * 4 + 3];
            // Already see-through: nothing to lift, and the plate may continue beyond it.
            if (alpha == 0)
            {
                Neighbours(index, width, height).ToList().ForEach(queue.Enqueue);
                continue;
            }

            var colour = Straight(pbgra, index);
            var distance = Distance(colour, plate);
            if (distance > Fringe) continue;

            Write(result, index, WithoutPlate(colour, alpha, plate));
            if (distance <= Near) Neighbours(index, width, height).ToList().ForEach(queue.Enqueue);
        }
        return result;
    }

    // Colour to alpha: the least opacity at which some colour, laid over the plate, gives this pixel; and that colour.
    // A pixel the same as the plate comes out fully transparent; one far from it keeps nearly all its opacity.
    static (int B, int G, int R, int A) WithoutPlate(int[] colour, int alpha, int[] plate)
    {
        var share = Enumerable.Range(0, 3)
            .Select(c => colour[c] > plate[c]
                ? (colour[c] - plate[c]) / (255.0 - plate[c])
                : (plate[c] - colour[c]) / (double)plate[c])
            .Max();
        var newAlpha = (int)Math.Round(share * alpha);
        if (newAlpha <= 0) return (0, 0, 0, 0);
        // The colour that, at `share` opacity over the plate, gives `colour`, then premultiplied by the new alpha.
        int Channel(int c) =>
            (int)Math.Round(Math.Clamp((colour[c] - plate[c]) / share + plate[c], 0, 255) * newAlpha / 255);
        return (Channel(0), Channel(1), Channel(2), newAlpha);
    }

    // A pixel's straight (unpremultiplied) blue, green and red.
    static int[] Straight(byte[] pbgra, int index)
    {
        var alpha = pbgra[index * 4 + 3];
        return Enumerable.Range(0, 3)
            .Select(c => alpha == 0 ? 0 : Math.Min(255, pbgra[index * 4 + c] * 255 / alpha))
            .ToArray();
    }

    static int Distance(int[] colour, int[] plate) =>
        Enumerable.Range(0, 3).Max(c => Math.Abs(colour[c] - plate[c]));

    static void Write(byte[] pbgra, int index, (int B, int G, int R, int A) pixel)
    {
        pbgra[index * 4] = (byte)pixel.B;
        pbgra[index * 4 + 1] = (byte)pixel.G;
        pbgra[index * 4 + 2] = (byte)pixel.R;
        pbgra[index * 4 + 3] = (byte)pixel.A;
    }

    // The indices of the outermost ring of pixels, each once.
    static IEnumerable<int> BorderIndices(int width, int height) =>
        Enumerable.Range(0, width * height)
            .Where(i => i % width == 0 || i % width == width - 1 || i / width == 0 || i / width == height - 1);

    static IEnumerable<int> Corners(int width, int height) =>
        [0, width - 1, (height - 1) * width, height * width - 1];

    static IEnumerable<int> Neighbours(int index, int width, int height)
    {
        var (x, y) = (index % width, index / width);
        if (x > 0) yield return index - 1;
        if (x < width - 1) yield return index + 1;
        if (y > 0) yield return index - width;
        if (y < height - 1) yield return index + width;
    }
}
