using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Deskbar.App;

namespace Deskbar.Windows.Tests;

// The bar's arrange mode (FloatingBar.Arrange.cs; spec docs/superpowers/specs/2026-11-20-bar-edit-mode-design.md, "Turning
// it on and off" and "What changes on the bar while it is on"). Only the MODE is covered here: the grips, the dimmed
// icons, the Done strip, the presses that must do nothing, and the ways out. The drag that arranges is a later part.
//
// Built on the rebuild tests' Harness (a grouped bar: GEPHA and Sparrow and Archive, the last two in the group "Team"),
// parked far off every screen like every bar test.
public class FloatingBarArrangeTests
{
    // Every workspace row and every group header gets a grip before its name: three rows and one header here. The grip
    // is a TextBlock of its own, so a row's name is still exactly its name.
    [Fact]
    public void Arranging_puts_a_grip_on_every_row_and_header() => StaThread.Run(() =>
    {
        var harness = Harness.Build(grouped: true);
        using var bar = harness.ShowBar();
        Assert.Empty(Grips(bar.Rows)); // precondition: none while the mode is off

        Assert.True(bar.Bar.SetArranging(true).IsSuccess);

        Assert.Equal(4, Grips(bar.Rows).Count);
        Assert.All(Grips(bar.Rows), grip => Assert.Equal("⋮⋮", grip.Text));
        Assert.Contains(Texts(bar.Rows), t => t == "GEPHA"); // the name itself is untouched

        Assert.True(bar.Bar.SetArranging(false).IsSuccess);

        Assert.Empty(Grips(bar.Rows));
    });

    // The whole point of the mode: a press that would switch a row does nothing, and the press is marked handled so
    // nothing further along its route (a row, a fold toggle, a window drag) sees it either.
    [Fact]
    public void A_click_on_a_row_does_not_switch_while_arranging() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = harness.ShowBar();
        var row = RowFor(bar.Rows, "Sparrow");

        Assert.True(bar.Bar.SetArranging(true).IsSuccess);
        row = RowFor(bar.Rows, "Sparrow"); // the mode rebuilt the bar
        var press = Press(row);
        Release(row);

        Assert.Empty(harness.Desktops.Switched);
        Assert.True(press.Handled);

        // ...and once the mode is off the same click switches again, so the test is about the mode and not a dead row.
        Assert.True(bar.Bar.SetArranging(false).IsSuccess);
        row = RowFor(bar.Rows, "Sparrow");
        Press(row);
        Release(row);

