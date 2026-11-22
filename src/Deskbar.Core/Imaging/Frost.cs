namespace Deskbar.Core.Imaging;

// THE FROST: a small picture blurred until it has no edges left, and its colour lifted so the blur does not wash it
// grey. The docked bar paints the strip of wallpaper behind it this way, under a light veil, behind its idle
// workspace rows (FloatingBar.Frost.cs). In the mockup Petre picked, it was CSS's `blur(18px) saturate(1.6)`; this is
// the same idea done once, on the bar's own strip, because WPF has no backdrop blur.
//
// The caller shrinks the strip first (an eighth of its size), so a radius of 3 here is about 25 screen pixels of blur
// and the whole thing is a few tens of thousands of pixels: cheap enough to redo whenever the wallpaper changes, and
// the result scales back up smoothly because it has no detail left to lose.
//
// Pure, on a Bgra32 buffer (blue, green, red, alpha per pixel), so it is testable without WPF. Plain loops rather
// than LINQ: this is per-pixel arithmetic over flat arrays, where loops say exactly what happens and allocate nothing.
public static class Frost
{
    // The strip blurred by three passes of a box blur of `radius` (three boxes are close to a Gaussian), then each
    // pixel's colour pushed away from its own brightness by `saturation` (1 leaves it alone). Edges are clamped, not
    // wrapped, so the top of the strip never borrows from the bottom. Every pixel comes out opaque: the frost is a
    // background and must not let anything through by itself; the veil over it decides how dark it reads.
    public static byte[] Apply(ReadOnlySpan<byte> bgra, int width, int height, int radius, double saturation)
    {
        if (width <= 0 || height <= 0 || bgra.Length != width * height * 4)
            throw new ArgumentException($"Expected {width} x {height} Bgra32 pixels, got {bgra.Length} bytes.", nameof(bgra));

        // One float plane per colour channel, so the blur does not round on every pass.
        var planes = Enumerable.Range(0, 3).Select(_ => new float[width * height]).ToArray();
        for (var i = 0; i < width * height; i++)
            for (var c = 0; c < 3; c++)
                planes[c][i] = bgra[i * 4 + c];

        var scratch = new float[width * height];
        foreach (var plane in planes)
            for (var pass = 0; pass < 3; pass++)
            {
                BoxAlongRows(plane, scratch, width, height, radius);
                BoxAlongColumns(scratch, plane, width, height, radius);
            }

        var result = new byte[bgra.Length];
        for (var i = 0; i < width * height; i++)
        {
            var (b, g, r) = (planes[0][i], planes[1][i], planes[2][i]);
            // Rec. 709 brightness, the weights CSS's saturate() uses, so this matches the mockup.
            var luma = 0.0722f * b + 0.7152f * g + 0.2126f * r;
            result[i * 4] = Byte(luma + (b - luma) * saturation);
            result[i * 4 + 1] = Byte(luma + (g - luma) * saturation);
            result[i * 4 + 2] = Byte(luma + (r - luma) * saturation);
            result[i * 4 + 3] = 255;
        }
        return result;
    }

    // A running-sum box blur along each row: every output is the mean of the 2 * radius + 1 inputs around it, with
    // the row's end pixels repeated past its ends.
    static void BoxAlongRows(float[] source, float[] target, int width, int height, int radius)
    {
        var span = 2 * radius + 1;
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            var sum = 0f;
            for (var k = -radius; k <= radius; k++) sum += source[row + Math.Clamp(k, 0, width - 1)];
            for (var x = 0; x < width; x++)
            {
                target[row + x] = sum / span;
                sum += source[row + Math.Min(x + radius + 1, width - 1)] - source[row + Math.Max(x - radius, 0)];
            }
        }
    }

    // The same down each column.
    static void BoxAlongColumns(float[] source, float[] target, int width, int height, int radius)
    {
        var span = 2 * radius + 1;
        for (var x = 0; x < width; x++)
        {
            var sum = 0f;
            for (var k = -radius; k <= radius; k++) sum += source[Math.Clamp(k, 0, height - 1) * width + x];
            for (var y = 0; y < height; y++)
            {
                target[y * width + x] = sum / span;
                sum += source[Math.Min(y + radius + 1, height - 1) * width + x] - source[Math.Max(y - radius, 0) * width + x];
            }
        }
    }

    static byte Byte(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);
}
