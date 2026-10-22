using Deskbar.App;
using Deskbar.Core.Domain;

namespace Deskbar.Windows.Tests;

// Petre: "dimming should turn off in docked mode". A docked bar covers nothing, since the shell lays windows
// out beside its strip, so it rests at full strength whatever the dimming slider says. Pure, so it is tested
// without registering a real app bar on the machine running the suite.
public class FloatingBarDockedFadeTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(0.05)]
    [InlineData(0.35)]
    public void A_docked_bar_rests_at_full_strength(double? configured) =>
        Assert.Equal(1.0, FloatingBar.RestingOpacity(docked: true, configured));

    // Floating, the setting applies as it always did, default included.
    [Fact]
    public void A_floating_bar_rests_at_the_configured_dimming()
    {
        Assert.Equal(0.5, FloatingBar.RestingOpacity(docked: false, 0.5));
        Assert.Equal(BarFading.Default, FloatingBar.RestingOpacity(docked: false, null));
    }
}
