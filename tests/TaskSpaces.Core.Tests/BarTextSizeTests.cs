using TaskSpaces.Core.Domain;

namespace TaskSpaces.Core.Tests;

// The bar's text size, as one setting. Petre: "increase font size by one more and make it configurable".
//
// The default started as +0.5, one half-point step. Petre then tried the slider's top: "i set text size to max
// and it's much better. make this, max size the default, ability to up or down". So it is +4 over the sizes
// written in the code, with room above it as well as below, and the setting still moves all of them together,
// in half points, so they keep their proportions to one another.
public class BarTextSizeTests
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

    // Never configured is the old maximum, the size Petre chose by trying it.
    [Fact]
    public void Unset_is_four_points_up() =>
        Assert.Equal(4, BarText.Clamp(null));

    // "Ability to up or down": the default sits strictly inside the rails, so the slider moves both ways.
    [Fact]
    public void The_default_leaves_room_both_ways()
    {
        Assert.True(BarText.Default > BarText.Minimum);
        Assert.True(BarText.Default < BarText.Maximum);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void A_value_that_is_not_a_number_is_the_default(double value) =>
        Assert.Equal(BarText.Default, BarText.Clamp(value));

    [Fact]
    public void It_is_held_inside_its_rails()
    {
        Assert.Equal(BarText.Minimum, BarText.Clamp(-50));
        Assert.Equal(BarText.Maximum, BarText.Clamp(50));
    }

    // Half points only, because that is the step every size on the bar moves in, and a hand-edited 0.3
    // would otherwise land between two sizes nobody chose.
    [Theory]
    [InlineData(0.3, 0.5)]
    [InlineData(1.2, 1.0)]
    [InlineData(-0.8, -1.0)]
    public void It_snaps_to_half_points(double value, double snapped) =>
        Assert.Equal(snapped, BarText.Clamp(value));

    [Fact]
    public void A_size_is_the_base_plus_the_offset() =>
        Assert.Equal(11.5, BarText.Size(10.5, 1.0));

    [Fact]
    public void The_offset_is_stored_and_persisted()
    {
        var manager = Started();

        manager.SetBarTextOffset(1.5);

        Assert.Equal(1.5, manager.State.BarTextOffset);
        Assert.Equal(1.5, store.Stored.BarTextOffset);
    }

    // The pulse is the live apply, exactly as for the other appearance settings.
    [Fact]
    public void A_change_pulses_and_a_non_change_does_not()
    {
        var manager = Started();
        var pulses = 0;
        using var _ = manager.StateChanged.Subscribe(_ => pulses++);

        manager.SetBarTextOffset(1.5);
        manager.SetBarTextOffset(1.5);

        Assert.Equal(1, pulses);
    }

    // Null clears it, so a later change of default still reaches whoever pressed Reset.
    [Fact]
    public void Null_goes_back_to_the_default()
    {
        var manager = Started();
        manager.SetBarTextOffset(2);

        manager.SetBarTextOffset(null);

        Assert.Null(manager.State.BarTextOffset);
    }

    [Fact]
    public void It_is_clamped_on_the_way_in()
    {
        var manager = Started();

        manager.SetBarTextOffset(99);

        Assert.Equal(BarText.Maximum, manager.State.BarTextOffset);
    }
}
