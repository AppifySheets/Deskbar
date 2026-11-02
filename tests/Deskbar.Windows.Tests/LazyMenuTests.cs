using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Deskbar.App;
using Deskbar.Core.Overview;

namespace Deskbar.Windows.Tests;

// Petre: "when activating a bar in deskbar, it takes a little while to activate." Part of the wait was the
// bar building a context menu for every icon and every row on every rebuild, right-clicked or not: measured
// at 50-80ms of a 140ms draw on his bar, two rebuilds per switch. A menu is now filled the first time it is
// asked to open, and FloatingBar.MenuOf is the one way to ask, for WPF and for these tests alike.
public class LazyMenuTests
{
    [Fact]
    public void An_icons_menu_is_empty_until_it_is_asked_for() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = harness.ShowBar();
        var icon = IconButtons(bar.Rows).First();

        Assert.NotNull(icon.ContextMenu);
        Assert.Empty(icon.ContextMenu.Items);

        var menu = FloatingBar.MenuOf(icon);
        Assert.NotNull(menu);
        Assert.Contains("Rename this window…", menu.Items.OfType<MenuItem>().Select(i => (string)i.Header));
        Assert.Same(menu, icon.ContextMenu);
        Assert.Same(menu, FloatingBar.MenuOf(icon));
        Assert.Equal(menu.Items.Count, FloatingBar.MenuOf(icon)!.Items.Count); // asked twice, filled once
    });

    [Fact]
    public void A_workspace_rows_menu_is_empty_until_it_is_asked_for() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = harness.ShowBar();
        var row = RowNamed(bar.Rows, "Sparrow");

        Assert.NotNull(row.ContextMenu);
        Assert.Empty(row.ContextMenu.Items);

        var menu = FloatingBar.MenuOf(row);
        Assert.NotNull(menu);
        Assert.NotEmpty(menu.Items);
        Assert.Same(menu, row.ContextMenu);
    });

    // What WPF does on a right-click: it raises ContextMenuOpening on the element under the pointer, its own
    // class handler marks the innermost element that has a menu as the target, and after the event it opens
    // the target's menu. The target has to be the icon, not the row or the bar's own Root, which both have
    // menus of their own further up the tree, and the icon's menu has to be filled by the time the event is
    // over.
    [Fact]
    public void The_opening_event_fills_the_icons_menu_and_the_icon_is_the_target() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = harness.ShowBar();
        var icon = IconButtons(bar.Rows).First();

        var opening = (ContextMenuEventArgs)Activator.CreateInstance(typeof(ContextMenuEventArgs),
            BindingFlags.Instance | BindingFlags.NonPublic, null, [icon, true, 0.0, 0.0], null)!;
        opening.RoutedEvent = FrameworkElement.ContextMenuOpeningEvent;
        icon.RaiseEvent(opening);

        Assert.NotEmpty(icon.ContextMenu!.Items);
        var target = typeof(ContextMenuEventArgs).GetProperty("TargetElement", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(opening);
        Assert.Same(icon, target);
    });

    [Fact]
    public void A_row_that_never_had_a_menu_still_has_none() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = harness.ShowBar();

        Assert.Null(FloatingBar.MenuOf(RowNamed(bar.Rows, "Pinned")));
    });

    static IReadOnlyList<Button> IconButtons(DependencyObject root) =>
        Descendants(root).OfType<Button>().Where(b => b.DataContext is WindowRow).ToList();

    static Grid RowNamed(Panel rows, string name) =>
        rows.Children.OfType<Border>().Select(b => b.Child).OfType<Grid>()
            .Single(row => System.Windows.Automation.AutomationProperties.GetName(row) == name);

    static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        var count = VisualTreeHelper.GetChildrenCount(node);
        foreach (var i in Enumerable.Range(0, count))
        {
            var child = VisualTreeHelper.GetChild(node, i);
            yield return child;
            foreach (var deeper in Descendants(child)) yield return deeper;
        }
    }
}
