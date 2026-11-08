using System.Windows;
using System.Windows.Controls;
using Deskbar.App;

namespace Deskbar.Windows.Tests;

// The row menu's tiling item (docs/superpowers/specs/2026-11-08-tiling-design.md). Tiling is an experiment
// Petre wants "disabled by default, until i have tested it well", so while Manage's "Tiling (experimental)"
// box is unticked the row menu has no tiling item at all. Once it is ticked, a Pro copy gets a checkable
// "Tile windows", and a free copy gets the same words with "(Deskbar Pro)" after them, which explains rather
// than tiles. A workspace left tiled after Pro lapsed also offers "Stop tiling", so the flag can be cleared.
//
// Reached the way WPF reaches it: FloatingBar.MenuOf on the workspace row, which fills the lazy menu. The
// bar's IsPro seam stands in for Entitlement, because a Store licence cannot be faked into a test.
//
// The harness: GEPHA (current) and Sparrow, one window each (see Harness in FloatingBarRebuildTests).
public class FloatingBarTilingMenuTests
{
    static Guid Id(Harness harness, string name) => harness.Manager.State.Workspaces.Single(w => w.Name == name).Id;

    // Rows are a Border wrapping a Grid that carries the menu and the workspace's name as its accessible name.
    static Grid RowNamed(Panel rows, string name) =>
        rows.Children.OfType<Border>().Select(b => b.Child).OfType<Grid>()
            .Single(row => System.Windows.Automation.AutomationProperties.GetName(row) == name);

    static IReadOnlyList<MenuItem> Items(BarUnderTest bar, string row) =>
        FloatingBar.MenuOf(RowNamed(bar.Rows, row))!.Items.OfType<MenuItem>().ToList();

    static IReadOnlyList<string> Headers(BarUnderTest bar, string row) =>
        Items(bar, row).Select(i => i.Header?.ToString() ?? "").ToList();

    // Built here rather than through Harness.ShowBar, which has no way to say whether the copy is Pro.
    static BarUnderTest ShowBar(Harness harness, bool pro)
    {
        var bar = new FloatingBar(harness.Manager) { Left = -32000, Top = -32000, IsPro = () => pro };
        bar.Show();
        return new BarUnderTest(bar);
    }

    static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    // The default, and the whole of what Petre sees until he ticks the box: nothing about tiling, Pro or not.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void With_the_experiment_off_the_row_menu_has_no_tiling_item(bool pro) => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = ShowBar(harness, pro);

        var headers = Headers(bar, "Sparrow");

