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

    // Over the frost (a docked bar's blurred wallpaper) the band keeps half the bar's colour, the mockup Petre picked.
    [Fact]
    public void The_veil_over_the_frost_is_the_bar_colour_at_half()
    {
        var veil = Assert.IsType<SolidColorBrush>(BarSurface.FrostVeil).Color;
        Assert.Equal(0x80, veil.A);
        Assert.Equal((BarSurface.Base.R, BarSurface.Base.G, BarSurface.Base.B), (veil.R, veil.G, veil.B));
        Assert.True(BarSurface.FrostVeil.IsFrozen);
    }

    // The veil eases in from the inner edge (Petre: "make edges less pronounced"): none at that edge, half from the
    // ease's end, on the side the bar faces the desktop.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_eased_veil_starts_clear_at_the_inner_edge_and_is_half_past_the_ease(bool fromLeft)
    {
        var veil = Assert.IsType<LinearGradientBrush>(BarSurface.FrostVeilFrom(fromLeft, 0.35));
        var byOffset = veil.GradientStops.OrderBy(s => s.Offset).ToList();
        var (edge, inner) = fromLeft ? (byOffset.First(), byOffset.Last()) : (byOffset.Last(), byOffset.First());
        Assert.Equal(fromLeft ? 0 : 1, edge.Offset, 3);
        Assert.Equal(fromLeft ? 0.35 : 0.65, inner.Offset, 3);
        Assert.Equal(0, edge.Color.A);
        Assert.Equal(0x80, inner.Color.A);
        Assert.True(veil.IsFrozen);
        Assert.Same(veil, BarSurface.FrostVeilFrom(fromLeft, 0.35));
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
