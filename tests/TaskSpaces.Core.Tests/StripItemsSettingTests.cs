using TaskSpaces.Core.Persistence;

namespace TaskSpaces.Core.Tests;

// Which of the bar's bottom strip items show. Petre: "i don't want wifi there", "and i don't think i want
// notifications either", "make it a setting, what to show". The bar and the Settings tab are tested with the
// real windows; this is the setting itself.
public class StripItemsSettingTests
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

    // Nothing hidden until someone hides something, so an item added in a later build shows for everyone.
    [Fact]
    public void Everything_shows_by_default() =>
        Assert.Empty(Started().State.HiddenStripItems);

    // Saved, in StripItem's order and once each, whatever order the boxes were clicked in.
    [Fact]
    public void The_hidden_set_is_saved_in_one_shape()
    {
        var manager = Started();

        Assert.True(manager.SetHiddenStripItems([StripItem.Notifications, StripItem.QuickSettings, StripItem.Notifications]).IsSuccess);

        Assert.Equal([StripItem.QuickSettings, StripItem.Notifications], manager.State.HiddenStripItems);
        Assert.Equal([StripItem.QuickSettings, StripItem.Notifications], store.Stored.HiddenStripItems);
    }

    // THE ICON SIZE, the other setting the bottom strip round brought (IconScaling). Petre: "let's also make icon
    // sizes configurable", "as a separate setting", "including icons at the bottom".
    [Fact]
    public void Icon_size_is_built_size_until_set() =>
        Assert.Equal(1.0, Domain.IconScaling.Clamp(Started().State.IconScale));

    [Theory]
    [InlineData(0.1, 0.6)]
    [InlineData(9.0, 1.6)]
    [InlineData(1.23, 1.25)]
    [InlineData(double.NaN, 1.0)]
    public void Icon_size_is_held_to_its_rails_and_steps(double value, double expected) =>
        Assert.Equal(expected, Domain.IconScaling.Clamp(value), 6);

    [Fact]
    public void Icon_size_is_saved_and_the_same_value_again_writes_nothing()
    {
        var manager = Started();
        Assert.True(manager.SetIconScale(1.5).IsSuccess);
        Assert.Equal(1.5, store.Stored.IconScale);

        var pulses = 0;
        using var _ = manager.StateChanged.Subscribe(_ => pulses++);
        manager.SetIconScale(1.5);
        Assert.Equal(0, pulses);

        // Null is "back to the built size", not a value.
        manager.SetIconScale(null);
        Assert.Null(manager.State.IconScale);
    }

    // The same set again is not a change, so ticking boxes from state when Manage opens rebuilds nothing.
    [Fact]
    public void The_same_set_again_writes_nothing()
    {
        var manager = Started();
        manager.SetHiddenStripItems([StripItem.Notifications]);
        var pulses = 0;
        using var _ = manager.StateChanged.Subscribe(_ => pulses++);

        manager.SetHiddenStripItems([StripItem.Notifications]);

        Assert.Equal(0, pulses);
    }
}
