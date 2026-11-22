using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Deskbar.App;
using Xunit.Abstractions;

namespace Deskbar.Windows.Tests;

// Petre picked a grain texture for the bar (2026-11-22, "texture"), then, looking at the first build: "i can't see
// texture". A brush that builds and freezes proves nothing about whether anyone can see it, so these render the
// surface to a bitmap and MEASURE the grain, the same way RowRingPixelTests counts ring ink.
//
// No window is opened: a bare Border is measured, arranged and rendered with RenderTargetBitmap, so nothing appears
// on the screen of whoever runs the tests.
public class BarSurfaceTests(ITestOutputHelper output)
{
    // At 96 dpi and at 150% and 200%, the scalings the bar actually runs at: WPF scales the 96-dpi tile up with
    // bilinear filtering, which smooths the grain, so the check has to happen at the scales that matter.
    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    [InlineData(192)]
    public void The_grain_is_strong_enough_to_see(int dpi) => StaThread.Run(() =>
    {
        var (mean, deviation) = Luminance(Render(BarSurface.Textured, dpi));
        output.WriteLine($"dpi={dpi} mean={mean:0.0} deviation={deviation:0.00}");

        // The surface sits on the base colour: the noise is centred on it, so the average stays put.
        var baseLuminance = (BarSurface.Base.R + BarSurface.Base.G + BarSurface.Base.B) / 3.0;
        Assert.InRange(mean, baseLuminance - 2, baseLuminance + 2);

        // Visible grain. A standard deviation of 3 levels on a near-black surface is what the first build
        // measured, and Petre could not see it; 6 is the floor for grain you notice without looking for it.
        Assert.True(deviation >= 6, $"the grain measured a deviation of {deviation:0.00} levels at {dpi} dpi");
    });

    // The flat base renders flat, so the knob's rim really is one colour.
    [Fact]
    public void The_solid_base_has_no_grain() => StaThread.Run(() =>
    {
        var (_, deviation) = Luminance(Render(BarSurface.Solid, 96));
        Assert.True(deviation < 0.5, $"the solid base measured a deviation of {deviation:0.00}");
    });

    static BitmapSource Render(Brush brush, int dpi)
    {
        const int size = 256;
        var surface = new Border { Width = size, Height = size, Background = brush };
        surface.Measure(new Size(size, size));
        surface.Arrange(new Rect(0, 0, size, size));
        var pixels = (int)(size * dpi / 96.0);
        var bitmap = new RenderTargetBitmap(pixels, pixels, dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        return bitmap;
    }

    // The mean and standard deviation of each pixel's grey level (the average of its three channels).
    static (double Mean, double Deviation) Luminance(BitmapSource bitmap)
    {
        var stride = bitmap.PixelWidth * 4;
        var bytes = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(bytes, stride, 0);
        var levels = Enumerable.Range(0, bytes.Length / 4)
            .Select(i => (bytes[i * 4] + bytes[i * 4 + 1] + bytes[i * 4 + 2]) / 3.0)
            .ToArray();
        var mean = levels.Average();
        return (mean, Math.Sqrt(levels.Select(level => (level - mean) * (level - mean)).Average()));
    }
}
