using Deskbar.Core.Domain;

namespace Deskbar.Core.Tests;

// The frost's blur strength (FrostBlur). Petre on the first frost: "it's too blurry", "make it a setting?"
public class FrostBlurTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();

    WorkspaceManager Started()
    {
        var manager = new WorkspaceManager(desktops, monitor, titles, store);
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    // Manage's slider writes through SetFrostBlur: stored, clamped, and an unchanged value pulses nothing, so opening
    // Manage (which sets the slider from state) neither rewrites state.json nor rebuilds the bar.
    [Fact]
    public void The_setting_is_stored_clamped_and_written_once()
    {
        var manager = Started();
        var pulses = 0;
        using var _ = manager.StateChanged.Subscribe(_ => pulses++);

        manager.SetFrostBlur(99);
        manager.SetFrostBlur(99);

        Assert.Equal(FrostBlur.Maximum, manager.State.FrostBlur);
        Assert.Equal(FrostBlur.Maximum, store.Stored.FrostBlur);
        Assert.Equal(1, pulses);
    }

    // Null clears it, so a later change of default still reaches whoever pressed Reset.
    [Fact]
    public void Null_goes_back_to_the_default()
    {
        var manager = Started();
        manager.SetFrostBlur(3);
        manager.SetFrostBlur(null);
        Assert.Null(manager.State.FrostBlur);
    }

    [Fact]
    public void Never_configured_means_the_default() => Assert.Equal(FrostBlur.Default, FrostBlur.Clamp(null));

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(100, 32)]
    [InlineData(7.4, 7)]
    [InlineData(7.5, 8)]
    [InlineData(double.NaN, FrostBlur.Default)]
    [InlineData(double.PositiveInfinity, FrostBlur.Default)]
    public void Values_are_snapped_and_held_inside_the_rails(double given, double expected) =>
        Assert.Equal(expected, FrostBlur.Clamp(given));

    // No blur is no blur.
    [Fact]
    public void Zero_pixels_is_radius_zero() => Assert.Equal(0, FrostBlur.RadiusAt(0));

    // The radius gives back about the spread asked for: three boxes of radius r spread a point by sqrt(r(r + 1))
    // frost pixels, which is Shrink times that in screen pixels.
    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(28)]
    public void The_radius_gives_back_about_the_spread_asked_for(double screenPixels)
    {
        var r = FrostBlur.RadiusAt(screenPixels);
        var spread = Math.Sqrt(r * (r + 1.0)) * FrostBlur.Shrink;
        Assert.InRange(spread, screenPixels * 0.75, screenPixels * 1.25);
    }

    // A stronger setting never blurs less.
    [Fact]
    public void The_radius_never_falls_as_the_setting_rises() =>
        Assert.All(Enumerable.Range(1, 32), px => Assert.True(FrostBlur.RadiusAt(px) >= FrostBlur.RadiusAt(px - 1)));
}
