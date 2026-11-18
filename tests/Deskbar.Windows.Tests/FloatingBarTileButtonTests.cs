using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Deskbar.App;

namespace Deskbar.Windows.Tests;

// The tiling button in the bar's header, beside Start and Search. Petre, testing tiling live: "maybe add a button
// at the top of the bar", then picked it from a mockup: one button for the workspace you are on, lit while that
// workspace is tiled, shown only while Manage's "Tiling (experimental)" box is ticked.
//
// The harness: GEPHA (current) and Sparrow, one window each (see Harness in FloatingBarRebuildTests). The bar's
// IsPro seam stands in for Entitlement, as in FloatingBarTilingMenuTests.
public class FloatingBarTileButtonTests
{
    static BarUnderTest ShowBar(Harness harness, bool pro)
    {
        var bar = new FloatingBar(harness.Manager) { Left = -32000, Top = -32000, IsPro = () => pro };
        bar.Show();
        return new BarUnderTest(bar);
    }

    // The header's own button, not the seam twin's (that one is only shown docked across two screens).
    static Button Button(BarUnderTest bar) => bar.Bar.TileButtons[0];

    static bool Tiled(Harness harness, string name) => harness.Manager.State.Workspaces.Single(w => w.Name == name).Tiled;

    // A state pulse rebuilds the bar through the dispatcher; this lets it run.
    static void Settle(BarUnderTest bar) => bar.Bar.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

    static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    // The default: nothing about tiling on the bar, Pro or not.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void With_the_experiment_off_there_is_no_tile_button(bool pro) => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = ShowBar(harness, pro);

        Assert.All(bar.Bar.TileButtons, b => Assert.Equal(Visibility.Collapsed, b.Visibility));
    });

    [Fact]
    public void Clicking_it_tiles_the_workspace_you_are_on_and_again_stops() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        Assert.True(harness.Manager.SetExperimentalTiling(true).IsSuccess);
        using var bar = ShowBar(harness, pro: true);
        Settle(bar);
        Assert.Equal(Visibility.Visible, Button(bar).Visibility);
        Assert.Equal("Tile windows on GEPHA", bar.Bar.TileTip());

        Click(Button(bar));
        Settle(bar);

        Assert.True(Tiled(harness, "GEPHA"));
        Assert.False(Tiled(harness, "Sparrow"));
        Assert.Equal("Stop tiling GEPHA", bar.Bar.TileTip());

        Click(Button(bar));
        Settle(bar);

        Assert.False(Tiled(harness, "GEPHA"));
    });

    // Lit the way Start and Search are while their flyout is open: the hover square kept.
    [Fact]
    public void It_is_lit_while_the_workspace_you_are_on_is_tiled() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        Assert.True(harness.Manager.SetExperimentalTiling(true).IsSuccess);
        using var bar = ShowBar(harness, pro: true);
        Settle(bar);
        var unlit = Button(bar).Background;

        Assert.True(harness.Manager.SetWorkspaceTiled(harness.Manager.State.Workspaces.Single(w => w.Name == "GEPHA").Id, true).IsSuccess);
        Settle(bar);

        Assert.NotEqual(unlit, Button(bar).Background);
    });

    // The free tier sees where tiling comes from, as the row menu says it.
    [Fact]
    public void Free_tier_is_told_it_comes_with_Deskbar_Pro() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        Assert.True(harness.Manager.SetExperimentalTiling(true).IsSuccess);
        using var bar = ShowBar(harness, pro: false);
        Settle(bar);

        Assert.Equal(Visibility.Visible, Button(bar).Visibility);
        Assert.Equal("Tile windows (Deskbar Pro)", bar.Bar.TileTip());
    });

    // Right-click offers "Even out tiles" once a split has been dragged (a stored size), and clicking it forgets the
    // sizes, as the row menu's item does. With nothing to even out the menu stays empty, so none opens.
    [Fact]
    public void Right_click_offers_Even_out_tiles_once_a_split_was_dragged() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        var gepha = harness.Manager.State.Workspaces.Single(w => w.Name == "GEPHA").Id;
        Assert.True(harness.Manager.SetExperimentalTiling(true).IsSuccess);
        Assert.True(harness.Manager.SetWorkspaceTiled(gepha, true).IsSuccess);
        using var bar = ShowBar(harness, pro: true);
        Settle(bar);
        var menu = new ContextMenu();

        bar.Bar.FillTileMenu(menu);
        Assert.Empty(menu.Items);

        Assert.True(harness.Manager.SetTileSizes(gepha, "0,0,1920,1080", new([0.3, 0.7], [[1.0], [0.25, 0.75]])).IsSuccess);
        Settle(bar);
        bar.Bar.FillTileMenu(menu);
        var even = Assert.Single(menu.Items.OfType<MenuItem>());
        Assert.Equal("Even out tiles", even.Header);

        even.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Empty(harness.Manager.State.Workspaces.Single(w => w.Name == "GEPHA").TileSizes);
    });
}
