using System.Windows;
using System.Windows.Controls;
using Deskbar.App;
using Deskbar.Core.Domain;
using Deskbar.Core.Tiling;

namespace Deskbar.Windows.Tests;

// The row menu's tiling item (docs/superpowers/specs/2026-11-08-tiling-design.md). There is no on/off switch
// for tiling (Manage's "Tiling (experimental)" box went on 2026-11-19), so the item is always there. A Pro
// copy gets a checkable "Tile windows", and a free copy gets the same words with "(Deskbar Pro)" after them, which explains rather
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

    [Fact]
    public void Pro_gets_a_checkable_Tile_windows_that_shows_the_stored_flag() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
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
        Assert.True(harness.Manager.SetWorkspaceTiled(Id(harness, "Sparrow"), true).IsSuccess);
        using var bar = ShowBar(harness, pro: false);

        var headers = Headers(bar, "Sparrow");
        Assert.Contains("Tile windows (Deskbar Pro)", headers);
        Assert.Contains("Stop tiling", headers);

        Click(Items(bar, "Sparrow").Single(i => i.Header?.ToString() == "Stop tiling"));

        Assert.False(harness.Manager.State.Workspaces.Single(w => w.Name == "Sparrow").Tiled);
    });

    // The tidy item: "Even out tiles" or "Reset to grid", never both (docs/superpowers/specs/2026-11-19-tiling-split-tree-
    // design.md, "Even out tiles"). Which one the menu offers comes from what the workspace has stored
    // (Workspace.TileLayouts, read through TileArrange.TidyStep): a split dragged off even offers Even out, which evens
    // the splits and keeps the shape; a hand-made shape whose splits are all even offers Reset to grid, which forgets
    // it; nothing stored offers neither.
    //
    // A | C over B: two tiles side by side on top, one full width underneath, with the top pair split unevenly. Not the
    // automatic shape for three windows, so it stays stored once evened.
    static readonly TileLayout UnevenShape = TileLayout.Normal(
        new TileBranch(Axis.Down, [
            new TileChild(new TileBranch(Axis.Across, [new TileChild(TileLeaf.Instance, 0.3), new TileChild(TileLeaf.Instance, 0.7)]), 0.6),
            new TileChild(TileLeaf.Instance, 0.4)]),
        null);

    // The same shape with every split even: what Even out leaves behind.
    static readonly TileLayout EvenShape = UnevenShape.Evened();

    const string Screen = "0,0,1920,1080";

    // A tiled Sparrow and, when given, one screen's stored layout.
    static Harness TiledSparrow(TileLayout? stored = null)
    {
        var harness = Harness.Build();
        Assert.True(harness.Manager.SetWorkspaceTiled(Id(harness, "Sparrow"), true).IsSuccess);
        if (stored is not null)
            Assert.True(harness.Manager.SetTileLayout(Id(harness, "Sparrow"), Screen, stored).IsSuccess);
        return harness;
    }

    static IReadOnlyList<ScreenTileLayout> Stored(Harness harness) =>
        harness.Manager.State.Workspaces.Single(w => w.Name == "Sparrow").TileLayouts;

    // Even out keeps the shape, so the next look at the menu offers the second step, Reset to grid.
    [Fact]
    public void Menu_offers_even_out_while_a_split_is_uneven() => StaThread.Run(() =>
    {
        var harness = TiledSparrow(UnevenShape);
        using var bar = ShowBar(harness, pro: true);

        var headers = Headers(bar, "Sparrow").ToList();
        // Directly under "Tile windows".
        Assert.Equal(headers.IndexOf("Tile windows") + 1, headers.IndexOf("Even out tiles"));
        Assert.DoesNotContain("Reset to grid", headers);

        Click(Items(bar, "Sparrow").Single(i => i.Header?.ToString() == "Even out tiles"));

        Assert.Equal(EvenShape, Assert.Single(Stored(harness)).Layout);
        Assert.Contains("Reset to grid", Headers(bar, "Sparrow"));
    });

    [Fact]
    public void Menu_offers_reset_to_grid_once_even_with_a_custom_shape() => StaThread.Run(() =>
    {
        var harness = TiledSparrow(EvenShape);
        using var bar = ShowBar(harness, pro: true);

        var headers = Headers(bar, "Sparrow").ToList();
        Assert.Equal(headers.IndexOf("Tile windows") + 1, headers.IndexOf("Reset to grid"));
        Assert.DoesNotContain("Even out tiles", headers);

        Click(Items(bar, "Sparrow").Single(i => i.Header?.ToString() == "Reset to grid"));

        Assert.Empty(Stored(harness));
    });

    [Fact]
    public void Menu_offers_neither_with_nothing_stored() => StaThread.Run(() =>
    {
        var harness = TiledSparrow();
        using var bar = ShowBar(harness, pro: true);

        var headers = Headers(bar, "Sparrow");
        Assert.DoesNotContain("Even out tiles", headers);
        Assert.DoesNotContain("Reset to grid", headers);
    });

    const string KeepSpace = "Keep the space when a window closes";

    // The keep-space switch (Workspace.TileKeepsSpace): after the other tiling items, so after the tidy item when
    // that is offered and directly after "Tile windows" when it is not.
    [Theory]
    [InlineData(true, false, "Even out tiles")]
    [InlineData(false, true, "Reset to grid")]
    [InlineData(false, false, "Tile windows")]
    public void Keep_space_is_offered_on_a_tiled_workspace_after_the_tiling_items(bool uneven, bool even, string lastTilingItem) => StaThread.Run(() =>
    {
        var harness = TiledSparrow(uneven ? UnevenShape : even ? EvenShape : null);
        using var bar = ShowBar(harness, pro: true);

        var headers = Headers(bar, "Sparrow").ToList();

        Assert.Equal(headers.IndexOf(lastTilingItem) + 1, headers.IndexOf(KeepSpace));
    });

    [Fact]
    public void Keep_space_is_not_offered_on_an_untiled_workspace() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = ShowBar(harness, pro: true);

        Assert.DoesNotContain(KeepSpace, Headers(bar, "Sparrow"));
    });

    [Fact]
    public void Keep_space_is_not_offered_without_Pro() => StaThread.Run(() =>
    {
        var harness = TiledSparrow();
        using var free = ShowBar(harness, pro: false);

        Assert.DoesNotContain(KeepSpace, Headers(free, "Sparrow"));
    });

    [Fact]
    public void Keep_space_is_ticked_when_on_and_clicking_flips_it() => StaThread.Run(() =>
    {
        var harness = TiledSparrow();
        using var bar = ShowBar(harness, pro: true);

        var item = Items(bar, "Sparrow").Single(i => i.Header?.ToString() == KeepSpace);
        Assert.True(item.IsCheckable);
        Assert.False(item.IsChecked);

        Click(item);
        Assert.True(harness.Manager.State.Workspaces.Single(w => w.Name == "Sparrow").TileKeepsSpace);

        // The menu is filled again from the state, ticked now, and a second click turns it back off.
        var again = Items(bar, "Sparrow").Single(i => i.Header?.ToString() == KeepSpace);
        Assert.True(again.IsChecked);
        Click(again);
        Assert.False(harness.Manager.State.Workspaces.Single(w => w.Name == "Sparrow").TileKeepsSpace);
    });
}
