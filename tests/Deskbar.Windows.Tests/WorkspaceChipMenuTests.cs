using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core.Domain;
using Deskbar.Core.Geometry;
using Deskbar.Core.Persistence;
using Deskbar.Core.Tasks;

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

    // Type to jump (task 6184e8bf): letters typed in the menu name the chip the keyboard goes to, a name's start first,
    // then any part of it, ignoring case; letters that name nothing leave it where it was.
    [Fact]
    public void Typed_letters_name_the_chip_by_its_start_then_by_any_part() => StaThread.Run(() =>
    {
        var harness = Harness.Build(grouped: true);
        var menu = WorkspaceChipMenu.Build(WorkspaceChipMenu.Entries(harness.Manager.State, withNone: true), Maybe<Guid>.None, _ => { }, null);

        Assert.Equal("Sparrow", NameOf(WorkspaceChipMenu.TypeAheadMatch(menu, "sp").Value));
        Assert.Equal("GEPHA", NameOf(WorkspaceChipMenu.TypeAheadMatch(menu, "g").Value));
        Assert.Equal("Sparrow", NameOf(WorkspaceChipMenu.TypeAheadMatch(menu, "arr").Value));
        Assert.True(WorkspaceChipMenu.TypeAheadMatch(menu, "zz").HasNoValue);
    });

    // Review minor 4 of 187cf4a: one very long workspace name is trimmed inside its chip, the full name in the chip's
    // tooltip, so it cannot run past the menu's edge.
    [Fact]
    public void A_very_long_name_is_trimmed_with_the_whole_name_in_its_tooltip() => StaThread.Run(() =>
    {
        var harness = Harness.Build(grouped: true);
        var sparrow = harness.Manager.State.Workspaces.Single(w => w.Name == "Sparrow").Id;
        var longName = string.Concat(Enumerable.Repeat("a very long workspace name ", 8)).Trim();
        Assert.True(harness.Manager.RenameWorkspace(sparrow, longName).IsSuccess);

        var menu = WorkspaceChipMenu.Build(WorkspaceChipMenu.Entries(harness.Manager.State, withNone: true), Maybe<Guid>.None, _ => { }, null);
        var chip = WorkspaceChipMenu.Chips(menu).Single(c => NameOf(c) == longName);
        var text = (TextBlock)chip.Content;
        text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        Assert.True(text.DesiredSize.Width <= WorkspaceChipMenu.ChipTextCeiling, $"{text.DesiredSize.Width}");
        Assert.Equal(TextTrimming.CharacterEllipsis, text.TextTrimming);
        Assert.Equal(longName, chip.ToolTip);
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
            // The menu's Opened handler gives the keyboard to StartChip. The test checks that choice rather than the focus
            // itself: the CI runner never hands an opening popup the keyboard, so no chip was focused there even after
            // the dispatcher ran down, while on a desktop one is.
            Assert.Equal("Sparrow", NameOf(WorkspaceChipMenu.StartChip(menu).Value));
            var noCurrent = WorkspaceChipMenu.Build(WorkspaceChipMenu.Entries(state, withNone: true), Guid.NewGuid(), _ => { }, anchor);
            Assert.Same(WorkspaceChipMenu.Chips(noCurrent)[0], WorkspaceChipMenu.StartChip(noCurrent).Value);
            return 0;
        });
    });

    // A task made `minutes` after a fixed moment in `workspace`, for the Recent row (task 46eddcb0).
    static TaskItem TaskIn(Guid workspace, int minutes)
    {
        var at = new DateTimeOffset(2026, 11, 28, 8, 0, 0, TimeSpan.Zero).AddMinutes(minutes);
        return new(Guid.NewGuid(), "t", workspace, Deskbar.Core.Tasks.TaskStatus.InProgress, "", Maybe<string>.None, at, at, at, [], []);
    }

    // Task 46eddcb0, Petre: "show recent workspaces at the top and let me select from them first". Given the tasks, the
    // entries open with a "Recent" caption and the workspaces of the newest tasks, newest first; the full list follows
    // unchanged, so those workspaces show twice.
    [Fact]
    public void Recent_workspaces_lead_the_entries_and_stay_in_their_places() => StaThread.Run(() =>
    {
        var harness = Harness.Build(grouped: true);
        var state = harness.Manager.State;
        Guid Id(string name) => state.Workspaces.Single(w => w.Name == name).Id;
        var tasks = new[] { TaskIn(Id("GEPHA"), 1), TaskIn(Id("Archive"), 2) };

        var entries = WorkspaceChipMenu.Entries(state, withNone: true, tasks: tasks);

        Assert.Equal(["Recent", "Archive", "GEPHA", "none", "GEPHA", "Team", "Sparrow", "Archive"], entries.Select(e => e.Name));
        Assert.Equal([true, true, true, false, false, false, false, false], entries.Select(e => e.Recent));
        // No tasks, or none in a workspace still on the menu: no Recent caption over an empty row.
        Assert.DoesNotContain(WorkspaceChipMenu.Entries(state, withNone: true, tasks: []), e => e.Recent);
        Assert.DoesNotContain(WorkspaceChipMenu.Entries(state, withNone: true, w => w.Name == "Sparrow", tasks), e => e.Recent);
    });

    // The Recent row is drawn above the "none" block and the groups, so the keyboard starts in it: on the current choice
    // when it is recent, else on the row's first chip, and type to jump finds the recent chip before its twin.
    [Fact]
    public void The_recent_row_is_drawn_first_and_the_keyboard_starts_in_it() => StaThread.Run(() =>
    {
        var harness = Harness.Build(grouped: true);
        var state = harness.Manager.State;
        Guid Id(string name) => state.Workspaces.Single(w => w.Name == name).Id;
        var tasks = new[] { TaskIn(Id("GEPHA"), 1), TaskIn(Id("Archive"), 2) };
        var picked = new List<Maybe<Guid>>();

        WithAnchor(anchor =>
        {
            var entries = WorkspaceChipMenu.Entries(state, withNone: true, tasks: tasks);
            var menu = WorkspaceChipMenu.Build(entries, Id("GEPHA"), picked.Add, anchor);
            var chips = WorkspaceChipMenu.Chips(menu);
            Assert.Equal(["Archive", "GEPHA", "none", "GEPHA", "Sparrow", "Archive"], chips.Select(NameOf));
            var caption = ((StackPanel)menu.Items[0]).Children.OfType<TextBlock>().Single();
            Assert.Equal(WorkspaceChipMenu.RecentCaption, caption.Text);
            // The current choice is outlined in both places; the keyboard starts on its recent twin.
            Assert.Same(chips[1], WorkspaceChipMenu.StartChip(menu).Value);
            Assert.True(IsCurrent(chips[1]) && IsCurrent(chips[3]));
            Assert.Same(chips[0], WorkspaceChipMenu.TypeAheadMatch(menu, "arc").Value);
            // A current choice that is not recent keeps the keyboard where it always was; none at all starts on the row.
            var sparrow = WorkspaceChipMenu.Build(entries, Id("Sparrow"), _ => { }, anchor);
            Assert.Equal("Sparrow", NameOf(WorkspaceChipMenu.StartChip(sparrow).Value));
            var free = WorkspaceChipMenu.Build(entries, Guid.NewGuid(), _ => { }, anchor);
            Assert.Same(WorkspaceChipMenu.Chips(free)[0], WorkspaceChipMenu.StartChip(free).Value);
            // A recent chip picks its workspace like any other.
            Click(chips[0]);
            Assert.Equal([Maybe.From(Id("Archive"))], picked);
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

    // Task f94524ce, Petre: "can't go to other grouped workspace, can't go to deskbar from here with the keyboard". The
    // arrows stopped at a group's edge. In a menu shaped like his (ungrouped workspaces, then groups under captions),
    // Up from a chip deep in the groups climbs a line at a time, across every caption, to the first block; Down from
    // the first block's last line lands in the first group; and the menu takes the arrow keys itself.
    [Fact]
    public void The_arrows_cross_group_captions() => StaThread.Run(() =>
    {
        var groups = Enumerable.Range(1, 4).Select(i => new Group(Guid.NewGuid(), $"Group {i}")).ToList();
        var names = new[] { "Messengers", "Meta", "Research", "HRIS", "Rustdesk", "OTHER", "contabo",
            "GPH", "core-api", "ecom", "Deskbar", "Deskbar site", "Personal", "dating", "Video", "iam.ge", "docs", "site" };
        var workspaces = names.Select((name, i) => new Workspace(Guid.NewGuid(), name, Guid.NewGuid())
        {
            // Seven ungrouped, then groups of three, two, three and three.
            GroupId = i < 7 ? null : i < 10 ? groups[0].Id : i < 12 ? groups[1].Id : i < 15 ? groups[2].Id : groups[3].Id,
        }).ToList();
        var state = AppState.Empty with { Workspaces = workspaces, Groups = groups };

        WithAnchor(anchor =>
        {
            var menu = WorkspaceChipMenu.Build(WorkspaceChipMenu.Entries(state, withNone: true), Maybe<Guid>.None, _ => { }, anchor);
            menu.IsOpen = true;
            menu.UpdateLayout();
            var chips = WorkspaceChipMenu.Chips(menu);
            Button Named(string name) => chips.Single(c => NameOf(c) == name);
            double TopOf(Button chip) => chip.TransformToAncestor(menu).TransformBounds(new Rect(chip.RenderSize)).Top;

            // Up from iam.ge, the last group, a line at a time until it reaches the first block's first line.
            var firstLine = TopOf(chips[0]);
            var walked = new List<string>();
            var at = Named("iam.ge");
            while (TopOf(at) > firstLine + 0.5 && walked.Count < chips.Count)
            {
                var up = WorkspaceChipMenu.ArrowTarget(menu, at, ChipStep.Up).Value;
                Assert.True(TopOf(up) < TopOf(at), $"Up from {NameOf(at)} went to {NameOf(up)}, not a line higher");
                walked.Add(NameOf(up));
                at = up;
            }
            Assert.Equal(firstLine, TopOf(at), 0.5);
            // On the way it stood in Group 2, the group holding Deskbar.
            Assert.Contains(walked, name => name.StartsWith("Deskbar"));

            // Down from the first block's last chip lands in the first group.
            Assert.Equal(groups[0].Id, workspaces.Single(w => w.Name == NameOf(WorkspaceChipMenu.ArrowTarget(menu, Named("contabo"), ChipStep.Down).Value)).GroupId);
            // Right from the last chip of a group goes on to the next group's first.
            Assert.Equal("Deskbar", NameOf(WorkspaceChipMenu.ArrowTarget(menu, Named("ecom"), ChipStep.Right).Value));

            // The menu takes an arrow key itself, so WPF's own handling cannot stop it at a block's edge.
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(Named("iam.ge")), 0, Key.Up)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            };
            Named("iam.ge").RaiseEvent(key);
            Assert.True(key.Handled);
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

    // Ruling (g), 2026-11-23: under a light Windows theme the system menu drew a near-white surface (#F3F3F3) with an
    // icon gutter down it, and the pastel chips with near-white names on it could barely be read. The menu draws its
    // own dark surface, the tasks window's, whatever the theme, and has no gutter: its template is a border round a
    // scroll viewer round the blocks, nothing else.
    [Fact]
    public void The_menu_draws_its_own_dark_surface_under_the_light_theme() => StaThread.Run(() =>
    {
        var entries = WorkspaceChipMenu.Entries(Harness.Build(grouped: true).Manager.State, withNone: true);
        WithAnchor(anchor =>
        {
#pragma warning disable WPF0001 // ThemeMode is marked experimental; used only to put the anchor under the light theme
            anchor.ThemeMode = ThemeMode.Light;
#pragma warning restore WPF0001
            var menu = WorkspaceChipMenu.Build(entries, Maybe<Guid>.None, _ => { }, anchor);
            menu.IsOpen = true;
            menu.UpdateLayout();

            var surface = Descendants(menu).OfType<Border>().First();
            Assert.Equal(((SolidColorBrush)WorkspaceChipMenu.Surface).Color, ((SolidColorBrush)surface.Background).Color);
            Assert.Equal(((SolidColorBrush)WorkspaceChipMenu.Surface).Color, ((SolidColorBrush)menu.Background).Color);
            // No gutter: nothing between the surface and the blocks but the scroll viewer and its own parts.
            var scroller = Descendants(surface).OfType<ScrollViewer>().First();
            Assert.Same(scroller, VisualTreeHelper.GetChild(surface, 0));
            Assert.Equal(ScrollBarVisibility.Disabled, scroller.HorizontalScrollBarVisibility);
            menu.IsOpen = false;
            return 0;
        });
    });

    // Every palette colour's chip, as drawn (its fill at the chip's alpha over the menu's surface), keeps its name
    // readable: WCAG's 4.5:1 for normal text. The "none" chip too.
    [Fact]
    public void Every_palette_chip_is_readable_on_the_menu() => StaThread.Run(() =>
    {
        var entries = WorkspacePalette.Swatches
            .Select(s => new WorkspaceChipMenu.Entry(false, Guid.NewGuid(), s.Name, new SolidColorBrush((Color)ColorConverter.ConvertFromString(s.Hex))))
            .Prepend(new WorkspaceChipMenu.Entry(false, Maybe<Guid>.None, "none", null))
            .ToList();
        var menu = WorkspaceChipMenu.Build(entries, Maybe<Guid>.None, _ => { }, null);
        var surface = ((SolidColorBrush)WorkspaceChipMenu.Surface).Color;

        Assert.All(WorkspaceChipMenu.Chips(menu), chip =>
        {
            var fill = Over(((SolidColorBrush)chip.Background).Color, surface);
            var ink = ((SolidColorBrush)((TextBlock)chip.Content).Foreground).Color;
            var ratio = Contrast(ink, fill);
            Assert.True(ratio >= 4.5, $"{NameOf(chip)}: {ratio:0.00}:1");
        });
    });

    // A colour with alpha laid over an opaque one, as the screen shows it.
    static Color Over(Color top, Color under)
    {
        var a = top.A / 255.0;
        byte Mix(byte t, byte u) => (byte)Math.Round(a * t + (1 - a) * u);
        return Color.FromRgb(Mix(top.R, under.R), Mix(top.G, under.G), Mix(top.B, under.B));
    }

    // WCAG 2's contrast ratio.
    static double Contrast(Color one, Color two)
    {
        static double Channel(byte c) => c / 255.0 is var v && v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        static double Luminance(Color c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        var (a, b) = (Luminance(one), Luminance(two));
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    static IEnumerable<DependencyObject> Descendants(DependencyObject node) =>
        Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(node)).Select(i => VisualTreeHelper.GetChild(node, i))
            .SelectMany(child => new[] { child }.Concat(Descendants(child)));
}
