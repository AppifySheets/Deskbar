using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Deskbar.App;

namespace Deskbar.Windows.Tests;

// The window icon menu's "Split <app> windows last" (spec 2026-11-23-split-recent-app-windows-last-design): a
// checkable item that marks the window's APP, so its three latest windows are split only when no other tile is free.
// It reads and writes the manager's state directly, like "Remember <app>'s workspace".
//
// The harness: GEPHA (current) and Sparrow, one window each (see Harness in FloatingBarRebuildTests).
public class FloatingBarSplitLastMenuTests
{
    static IReadOnlyList<Button> IconButtons(DependencyObject root)
    {
        var found = new List<Button>();
        Collect(root);
        return found;

        void Collect(DependencyObject node) =>
            LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>().ToList().ForEach(child =>
            {
                if (child is Button { Tag: "icon" } button) found.Add(button);
                Collect(child);
            });
    }

    static MenuItem SplitLastItem(Button icon) =>
        FloatingBar.MenuOf(icon)!.Items.OfType<MenuItem>()
            .Single(i => i.Header?.ToString() is { } h && h.StartsWith("Split ") && h.EndsWith(" windows last"));

    static BarUnderTest ShowBar(Harness harness)
    {
        var bar = new FloatingBar(harness.Manager) { Left = -32000, Top = -32000 };
        bar.Show();
        return new BarUnderTest(bar);
    }

    [Fact]
    public void A_window_offers_an_unticked_Split_app_windows_last() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = ShowBar(harness);

        var item = SplitLastItem(IconButtons(bar.Rows).First());

        Assert.True(item.IsCheckable);
        Assert.False(item.IsChecked);
    });

    // Ticked through the pipe (or another window of the same app) after the menu was first built: opening the menu
    // shows the tick, because the item is read again each time the menu opens.
    [Fact]
    public void An_app_already_split_last_shows_it_ticked_when_the_menu_opens() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = ShowBar(harness);
        var icon = IconButtons(bar.Rows).First();
        var item = SplitLastItem(icon);
        var window = harness.Manager.KnownWindows.First(w => item.Header!.ToString()!.Contains(w.ProcessName));

        Assert.True(harness.Manager.SetSplitLast(window.Handle, true).IsSuccess);
        FloatingBar.MenuOf(icon)!.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));

        Assert.True(item.IsChecked);
    });

    [Fact]
    public void Ticking_it_marks_the_app() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = ShowBar(harness);
        var item = SplitLastItem(IconButtons(bar.Rows).First());
        var app = harness.Manager.KnownWindows.First(w => item.Header!.ToString()!.Contains(w.ProcessName)).ProcessName;

        item.IsChecked = true;
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        Assert.True(harness.Manager.SplitsLast(app));
    });
}
