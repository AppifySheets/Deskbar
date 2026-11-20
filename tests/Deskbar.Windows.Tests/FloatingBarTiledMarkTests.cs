using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Deskbar.App;
using Deskbar.Core.Domain;

namespace Deskbar.Windows.Tests;

// Which workspaces are tiled, seen from every row and not only the one you are on. Petre: "i want to have workspaces
// showing if they're tiled or not", then "icons in them are showing if they're tiled or they're not when it's active;
// let's show it for collapsed workspaces as well".
//
//   * A tiled row's caption carries the tile mark right after the name (FloatingBar.Tiling.cs, TiledMark), folded or
//     not, current or not, and only while tiling acts on it (a free copy shows none).
//   * The tile badge on an icon is asked with the row's workspace (FloatingBar.IsTiled), so the Tiler can answer for
//     a tiled workspace you are not on (Tiler.InTile).
//
// The harness: GEPHA (current, holding First) and Sparrow (holding Second); see Harness in FloatingBarRebuildTests.
public class FloatingBarTiledMarkTests
{
    static IEnumerable<DependencyObject> Descendants(DependencyObject node) =>
        LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()
            .SelectMany(child => new[] { child }.Concat(Descendants(child)));

    static Guid Id(Harness harness, string name) => harness.Manager.State.Workspaces.Single(w => w.Name == name).Id;

    static BarUnderTest ShowBar(Harness harness, bool pro = true, Func<WindowHandle, Guid?, bool>? isTiled = null)
    {
        var bar = new FloatingBar(harness.Manager)
        {
            Left = -32000, Top = -32000, IsPro = () => pro,
            IsTiled = isTiled ?? ((_, _) => false),
        };
        bar.Show();
        return new BarUnderTest(bar);
    }

    // A state pulse rebuilds the bar through the dispatcher; this lets it run. Every test changes the state AFTER the
    // bar is shown: the bar builds its rows once in its constructor, before the IsPro and IsTiled seams are set, and
    // only a later rebuild reads them (as in the app, where the 1 s tick's RefreshWindowFits is that rebuild).
    static void Settle(BarUnderTest bar) => bar.Bar.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

    // The caption line is a DockPanel holding the name's TextBlock and its marks (RowLabel); a caption with no marks
    // is the bare TextBlock and so has no tile mark either.
    static bool HasTileMark(Panel rows, string name) =>
        Descendants(rows).OfType<DockPanel>().Any(line =>
            line.Children.OfType<TextBlock>().Any(t => t.Text == name)
            && line.Children.OfType<FrameworkElement>().Any(e => Equals(e.Tag, FloatingBar.TiledMarkTag)));

    // The icon on the bar standing for `window`, named after its title (FloatingBar's IconTag is "icon").
    static Button Icon(Panel rows, WindowInfo window) =>
        Descendants(rows).OfType<Button>().First(b => b.Tag is "icon"
            && System.Windows.Automation.AutomationProperties.GetName(b) == window.DisplayName);

    static bool HasFitBadge(Button icon) =>
        Descendants(icon).OfType<FrameworkElement>().Any(e => Equals(e.Tag, FloatingBar.WindowFitBadgeTag));

    [Fact]
    public void A_tiled_workspace_you_are_not_on_carries_the_tile_mark_and_an_untiled_one_none() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = ShowBar(harness);
        Assert.True(harness.Manager.SetWorkspaceTiled(Id(harness, "Sparrow"), true).IsSuccess);
        Settle(bar);

        Assert.True(HasTileMark(bar.Rows, "Sparrow"));
        Assert.False(HasTileMark(bar.Rows, "GEPHA"));
    });

    [Fact]
    public void The_mark_comes_and_goes_with_the_flag() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = ShowBar(harness);
        Settle(bar);
        Assert.False(HasTileMark(bar.Rows, "GEPHA"));

        Assert.True(harness.Manager.SetWorkspaceTiled(Id(harness, "GEPHA"), true).IsSuccess);
        Settle(bar);
        Assert.True(HasTileMark(bar.Rows, "GEPHA"));

        Assert.True(harness.Manager.SetWorkspaceTiled(Id(harness, "GEPHA"), false).IsSuccess);
        Settle(bar);
        Assert.False(HasTileMark(bar.Rows, "GEPHA"));
    });

    // A folded row has no icons to carry their badges, so the caption is the only place left to say it.
    [Fact]
    public void A_folded_tiled_row_keeps_the_mark() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        Assert.True(harness.Manager.SetWorkspaceFolded(Id(harness, "Sparrow"), true).IsSuccess);
        using var bar = ShowBar(harness);
        Assert.True(harness.Manager.SetWorkspaceTiled(Id(harness, "Sparrow"), true).IsSuccess);
        Settle(bar);

        Assert.True(HasTileMark(bar.Rows, "Sparrow"));
    });

    // The flag outlives a lapsed Pro, but nothing arranges the workspace then (TilingGate.Acts), so the mark would lie.
    [Fact]
    public void A_copy_that_may_not_tile_shows_no_mark() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = ShowBar(harness, pro: false);
        Assert.True(harness.Manager.SetWorkspaceTiled(Id(harness, "Sparrow"), true).IsSuccess);
        Settle(bar);

        Assert.False(HasTileMark(bar.Rows, "Sparrow"));
    });

    // The icon's tile badge is asked with the workspace of the row it sits on, which is what lets a workspace you are
    // not on show its tiles.
    [Fact]
    public void The_tile_badge_is_asked_with_the_rows_workspace() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        var sparrow = Id(harness, "Sparrow");
        using var bar = ShowBar(harness, isTiled: (_, workspace) => workspace == sparrow);
        // The tick's check: the badges drawn before the seam was set are now out of date, so it rebuilds.
        bar.Bar.RefreshWindowFits();
        Settle(bar);

        Assert.True(HasFitBadge(Icon(bar.Rows, harness.Second)));
        Assert.False(HasFitBadge(Icon(bar.Rows, harness.First)));
    });
}
