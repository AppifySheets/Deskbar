using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Deskbar.App;
using Deskbar.Core.Domain;
using Deskbar.Core.Tiling;

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

    // The button's right-click menu offers the row menu's tidy item on the same terms (TileArrange.TidyStep over the
    // workspace's stored layouts), first, then the keep-space switch, which a tiled workspace always has, so the menu
    // is never empty there. The layouts are FloatingBarTilingMenuTests' A | C over B.
    static readonly TileLayout UnevenShape = TileLayout.Normal(
        new TileBranch(Axis.Down, [
            new TileChild(new TileBranch(Axis.Across, [new TileChild(TileLeaf.Instance, 0.3), new TileChild(TileLeaf.Instance, 0.7)]), 0.6),
            new TileChild(TileLeaf.Instance, 0.4)]),
        null);

    static readonly TileLayout EvenShape = UnevenShape.Evened();

    const string KeepSpace = "Keep the space when a window closes";

    static Guid Gepha(Harness harness) => harness.Manager.State.Workspaces.Single(w => w.Name == "GEPHA").Id;

    static IReadOnlyList<ScreenTileLayout> Stored(Harness harness) =>
        harness.Manager.State.Workspaces.Single(w => w.Name == "GEPHA").TileLayouts;

    // GEPHA (the workspace you are on) tiled, with `stored` as one screen's layout when given, and the button's menu
    // filled as a right-click fills it.
    static (Harness Harness, BarUnderTest Bar, ContextMenu Menu) TileMenu(bool pro, TileLayout? stored = null)
    {
        var harness = Harness.Build();
        Assert.True(harness.Manager.SetWorkspaceTiled(Gepha(harness), true).IsSuccess);
        if (stored is not null)
            Assert.True(harness.Manager.SetTileLayout(Gepha(harness), "0,0,1920,1080", stored).IsSuccess);
        var bar = ShowBar(harness, pro);
        Settle(bar);
        var menu = new ContextMenu();
        bar.Bar.FillTileMenu(menu);
        return (harness, bar, menu);
    }

    static IReadOnlyList<string?> Headers(ContextMenu menu) => menu.Items.OfType<MenuItem>().Select(i => i.Header?.ToString()).ToList();

    static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    [Fact]
    public void Menu_offers_even_out_while_a_split_is_uneven() => StaThread.Run(() =>
    {
        var (harness, bar, menu) = TileMenu(pro: true, UnevenShape);
        using var _ = bar;

        Assert.Equal(["Even out tiles", KeepSpace], Headers(menu));

        Click(menu.Items.OfType<MenuItem>().First());

        // The shape is kept, evened, and the next right-click offers the second step.
        Assert.Equal(EvenShape, Assert.Single(Stored(harness)).Layout);
        bar.Bar.FillTileMenu(menu);
        Assert.Equal(["Reset to grid", KeepSpace], Headers(menu));
    });

    [Fact]
    public void Menu_offers_reset_to_grid_once_even_with_a_custom_shape() => StaThread.Run(() =>
    {
        var (harness, bar, menu) = TileMenu(pro: true, EvenShape);
        using var _ = bar;

        Assert.Equal(["Reset to grid", KeepSpace], Headers(menu));

        Click(menu.Items.OfType<MenuItem>().First());

        Assert.Empty(Stored(harness));
    });

    [Fact]
    public void Menu_offers_neither_with_nothing_stored() => StaThread.Run(() =>
    {
        var (harness, bar, menu) = TileMenu(pro: true);
        using var _ = bar;

        // Ruling 2 (old empty tile-button menu): a tiled workspace always offers keep-space, so the menu is not empty.
        var keep = Assert.Single(menu.Items.OfType<MenuItem>());
        Assert.Equal(KeepSpace, keep.Header);
        Assert.True(keep.IsCheckable);

        // The switch flips from the same menu.
        Click(keep);
        Assert.True(harness.Manager.State.Workspaces.Single(w => w.Name == "GEPHA").TileKeepsSpace);
    });

    // The free tier gets no tidy item and no switch from the button: nothing to offer, so no menu opens at all.
    [Fact]
    public void Menu_is_empty_without_Pro() => StaThread.Run(() =>
    {
        var (_, bar, menu) = TileMenu(pro: false, UnevenShape);
        using var _ = bar;

        Assert.Empty(menu.Items);
    });
}
