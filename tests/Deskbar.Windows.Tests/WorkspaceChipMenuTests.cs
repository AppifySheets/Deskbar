using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;

namespace Deskbar.Windows.Tests;

// The task list's workspace menus (WorkspaceChipMenu): the add box's "other…", a line's workspace chip, the tasks
// window's "Move to", filter and add chip. Petre: "when selecting, give them their formatting and colors", then
// "compact it". Each workspace is its chip in its lane colour, side by side and wrapping, in the bar's order, grouped
// workspaces under their group's caption, which cannot be clicked. Built over the shared harness with the grouped
// fixture: GEPHA alone, then Sparrow and Archive in the group "Team".
public class WorkspaceChipMenuTests
{
    static string NameOf(Button chip) => AutomationProperties.GetName(chip);

    static bool IsCurrent(Button chip) => AutomationProperties.GetItemStatus(chip) == WorkspaceChipMenu.CurrentStatus;

    static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    // A hidden anchor window to open a menu from, off every screen, as the bar under test is.
    static T WithAnchor<T>(Func<Window, T> body)
    {
        var anchor = new Window { Left = -32000, Top = -32000, Width = 100, Height = 100, ShowActivated = false, ShowInTaskbar = false };
        anchor.Show();
        try
        {
            return body(anchor);
        }
        finally
        {
            anchor.Close();
        }
    }

    [Fact]
    public void The_order_is_the_bars_with_a_caption_over_a_group() => StaThread.Run(() =>
    {
        var harness = Harness.Build(grouped: true);

        var entries = WorkspaceChipMenu.Entries(harness.Manager.State, withNone: true);

        Assert.Equal(["none", "GEPHA", "Team", "Sparrow", "Archive"], entries.Select(e => e.Name));
        Assert.Equal([false, false, true, false, false], entries.Select(e => e.IsGroup));
        Assert.Equal([false, false, false, true, true], entries.Select(e => e.InGroup));
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
    public void Chips_carry_the_workspace_colours_and_captions_cannot_be_clicked() => StaThread.Run(() =>
    {
        var harness = Harness.Build(grouped: true);
        var state = harness.Manager.State;
        var picked = new List<Maybe<Guid>>();

        var menu = WorkspaceChipMenu.Build(WorkspaceChipMenu.Entries(state, withNone: true), Maybe<Guid>.None, picked.Add, null);

        var chips = WorkspaceChipMenu.Chips(menu);
        // "none" first, the ungrouped workspace next, then the group's members.
        Assert.Equal(["none", "GEPHA", "Sparrow", "Archive"], chips.Select(NameOf));
        // Every workspace's chip is filled with its lane colour, as LaneColour resolves it for the bar.
        state.Workspaces.ToList().ForEach(w =>
        {
            var expected = (Color)ColorConverter.ConvertFromString(LaneColour.Of(state, w));
            var fill = ((SolidColorBrush)chips.Single(c => NameOf(c) == w.Name).Background).Color;
            Assert.Equal((expected.R, expected.G, expected.B), (fill.R, fill.G, fill.B));
        });
        // The group's caption is in the group's colour (its members share it), sits over its chips, and cannot be chosen.
        var caption = menu.Items.OfType<StackPanel>().SelectMany(b => b.Children.OfType<TextBlock>()).Single(t => Equals(t.Tag, WorkspaceChipMenu.GroupTag));
        Assert.Equal("Team", caption.Text);
        Assert.False(caption.IsHitTestVisible);
        Assert.False(caption.Focusable);
        var sparrowColour = (Color)ColorConverter.ConvertFromString(LaneColour.Of(state, state.Workspaces.Single(w => w.Name == "Sparrow")));
        Assert.Equal(sparrowColour, ((SolidColorBrush)caption.Foreground).Color);

        // "none" is the current choice, alone; a click on a chip picks its workspace.
        Assert.Equal(["none"], chips.Where(IsCurrent).Select(NameOf));
        Click(chips.Single(c => NameOf(c) == "Archive"));
        Assert.Equal([Maybe<Guid>.From(state.Workspaces.Single(w => w.Name == "Archive").Id)], picked);
    });

    // The keyboard still works without menu rows: the chips are focusable buttons the Tab key and the arrows walk in a
    // cycle, Enter on one picks it (a button's own Enter), and the menu opens with the keyboard on the current choice.
    [Fact]
    public void The_chips_take_the_keyboard() => StaThread.Run(() =>
    {
        var harness = Harness.Build(grouped: true);
        var state = harness.Manager.State;
        var sparrow = state.Workspaces.Single(w => w.Name == "Sparrow").Id;

        WithAnchor(anchor =>
        {
            var menu = WorkspaceChipMenu.Build(WorkspaceChipMenu.Entries(state, withNone: true), sparrow, _ => { }, anchor);
            Assert.Equal(KeyboardNavigationMode.Cycle, KeyboardNavigation.GetTabNavigation(menu));
            Assert.Equal(KeyboardNavigationMode.Cycle, KeyboardNavigation.GetDirectionalNavigation(menu));
            Assert.All(WorkspaceChipMenu.Chips(menu), chip => Assert.True(chip.Focusable && chip.IsTabStop));
            menu.IsOpen = true;
            menu.UpdateLayout();
            Assert.Equal("Sparrow", NameOf(WorkspaceChipMenu.Chips(menu).Single(c => c.IsKeyboardFocused || c.IsFocused)));
            menu.IsOpen = false;
            return 0;
        });
    });

    // Petre: "compact it". With 22 workspaces in 6 groups, the compact menu is under 40% of the height of the first
    // version, which drew each chip as a menu row of its own (built here the way that version built it).
    [Fact]
    public void Twenty_two_workspaces_in_six_groups_take_under_forty_percent_of_the_old_height() => StaThread.Run(() =>
    {
        var groups = Enumerable.Range(1, 6).Select(i => new Group(Guid.NewGuid(), $"Group {i}")).ToList();
        var names = new[] { "Deskbar", "iam.ge", "Portal", "Store", "Site", "Runner", "Claude", "Docs", "Mail", "Bank", "Taxes",
            "Home", "Car", "Shop", "Music", "Photos", "Notes", "Ideas", "Kids", "Garden", "Travel", "Misc" };
        var workspaces = names.Select((name, i) => new Workspace(Guid.NewGuid(), name, Guid.NewGuid())
        {
            // Four ungrouped, then six groups of three.
            GroupId = i < 4 ? null : groups[(i - 4) / 3].Id,
        }).ToList();
        var state = AppState.Empty with { Workspaces = workspaces, Groups = groups };
        var entries = WorkspaceChipMenu.Entries(state, withNone: true);
        Assert.Equal(22 + 1 + 6, entries.Count);
        var picked = new List<Maybe<Guid>>();

        var (compact, old) = WithAnchor(anchor =>
        {
            var menu = WorkspaceChipMenu.Build(entries, Maybe<Guid>.None, picked.Add, anchor);
            var first = new ContextMenu { PlacementTarget = anchor };
            entries.ToList().ForEach(e => first.Items.Add(new MenuItem
            {
                Header = e.IsGroup
                    ? new TextBlock { Text = e.Name, FontSize = 10.5, FontWeight = FontWeights.Bold }
                    : new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 1, 8, 2), Child = new TextBlock { Text = e.Name } },
            }));
            menu.IsOpen = true;
            first.IsOpen = true;
            menu.UpdateLayout();
            first.UpdateLayout();
            var heights = (menu.ActualHeight, first.ActualHeight);
            // A pick still calls the same action and closes the menu.
            Click(WorkspaceChipMenu.Chips(menu).Single(c => NameOf(c) == "Garden"));
            Assert.False(menu.IsOpen);
            first.IsOpen = false;
            return heights;
        });

        // Measured on the theme the tests load, whose menu rows are about 22 DIP: 296 against 644 DIP when this was
        // written, under half. Petre's screenshot was the app's Fluent theme, whose rows were about 56 DIP each; the 29
        // rows of the first version took about 29 x 56 DIP there, and the compact menu is under 40% of that.
        Assert.True(compact < old * 0.5, $"compact {compact} DIP against {old} DIP");
        Assert.True(compact < entries.Count * 56 * 0.4, $"compact {compact} DIP against {entries.Count * 56} DIP of Fluent rows");
        Assert.Equal([Maybe<Guid>.From(workspaces.Single(w => w.Name == "Garden").Id)], picked);
    });