        Assert.Single(harness.Desktops.Switched);
    });

    // The bar's own menu carries one item that flips with the mode, and a group header's menu carries it too.
    [Fact]
    public void The_menu_item_flips_to_done_arranging() => StaThread.Run(() =>
    {
        var harness = Harness.Build(grouped: true);
        using var bar = harness.ShowBar();
        var sparrow = harness.Manager.State.Workspaces.Single(w => w.Name == "Sparrow").Id;
        var group = harness.Manager.State.Groups.Single();

        IReadOnlyList<string?> Headers(ContextMenu menu) => menu.Items.OfType<MenuItem>().Select(i => i.Header as string).ToList();
        MenuItem Item(ContextMenu menu, string header) => menu.Items.OfType<MenuItem>().Single(i => i.Header as string == header);

        Assert.Contains("Arrange workspaces", Headers(bar.Bar.WorkspaceMenu(sparrow, "Sparrow", folded: false)));
        Assert.Contains("Arrange workspaces", Headers(bar.Bar.GroupMenu(group)));

        Item(bar.Bar.WorkspaceMenu(sparrow, "Sparrow", folded: false), "Arrange workspaces").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.True(bar.Bar.Arranging);

        Assert.Contains("Done arranging", Headers(bar.Bar.WorkspaceMenu(sparrow, "Sparrow", folded: false)));
        Assert.DoesNotContain("Arrange workspaces", Headers(bar.Bar.WorkspaceMenu(sparrow, "Sparrow", folded: false)));
        Assert.Contains("Done arranging", Headers(bar.Bar.GroupMenu(group)));

        Item(bar.Bar.GroupMenu(group), "Done arranging").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.False(bar.Bar.Arranging);
    });

    // Esc is a preview key on the bar, handled so it means nothing else, and only while the mode is on.
    [Fact]
    public void Esc_ends_the_mode() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = harness.ShowBar();
        Assert.True(bar.Bar.SetArranging(true).IsSuccess);
        Assert.True(bar.Bar.Arranging);

        var key = EscapeKey(bar.Bar);
        bar.Bar.RaiseEvent(key);

        Assert.False(bar.Bar.Arranging);
        Assert.True(key.Handled);
        Assert.Empty(Grips(bar.Rows));

        // With the mode off Esc is left alone for whatever else wants it.
        var again = EscapeKey(bar.Bar);
        bar.Bar.RaiseEvent(again);
        Assert.False(again.Handled);
    });

    // The strip at the foot of the rows: the instruction, and a Done button that ends the mode. Gone with the mode.
    [Fact]
    public void Done_ends_the_mode() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = harness.ShowBar();
        Assert.Null(ArrangeStrip(bar.Bar)); // precondition: no strip while the mode is off

        Assert.True(bar.Bar.SetArranging(true).IsSuccess);

        var strip = ArrangeStrip(bar.Bar)!;
        Assert.Contains(Texts(strip), t => t == "Drag rows to arrange · Esc");
        var done = Descendants(strip).OfType<Button>().Single();
        Assert.Contains(Texts(done), t => t == "Done");

        done.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        Assert.False(bar.Bar.Arranging);
        Assert.Null(ArrangeStrip(bar.Bar));
    });

    // A press on the Done strip is not swallowed like a press on a row: it has to reach its button.
    [Fact]
    public void A_press_on_the_done_strip_is_left_for_its_button() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = harness.ShowBar();
        Assert.True(bar.Bar.SetArranging(true).IsSuccess);
        var done = Descendants(ArrangeStrip(bar.Bar)!).OfType<Button>().Single();

        var press = Press(done);

        Assert.False(press.Handled);
    });

    // Icons dim to 45% while the mode is on, and come back at full strength when it ends.
    [Fact]
    public void Icons_are_dimmed_while_arranging() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = harness.ShowBar();
        Assert.All(IconButtons(bar.Rows), icon => Assert.Equal(1.0, icon.Opacity));

        Assert.True(bar.Bar.SetArranging(true).IsSuccess);

        Assert.NotEmpty(IconButtons(bar.Rows));
        Assert.All(IconButtons(bar.Rows), icon => Assert.Equal(0.45, icon.Opacity, 3));

        Assert.True(bar.Bar.SetArranging(false).IsSuccess);

        Assert.All(IconButtons(bar.Rows), icon => Assert.Equal(1.0, icon.Opacity));
    });

    // --- helpers ------------------------------------------------------------------------------------------------

    static KeyEventArgs EscapeKey(FloatingBar bar) =>
        new(Keyboard.PrimaryDevice, PresentationSource.FromVisual(bar)!, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };

    // The tunnelling and bubbling halves of a press as the input manager delivers them (see FloatingBarRebuildTests.Press).
    // Returns the TUNNELLING half, the one the bar's window-level handler sees first, so a test can read Handled.
    static MouseButtonEventArgs Press(UIElement target)
    {
        var preview = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent };
        target.RaiseEvent(preview);
        target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseDownEvent });
        return preview;
    }

    static void Release(UIElement target) =>
        target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseUpEvent });

    static Grid RowFor(Panel rows, string label) =>
        rows.Children.OfType<Border>()
            .Select(b => b.Child)
            .OfType<Grid>()
            .Single(row => Texts(row).Any(t => t == label)
                           || System.Windows.Automation.AutomationProperties.GetName(row) == label);

    static IReadOnlyList<TextBlock> Grips(DependencyObject root) =>
        Descendants(root).OfType<TextBlock>().Where(t => t.Tag is FloatingBar.ArrangeGripTag).ToList();

    static IReadOnlyList<string> Texts(DependencyObject root) =>
        Descendants(root).OfType<TextBlock>().Select(t => t.Text).ToList();

    static IReadOnlyList<Button> IconButtons(DependencyObject root) =>
        Descendants(root).OfType<Button>().Where(b => b.Tag is "icon").ToList();

    static Border? ArrangeStrip(FloatingBar bar) =>
        Descendants(bar).OfType<Border>().SingleOrDefault(b => b.Tag is "arrange-strip");

    // The logical tree, flattened: built but not necessarily laid out, so templates may not have expanded. A Button's
    // content is a logical child, which is all these tests need.
    static IReadOnlyList<DependencyObject> Descendants(DependencyObject root) =>
        LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()
            .SelectMany(child => Descendants(child).Prepend(child))
            .ToList();
}
