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

    // Radius 0 is the setting's "no blur": only the colour lift is left.
    [Fact]
    public void Radius_zero_keeps_every_pixel_where_it_was()
    {
        var stripes = Image(6, 2, (x, _) => x % 2 == 0 ? ((byte)200, (byte)200, (byte)200) : ((byte)20, (byte)20, (byte)20));
        var frosted = Frost.Apply(stripes, 6, 2, radius: 0, saturation: 1.0);
        Assert.Equal((200, 200, 200, 255), At(frosted, 6, 0, 0));
        Assert.Equal((20, 20, 20, 255), At(frosted, 6, 1, 0));
    }

    // THE SOFT EDGE (Petre: "ease into it", "make edges less pronounced"). Sharp black, frosted white, 20 wide.
    static readonly byte[] Black = Image(20, 2, (_, _) => (0, 0, 0));
    static readonly byte[] White = Image(20, 2, (_, _) => (255, 255, 255));

    [Fact]
    public void The_edge_facing_the_desktop_is_the_sharp_picture_and_the_far_side_the_frost()
    {
        var eased = Frost.Ease(Black, White, 20, 2, ramp: 0.5, fromLeft: true);
        Assert.Equal(0, At(eased, 20, 0, 0).B);
        Assert.Equal(255, At(eased, 20, 10, 0).B);
        Assert.Equal(255, At(eased, 20, 19, 1).B);
    }

    [Fact]
    public void The_frost_rises_steadily_across_the_ramp()
    {
        var eased = Frost.Ease(Black, White, 20, 2, ramp: 0.5, fromLeft: true);
        var row = Enumerable.Range(0, 20).Select(x => At(eased, 20, x, 0).B).ToArray();
        Assert.All(Enumerable.Range(1, 19), x => Assert.True(row[x] >= row[x - 1], $"column {x}: {row[x - 1]} then {row[x]}"));
        Assert.InRange(row[5], 64, 192); // halfway up the ramp is about half
    }

    // A bar docked on the left faces the desktop with its right side.
    [Fact]
    public void A_bar_facing_the_desktop_on_its_right_eases_from_the_right()
    {
        var eased = Frost.Ease(Black, White, 20, 2, ramp: 0.5, fromLeft: false);
        Assert.Equal(0, At(eased, 20, 19, 0).B);
        Assert.Equal(255, At(eased, 20, 0, 0).B);
    }

    [Fact]
    public void Two_buffers_of_different_sizes_are_refused() =>
        Assert.Throws<ArgumentException>(() => Frost.Ease(Black, new byte[8], 20, 2, ramp: 0.3, fromLeft: true));
}
