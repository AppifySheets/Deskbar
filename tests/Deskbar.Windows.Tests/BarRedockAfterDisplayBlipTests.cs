using Deskbar.App;

namespace Deskbar.Windows.Tests;

// When a settle sends a docked bar back to the dock remembered for the desk (FloatingBar.GoBackToRememberedDock).
// Petre (2026-11-23): "deskbar reappeared on another monitor after i attempted to connect rdp". Both screens left
// and came back inside one debounce, so the settle saw the same desk and kept the bar docked on the primary, where
// Windows had moved it while the left screen was gone. Being off the remembered screen now sends it back too.
public class BarRedockAfterDisplayBlipTests
{
    [Theory]
    // The incident: same desk, docked right on the primary, remembered right on the left screen. Goes back.
    [InlineData(false, "right", false, "right", true, true)]
    // Same desk, on the remembered screen: nothing to do, and no undock-and-dock flicker.
    [InlineData(false, "right", false, "right", false, false)]
    // Another desk: goes back whether or not it strayed, as before this fix.
    [InlineData(true, "right", false, "right", false, true)]
    [InlineData(true, "seam", false, "seam", false, true)]
    // A seam dock spans two screens, so on the same desk "strayed" is not trusted for it.
    [InlineData(false, "seam", false, "seam", true, false)]
    [InlineData(false, "right", false, "seam", true, false)]
    // Floating, minimized, or nothing remembered for this desk: never.
    [InlineData(true, null, false, "right", true, false)]
    [InlineData(true, "right", true, "right", true, false)]
    [InlineData(true, "right", false, null, true, false)]
    public void A_docked_bar_goes_back_when_the_desk_changed_or_it_strayed_off_its_screen(
        bool deskChanged, string? dockedEdge, bool minimized, string? rememberedEdge, bool strayed, bool goesBack) =>
        Assert.Equal(goesBack, FloatingBar.GoBackToRememberedDock(deskChanged, dockedEdge, minimized, rememberedEdge, strayed));
}
