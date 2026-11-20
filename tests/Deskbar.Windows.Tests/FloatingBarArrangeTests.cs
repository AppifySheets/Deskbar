using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core.Arrange;

namespace Deskbar.Windows.Tests;

// The bar's arrange mode (FloatingBar.Arrange.cs; spec docs/superpowers/specs/2026-11-20-bar-edit-mode-design.md, "Turning
// it on and off" and "What changes on the bar while it is on"). Only the MODE is covered here: the grips, the dimmed
// icons, the Done strip, the presses that must do nothing, and the ways out; then the drag that arranges
// (FloatingBar.ArrangeDrag.cs): where the pointer aims, what the preview draws, the drop, the deferral and the cancel.
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

    // Review, Critical 1. The row's release handler and the icon's orphan rescue run even on a handled release, and read
    // flags the last click left behind. The swallowed press must not leave a click from before the mode alive: click a row,
    // enter the mode, click it again, and nothing switches.
    [Fact]
    public void A_click_before_the_mode_does_not_make_the_next_click_switch() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = harness.ShowBar();
        var row = RowFor(bar.Rows, "Sparrow");
        Press(row);
        Release(row);
        Assert.Single(harness.Desktops.Switched); // precondition: that click switched

        Assert.True(bar.Bar.SetArranging(true).IsSuccess);
        row = RowFor(bar.Rows, "Sparrow");
        Press(row);
        Release(row);

        Assert.Single(harness.Desktops.Switched);
    });

    // ...and an orphan release left by the last click (the post-switch case) does not turn every click in the mode into a
    // switch, or an icon click into a toggle of its window.
    [Fact]
    public void An_orphan_release_before_the_mode_does_not_leak_into_it() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = harness.ShowBar();
        OrphanRelease(RowFor(bar.Rows, "Sparrow")); // precondition for the bug: orphanRelease is now true
        Assert.Single(harness.Desktops.Switched);   // (the orphan rescue itself switched once, as designed)
        Assert.True(bar.Bar.SetArranging(true).IsSuccess);

        var row = RowFor(bar.Rows, "Sparrow");
        Press(row);
        Release(row);
        var icon = IconButtons(RowFor(bar.Rows, "Sparrow")).Single();
        Press(icon);
        Release(icon);

        Assert.Single(harness.Desktops.Switched);
    });

    // A press in the mode followed by a release after it (Esc, or `arrange off`, with the button held) is not an orphan
    // and switches nothing.
    [Fact]
    public void A_press_in_the_mode_and_a_release_after_it_switches_nothing() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = harness.ShowBar();
        Assert.True(bar.Bar.SetArranging(true).IsSuccess);
        var row = RowFor(bar.Rows, "Sparrow");
        Press(row);

        Assert.True(bar.Bar.SetArranging(false).IsSuccess);
        Release(RowFor(bar.Rows, "Sparrow"));

        Assert.Empty(harness.Desktops.Switched);
    });

    // Review, Important 2. A resize that is in progress when the mode starts is finished by its release, not left
    // captured and "moving".
    [Fact]
    public void A_resize_in_progress_is_finished_by_its_release_while_arranging() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = harness.ShowBar();
        bar.Bar.BeginResizeForTest();
        Assert.True(bar.Bar.IsResizingForTest);

        Assert.True(bar.Bar.SetArranging(true).IsSuccess);
        Release(RowFor(bar.Rows, "Sparrow"));

        Assert.False(bar.Bar.IsResizingForTest);
    });

    // Review, Important 3. Only a UI entry takes the foreground; the pipe's does not.
    [Fact]
    public void Only_the_menu_activates_the_bar() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = harness.ShowBar();
        var sparrow = harness.Manager.State.Workspaces.Single(w => w.Name == "Sparrow").Id;

        Assert.True(bar.Bar.SetArranging(true).IsSuccess);
        Assert.Equal(0, bar.Bar.ArrangeActivations);
        Assert.True(bar.Bar.SetArranging(false).IsSuccess);

        bar.Bar.WorkspaceMenu(sparrow, "Sparrow", folded: false).Items.OfType<MenuItem>()
            .Single(i => i.Header as string == "Arrange workspaces").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        Assert.Equal(1, bar.Bar.ArrangeActivations);
    });

    // --- dragging rows (FloatingBar.ArrangeDrag.cs) ---------------------------------------------------------------

    // The bar only measures WHERE in the row under the pointer it is; RowDrop.Plan decides what that means. A workspace
    // row: top quarter Before, bottom quarter After, the middle half Onto. A group header: top quarter Before, the rest
    // Onto. Below the last row: End, with no row. A dragged group header splits a row in halves instead, since it can
    // only go above or below.
    [Fact]
    public void The_zone_comes_from_the_row_under_the_pointer() => StaThread.Run(() =>
    {
        var harness = Harness.Build(grouped: true);
        using var bar = harness.ShowBar();
        Assert.True(bar.Bar.SetArranging(true).IsSuccess);
        bar.Bar.UpdateLayout();
        var gepha = new RowRef.Workspace(IdOf(harness, "GEPHA"));
        var archive = new RowRef.Workspace(IdOf(harness, "Archive"));
        var team = new RowRef.Group(harness.Manager.State.Groups.Single().Id);
        var archiveRow = AnyRow(bar.Rows, "Archive");
        var header = HeaderOf(bar.Rows, "TEAM");

        Assert.Equal((archive, DropZone.Before), bar.Bar.ArrangeTargetAt(gepha, At(bar.Bar, archiveRow, 0.1)));
        Assert.Equal((archive, DropZone.Onto), bar.Bar.ArrangeTargetAt(gepha, At(bar.Bar, archiveRow, 0.5)));
        Assert.Equal((archive, DropZone.After), bar.Bar.ArrangeTargetAt(gepha, At(bar.Bar, archiveRow, 0.9)));

        Assert.Equal((team, DropZone.Before), bar.Bar.ArrangeTargetAt(gepha, At(bar.Bar, header, 0.1)));
        Assert.Equal((team, DropZone.Onto), bar.Bar.ArrangeTargetAt(gepha, At(bar.Bar, header, 0.4)));
        Assert.Equal((team, DropZone.Onto), bar.Bar.ArrangeTargetAt(gepha, At(bar.Bar, header, 0.9)));

        var belowLast = new Point(At(bar.Bar, archiveRow, 0.5).X, BoundsOf(bar.Bar, archiveRow).Bottom + 30);
        Assert.Equal(((RowRef?)null, DropZone.End), bar.Bar.ArrangeTargetAt(gepha, belowLast));

        // A dragged header: halves.
        var gephaRow = AnyRow(bar.Rows, "GEPHA");
        Assert.Equal((gepha, DropZone.Before), bar.Bar.ArrangeTargetAt(team, At(bar.Bar, gephaRow, 0.4)));
        Assert.Equal((gepha, DropZone.After), bar.Bar.ArrangeTargetAt(team, At(bar.Bar, gephaRow, 0.6)));
    });

    // A folded group is a target only through its header, its one line: anywhere on it below the top quarter joins it.
    [Fact]
    public void A_folded_group_is_aimed_at_through_its_line() => StaThread.Run(() =>
    {
        var harness = Harness.Build(grouped: true);
        var team = harness.Manager.State.Groups.Single().Id;
        Assert.True(harness.Manager.SetGroupFolded(team, true).IsSuccess);
        using var bar = harness.ShowBar();
        Assert.True(bar.Bar.SetArranging(true).IsSuccess);
        bar.Bar.UpdateLayout();
        var gepha = new RowRef.Workspace(IdOf(harness, "GEPHA"));
        var line = Descendants(bar.Rows).OfType<Border>()
            .Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Team (folded)");

        Assert.Equal((new RowRef.Group(team), DropZone.Before), bar.Bar.ArrangeTargetAt(gepha, At(bar.Bar, line, 0.1)));
        Assert.Equal((new RowRef.Group(team), DropZone.Onto), bar.Bar.ArrangeTargetAt(gepha, At(bar.Bar, line, 0.6)));
    });

    // Review, Important (a). Joining a folded group puts the row before its first member, which a folded group does not
    // draw (only the member you are on is shown, Archive here). The preview must still show where the row goes: the
    // folded line is outlined, as a row being joined is, with no hint since no group is being made.
    [Fact]
    public void Joining_a_folded_group_outlines_its_line() => StaThread.Run(() =>
    {
        var harness = Harness.Build(grouped: true);
        var team = harness.Manager.State.Groups.Single().Id;
        Assert.True(harness.Manager.SetGroupFolded(team, true).IsSuccess);
        harness.Desktops.CurrentId = harness.Desktops.Desktops.Single(d => d.Name == "Archive").Id;
        using var bar = harness.ShowBar();
        Assert.True(bar.Bar.SetArranging(true).IsSuccess);
        bar.Bar.UpdateLayout();
        var gepha = IdOf(harness, "GEPHA");
        var line = Descendants(bar.Rows).OfType<Border>()
            .Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Team (folded)");
        Assert.DoesNotContain(Descendants(bar.Rows).OfType<Grid>(), g => System.Windows.Automation.AutomationProperties.GetName(g) == "Sparrow");

        Press(AnyRow(bar.Rows, "GEPHA"));
        bar.Bar.ArrangeMove(At(bar.Bar, line, 0.6));

        var painted = bar.Bar.ArrangePainting!;
        Assert.Equal(new DropShow.Line(new RowRef.Workspace(IdOf(harness, "Sparrow")), After: false, InsideGroup: true), painted.Show);
        Assert.Null(painted.Line);
        Assert.Null(painted.Hint);
        Assert.Equal(BoundsOf(bar.Bar, line), painted.Highlight);

        bar.Bar.ArrangeRelease(At(bar.Bar, line, 0.6));

        Assert.Equal([gepha, IdOf(harness, "Sparrow"), IdOf(harness, "Archive")], harness.Manager.State.MembersOf(team).Select(w => w.Id));
    });

    // Review, Important (b). The member of a folded group that is shown (the one you are on) dragged a few pixels and let
    // go over its own row: its own place, a no-op, never "the top of its group".
    [Fact]
    public void A_folded_groups_shown_member_over_its_own_row_changes_nothing() => StaThread.Run(() =>
    {
        var harness = Harness.Build(grouped: true);
        var team = harness.Manager.State.Groups.Single().Id;
        Assert.True(harness.Manager.SetGroupFolded(team, true).IsSuccess);
        harness.Desktops.CurrentId = harness.Desktops.Desktops.Single(d => d.Name == "Archive").Id;
        using var bar = harness.ShowBar();
        Assert.True(bar.Bar.SetArranging(true).IsSuccess);
        bar.Bar.UpdateLayout();
        var archive = new RowRef.Workspace(IdOf(harness, "Archive"));
        var order = harness.Manager.State.Workspaces.Select(w => w.Id).ToList();
        var own = AnyRow(bar.Rows, "Archive");

        Assert.Equal((archive, DropZone.Onto), bar.Bar.ArrangeTargetAt(archive, At(bar.Bar, own, 0.5)));
        Assert.IsType<DropAction.Nothing>(bar.Bar.ArrangePreviewAt(archive, At(bar.Bar, own, 0.5)).Action);

        Press(own);
        bar.Bar.ArrangeMove(At(bar.Bar, own, 0.9));
        bar.Bar.ArrangeRelease(At(bar.Bar, own, 0.9));

        Assert.Equal(order, harness.Manager.State.Workspaces.Select(w => w.Id).ToList());
    });

    // The controller's guard: a drop whose preview drew nothing is refused, so nothing changes that the user was not
    // shown. Made here by hiding the row the line would be drawn beside (Sparrow, the first member a join lands before).
    [Fact]
    public void A_drop_with_nothing_shown_is_refused() => StaThread.Run(() =>
    {
        var harness = Harness.Build(grouped: true);
        using var bar = harness.ShowBar();
        Assert.True(bar.Bar.SetArranging(true).IsSuccess);
        bar.Bar.UpdateLayout();
        var gepha = new RowRef.Workspace(IdOf(harness, "GEPHA"));
        var team = harness.Manager.State.Groups.Single().Id;
        var header = At(bar.Bar, HeaderOf(bar.Rows, "TEAM"), 0.6);
        var members = harness.Manager.State.MembersOf(team).Select(w => w.Id).ToList();
        Assert.IsType<DropAction.Place>(bar.Bar.ArrangePreviewAt(gepha, header).Action); // precondition: a real drop

        ((FrameworkElement)AnyRow(bar.Rows, "Sparrow").Parent).Visibility = Visibility.Collapsed;
        bar.Bar.UpdateLayout();

        Assert.IsType<DropAction.Place>(bar.Bar.ArrangePreviewAt(gepha, header).Action); // still planned...
        Assert.True(bar.Bar.ArrangeDropAt(gepha, header).IsSuccess);                       // ...but not applied

        Assert.Equal(members, harness.Manager.State.MembersOf(team).Select(w => w.Id));
    });

    // A join is drawn short, inside the group's bracket; a place among the top-level rows is drawn the bar's full width,
    // at the box's own edge when the row beside it is a whole group.
    [Fact]
    public void A_preview_line_inside_a_group_is_short() => StaThread.Run(() =>
    {
        var harness = Harness.Build(grouped: true);
        using var bar = harness.ShowBar();
        Assert.True(bar.Bar.SetArranging(true).IsSuccess);
        bar.Bar.UpdateLayout();
        var archive = IdOf(harness, "Archive");
        var team = harness.Manager.State.Groups.Single().Id;
        var rows = BoundsOf(bar.Bar, bar.Rows);

        Press(AnyRow(bar.Rows, "GEPHA"));
        bar.Bar.ArrangeMove(At(bar.Bar, AnyRow(bar.Rows, "Archive"), 0.9));

        Assert.True(bar.Bar.ArrangeDragging);
        var inside = bar.Bar.ArrangePainting!;
        Assert.Equal(new DropShow.Line(new RowRef.Workspace(archive), After: true, InsideGroup: true), inside.Show);
        Assert.Equal(2, inside.Line!.Value.Height, 3);
        Assert.True(inside.Line.Value.Left > rows.Left + 4, $"the join line starts inside the bracket: {inside.Line} in {rows}");
        Assert.True(inside.Line.Value.Width < rows.Width - 8, $"the join line is short: {inside.Line} in {rows}");

        // Below the last row: after the whole group, which is the last thing on the bar. Full width, on the box's edge.
        var archiveRow = BoundsOf(bar.Bar, AnyRow(bar.Rows, "Archive"));
        bar.Bar.ArrangeMove(new Point(archiveRow.Left + archiveRow.Width / 2, archiveRow.Bottom + 30));

        var after = bar.Bar.ArrangePainting!;
        Assert.Equal(new DropShow.Line(new RowRef.Group(team), After: true, InsideGroup: false), after.Show);
        Assert.Equal(rows.Left, after.Line!.Value.Left, 1);
        Assert.Equal(rows.Width, after.Line.Value.Width, 1);
        Assert.InRange(after.Line.Value.Top + 1, archiveRow.Bottom, archiveRow.Bottom + 3); // the box ends just under its last row

        bar.Bar.CancelArrangeDrag("the test is over"); // leave nothing captured
    });

    // Onto an ungrouped row: the row is outlined, and the hint under it says what a release will do.
    [Fact]
    public void A_group_with_target_is_highlighted_with_its_hint() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = harness.ShowBar();
        Assert.True(bar.Bar.SetArranging(true).IsSuccess);
        bar.Bar.UpdateLayout();
        var sparrow = new RowRef.Workspace(IdOf(harness, "Sparrow"));
        var gepha = new RowRef.Workspace(IdOf(harness, "GEPHA"));
        var gephaRow = RowFor(bar.Rows, "GEPHA");

        var (action, show) = bar.Bar.ArrangePreviewAt(sparrow, At(bar.Bar, gephaRow, 0.5));
        Assert.IsType<DropAction.GroupWith>(action);
        Assert.Equal(new DropShow.Highlight(gepha, "Group with GEPHA…"), show);

        Press(RowFor(bar.Rows, "Sparrow"));
        bar.Bar.ArrangeMove(At(bar.Bar, gephaRow, 0.5));

        var painted = bar.Bar.ArrangePainting!;
        Assert.Equal("Group with GEPHA…", painted.Hint);
        Assert.True(painted.Highlight!.Value.Contains(BoundsOf(bar.Bar, gephaRow)), $"the outline is the row: {painted.Highlight} vs {BoundsOf(bar.Bar, gephaRow)}");
        Assert.Null(painted.Line);
        // ...drawn on an adorner over the bar, never in the rows' own layout (the bar is SizeToContent).
        var root = RootOf(bar.Bar);
        Assert.Single(AdornerLayer.GetAdornerLayer(root)!.GetAdorners(root)!);

        bar.Bar.CancelArrangeDrag("the test is over"); // leave nothing captured

        Assert.Null(AdornerLayer.GetAdornerLayer(root)!.GetAdorners(root));
    });

    // The new-group drop asks for the name, prefilled with the target's; Cancel changes nothing, OK makes the group of
    // the target then the dragged row, in one go.
    [Fact]
    public void Dropping_onto_an_ungrouped_row_asks_for_a_name_prefilled_with_its_name() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = harness.ShowBar();
        Assert.True(bar.Bar.SetArranging(true).IsSuccess);
        bar.Bar.UpdateLayout();
        var sparrow = IdOf(harness, "Sparrow");
        var gepha = IdOf(harness, "GEPHA");
        var asked = new List<string>();

        bar.Bar.AskGroupName = name => { asked.Add(name); return Maybe<string>.None; };
        Assert.True(bar.Bar.ArrangeDropAt(new RowRef.Workspace(sparrow), At(bar.Bar, RowFor(bar.Rows, "GEPHA"), 0.5)).IsSuccess);
        Assert.Equal(["GEPHA"], asked);
        Assert.Empty(harness.Manager.State.Groups);

        bar.Bar.UpdateLayout();
        bar.Bar.AskGroupName = name => { asked.Add(name); return Maybe.From("Pair"); };
        Assert.True(bar.Bar.ArrangeDropAt(new RowRef.Workspace(sparrow), At(bar.Bar, RowFor(bar.Rows, "GEPHA"), 0.5)).IsSuccess);

        Assert.Equal(["GEPHA", "GEPHA"], asked);
        var group = Assert.Single(harness.Manager.State.Groups);
        Assert.Equal("Pair", group.Name);
        Assert.Equal([gepha, sparrow], harness.Manager.State.MembersOf(group.Id).Select(w => w.Id));
    });

    // Review focus 3: a rebuild throws every row away, including the one being dragged and the ones the preview is
    // measured against, so one that arrives mid-drag waits, and is served as soon as the drag ends, even by a drop that
    // changes nothing.
    [Fact]
    public void A_rebuild_during_an_arrange_drag_is_deferred_until_the_drop() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = harness.ShowBar();
        Assert.True(bar.Bar.SetArranging(true).IsSuccess);
        bar.Bar.UpdateLayout();
        var sparrowRow = RowFor(bar.Rows, "Sparrow");

        Press(sparrowRow);
        bar.Bar.ArrangeMove(At(bar.Bar, RowFor(bar.Rows, "GEPHA"), 0.1));
        Assert.True(bar.Bar.ArrangeDragging);
        var before = bar.Rows.Children.Cast<UIElement>().ToList();

        Assert.True(harness.Manager.RenameWorkspace(IdOf(harness, "GEPHA"), "Renamed").IsSuccess);

        Assert.Equal(before, bar.Rows.Children.Cast<UIElement>().ToList());
        Assert.DoesNotContain("Renamed", Texts(bar.Rows));

        // Released on its own place: a drop that changes nothing, so only the deferral can bring the rebuild.
        bar.Bar.ArrangeRelease(At(bar.Bar, sparrowRow, 0.5));
        bar.Bar.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

        Assert.False(bar.Bar.ArrangeDragging);
        Assert.Contains("Renamed", Texts(bar.Rows));
    });

    // Esc in the middle of a drag drops nothing and asks nothing. It ends the DRAG, not the mode: a second Esc does that.
    [Fact]
    public void Escape_during_a_drag_cancels_it_and_changes_nothing() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = harness.ShowBar();
        Assert.True(bar.Bar.SetArranging(true).IsSuccess);
        bar.Bar.UpdateLayout();
        var asked = false;
        bar.Bar.AskGroupName = _ => { asked = true; return Maybe.From("Pair"); };
        var order = harness.Manager.State.Workspaces.Select(w => w.Id).ToList();
        var gephaRow = RowFor(bar.Rows, "GEPHA");

        Press(RowFor(bar.Rows, "Sparrow"));
        bar.Bar.ArrangeMove(At(bar.Bar, gephaRow, 0.5));
        Assert.IsType<DropShow.Highlight>(bar.Bar.ArrangePainting!.Show);

        var key = EscapeKey(bar.Bar);
        bar.Bar.RaiseEvent(key);

        Assert.True(key.Handled);
        Assert.False(bar.Bar.ArrangeDragging);
        Assert.Null(bar.Bar.ArrangePainting);
        Assert.True(bar.Bar.Arranging);

        // The release that follows the Esc belongs to the cancelled drag and drops nothing.
        bar.Bar.ArrangeRelease(At(bar.Bar, gephaRow, 0.5));

        Assert.False(asked);
        Assert.Empty(harness.Manager.State.Groups);
        Assert.Equal(order, harness.Manager.State.Workspaces.Select(w => w.Id).ToList());
    });

    // --- helpers ------------------------------------------------------------------------------------------------

    static Guid IdOf(Harness harness, string name) => harness.Manager.State.Workspaces.Single(w => w.Name == name).Id;

    static FrameworkElement RootOf(FloatingBar bar) => (FrameworkElement)bar.FindName("Root")!;

    // Where an element is drawn, in the coordinates of the bar's Root, which is what the arrange hit test takes.
    static Rect BoundsOf(FloatingBar bar, FrameworkElement element) =>
        element.TransformToAncestor(RootOf(bar)).TransformBounds(new Rect(element.RenderSize));

    // A point at `fraction` of the element's height, across its middle.
    static Point At(FloatingBar bar, FrameworkElement element, double fraction) =>
        BoundsOf(bar, element) is var b ? new Point(b.Left + b.Width / 2, b.Top + b.Height * fraction) : default;

    // Any workspace row by its name, a group member included (RowFor only finds the top-level ones).
    static Grid AnyRow(Panel rows, string label) =>
        Descendants(rows).OfType<Grid>().Single(g => System.Windows.Automation.AutomationProperties.GetName(g) == label);

    // A group's header: the Grid its capitalised name sits in.
    static FrameworkElement HeaderOf(Panel rows, string upper)
    {
        DependencyObject node = Descendants(rows).OfType<TextBlock>().Single(t => t.Text == upper);
        while (node is not Grid) node = LogicalTreeHelper.GetParent(node);
        return (Grid)node;
    }

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

    // Both halves of a release, tunnelling preview first (where the bar's window-level handler runs) then the bubbling
    // MouseUp (where the row's and the icon's handledEventsToo handlers run), as the input manager delivers them.
    static void Release(UIElement target)
    {
        target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent });
        target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseUpEvent });
    }

    // A release with no press before it, which the bar marks an orphan (#48).
    static void OrphanRelease(UIElement target) => Release(target);

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