    // Review I-1: a block wider than the menu must wrap inside it. A menu's scroll viewer can measure its content at an
    // infinite width (the cause the bar's list had, ruling 20), and then Width only clips. On Petre's 140 to 197 DIP bar
    // the first block ("none" and the ungrouped workspaces) ran past the right edge.
    [Fact]
    public void A_block_wider_than_the_menu_wraps_inside_it() => StaThread.Run(() =>
    {
        var entries = new[] { "none" }.Concat(Enumerable.Range(1, 9).Select(i => $"Workspace {i}"))
            .Select((name, i) => new WorkspaceChipMenu.Entry(false, i == 0 ? Maybe<Guid>.None : Guid.NewGuid(), name, null))
            .ToList();
        WithAnchor(anchor =>
        {
            var menu = WorkspaceChipMenu.Build(entries, Maybe<Guid>.None, _ => { }, anchor, width: 150);
            menu.IsOpen = true;
            menu.UpdateLayout();

            var chips = WorkspaceChipMenu.Chips(menu).Select(c => c.TransformToAncestor(menu).TransformBounds(new Rect(c.RenderSize))).ToList();
            Assert.All(chips, chip => Assert.True(chip.Right <= menu.ActualWidth + 0.5, $"a chip ends at {chip.Right} in a menu {menu.ActualWidth} wide"));
            Assert.True(chips.Select(c => Math.Round(c.Top)).Distinct().Count() > 1, "the chips stayed on one row");
            menu.IsOpen = false;
            return 0;
        });
    });

    // A long menu is bounded by the work area of its screen, and its template scrolls what does not fit.
    [Fact]
    public void A_long_menu_is_bounded_and_scrolls() => StaThread.Run(() =>
    {
        var entries = Enumerable.Range(1, 400)
            .Select(i => new WorkspaceChipMenu.Entry(false, Guid.NewGuid(), $"Workspace {i}", null))
            .ToList();
        WithAnchor(anchor =>
        {
            var menu = WorkspaceChipMenu.Build(entries, Maybe<Guid>.None, _ => { }, anchor);
            menu.IsOpen = true;
            menu.UpdateLayout();

            Assert.True(menu.MaxHeight > 0 && !double.IsInfinity(menu.MaxHeight), $"MaxHeight {menu.MaxHeight}");
            Assert.True(menu.ActualHeight <= menu.MaxHeight + 0.5);
            Assert.NotNull(Descendants(menu).OfType<ScrollViewer>().FirstOrDefault());
            menu.IsOpen = false;
            return 0;
        });
    });

    static IEnumerable<DependencyObject> Descendants(DependencyObject node) =>
        Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(node)).Select(i => VisualTreeHelper.GetChild(node, i))
            .SelectMany(child => new[] { child }.Concat(Descendants(child)));
}
