using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Deskbar.App;

namespace Deskbar.Windows.Tests;

// The tiling button in the bar's header, beside Start and Search. Petre, testing tiling live: "maybe add a button
// at the top of the bar", then picked it from a mockup: one button for the workspace you are on, lit while that
// workspace is tiled. Since 2026-11-19 it is always shown on a named workspace: Manage's "Tiling (experimental)"
// box is gone, and this button is how a workspace is tiled.
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

    // The default, with nothing switched on anywhere: the button is there, Pro or not. Petre: "enable tiling in
    // the settings and remove it as an option; it's enough to have a button at the top".
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_tile_button_is_there_without_any_setting(bool pro) => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = ShowBar(harness, pro);
        Settle(bar);

        Assert.Equal(Visibility.Visible, Button(bar).Visibility);
    });

    [Fact]
    public void Clicking_it_tiles_the_workspace_you_are_on_and_again_stops() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
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
        using var bar = ShowBar(harness, pro: false);
        Settle(bar);

        Assert.Equal(Visibility.Visible, Button(bar).Visibility);
        Assert.Equal("Tile windows (Deskbar Pro)", bar.Bar.TileTip());
    });

    // Right-click offers "Even out tiles" once a split has been dragged (a stored size), and clicking it forgets the
    // sizes, as the row menu's item does. A tiled workspace always offers the keep-space switch, so the menu is
    // never empty there; "Even out tiles" comes first, and only once a split was dragged.
    [Fact]
    public void Right_click_offers_keep_space_on_a_tiled_workspace_and_Even_out_tiles_once_a_split_was_dragged() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        var gepha = harness.Manager.State.Workspaces.Single(w => w.Name == "GEPHA").Id;
        Assert.True(harness.Manager.SetWorkspaceTiled(gepha, true).IsSuccess);
        using var bar = ShowBar(harness, pro: true);
        Settle(bar);
        var menu = new ContextMenu();

        bar.Bar.FillTileMenu(menu);
        // Ruling 2 (old empty tile-button menu): a tiled workspace now always offers keep-space, so the menu is not empty.
        var keep = Assert.Single(menu.Items.OfType<MenuItem>());
        Assert.Equal("Keep the space when a window closes", keep.Header);
        Assert.True(keep.IsCheckable);

        Assert.True(harness.Manager.SetTileSizes(gepha, "0,0,1920,1080", new([0.3, 0.7], [[1.0], [0.25, 0.75]])).IsSuccess);
        Settle(bar);
        bar.Bar.FillTileMenu(menu);
        Assert.Equal(["Even out tiles", "Keep the space when a window closes"],
            menu.Items.OfType<MenuItem>().Select(i => i.Header?.ToString()).ToList());
        var even = menu.Items.OfType<MenuItem>().First();

        even.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Empty(harness.Manager.State.Workspaces.Single(w => w.Name == "GEPHA").TileSizes);

        // The switch flips from the same menu.
        bar.Bar.FillTileMenu(menu);
        menu.Items.OfType<MenuItem>().Single(i => i.IsCheckable).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.True(harness.Manager.State.Workspaces.Single(w => w.Name == "GEPHA").TileKeepsSpace);
    });
}