        Assert.Contains("Rename…", headers); // the menu itself is there
        Assert.DoesNotContain(headers, h => h.Contains("Tile", StringComparison.OrdinalIgnoreCase) || h.Contains("tiling"));
    });

    // ...including on a workspace still marked tiled from before the experiment was switched off: the flag is
    // kept (WorkspaceManager.SetExperimentalTiling), and the menu stays silent about it.
    [Fact]
    public void With_the_experiment_off_a_tiled_workspace_offers_nothing_either() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        Assert.True(harness.Manager.SetWorkspaceTiled(Id(harness, "Sparrow"), true).IsSuccess);
        using var bar = ShowBar(harness, pro: false);

        Assert.DoesNotContain(Headers(bar, "Sparrow"), h => h.Contains("Tile", StringComparison.OrdinalIgnoreCase) || h.Contains("tiling"));
    });

    [Fact]
    public void Pro_gets_a_checkable_Tile_windows_that_shows_the_stored_flag() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        Assert.True(harness.Manager.SetExperimentalTiling(true).IsSuccess);
        Assert.True(harness.Manager.SetWorkspaceTiled(Id(harness, "Sparrow"), true).IsSuccess);
        using var bar = ShowBar(harness, pro: true);

        var sparrow = Items(bar, "Sparrow").Single(i => i.Header?.ToString() == "Tile windows");
        Assert.True(sparrow.IsCheckable);
        Assert.True(sparrow.IsChecked);

        var gepha = Items(bar, "GEPHA").Single(i => i.Header?.ToString() == "Tile windows");
        Assert.True(gepha.IsCheckable);
        Assert.False(gepha.IsChecked);

        // Pro never sees the free-tier wording, nor a separate way to stop: unticking is the way.
        Assert.DoesNotContain(Headers(bar, "Sparrow"), h => h.Contains("Deskbar Pro") || h == "Stop tiling");
    });

    [Fact]
    public void Clicking_Tile_windows_flips_the_workspaces_flag() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        Assert.True(harness.Manager.SetExperimentalTiling(true).IsSuccess);
        using var bar = ShowBar(harness, pro: true);

        Click(Items(bar, "Sparrow").Single(i => i.Header?.ToString() == "Tile windows"));

        Assert.True(harness.Manager.State.Workspaces.Single(w => w.Name == "Sparrow").Tiled);
        Assert.False(harness.Manager.State.Workspaces.Single(w => w.Name == "GEPHA").Tiled);
    });

    // The free tier sees what it would get and where it comes from, not a box that does nothing when ticked.
    [Fact]
    public void Free_tier_gets_the_Deskbar_Pro_wording_and_no_checkbox() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        Assert.True(harness.Manager.SetExperimentalTiling(true).IsSuccess);
        using var bar = ShowBar(harness, pro: false);

        var items = Items(bar, "Sparrow");
        var item = items.Single(i => i.Header?.ToString() == "Tile windows (Deskbar Pro)");
        Assert.False(item.IsCheckable);
        Assert.DoesNotContain(items, i => i.Header?.ToString() is "Tile windows" or "Stop tiling");
    });

    // Pro lapsed (a trial ended) on a workspace already tiled: the Tiler stops acting by itself, and the menu
    // offers the one thing still worth doing, clearing the flag, so a later purchase does not surprise anyone
    // by tiling a workspace they had forgotten about.
    [Fact]
    public void A_tiled_workspace_without_Pro_offers_Stop_tiling_which_clears_the_flag() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        Assert.True(harness.Manager.SetExperimentalTiling(true).IsSuccess);
        Assert.True(harness.Manager.SetWorkspaceTiled(Id(harness, "Sparrow"), true).IsSuccess);
        using var bar = ShowBar(harness, pro: false);

        var headers = Headers(bar, "Sparrow");
        Assert.Contains("Tile windows (Deskbar Pro)", headers);
        Assert.Contains("Stop tiling", headers);

        Click(Items(bar, "Sparrow").Single(i => i.Header?.ToString() == "Stop tiling"));

        Assert.False(harness.Manager.State.Workspaces.Single(w => w.Name == "Sparrow").Tiled);
    });

    // "Even out tiles" (docs/superpowers/specs/2026-11-08-tile-resize-design.md): a split dragged by hand is
    // stored per screen, and this item forgets them all. Any stored entry is non-equal (SetTileSizes never
    // stores equal sizes), so "has stored sizes" is the whole test for "has a moved split".
    static readonly Deskbar.Core.Tiling.TileSizes Uneven = new([0.3, 0.7], [[1.0], [0.25, 0.75]]);

    // A tiled Sparrow with experiment on and, when asked, one moved split on a screen.
    static Harness TiledSparrow(bool moved)
    {
        var harness = Harness.Build();
        Assert.True(harness.Manager.SetExperimentalTiling(true).IsSuccess);
        Assert.True(harness.Manager.SetWorkspaceTiled(Id(harness, "Sparrow"), true).IsSuccess);
        if (moved)
            Assert.True(harness.Manager.SetTileSizes(Id(harness, "Sparrow"), "0,0,1920,1080", Uneven).IsSuccess);
        return harness;
    }

    [Fact]
    public void Even_out_is_offered_once_a_split_has_moved() => StaThread.Run(() =>
    {
        var harness = TiledSparrow(moved: true);
        using var bar = ShowBar(harness, pro: true);

        var headers = Headers(bar, "Sparrow").ToList();

        // Directly under "Tile windows".
        Assert.Equal(headers.IndexOf("Tile windows") + 1, headers.IndexOf("Even out tiles"));
    });

    [Fact]
    public void Even_out_is_not_offered_with_equal_tiles() => StaThread.Run(() =>
    {
        var harness = TiledSparrow(moved: false);
        using var bar = ShowBar(harness, pro: true);

        Assert.DoesNotContain("Even out tiles", Headers(bar, "Sparrow"));
    });

    [Fact]
    public void Even_out_is_not_offered_while_the_experiment_is_off() => StaThread.Run(() =>
    {
        var harness = TiledSparrow(moved: true);
        Assert.True(harness.Manager.SetExperimentalTiling(false).IsSuccess);
        using var bar = ShowBar(harness, pro: true);

        Assert.DoesNotContain("Even out tiles", Headers(bar, "Sparrow"));
    });

    [Fact]
    public void Even_out_clears_the_sizes_when_clicked() => StaThread.Run(() =>
    {
        var harness = TiledSparrow(moved: true);
        using var bar = ShowBar(harness, pro: true);

        Click(Items(bar, "Sparrow").Single(i => i.Header?.ToString() == "Even out tiles"));

        Assert.Empty(harness.Manager.State.Workspaces.Single(w => w.Name == "Sparrow").TileSizes);
    });
}
