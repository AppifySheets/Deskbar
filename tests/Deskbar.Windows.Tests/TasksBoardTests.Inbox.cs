using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core.Tasks;

namespace Deskbar.Windows.Tests;

// The tasks panel's Inbox view (Deskbar task 740a2231, slice 1): a fourth view beside List, Matrix and Stats, three tabs
// over the List's own lines, a task opened as a page. Built on the same rig as the rest of TasksBoardTests.
public partial class TasksBoardTests
{
    // Every kind of line the Inbox sorts: a question, a Done task, Claude working and idle, a note.
    static (Guid Asking, Guid Done, Guid Working, Guid Idle, Guid Note) InboxTasks(Board t)
    {
        var note = t.Tasks.AddNote("Book the dentist", Maybe<Guid>.None).Value.Id;
        var working = t.Tasks.Create("Review the diff", t.Gepha, Session("s1")).Value.Id;
        var idle = t.Tasks.Create("Read the extension", t.Gepha, Session("s2")).Value.Id;
        var asking = t.Tasks.Create("Docked bar jumps", t.Gepha, Session("s3")).Value.Id;
        var done = t.Tasks.Create("Lighter terminal", t.Sparrow, Session("s4")).Value.Id;
        t.Tasks.Ask(asking, "Does the bar stay where you put it?");
        t.Tasks.MarkDone(done);
        return (asking, done, working, idle, note);
    }

    // The switch's Inbox, as a click on it calls it.
    static void ChooseInbox(Board t) => t.Window.Actions!.ShowInbox!(true);

    [Fact]
    public void A_panel_state_without_the_key_keeps_todays_view() => StaThread.Run(() =>
    {
        using var t = Build();
        InboxTasks(t);

        t.Controller.Show();

        Assert.Null(t.Window.InboxShown);
        Assert.Contains("Waiting on you", t.Window.Headers);
    });

    [Fact]
    public void The_inbox_is_remembered_once_chosen_and_left_by_list_or_matrix() => StaThread.Run(() =>
    {
        using var t = Build();
        InboxTasks(t);
        t.Controller.Show();

        ChooseInbox(t);
        Assert.NotNull(t.Window.InboxShown);
        Assert.True(t.Harness.Manager.State.TasksPanel!.Inbox);
        // Stats goes over it without forgetting it; Inbox leaves Stats again.
        t.Window.Actions!.ShowStats!();
        Assert.Null(t.Window.InboxShown);
        Assert.True(t.Harness.Manager.State.TasksPanel!.Inbox);
        ChooseInbox(t);
        Assert.NotNull(t.Window.InboxShown);

        t.Window.Actions!.ShowMatrix(false);
        Assert.Null(t.Window.InboxShown);
        Assert.False(t.Harness.Manager.State.TasksPanel!.Inbox);

        ChooseInbox(t);
        t.Window.Actions!.ShowMatrix(true);
        Assert.Null(t.Window.InboxShown);
        Assert.True(t.Window.MatrixShown);
    });

    // Criteria 2 and 3: the three tabs together hold exactly the List's lines, each once, and each count is its lines.
    [Fact]
    public void The_three_tabs_together_hold_the_lists_lines() => StaThread.Run(() =>
    {
        using var t = Build(workingOf: task => task.Title == "Review the diff" ? 1 : 0);
        var k = InboxTasks(t);
        t.Controller.Show();
        var listKeys = t.Window.Keys.ToArray();

        ChooseInbox(t);
        var inbox = t.Window.InboxShown!;

        Assert.True(inbox.Tabbed);
        Assert.Equal(listKeys.Order(), inbox.Lines.Select(l => l.Id).Order());
        Assert.Equal([k.Asking, k.Done], inbox.In(InboxTab.NeedsYou).Select(l => l.Id));
        Assert.Equal([k.Working, k.Idle], inbox.In(InboxTab.Claude).Select(l => l.Id));
        Assert.Equal([k.Note], inbox.In(InboxTab.Notes).Select(l => l.Id));
        Assert.Equal([2, 2, 1], new[] { InboxTab.NeedsYou, InboxTab.Claude, InboxTab.Notes }.Select(inbox.Count));
        // The Inbox draws from the List's rows: the view keeps its groups, and the Matrix setting does not take over.
        Assert.False(t.Window.MatrixShown);
    });

