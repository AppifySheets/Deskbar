using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Deskbar.App;
using Xunit.Abstractions;

namespace Deskbar.Windows.Tests;

// Task 9faa55f0, Petre: "use the same sized circle, but without a fill". The hollow ring a Done task wears while its
// change is not running yet (task 8470d176) came out visibly smaller than the filled disc it turns into: the code
// placed a 12.2-unit ellipse 0.9 in, expecting its stroke to straddle the edge, but WPF draws a shape's stroke inside
// its bounds. Its comment said "the same size", and the property values agreed with the comment, so this renders both
// marks and measures the INK, the only thing his eye sees.
public class TaskStatusMarkSizeTests(ITestOutputHelper output)
{
    [Fact]
    public void The_ring_paints_the_same_width_and_height_as_the_disc() => StaThread.Run(() =>
    {
        // Large enough that one pixel of antialiasing is a small error, at 96 DPI so a DIP is a pixel.
        const int size = 48;
        var disc = InkBounds(TaskStatusMarks.DoneTick(size), size);
        var ring = InkBounds(TaskStatusMarks.DoneTick(size, shipping: true), size);
        output.WriteLine($"disc {disc}, ring {ring}");

        // Within a pixel each way: the two are the same circle, one filled and one stroked.
        Assert.InRange(ring.Width, disc.Width - 1, disc.Width + 1);
        Assert.InRange(ring.Height, disc.Height - 1, disc.Height + 1);
        Assert.InRange(ring.X, disc.X - 1, disc.X + 1);
        Assert.InRange(ring.Y, disc.Y - 1, disc.Y + 1);
    });

    // The box around every pixel the mark paints with more than a quarter of full opacity, rendered on a transparent
    // background at its own size.
    static Int32Rect InkBounds(FrameworkElement mark, int size)
    {
        mark.Measure(new Size(size, size));
        mark.Arrange(new Rect(0, 0, size, size));
        mark.UpdateLayout();
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(mark);
        var pixels = new byte[size * size * 4];
        bitmap.CopyPixels(pixels, size * 4, 0);
        // Alpha is the fourth byte of each BGRA pixel.
        var inked = Enumerable.Range(0, size * size).Where(i => pixels[i * 4 + 3] > 64).ToList();
        Assert.NotEmpty(inked);
        var xs = inked.Select(i => i % size).ToList();
        var ys = inked.Select(i => i / size).ToList();
        return new Int32Rect(xs.Min(), ys.Min(), xs.Max() - xs.Min() + 1, ys.Max() - ys.Min() + 1);
    }
}
