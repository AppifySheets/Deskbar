using System.Windows;
using System.Windows.Controls;
using Deskbar.App;
using Deskbar.Core.Persistence;

namespace Deskbar.Windows.Tests;

// The "Window list" submenu (docs/superpowers/specs/2026-11-19-screen-strips-design.md, "Choosing it"). The
// current workspace's titled list used to have one flipping menu item; with a third place, the screens, a flip
// cannot name three things, so both the workspace row menu and the bar's background menu now carry a parent
// item with one checkable child per place, the current one ticked.
//
// Reached the way WPF reaches it: FloatingBar.MenuOf on a workspace row, and FillBarMenu for the background.
public class FloatingBarWindowListMenuTests
{
    const string Row = "In the workspace's row";
    const string Bottom = "At the bottom of the bar";
    const string Screens = "At the top of each screen";

    static Grid RowNamed(Panel rows, string name) =>
        rows.Children.OfType<Border>().Select(b => b.Child).OfType<Grid>()
            .Single(row => System.Windows.Automation.AutomationProperties.GetName(row) == name);

    static BarUnderTest ShowBar(Harness harness)
    {
        var bar = new FloatingBar(harness.Manager) { Left = -32000, Top = -32000 };
        bar.Show();
        return new BarUnderTest(bar);
    }

    static IReadOnlyList<MenuItem> RowMenu(BarUnderTest bar) =>
        FloatingBar.MenuOf(RowNamed(bar.Rows, "Sparrow"))!.Items.OfType<MenuItem>().ToList();

    // The background menu is refilled on demand; FillBarMenu is the same call ContextMenuOpening makes.
    static IReadOnlyList<MenuItem> BarMenu(BarUnderTest bar)
    {
        var menu = new ContextMenu();
        bar.Bar.FillBarMenu(menu);
        return menu.Items.OfType<MenuItem>().ToList();
    }

    static MenuItem WindowList(IReadOnlyList<MenuItem> items) => items.Single(i => i.Header?.ToString() == "Window list");

    static IReadOnlyList<MenuItem> Places(IReadOnlyList<MenuItem> items) => WindowList(items).Items.OfType<MenuItem>().ToList();

    static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    [Fact]
    public void Both_menus_offer_the_three_places() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = ShowBar(harness);

        foreach (var items in new[] { RowMenu(bar), BarMenu(bar) })
        {
            Assert.Equal([Row, Bottom, Screens], Places(items).Select(i => i.Header?.ToString()));
            Assert.All(Places(items), i => Assert.True(i.IsCheckable));
        }
    });

    [Theory]
    [InlineData(WindowListPlace.Row, Row)]
    [InlineData(WindowListPlace.Bottom, Bottom)]
    [InlineData(WindowListPlace.Screens, Screens)]
    public void The_current_place_is_checked(WindowListPlace place, string header) => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        Assert.True(harness.Manager.SetWindowListPlace(place).IsSuccess);
        using var bar = ShowBar(harness);

        foreach (var items in new[] { RowMenu(bar), BarMenu(bar) })
            Assert.Equal([header], Places(items).Where(i => i.IsChecked).Select(i => i.Header?.ToString()));
    });

    [Theory]
    [InlineData(Row, WindowListPlace.Row)]
    [InlineData(Bottom, WindowListPlace.Bottom)]
    [InlineData(Screens, WindowListPlace.Screens)]
    public void Choosing_a_place_sets_it(string header, WindowListPlace expected) => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        // Start somewhere else, so choosing is a change and not a no-op.
        Assert.True(harness.Manager.SetWindowListPlace(expected == WindowListPlace.Bottom ? WindowListPlace.Screens : WindowListPlace.Bottom).IsSuccess);
        using var bar = ShowBar(harness);

        Click(Places(RowMenu(bar)).Single(i => i.Header?.ToString() == header));

        Assert.Equal(expected, harness.Manager.State.WindowListPlace);
    });

    // "Clicking a collapsed bar ..." only means something while rows expand (Row); at the bottom or on the
    // screens no bar ever opens.
    [Theory]
    [InlineData(WindowListPlace.Row, true)]
    [InlineData(WindowListPlace.Bottom, false)]
    [InlineData(WindowListPlace.Screens, false)]
    public void The_collapsed_click_item_is_offered_only_for_row(WindowListPlace place, bool offered) => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        Assert.True(harness.Manager.SetWindowListPlace(place).IsSuccess);
        using var bar = ShowBar(harness);

        foreach (var items in new[] { RowMenu(bar), BarMenu(bar) })
            Assert.Equal(offered, items.Any(i => i.Header?.ToString() is "Icons on collapsed bars bring their window up" or "Clicking a collapsed bar switches to its workspace"));
    });
}