    // A session stopped on the usage limit: Deskbar's own status line says so, and the line moves to the Claude tab's
    // last part.
    [Fact]
    public void A_task_stopped_on_the_usage_limit_is_under_stopped() => StaThread.Run(() =>
    {
        using var t = Build(workingOf: _ => 0);
        var limited = t.Tasks.Create("Store icons", t.Gepha, Session("s1")).Value.Id;
        t.Tasks.SetStatusLine(limited, ClaudeSessions.LimitLine + "Thu 06:00");
        t.Controller.Show();
        ChooseInbox(t);

        var line = Assert.Single(t.Window.InboxShown!.In(InboxTab.Claude));
        Assert.Equal(InboxSection.UsageLimit, line.Section);
        Assert.Equal(InboxMark.UsageLimit, line.Mark);
    });

    // Under a search the tabs do not apply: one list of the matches.
    [Fact]
    public void A_search_shows_the_matches_without_tabs() => StaThread.Run(() =>
    {
        using var t = Build();
        var k = InboxTasks(t);
        t.Controller.Show();
        ChooseInbox(t);

        t.Controller.Search("dentist");

        Assert.False(t.Window.InboxShown!.Tabbed);
        Assert.Equal([k.Note], t.Window.InboxShown!.Lines.Select(l => l.Id));
        Assert.Equal([k.Note], DrawnInboxLines(t));
        // The three tab buttons are dimmed while the tabs do not apply.
        Assert.True(ButtonNamed(t.Window.InboxHeaderShown!, "Needs you").Opacity < 1);

        // By id: the first eight characters find the task wherever it is.
        t.Window.ClearSearch();
        t.Controller.Search(k.Done.ToString()[..8]);
        Assert.False(t.Window.InboxShown!.Tabbed);
        Assert.Equal([k.Done], DrawnInboxLines(t));
    });

    // The ids of the Inbox lines drawn now, in order.
    static IReadOnlyList<Guid> DrawnInboxLines(Board t) =>
        Inside(t.Window).OfType<Button>().Where(b => b.Tag is Guid).Select(b => (Guid)b.Tag).ToArray();

    // Criterion 1: the switch is Inbox, List, Matrix (from 780 DIP) and Stats, in the footer while the Inbox shows; List,
    // Matrix and Stats look as before, the toolbar's title line and chip rows back in place.
    [Fact]
    public void The_switch_shows_inbox_list_matrix_and_stats_and_the_old_views_come_back_as_they_were() => StaThread.Run(() =>
    {
        using var t = Build();
        InboxTasks(t);
        t.Controller.Show();
        var headers = t.Window.Headers.ToArray();
        var keys = t.Window.Keys.ToArray();
        var chips = t.Window.FilterLabels.ToArray();
        Assert.True(t.Window.ToolbarTitleShown);
        Assert.True(HasButtonNamed(t.Window, "Inbox"));

        ChooseInbox(t);
        var footer = t.Window.InboxFooterShown!;
        Assert.True(HasButtonNamed(footer, "Inbox") && HasButtonNamed(footer, "List") && HasButtonNamed(footer, "Matrix") && HasButtonNamed(footer, "Stats"));
        Assert.True(HasButtonNamed(footer, "Manager"));
        Assert.False(t.Window.ToolbarTitleShown);
        Assert.True(t.Window.FiltersInSheet);

        Click(ButtonNamed(footer, "List"));
        Assert.Null(t.Window.InboxShown);
        Assert.Null(t.Window.InboxFooterShown);
        Assert.True(t.Window.ToolbarTitleShown);
        Assert.False(t.Window.FiltersInSheet);
        Assert.Equal(headers, t.Window.Headers);
        Assert.Equal(keys, t.Window.Keys);
        Assert.Equal(chips, t.Window.FilterLabels);
    });

    [Fact]
    public void The_footer_offers_the_matrix_only_from_780() => StaThread.Run(() =>
    {
        using var t = Build(width: 460);
        InboxTasks(t);
        t.Controller.Show();
        ChooseInbox(t);

        Assert.False(HasButtonNamed(t.Window.InboxFooterShown!, "Matrix"));
        Assert.True(HasButtonNamed(t.Window.InboxFooterShown!, "Stats"));
    });

