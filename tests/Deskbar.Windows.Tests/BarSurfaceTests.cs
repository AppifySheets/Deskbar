using System.Windows.Media;
using Deskbar.App;

namespace Deskbar.Windows.Tests;

// The bar's two surfaces (BarSurface). Petre picked the see-through band at 85% from a mockup over his own wallpaper
// ("let's take that instead, 85%"), so the number is his, and these pin it: the band is the bar's own flat colour,
// only less opaque, and the flat surface is fully opaque, since the top, the footer and the strips must never let a
// wallpaper read through behind their text (FloatingBar.xaml explains why the bar went opaque).
public class BarSurfaceTests
{
    [Fact]
    public void The_see_through_band_is_the_bar_colour_at_85_percent()
    {
        var band = Assert.IsType<SolidColorBrush>(BarSurface.SeeThrough).Color;
        Assert.Equal(0xD9, band.A); // 85% of 0xFF
        Assert.Equal((BarSurface.Base.R, BarSurface.Base.G, BarSurface.Base.B), (band.R, band.G, band.B));
        Assert.True(BarSurface.SeeThrough.IsFrozen);
    }

    [Fact]
    public void The_flat_surface_is_fully_opaque()
    {
        var flat = Assert.IsType<SolidColorBrush>(BarSurface.Solid).Color;
        Assert.Equal(0xFF, flat.A);
        Assert.Equal(BarSurface.Base, flat);
        Assert.True(BarSurface.Solid.IsFrozen);
    }
}
