using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CSharpFunctionalExtensions;
using Deskbar.App;

namespace Deskbar.Windows.Tests;

// The task list's workspace menus (WorkspaceChipMenu): the add box's "other…", a line's workspace chip, the tasks window's
// "Move to". Petre: "when selecting, give them their formatting and colors". Each item is the workspace's chip in its
// lane colour, in the bar's order, grouped workspaces under their group's caption, which cannot be clicked. Built over
// the shared harness with the grouped fixture: GEPHA alone, then Sparrow and Archive in the group "Team".
public class WorkspaceChipMenuTests
{
    static IEnumerable<object> Inside(object node) =>
        new[] { node }.Concat(node is DependencyObject d ? LogicalTreeHelper.GetChildren(d).Cast<object>().SelectMany(Inside) : []);

    static string NameOf(MenuItem item) => Inside(item.Header).OfType<TextBlock>().First().Text;

    [Fact]
    public void The_order_is_the_bars_with_a_caption_over_a_group() => StaThread.Run(() =>
    {
        var harness = Harness.Build(grouped: true);

        var entries = WorkspaceChipMenu.Entries(harness.Manager.State, withNone: true);

        Assert.Equal(["none", "GEPHA", "Team", "Sparrow", "Archive"], entries.Select(e => e.Name));
        Assert.Equal([false, false, true, false, false], entries.Select(e => e.IsGroup));
    });

    [Fact]
    public void A_pinned_workspace_comes_first_as_on_the_bar() => StaThread.Run(() =>
    {
        var harness = Harness.Build(extraWorkspace: true);
        var archive = harness.Manager.State.Workspaces.Single(w => w.Name == "Archive").Id;
        Assert.True(harness.Manager.SetWorkspacePinned(archive, true).IsSuccess);

        Assert.Equal(["Archive", "GEPHA", "Sparrow"], WorkspaceChipMenu.Entries(harness.Manager.State, withNone: false).Select(e => e.Name));
    });

    [Fact]
    public void A_group_left_with_no_member_loses_its_caption() => StaThread.Run(() =>
    {
        var harness = Harness.Build(grouped: true);

        var entries = WorkspaceChipMenu.Entries(harness.Manager.State, withNone: false, w => w.Name == "GEPHA");

        Assert.Equal(["GEPHA"], entries.Select(e => e.Name));
    });

    [Fact]
    public void Items_carry_the_workspace_colours_and_captions_cannot_be_clicked() => StaThread.Run(() =>
    {
        var harness = Harness.Build(grouped: true);
        var state = harness.Manager.State;
        var picked = new List<Maybe<Guid>>();

        var menu = WorkspaceChipMenu.Fill(new ContextMenu(), WorkspaceChipMenu.Entries(state, withNone: true), Maybe<Guid>.None, picked.Add);

        var items = menu.Items.OfType<MenuItem>().ToList();
        // Every workspace's chip is filled with its lane colour, as LaneColour resolves it for the bar.
        state.Workspaces.ToList().ForEach(w =>
        {
            var chip = (Border)items.Single(i => NameOf(i) == w.Name).Header;
            var expected = (Color)ColorConverter.ConvertFromString(LaneColour.Of(state, w));
            var fill = ((SolidColorBrush)chip.Background).Color;
            Assert.Equal((expected.R, expected.G, expected.B), (fill.R, fill.G, fill.B));
        });
        // The group's caption is in the group's colour (its members share it) and cannot be chosen.
        var caption = items.Single(i => Equals(i.Tag, WorkspaceChipMenu.GroupTag));
        Assert.Equal("Team", NameOf(caption));
        Assert.False(caption.IsHitTestVisible);
        Assert.False(caption.Focusable);
        var sparrowColour = (Color)ColorConverter.ConvertFromString(LaneColour.Of(state, state.Workspaces.Single(w => w.Name == "Sparrow")));
        Assert.Equal(sparrowColour, ((SolidColorBrush)((TextBlock)caption.Header).Foreground).Color);

        // "none" is first and ticked as the current choice; a click on a chip picks its workspace.
        Assert.Equal("none", NameOf(items[0]));
        Assert.True(items[0].IsChecked);
        Assert.Equal(1, items.Count(i => i.IsChecked));
        items.Single(i => NameOf(i) == "Archive").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal([Maybe<Guid>.From(state.Workspaces.Single(w => w.Name == "Archive").Id)], picked);
    });

    // A long menu must scroll rather than run off the screen: the menu is bounded by the work area, and its template
    // holds the ScrollViewer that scrolls what does not fit. Checked, not assumed.
    [Fact]
    public void A_long_menu_is_bounded_and_scrolls() => StaThread.Run(() =>
    {
        var entries = Enumerable.Range(1, 60)
            .Select(i => new WorkspaceChipMenu.Entry(false, Guid.NewGuid(), $"Workspace {i}", null))
            .ToList();
        var anchor = new Window { Left = -32000, Top = -32000, Width = 100, Height = 100, ShowActivated = false, ShowInTaskbar = false };
        anchor.Show();
        try
        {
            var menu = WorkspaceChipMenu.Fill(new ContextMenu { PlacementTarget = anchor }, entries, Maybe<Guid>.None, _ => { });
            menu.IsOpen = true;
            menu.UpdateLayout();

            Assert.Equal(SystemParameters.WorkArea.Height, menu.MaxHeight);
            Assert.True(menu.ActualHeight <= SystemParameters.WorkArea.Height + 0.5);
            Assert.NotNull(Descendants(menu).OfType<ScrollViewer>().FirstOrDefault());
            menu.IsOpen = false;
        }
        finally
        {
            anchor.Close();
        }
    });

    static IEnumerable<DependencyObject> Descendants(DependencyObject node) =>
        Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(node)).Select(i => VisualTreeHelper.GetChild(node, i))
            .SelectMany(child => new[] { child }.Concat(Descendants(child)));
}
