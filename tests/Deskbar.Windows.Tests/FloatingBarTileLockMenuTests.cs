using System.Windows;
using System.Windows.Controls;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core.Domain;

namespace Deskbar.Windows.Tests;

// The window icon menu's "Don't split this tile" (spec 2026-11-22-tile-lock-and-other-screens): a checkable item,
// shown only while the window is a tile of the workspace being looked at, that locks or unlocks it in the Tiler. The
// bar reaches the Tiler through two seams App wires (TileLockOf, SetTileLocked); these tests set them by hand.
//
// The harness: GEPHA (current) and Sparrow, one window each (see Harness in FloatingBarRebuildTests).
public class FloatingBarTileLockMenuTests
{
    const string Header = "Don't split this tile";

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

    static MenuItem LockItem(Button icon) =>
        FloatingBar.MenuOf(icon)!.Items.OfType<MenuItem>().Single(i => i.Header?.ToString() == Header);

    static BarUnderTest ShowBar(Harness harness, Func<WindowHandle, Maybe<bool>> lockOf,
        Func<WindowHandle, bool, Result>? setLocked = null)
    {
        var bar = new FloatingBar(harness.Manager)
        {
            Left = -32000,
            Top = -32000,
            TileLockOf = lockOf,
            SetTileLocked = setLocked ?? ((_, _) => Result.Success()),
        };
        bar.Show();
        return new BarUnderTest(bar);
    }

    [Fact]
    public void A_tile_offers_an_unticked_Dont_split_this_tile() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = ShowBar(harness, _ => Maybe.From(false));

        var item = LockItem(IconButtons(bar.Rows).First());

        Assert.Equal(Visibility.Visible, item.Visibility);
        Assert.True(item.IsCheckable);
        Assert.False(item.IsChecked);
    });

    [Fact]
    public void A_locked_tile_shows_it_ticked() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = ShowBar(harness, _ => Maybe.From(true));

        Assert.True(LockItem(IconButtons(bar.Rows).First()).IsChecked);
    });

    // Review focus 5: a window that is not a tile shows nothing new. The item is still in the menu (the menu is built
    // once and kept, and the window can become a tile later), but collapsed.
    [Fact]
    public void A_window_that_is_not_a_tile_has_the_item_hidden() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = ShowBar(harness, _ => Maybe<bool>.None);

        Assert.Equal(Visibility.Collapsed, LockItem(IconButtons(bar.Rows).First()).Visibility);
    });

    // Clicking locks THAT icon's window: each icon's item names its own handle.
    [Fact]
    public void Clicking_it_locks_the_icons_own_window() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        var calls = new List<(WindowHandle Window, bool On)>();
        using var bar = ShowBar(harness, _ => Maybe.From(false), (w, on) => { calls.Add((w, on)); return Result.Success(); });

        IconButtons(bar.Rows).ToList().ForEach(icon => LockItem(icon).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)));

        Assert.Equal(
            new[] { harness.First.Handle, harness.Second.Handle }.OrderBy(h => h.Value),
            calls.Select(c => c.Window).OrderBy(h => h.Value));
        Assert.All(calls, c => Assert.True(c.On));
    });
}
