using System.Windows;
using System.Windows.Controls;
using Deskbar.App;

namespace Deskbar.Windows.Tests;

// The two activity marks must not change what an icon measures. Petre, on the sound ring: "when they do,
// workspace height grows; it has to be same height". The icon button wraps a 20 DIP artwork in 1 DIP of padding
// and 1 DIP of border to make the 24 DIP cell, so anything the marks add INSIDE the content is paid again by the
// row: a 24 DIP ring in a 20 DIP content slot made the button 28 and the line 4 DIP taller.
public class FloatingBarActivityLayoutTests
{
    static UIElement Artwork() => new Border { Width = 20, Height = 20 };

    static Size Measured(UIElement element)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return element.DesiredSize;
    }

    [Fact]
    public void A_sounding_icon_measures_the_same_as_a_silent_one() => StaThread.Run(() =>
        Assert.Equal(Measured(Artwork()), Measured(FloatingBar.Dressed(Artwork(), tabCount: 0, loud: true))));

    [Fact]
    public void A_tab_count_does_not_change_what_the_icon_measures() => StaThread.Run(() =>
        Assert.Equal(Measured(Artwork()), Measured(FloatingBar.Dressed(Artwork(), tabCount: 7, loud: false))));

    [Fact]
    public void Both_marks_together_do_not_change_what_the_icon_measures() => StaThread.Run(() =>
        Assert.Equal(Measured(Artwork()), Measured(FloatingBar.Dressed(Artwork(), tabCount: 7, loud: true))));

    // The pin badge on a pinned window in the window list ("a, pin badge") hangs past the corner, never into layout.
    [Fact]
    public void A_pin_badge_does_not_change_what_the_icon_measures() => StaThread.Run(() =>
        Assert.Equal(Measured(Artwork()), Measured(FloatingBar.WithPinBadge(Artwork()))));
}