    // Each tab button says its count, and a click on it shows its lines.
    [Fact]
    public void Each_tab_button_counts_its_lines_and_shows_them() => StaThread.Run(() =>
    {
        using var t = Build(workingOf: task => task.Title == "Review the diff" ? 1 : 0);
        var k = InboxTasks(t);
        t.Controller.Show();
        ChooseInbox(t);

        Assert.Equal("Needs you: 2", ButtonNamed(t.Window.InboxHeaderShown!, "Needs you").ToolTip);
        Assert.Equal("Claude: 2", ButtonNamed(t.Window.InboxHeaderShown!, "Claude").ToolTip);
        Assert.Equal("Notes: 1", ButtonNamed(t.Window.InboxHeaderShown!, "Notes").ToolTip);
        Assert.Equal([k.Asking, k.Done], DrawnInboxLines(t));
        Assert.Equal(["Questions", "Finished"], t.Window.Headers);

        Click(ButtonNamed(t.Window.InboxHeaderShown!, "Claude"));
        Assert.Equal([k.Working, k.Idle], DrawnInboxLines(t));
        Assert.Equal(["Working now", "Idle"], t.Window.Headers);

        Click(ButtonNamed(t.Window.InboxHeaderShown!, "Notes"));
        Assert.Equal([k.Note], DrawnInboxLines(t));
    });

