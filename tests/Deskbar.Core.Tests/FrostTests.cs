using Deskbar.Core.Imaging;

namespace Deskbar.Core.Tests;

// The frost the docked bar paints behind its idle rows: the wallpaper strip blurred, and its colour lifted, so the
// picture shows as colour and light with no edges left to read through the captions.
public class FrostTests
{
    static byte[] Image(int width, int height, Func<int, int, (byte B, byte G, byte R)> pixel) =>
        Enumerable.Range(0, width * height)
            .SelectMany(i => pixel(i % width, i / width) is var (b, g, r) ? new[] { b, g, r, (byte)255 } : [])
            .ToArray();

    static (int B, int G, int R, int A) At(byte[] bgra, int width, int x, int y) =>
        (bgra[(y * width + x) * 4], bgra[(y * width + x) * 4 + 1], bgra[(y * width + x) * 4 + 2], bgra[(y * width + x) * 4 + 3]);

    [Fact]
    public void A_flat_colour_stays_that_colour_when_saturation_is_left_alone()
    {
        var flat = Image(8, 8, (_, _) => (40, 120, 200));
        var frosted = Frost.Apply(flat, 8, 8, radius: 2, saturation: 1.0);
        Assert.All(Enumerable.Range(0, 64), i => Assert.Equal((40, 120, 200, 255), At(frosted, 8, i % 8, i / 8)));
    }

    // Grey has no colour to lift, so saturation leaves it grey.
    [Fact]
    public void Saturation_leaves_grey_grey()
    {
        var grey = Image(4, 4, (_, _) => (128, 128, 128));
        var frosted = Frost.Apply(grey, 4, 4, radius: 1, saturation: 1.6);
        Assert.Equal((128, 128, 128, 255), At(frosted, 4, 1, 1));
    }

    // Saturation pushes each channel away from the pixel's brightness: the strong channel up, the weak ones down.
    [Fact]
    public void Saturation_pushes_a_colour_further_from_grey()
    {
        var green = Image(4, 4, (_, _) => (40, 160, 90));
        var (b, g, r, _) = At(Frost.Apply(green, 4, 4, radius: 1, saturation: 1.6), 4, 1, 1);
        Assert.True(g > 160, $"green {g}");
        Assert.True(b < 40, $"blue {b}");
        Assert.True(r < 90, $"red {r}");
    }

    // The blur spreads one bright pixel into its neighbours, and keeps the image's total light (up to rounding).
    [Fact]
    public void The_blur_spreads_a_bright_point_and_keeps_its_light()
    {
        var point = Image(15, 15, (x, y) => x == 7 && y == 7 ? ((byte)255, (byte)255, (byte)255) : ((byte)0, (byte)0, (byte)0));
        var frosted = Frost.Apply(point, 15, 15, radius: 2, saturation: 1.0);
        Assert.True(At(frosted, 15, 7, 7).B < 255);
        Assert.True(At(frosted, 15, 9, 7).B > 0);
        Assert.True(At(frosted, 15, 7, 9).B > 0);
        var total = Enumerable.Range(0, 225).Sum(i => (int)frosted[i * 4]);
        Assert.InRange(total, 255 - 40, 255 + 40);
    }

    // Edges are clamped, not wrapped: a bright left column must not bleed into the right edge.
    [Fact]
    public void The_blur_does_not_wrap_round_the_edges()
    {
        var column = Image(20, 4, (x, _) => x == 0 ? ((byte)255, (byte)255, (byte)255) : ((byte)0, (byte)0, (byte)0));
        var frosted = Frost.Apply(column, 20, 4, radius: 2, saturation: 1.0);
        Assert.Equal(0, At(frosted, 20, 19, 1).B);
    }

    [Fact]
    public void Every_pixel_comes_out_opaque()
    {
        var any = Image(6, 6, (x, y) => ((byte)(x * 40), (byte)(y * 40), 7));
        var frosted = Frost.Apply(any, 6, 6, radius: 3, saturation: 1.6);
        Assert.All(Enumerable.Range(0, 36), i => Assert.Equal(255, frosted[i * 4 + 3]));
    }

    [Fact]
    public void A_buffer_of_the_wrong_size_is_refused() =>
        Assert.Throws<ArgumentException>(() => Frost.Apply(new byte[10], 4, 4, radius: 1, saturation: 1.0));
}