    // Criterion 4: the line's menu is the List's, every item, from "⋯" and from a right-click.
    [Fact]
    public void The_dots_and_a_right_click_open_the_lists_whole_menu() => StaThread.Run(() =>
    {
        using var t = Build(startClaude: _ => { });
        var k = InboxTasks(t);
        t.Controller.Show();
        var listMenu = MenuWords(t.Window.OpenMenuFor(k.Note));
        t.Window.OpenedMenu!.IsOpen = false;
        ChooseInbox(t);
        t.Window.ChooseInboxTab(InboxTab.Notes);

        Click(ButtonNamed(LineOf(t.Window, k.Note), "More"));
        var menu = t.Window.OpenedMenu!;
        Assert.Equal(listMenu, MenuWords(menu));
        Assert.Same(ButtonNamed(LineOf(t.Window, k.Note), "More"), menu.PlacementTarget);
        menu.IsOpen = false;

        LineOf(t.Window, k.Note).RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent });
        Assert.Equal(listMenu, MenuWords(t.Window.OpenedMenu!));
        t.Window.OpenedMenu!.IsOpen = false;
    });

    // Criterion 5: a note's box closes it with six seconds of Undo; the Close button on a task Claude still works on offers
    // "Stop Claude and close" first.
    [Fact]
    public void A_notes_box_closes_it_with_undo_and_claudes_close_asks_first() => StaThread.Run(() =>
    {
        using var t = Build(workingOf: _ => 1);
        var k = InboxTasks(t);
        t.Controller.Show();
        ChooseInbox(t);
        t.Window.ChooseInboxTab(InboxTab.Notes);

        Click(ButtonNamed(LineOf(t.Window, k.Note), "Close"));
        Assert.Equal(TaskStatus.Closed, t.Tasks.Board.Tasks.Single(x => x.Id == k.Note).Status);
        Assert.True(t.Window.IsClosing(k.Note));
        Click(ButtonNamed(LineOf(t.Window, k.Note), "Undo"));
        Assert.NotEqual(TaskStatus.Closed, t.Tasks.Board.Tasks.Single(x => x.Id == k.Note).Status);

        Click(ButtonNamed(LineOf(t.Window, k.Note), TasksWindow.CloseTaskText));
        Assert.True(t.Window.IsClosing(k.Note));
        t.Timers.FireAll();
        Assert.DoesNotContain(k.Note, DrawnInboxLines(t));

        t.Window.ChooseInboxTab(InboxTab.Claude);
        Click(ButtonNamed(LineOf(t.Window, k.Working), TasksWindow.CloseTaskText));
        Assert.Contains(TaskViews.StopClaudeAndClose, MenuWords(t.Window.OpenedMenu!));
        Assert.NotEqual(TaskStatus.Closed, t.Tasks.Board.Tasks.Single(x => x.Id == k.Working).Status);
        t.Window.OpenedMenu!.IsOpen = false;
    });

    // Criterion 6: the chip opens the move menu, under the chip.
    [Fact]
    public void The_chip_opens_the_move_menu() => StaThread.Run(() =>
    {
        using var t = Build();
        var k = InboxTasks(t);
        t.Controller.Show();
        ChooseInbox(t);

        var chip = ButtonNamed(LineOf(t.Window, k.Asking), TasksWindow.PlaceChipName);
        Click(chip);
        Assert.Same(chip, t.Window.OpenedMenu!.PlacementTarget);
        t.Window.OpenedMenu!.IsOpen = false;
        // The pick itself is the List's own (OpenMoveMenu, MoveTo): moved the same way the List's chip moves it.
        t.Window.Actions!.MoveTo(k.Asking, t.Sparrow);
        Assert.Equal(t.Sparrow, t.Tasks.Board.Tasks.Single(x => x.Id == k.Asking).WorkspaceId.Value);
    });

    // Criterion 7, its list half: a click picks the task and opens its page; a double-click goes to its workspace.
    [Fact]
    public void A_click_opens_the_tasks_page_and_a_double_click_goes_there() => StaThread.Run(() =>
    {
        var selection = new TaskSelection();
        using var t = Build(selection: selection);
        var k = InboxTasks(t);
        t.Controller.Show();
        ChooseInbox(t);

        Click(LineOf(t.Window, k.Asking));
        Assert.Equal(k.Asking, t.Window.InboxPageShown);
        Assert.Equal(k.Asking, selection.Current.Value);

        t.Window.Actions!.ToggleThread(k.Asking);
        PressAndClick(LineOf(t.Window, k.Done), 2);
        Assert.Contains(k.Done, t.WentTo);
    });

    // Criterion 9: the Filter sheet holds every control of the chip rows, and they narrow the Inbox; its button counts them.
    [Fact]
    public void The_filter_sheet_holds_the_chip_rows_and_they_narrow_the_inbox() => StaThread.Run(() =>
    {
        using var t = Build();
        var k = InboxTasks(t);
        t.Controller.Show();
        var chips = t.Window.FilterLabels.ToArray();
        ChooseInbox(t);
        Assert.False(t.Window.InboxFilterSheetOpen);

        Click(ButtonNamed(t.Window.InboxHeaderShown!, "Filter"));
        Assert.True(t.Window.InboxFilterSheetOpen);
        Assert.True(t.Window.FiltersInSheet);
        Assert.Equal(chips, t.Window.FilterLabels);
        foreach (var name in new[] { "Filter: Claude", "Subtasks", "Current workspace", "Workspace filter", "Assignee filter" })
            Assert.True(HasButtonNamed(t.Window.InboxHeaderShown!, name), name);

        Click(ButtonNamed(t.Window.InboxHeaderShown!, $"Filter: {chips.Single(c => c.StartsWith("Done"))}"));
        Assert.Equal([k.Done], t.Window.InboxShown!.Lines.Select(l => l.Id));
        Assert.True(HasButtonNamed(t.Window.InboxHeaderShown!, "Filter"));
        Assert.Contains(Inside(ButtonNamed(t.Window.InboxHeaderShown!, "Filter")).OfType<TextBlock>(), b => b.Text == "Filter · 1");
        // Still open: the sheet folds only from its button.
        Assert.True(t.Window.InboxFilterSheetOpen);
        Click(ButtonNamed(t.Window.InboxHeaderShown!, "Filter"));
        Assert.False(t.Window.InboxFilterSheetOpen);
    });

    // "This workspace | Everywhere" is the Current workspace checkbox, same memory.
    [Fact]
    public void This_workspace_and_everywhere_are_the_current_workspace_switch() => StaThread.Run(() =>
    {
        using var t = Build();
        InboxTasks(t);
        t.Controller.Show();
        ChooseInbox(t);

        Click(ButtonNamed(t.Window.InboxHeaderShown!, "This workspace"));
        Assert.True(t.Window.CurrentWorkspaceOn);
        Assert.True(t.Harness.Manager.State.TasksPanel!.CurrentWorkspace);
        Click(ButtonNamed(t.Window.InboxHeaderShown!, "Everywhere"));
        Assert.False(t.Window.CurrentWorkspaceOn);
    });

    // Criterion 12: Snooze from a line hides the task; Snoozed in Filter lists it, and it can be woken.
    [Fact]
    public void Snooze_from_a_line_hides_it_and_snoozed_lists_and_wakes_it() => StaThread.Run(() =>
    {
        using var t = Build();
        var k = InboxTasks(t);
        t.Controller.Show();
        ChooseInbox(t);
        t.Window.ChooseInboxTab(InboxTab.Notes);

        Click(ButtonNamed(LineOf(t.Window, k.Note), "Snooze"));
        var first = t.Window.OpenedMenu!.Items.OfType<MenuItem>().First(i => i.IsEnabled);
        first.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        t.Window.OpenedMenu!.IsOpen = false;
        Assert.DoesNotContain(k.Note, t.Window.InboxShown!.Lines.Select(l => l.Id));

        t.Controller.Filter(TaskFilter.Snoozed);
        Assert.False(t.Window.InboxShown!.Tabbed);
        Assert.Equal([k.Note], DrawnInboxLines(t));
        Assert.Equal(InboxMark.Snoozed, t.Window.InboxShown!.Lines.Single().Mark);
        t.Window.Actions!.Wake!(k.Note);
        t.Controller.Filter(TaskFilter.Open);
        Assert.Contains(k.Note, t.Window.InboxShown!.Lines.Select(l => l.Id));
    });

    // Criterion 13: every state mark has a shape of its own, so the screen in grey still tells them apart.
    [Fact]
    public void The_state_marks_differ_by_shape()
    {
        var shapes = Enum.GetValues<InboxMark>().Select(InboxMarks.ShapeOf).ToArray();
        Assert.Equal(shapes.Length, shapes.Distinct().Count());
    }

    // The drag is the List's: in Notes a line dragged onto another quadrant's section takes that quadrant's flags.
    [Fact]
    public void A_drag_in_notes_moves_a_line_between_quadrants() => StaThread.Run(() =>
    {
        using var t = Build();
        var plain = t.Tasks.AddNote("Someday thing", t.Gepha).Value.Id;
        var plan = t.Tasks.AddNote("A plan", t.Gepha).Value.Id;
        t.Tasks.SetFlag(plan, TaskFlag.Important, true);
        t.Window.DragMouse = ArrangeMouse.Held;
        t.Controller.Show();
        ChooseInbox(t);
        t.Window.ChooseInboxTab(InboxTab.Notes);
        t.Window.UpdateLayout();
        Assert.Equal([plan, plain], DrawnInboxLines(t));

        WindowDrag(t, plain, InLine(t, plan, 0.2));
        t.Window.UpdateLayout();

        Assert.True(t.Tasks.Board.Tasks.Single(x => x.Id == plain).Important);
        Assert.Equal([plain, plan], DrawnInboxLines(t));
    });

    // Edit in place (F2) on a selected Inbox line: the List's editor, in the line.
    [Fact]
    public void F2_edits_the_selected_line_in_place() => StaThread.Run(() =>
    {
        var selection = new TaskSelection();
        using var t = Build(selection: selection);
        var k = InboxTasks(t);
        t.Controller.Show();
        ChooseInbox(t);
        t.Window.ChooseInboxTab(InboxTab.Notes);
        selection.Select(k.Note);

        Assert.True(t.Window.Press(Key.F2, ModifierKeys.None));
        Assert.NotNull(t.Window.Editor);
        Assert.True(t.Window.Editor!.IsDescendantOf(LineOf(t.Window, k.Note)) || Inside(LineOf(t.Window, k.Note)).Contains(t.Window.Editor));
        t.Window.Editor!.Text = "Book the dentist for Monday";
        t.Window.SaveEditor();
        Assert.Equal("Book the dentist for Monday", t.Tasks.Board.Tasks.Single(x => x.Id == k.Note).Title);
    });

    // The time on a line: how long its state has lasted, in the panel's short form.
    [Fact]
    public void A_line_shows_how_long_its_state_has_lasted() => StaThread.Run(() =>
    {
        using var t = Build();
        var k = InboxTasks(t);
        t.Controller.Show();
        ChooseInbox(t);
        var since = t.Window.InboxShown!.Lines.Single(l => l.Id == k.Asking).Since;

        Assert.Equal(TaskAge.Format(Start.AddHours(1) - since), t.Window.InboxTimeOf(k.Asking));
    });
}
