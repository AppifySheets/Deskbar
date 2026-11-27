using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
        // The Inbox's one entry is the panel's foot; the toolbar is main's again, with no Inbox chip.
        Assert.True(HasButtonNamed(t.Window.InboxFooterShown!, "Inbox"));
        Assert.False(HasButtonNamed(t.Window.Toolbar, "Inbox"));

        ChooseInbox(t);
        var footer = t.Window.InboxFooterShown!;
        Assert.True(HasButtonNamed(footer, "Inbox") && HasButtonNamed(footer, "List") && HasButtonNamed(footer, "Matrix") && HasButtonNamed(footer, "Stats"));
        Assert.True(HasButtonNamed(footer, "Manager"));
        Assert.False(t.Window.ToolbarTitleShown);
        Assert.True(t.Window.FiltersInSheet);

        Click(ButtonNamed(footer, "List"));
        Assert.Null(t.Window.InboxShown);
        Assert.True(t.Window.ToolbarTitleShown);
        Assert.False(t.Window.FiltersInSheet);
        Assert.Equal(headers, t.Window.Headers);
        Assert.Equal(keys, t.Window.Keys);
        Assert.Equal(chips, t.Window.FilterLabels);
    });

    // The owner, of the Inbox's foot: "put this on the bottom of the list as well". The same row is the panel's foot in
    // List, Matrix and Stats, the view on show on its pill, and its clicks do what the Inbox's foot does.
    [Fact]
    public void The_inbox_foot_is_the_panels_foot_in_every_view() => StaThread.Run(() =>
    {
        using var t = Build();
        InboxTasks(t);
        t.Controller.Show();
        static string On(UIElement foot) =>
            AutomationProperties.GetName(Inside(foot).OfType<Button>().Single(b => AutomationProperties.GetItemStatus(b) == "checked"));

        Assert.True(t.Window.InboxFooterOnShow);
        Assert.Equal("List", On(t.Window.InboxFooterShown!));
        Assert.True(HasButtonNamed(t.Window.InboxFooterShown!, "Manager"));

        Click(ButtonNamed(t.Window.InboxFooterShown!, "Matrix"));
        Assert.True(t.Window.MatrixShown);
        Assert.Equal("Matrix", On(t.Window.InboxFooterShown!));

        Click(ButtonNamed(t.Window.InboxFooterShown!, "Stats"));
        Assert.True(t.Window.StatsShown);
        Assert.Equal("Stats", On(t.Window.InboxFooterShown!));

        Click(ButtonNamed(t.Window.InboxFooterShown!, "Inbox"));
        Assert.NotNull(t.Window.InboxShown);
        Assert.True(t.Harness.Manager.State.TasksPanel!.Inbox);
        Assert.Equal("Inbox", On(t.Window.InboxFooterShown!));

        Click(ButtonNamed(t.Window.InboxFooterShown!, "List"));
        Assert.Null(t.Window.InboxShown);
        Assert.False(t.Window.MatrixShown);
        Assert.False(t.Harness.Manager.State.TasksPanel!.Inbox);
        Assert.Equal("List", On(t.Window.InboxFooterShown!));
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

    // The All tab, the row's last card: its count is the other three's added up, and it draws every line of theirs, Needs
    // you's first, then Claude's, then Notes', under their own captions. F2 reaches any of them (Keys are its lines).
    [Fact]
    public void The_all_tab_counts_and_shows_every_line_of_the_other_three() => StaThread.Run(() =>
    {
        var selection = new TaskSelection();
        using var t = Build(selection: selection, workingOf: task => task.Title == "Review the diff" ? 1 : 0);
        var k = InboxTasks(t);
        t.Controller.Show();
        ChooseInbox(t);

        var all = ButtonNamed(t.Window.InboxHeaderShown!, "All");
        Assert.Equal("All: 5", all.ToolTip);
        // The last of the four cards in the row.
        var row = (Grid)all.Parent;
        Assert.Equal(4, row.ColumnDefinitions.Count);
        Assert.Equal(3, Grid.GetColumn(all));

        Click(all);
        Assert.Equal(InboxTab.All, t.Window.InboxTabShown);
        Assert.Equal([k.Asking, k.Done, k.Working, k.Idle, k.Note], DrawnInboxLines(t));
        Assert.Equal(["Questions", "Finished", "Working now", "Idle", "Someday"], t.Window.Headers);
        Assert.Equal([k.Asking, k.Done, k.Working, k.Idle, k.Note], t.Window.Keys);

        selection.Select(k.Note);
        Assert.True(t.Window.Press(Key.F2, ModifierKeys.None));
        Assert.NotNull(t.Window.Editor);
    });

    // On All every line is on show, so a task shown from outside opens its page without leaving All, and a note added
    // from the panel glows there too.
    [Fact]
    public void All_stays_on_show_for_a_task_shown_from_outside_or_just_added() => StaThread.Run(() =>
    {
        using var t = Build(workingOf: _ => 1);
        var k = InboxTasks(t);
        t.Controller.Show();
        ChooseInbox(t);
        t.Window.ChooseInboxTab(InboxTab.All);

        Click(ButtonNamed(t.Window.InboxHeaderShown!, "New"));
        Assert.True(t.Window.TypeNote("Pay the rent", Key.Enter));
        var added = t.Tasks.Board.Tasks.Single(x => x.Title == "Pay the rent").Id;
        Assert.Equal(InboxTab.All, t.Window.InboxTabShown);
        Assert.True(t.Window.InboxGlowing(added));

        t.Controller.ShowTask(k.Working);
        Assert.Equal(InboxTab.All, t.Window.InboxTabShown);
        Assert.Equal(k.Working, t.Window.InboxPageShown);
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

    // A finished task's tick is its tick box too: one click closes it, with Undo, and does not open its page.
    [Fact]
    public void A_finished_tasks_tick_closes_it_with_undo() => StaThread.Run(() =>
    {
        using var t = Build(workingOf: _ => 1);
        var k = InboxTasks(t);
        t.Controller.Show();
        ChooseInbox(t);

        Click(ButtonNamed(LineOf(t.Window, k.Done), "Close"));
        Assert.Equal(TaskStatus.Closed, t.Tasks.Board.Tasks.Single(x => x.Id == k.Done).Status);
        Assert.True(t.Window.IsClosing(k.Done));
        Assert.Null(t.Window.InboxPageContent);
        Click(ButtonNamed(LineOf(t.Window, k.Done), "Undo"));
        Assert.Equal(TaskStatus.Done, t.Tasks.Board.Tasks.Single(x => x.Id == k.Done).Status);
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
        foreach (var name in new[] { "Filter: Claude", "Subtasks", "Workspace filter", "Assignee filter" })
            Assert.True(HasButtonNamed(t.Window.InboxHeaderShown!, name), name);
        // "Current workspace" is the header's "This workspace | Everywhere", so the sheet leaves its box out.
        Assert.False(HasButtonNamed(t.Window.InboxHeaderShown!, "Current workspace"));

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

    // --- the page --------------------------------------------------------------------------------------------------------

    // Criterion 7: a click opens the page over the list; the arrow and Esc go back to the list where it was scrolled.
    [Fact]
    public void A_click_opens_the_page_and_the_arrow_and_esc_go_back_to_the_same_place() => StaThread.Run(() =>
    {
        using var t = Build();
        var many = Enumerable.Range(0, 30).Select(i => t.Tasks.AddNote($"Errand {i}", t.Gepha).Value.Id).ToArray();
        t.Controller.Show();
        ChooseInbox(t);
        t.Window.ChooseInboxTab(InboxTab.Notes);
        t.Window.UpdateLayout();
        t.Window.Rows.Parent.SetValue(FrameworkElement.HeightProperty, 300.0);
        t.Window.UpdateLayout();
        ((ScrollViewer)t.Window.Rows.Parent).ScrollToVerticalOffset(400);
        t.Window.UpdateLayout();
        var offset = t.Window.ScrollOffset;
        Assert.True(offset > 0);
        var id = many[10];

        Click(LineOf(t.Window, id));
        Assert.NotNull(t.Window.InboxPageContent);
        Assert.True(t.Window.ThreadShown(id));
        Click(ButtonNamed(t.Window.InboxPageContent!, TasksWindow.InboxPageBackText));
        Assert.Null(t.Window.InboxPageContent);
        Assert.False(t.Window.ThreadShown(id));
        Assert.Equal(offset, t.Window.ScrollOffset, 1);

        Click(LineOf(t.Window, id));
        Assert.NotNull(t.Window.InboxPageContent);
        Assert.True(t.Window.Press(Key.Escape, ModifierKeys.None));
        Assert.Null(t.Window.InboxPageContent);
        Assert.Equal(offset, t.Window.ScrollOffset, 1);
    });

    // Criterion 8: the page shows every part of the thread the List's open line shows, and a comment with a picture posts
    // from it.
    [Fact]
    public void The_page_shows_the_thread_and_posts_a_comment_with_a_picture() => StaThread.Run(() =>
    {
        var root = ScratchRoot();
        try
        {
            using var t = Build(stateDir: root, sessionsOf: task => task.Sessions.Select(s => new SessionView(s.Id, s.Joined, SessionState.Idle)).ToArray(),
                openSession: (_, _) => CSharpFunctionalExtensions.Result.Success());
            var k = InboxTasks(t);
            var sub = t.Tasks.AddNote("A subtask", t.Gepha).Value.Id;
            t.Tasks.SetParent(sub, k.Asking);
            t.Tasks.Comment(k.Asking, Maybe<string>.None, "First word");
            t.Tasks.Comment(k.Asking, "s3", "An answer");
            t.Tasks.Describe(k.Asking, "What to try, in **full**.");
            t.Controller.Show();
            ChooseInbox(t);

            Click(LineOf(t.Window, k.Asking));
            var page = t.Window.InboxPageContent!;
            Assert.Equal(["you", "Claude"], t.Window.CommentAuthors(k.Asking));
            Assert.True(HasButtonNamed(page, TasksWindow.OpenInWindowText));
            Assert.True(HasButtonNamed(page, TasksWindow.OpenClaudeText));
            Assert.Contains(Inside(page).OfType<Border>(), b => Equals(b.Tag, TasksWindow.QuestionBubbleTag));
            Assert.Contains(Inside(page).OfType<RichTextBox>(), b => Equals(b.Tag, "task-description"));
            Assert.Contains(Inside(page).OfType<Button>(), b => AutomationProperties.GetName(b) == $"Subtask {FloatingBar.FirstLine("A subtask")}");
            Assert.NotNull(t.Window.CommentBoxOf(k.Asking));

            Assert.True(t.Window.PasteImages(k.Asking, BitmapData()));
            Assert.True(t.Window.TypeComment(k.Asking, "See this", Key.Enter));
            var posted = t.Tasks.Board.Tasks.Single(x => x.Id == k.Asking).Comments.Last().Markdown;
            Assert.Equal("See this", TaskImages.Split(posted).Text);
            Assert.Single(TaskImages.Split(posted).Images);
            // Still on the page after the post.
            Assert.Equal(k.Asking, t.Window.InboxPageShown);
        }
        finally { System.IO.Directory.Delete(root, true); }
    });

    // The thread's row of buttons shows all of them on the page, at any width: no List line stands above it.
    [Fact]
    public void The_pages_thread_row_has_every_button_at_the_narrowest_width() => StaThread.Run(() =>
    {
        using var t = Build(width: TasksPanelLayout.MinWidth, sessionsOf: task => task.Sessions.Select(s => new SessionView(s.Id, s.Joined, SessionState.Idle)).ToArray());
        var k = InboxTasks(t);
        t.Controller.Show();
        ChooseInbox(t);
        t.Window.ChooseInboxTab(InboxTab.Claude);

        Click(LineOf(t.Window, k.Idle));
        var row = Inside(t.Window.InboxPageContent!).OfType<WrapPanel>().Single(p => Equals(p.Tag, TasksWindow.ThreadActionsTag));
        var names = row.Children.OfType<Button>().Select(AutomationProperties.GetName).ToArray();
        Assert.Contains(TasksWindow.OpenInWindowText, names);
        Assert.Contains(TasksWindow.OpenClaudeText, names);
        Assert.All(row.Children.OfType<Button>(), b => Assert.Equal(Visibility.Visible, b.Visibility));
    });

    // Task 57191c84 ("these should be buttons, as with the list"): on a page the thread's row is the Inbox list's icon
    // buttons, not the List look's words. Each keeps its words as its name and at the start of its tooltip, and draws
    // no text of its own.
    [Fact]
    public void The_pages_thread_row_is_icon_buttons_not_words() => StaThread.Run(() =>
    {
        using var t = Build(sessionsOf: task => task.Sessions.Select(s => new SessionView(s.Id, s.Joined, SessionState.Idle)).ToArray());
        var k = InboxTasks(t);
        t.Controller.Show();
        ChooseInbox(t);
        t.Window.ChooseInboxTab(InboxTab.Claude);

        Click(LineOf(t.Window, k.Idle));
        var row = Inside(t.Window.InboxPageContent!).OfType<WrapPanel>().Single(p => Equals(p.Tag, TasksWindow.ThreadActionsTag));
        var buttons = row.Children.OfType<Button>().ToArray();
        Assert.Contains(TasksWindow.OpenInWindowText, buttons.Select(AutomationProperties.GetName));
        Assert.Contains(TasksWindow.OpenClaudeText, buttons.Select(AutomationProperties.GetName));
        Assert.All(buttons, b =>
        {
            Assert.Empty(Inside(b).OfType<TextBlock>());
            Assert.StartsWith(AutomationProperties.GetName(b), Assert.IsType<string>(b.ToolTip));
            // A square, as on the list's line.
            var face = Assert.IsType<Border>(b.Content);
            Assert.Equal(face.Width, face.Height);
        });
    });

    // A finished task's page ends with Close task (six seconds of Undo, back on the list) and Not done yet.
    [Fact]
    public void A_finished_pages_close_task_closes_it_with_undo_on_the_list() => StaThread.Run(() =>
    {
        using var t = Build();
        var k = InboxTasks(t);
        t.Controller.Show();
        ChooseInbox(t);
        Click(LineOf(t.Window, k.Done));
        Assert.True(HasButtonNamed(t.Window.InboxPageContent!, TasksWindow.NotDoneYetText));

        Click(ButtonNamed(t.Window.InboxPageContent!, TasksWindow.CloseTaskPageText));

        Assert.Equal(TaskStatus.Closed, t.Tasks.Board.Tasks.Single(x => x.Id == k.Done).Status);
        Assert.Null(t.Window.InboxPageContent);
        Assert.True(t.Window.IsClosing(k.Done));
        Assert.True(HasButtonNamed(LineOf(t.Window, k.Done), "Undo"));
    });

    // Criterion 11, and Stats: a task shown from outside (a click on the bar, a Stats line) lands on its page, in its tab.
    [Fact]
    public void A_task_shown_from_the_bar_or_stats_lands_on_its_page_in_its_tab() => StaThread.Run(() =>
    {
        using var t = Build(workingOf: _ => 1);
        var k = InboxTasks(t);
        t.Controller.Show();
        ChooseInbox(t);
        Assert.Equal(InboxTab.NeedsYou, t.Window.InboxTabShown);

        t.Controller.ShowTask(k.Working);
        Assert.Equal(InboxTab.Claude, t.Window.InboxTabShown);
        Assert.Equal(k.Working, t.Window.InboxPageShown);
        Assert.NotNull(t.Window.InboxPageContent);

        t.Window.Actions!.ShowStats!();
        Assert.Null(t.Window.InboxShown);
        t.Controller.OpenFromStats(k.Note);
        Assert.NotNull(t.Window.InboxShown);
        Assert.Equal(InboxTab.Notes, t.Window.InboxTabShown);
        Assert.Equal(k.Note, t.Window.InboxPageShown);
    });

    // A terminal window coming to the front picks its task, and never switches the tab or the page.
    [Fact]
    public void A_terminal_in_front_never_switches_the_tab_or_the_page() => StaThread.Run(() =>
    {
        var selection = new TaskSelection();
        using var t = Build(selection: selection);
        var k = InboxTasks(t);
        t.Controller.Show();
        ChooseInbox(t);
        Click(LineOf(t.Window, k.Asking));

        selection.Select(k.Working);
        t.Controller.Reveal();

        Assert.Equal(InboxTab.NeedsYou, t.Window.InboxTabShown);
        Assert.Equal(k.Asking, t.Window.InboxPageShown);
        // F2 and Esc still act on the page's task.
        Assert.True(t.Window.Press(Key.F2, ModifierKeys.None));
        Assert.True(Inside(t.Window.InboxPageContent!).Contains(t.Window.Editor));
        Assert.True(t.Window.Press(Key.Escape, ModifierKeys.None));
        Assert.Null(t.Window.Editor);
        Assert.True(t.Window.Press(Key.Escape, ModifierKeys.None));
        Assert.Null(t.Window.InboxPageShown);
    });

    // A terminal window coming to the front with no page open (the owner: "let's highlight those inbox issues when
    // selecting claude session / terminal windows, as it's done with the list"): its task's line is lit and scrolled into
    // view, the tab that holds it coming on show first; a tab that holds it already (All too) stays.
    [Fact]
    public void A_terminal_in_front_lights_its_tasks_line_in_the_tab_that_holds_it() => StaThread.Run(() =>
    {
        var selection = new TaskSelection();
        using var t = Build(selection: selection, workingOf: _ => 1);
        var many = Enumerable.Range(0, 30).Select(i => t.Tasks.Create($"Session {i}", t.Gepha, Session($"m{i}")).Value.Id).ToArray();
        var note = t.Tasks.AddNote("Book the dentist", t.Gepha).Value.Id;
        t.Controller.Show();
        ChooseInbox(t);
        t.Window.UpdateLayout();
        var scroller = (ScrollViewer)t.Window.Rows.Parent;
        scroller.SetValue(FrameworkElement.HeightProperty, 300.0);
        t.Window.UpdateLayout();
        Assert.Equal(InboxTab.NeedsYou, t.Window.InboxTabShown);

        // The last line of the Claude tab, far below the fold.
        var claudes = t.Window.InboxShown!.In(InboxTab.Claude).Select(l => l.Id).ToArray();
        Assert.Equal(many.Order(), claudes.Order());
        var id = claudes[^1];
        selection.Select(id);
        t.Controller.Reveal();
        t.Window.UpdateLayout();

        Assert.Equal(InboxTab.Claude, t.Window.InboxTabShown);
        Assert.Null(t.Window.InboxPageShown);
        var line = t.Window.InboxLineButton(id)!;
        Assert.Same(InboxLook.Raised, line.Background);
        var top = line.TranslatePoint(new Point(0, 0), scroller).Y;
        Assert.InRange(top, -0.5, scroller.ViewportHeight - line.ActualHeight + 0.5);

        // A tab that holds the line already stays: Claude for another session, All for the note.
        selection.Select(claudes[0]);
        t.Controller.Reveal();
        Assert.Equal(InboxTab.Claude, t.Window.InboxTabShown);
        t.Window.ChooseInboxTab(InboxTab.All);
        selection.Select(note);
        t.Controller.Reveal();
        Assert.Equal(InboxTab.All, t.Window.InboxTabShown);
        Assert.Same(InboxLook.Raised, t.Window.InboxLineButton(note)!.Background);
    });

    // A subtask in the page opens a page of its own; back returns to its parent's page.
    [Fact]
    public void A_subtask_opens_its_page_and_back_returns_to_the_parent() => StaThread.Run(() =>
    {
        using var t = Build();
        var parent = t.Tasks.AddNote("Parent errand", t.Gepha).Value.Id;
        var sub = t.Tasks.AddNote("Child errand", t.Gepha).Value.Id;
        t.Tasks.SetParent(sub, parent);
        t.Harness.Manager.SaveTasksPanel(t.Harness.Manager.State.TasksPanel! with { HideSubtasks = true });
        t.Controller.Show();
        ChooseInbox(t);
        t.Window.ChooseInboxTab(InboxTab.Notes);
        Click(LineOf(t.Window, parent));

        var subLine = Inside(t.Window.InboxPageContent!).OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Subtask Child errand");
        Click(subLine);
        Assert.Equal(sub, t.Window.InboxPageShown);

        Click(ButtonNamed(t.Window.InboxPageContent!, TasksWindow.InboxPageBackText));
        Assert.Equal(parent, t.Window.InboxPageShown);
        Click(ButtonNamed(t.Window.InboxPageContent!, TasksWindow.InboxPageBackText));
        Assert.Null(t.Window.InboxPageShown);
    });

    // The page opens scrolled to its end, where the newest comments and the comment box are.
    [Fact]
    public void A_page_opens_at_its_end() => StaThread.Run(() =>
    {
        using var t = Build(width: 460);
        var k = InboxTasks(t);
        t.Tasks.Describe(k.Asking, string.Join("\n\n", Enumerable.Range(0, 60).Select(i => $"Step {i} of a long brief, long enough to take a line of its own.")));
        t.Controller.Show();
        ChooseInbox(t);
        t.Window.UpdateLayout();

        Click(LineOf(t.Window, k.Asking));
        t.Window.UpdateLayout();

        var (offset, extent) = t.Window.InboxPageScroll;
        Assert.True(extent > 0);
        Assert.Equal(extent, offset, 1);
    });

    // A short page reads like a chat from the bottom up: the comment box at the bottom edge, the empty room on top.
    [Fact]
    public void A_short_page_sits_at_the_bottom() => StaThread.Run(() =>
    {
        using var t = Build(width: 460);
        var k = InboxTasks(t);
        t.Controller.Show();
        ChooseInbox(t);
        Click(LineOf(t.Window, k.Done));
        t.Window.UpdateLayout();

        var scroller = t.Window.InboxPageScroller!;
        Assert.Equal(0, scroller.ScrollableHeight, 1);
        var box = t.Window.CommentBoxOf(k.Done)!;
        var bottom = box.TranslatePoint(new Point(0, box.ActualHeight), scroller).Y;
        Assert.True(scroller.ViewportHeight - bottom < 40, $"box ends at {bottom} of {scroller.ViewportHeight}");
        Assert.True(scroller.ViewportHeight > 200);
    });

    // The manager's violet line stays in sight on a page too.
    [Fact]
    public void The_manager_line_stays_on_a_page() => StaThread.Run(() =>
    {
        using var t = Build(openManager: () => CSharpFunctionalExtensions.Result.Success());
        t.Harness.Manager.SetManagerSession(new ManagerRecord("mgr", new DateOnly(2026, 11, 22), Question: "Which comes first?", AskedAt: Start));
        var k = InboxTasks(t);
        t.Controller.Show();
        ChooseInbox(t);
        var host = (Border)LogicalTreeHelper.FindLogicalNode(t.Window, "ManagerLineHost");
        Assert.Equal(Visibility.Visible, host.Visibility);

        Click(LineOf(t.Window, k.Asking));
        Assert.NotNull(t.Window.InboxPageContent);
        Assert.Equal(Visibility.Visible, host.Visibility);
    });

    // The page's thread in the Inbox's colours; the List's thread keeps its own.
    [Fact]
    public void The_pages_bubbles_take_the_inbox_colours_and_the_lists_keep_theirs() => StaThread.Run(() =>
    {
        using var t = Build();
        var k = InboxTasks(t);
        t.Tasks.Comment(k.Asking, "s3", "An answer");
        t.Controller.Show();
        t.Controller.ToggleThread(k.Asking);
        static IReadOnlyList<Color> Fills(DependencyObject scope) =>
            Inside(scope).OfType<Border>().Select(b => b.Background).OfType<SolidColorBrush>().Select(b => b.Color).ToArray();
        var inbox = Color.FromRgb(0x1F, 0x25, 0x31);
        // The List's lines and thread, not the whole window: the panel's foot (the Inbox's view switch, in every view) wears
        // the Inbox's colours on its pill by design.
        Assert.DoesNotContain(inbox, Fills(t.Window.Rows));
        t.Controller.ToggleThread(k.Asking);

        ChooseInbox(t);
        Click(LineOf(t.Window, k.Asking));
        Assert.Contains(inbox, Fills((DependencyObject)t.Window.InboxPageContent!));
        Assert.DoesNotContain(Color.FromRgb(0x2B, 0x26, 0x20), Fills((DependencyObject)t.Window.InboxPageContent!));
    });

    // The working mark turns while it is on screen; nothing else does.
    [Fact]
    public void Only_the_working_mark_turns() => StaThread.Run(() =>
    {
        using var t = Build(workingOf: task => task.Title == "Review the diff" ? 1 : 0);
        var k = InboxTasks(t);
        t.Controller.Show();
        ChooseInbox(t);
        t.Window.ChooseInboxTab(InboxTab.Claude);

        static int Turning(DependencyObject line) => Inside(line).OfType<Canvas>().Count(c => Equals(c.Tag, InboxMarks.TurningTag));
        Assert.Equal(1, Turning(LineOf(t.Window, k.Working)));
        Assert.Equal(0, Turning(LineOf(t.Window, k.Idle)));
    });

    // Ctrl+F and the search button show the search row under the Inbox's header and put the keyboard in it.
    [Fact]
    public void Ctrl_f_and_the_search_button_show_and_focus_the_search_box() => StaThread.Run(() =>
    {
        using var t = Build();
        InboxTasks(t);
        t.Controller.Show();
        ChooseInbox(t);
        Assert.False(t.Window.InboxSearchShown);

        Assert.True(t.Window.Press(Key.F, ModifierKeys.Control));
        Assert.True(t.Window.InboxSearchShown);
        Assert.True(t.Window.SearchBox.IsKeyboardFocused);

        Click(ButtonNamed(t.Window.InboxHeaderShown!, "Search"));
        Assert.False(t.Window.InboxSearchShown);
        Click(ButtonNamed(t.Window.InboxHeaderShown!, "Search"));
        Assert.True(t.Window.InboxSearchShown);
        Assert.True(t.Window.SearchBox.IsKeyboardFocused);
    });

    // A pull request's number finds its task in the Inbox, past the tabs.
    [Fact]
    public void Search_by_a_pull_request_number_finds_its_task() => StaThread.Run(() =>
    {
        using var t = Build();
        var k = InboxTasks(t);
        t.Tasks.AddPullRequest(k.Done, new PullRequestLink("AppifySheets", "Deskbar", 523));
        t.Controller.Show();
        ChooseInbox(t);

        t.Window.SearchBox.Text = "#523";

        Assert.False(t.Window.InboxShown!.Tabbed);
        Assert.Equal([k.Done], DrawnInboxLines(t));
    });

    // Every text colour of the Inbox look reads at 4.5 to 1 or better on what it is drawn on (spec, "The frame").
    [Fact]
    public void Every_inbox_text_colour_has_a_contrast_of_at_least_4_5()
    {
        static double Luminance(Brush brush)
        {
            var c = ((SolidColorBrush)brush).Color;
            static double Channel(byte v) => v / 255.0 is var s && s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        }
        static double Contrast(Brush a, Brush b)
        {
            var (x, y) = (Luminance(a), Luminance(b));
            return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
        }
        var chat = new SolidColorBrush(Color.FromRgb(0x2A, 0x52, 0xB0));
        var lightInk = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6));
        var pairs = new (string Name, Brush Text, Brush Back)[]
        {
            ("ink on background", InboxLook.Ink, InboxLook.Background), ("second ink on background", InboxLook.Ink2, InboxLook.Background),
            ("third ink on background", InboxLook.Ink3, InboxLook.Background), ("amber on background", InboxLook.Amber, InboxLook.Background),
            ("blue on background", InboxLook.Blue, InboxLook.Background), ("violet on background", InboxLook.Violet, InboxLook.Background),
            ("ink on surface", InboxLook.Ink, InboxLook.Surface), ("second ink on surface", InboxLook.Ink2, InboxLook.Surface),
            ("third ink on surface", InboxLook.Ink3, InboxLook.Surface), ("ink on raised", InboxLook.Ink, InboxLook.Raised),
            ("second ink on raised", InboxLook.Ink2, InboxLook.Raised), ("third ink on raised", InboxLook.Ink3, InboxLook.Raised),
            ("amber on raised", InboxLook.Amber, InboxLook.Raised), ("second ink on the sheet", InboxLook.Ink2, InboxLook.Sheet),
            ("ink on the question", InboxLook.Ink, InboxLook.AskFill), ("amber on the question", InboxLook.Amber, InboxLook.AskFill),
            ("background on Close task", InboxLook.Background, InboxLook.Ink), ("bubble text on your bubble", lightInk, chat),
            ("bubble text on Claude's bubble", lightInk, InboxLook.Raised),
        };
        Assert.All(pairs, p => Assert.True(Contrast(p.Text, p.Back) >= 4.5, $"{p.Name}: {Contrast(p.Text, p.Back):0.00}"));
    }

    // `tasks-window inbox` and `list` change only the saved state; the open panel follows them (TasksBoard.ShownStateKey).
    [Fact]
    public void The_open_panel_follows_the_remembered_view_from_the_pipe() => StaThread.Run(() =>
    {
        using var t = Build();
        InboxTasks(t);
        t.Controller.Show();
        var control = new Deskbar.Core.Control.RemoteControl(t.Harness.Manager);

        Assert.Equal(0, control.Execute(["tasks-window", "inbox"]).ExitCode);
        Assert.NotNull(t.Window.InboxShown);
        Assert.Equal(0, control.Execute(["tasks-window", "list"]).ExitCode);
        Assert.Null(t.Window.InboxShown);
    });

    // The tester's M1: the Inbox's Keys are the lines drawn in the tab on show, so F2 with the selection in another tab
    // does nothing, and the panel keeps redrawing.
    [Fact]
    public void F2_with_the_selection_in_another_tab_does_nothing_and_the_panel_still_redraws() => StaThread.Run(() =>
    {
        var selection = new TaskSelection();
        using var t = Build(selection: selection);
        var k = InboxTasks(t);
        t.Controller.Show();
        ChooseInbox(t);
        Assert.Equal([k.Asking, k.Done], t.Window.Keys);
        selection.Select(k.Note);

        Assert.False(t.Window.Press(Key.F2, ModifierKeys.None));
        Assert.Null(t.Window.Editor);

        Click(ButtonNamed(t.Window.InboxHeaderShown!, "Notes"));
        Assert.Equal([k.Note], t.Window.Keys);
        Assert.Equal([k.Note], DrawnInboxLines(t));
        // On show now, F2 edits it.
        Assert.True(t.Window.Press(Key.F2, ModifierKeys.None));
        Assert.NotNull(t.Window.Editor);
    });

    // The tester's L1: a note added from the panel's add box shows in its tab, its line drawn and glowing, while the tab on
    // show was another; a task added from the bar's box or by a session leaves the tab alone.
    [Fact]
    public void A_note_added_from_the_panel_shows_in_its_tab_and_glows() => StaThread.Run(() =>
    {
        using var t = Build();
        var k = InboxTasks(t);
        t.Controller.Show();
        ChooseInbox(t);
        Assert.Equal(InboxTab.NeedsYou, t.Window.InboxTabShown);

        t.Tasks.AddNote("Added elsewhere", Maybe<Guid>.None);
        Assert.Equal(InboxTab.NeedsYou, t.Window.InboxTabShown);

        Click(ButtonNamed(t.Window.InboxHeaderShown!, "New"));
        Assert.True(t.Window.TypeNote("Pay the rent", Key.Enter));
        var added = t.Tasks.Board.Tasks.Single(x => x.Title == "Pay the rent").Id;

        Assert.Equal(InboxTab.Notes, t.Window.InboxTabShown);
        Assert.Contains(added, DrawnInboxLines(t));
        Assert.True(t.Window.InboxGlowing(added));
        Assert.Null(t.Window.InboxPageShown);
    });

    // L1, the page case: a subtask added from a page's thread keeps the page and the tab as they were.
    [Fact]
    public void A_subtask_added_from_a_page_keeps_the_page_and_the_tab() => StaThread.Run(() =>
    {
        using var t = Build();
        var k = InboxTasks(t);
        t.Controller.Show();
        ChooseInbox(t);
        Click(LineOf(t.Window, k.Asking));

        Click(ButtonNamed(t.Window.InboxPageContent!, "Add subtask"));
        Assert.True(t.Window.TypeSubtask("A step of it", Key.Enter));

        var sub = t.Tasks.Board.Tasks.Single(x => x.Title == "A step of it");
        Assert.Equal(k.Asking, sub.ParentId.Value);
        Assert.Equal(k.Asking, t.Window.InboxPageShown);
        Assert.NotNull(t.Window.InboxPageContent);
        Assert.Equal(InboxTab.NeedsYou, t.Window.InboxTabShown);
        Assert.Contains(Inside(t.Window.InboxPageContent!).OfType<Button>(), b => AutomationProperties.GetName(b) == "Subtask A step of it");
    });

    // The tester's L2: a task shown from outside replaces the open page, so back goes to the list.
    [Fact]
    public void A_task_shown_from_outside_replaces_the_page_and_back_goes_to_the_list() => StaThread.Run(() =>
    {
        using var t = Build();
        var k = InboxTasks(t);
        t.Controller.Show();
        ChooseInbox(t);
        Click(LineOf(t.Window, k.Asking));

        t.Controller.ShowTask(k.Note);
        Assert.Equal(k.Note, t.Window.InboxPageShown);

        Click(ButtonNamed(t.Window.InboxPageContent!, TasksWindow.InboxPageBackText));
        Assert.Null(t.Window.InboxPageShown);
        Assert.Null(t.Window.InboxPageContent);
    });

    // L2: parent page, its subtask's page, Open parent (back to the page under it on the stack), then back: the list.
    [Fact]
    public void Open_parent_from_a_subtasks_page_goes_back_to_the_parent_and_then_the_list() => StaThread.Run(() =>
    {
        using var t = Build();
        var parent = t.Tasks.AddNote("Parent errand", t.Gepha).Value.Id;
        var sub = t.Tasks.AddNote("Child errand", t.Gepha).Value.Id;
        t.Tasks.SetParent(sub, parent);
        t.Controller.Show();
        ChooseInbox(t);
        t.Window.ChooseInboxTab(InboxTab.Notes);
        Click(LineOf(t.Window, parent));
        Click(Inside(t.Window.InboxPageContent!).OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Subtask Child errand"));
        Assert.Equal(sub, t.Window.InboxPageShown);

        t.Controller.OpenParent(parent);
        Assert.Equal(parent, t.Window.InboxPageShown);

        Click(ButtonNamed(t.Window.InboxPageContent!, TasksWindow.InboxPageBackText));
        Assert.Null(t.Window.InboxPageShown);
    });

    // A real click on a workspace in the chip's move menu moves the task.
    [Fact]
    public void A_click_in_the_chips_move_menu_moves_the_task() => StaThread.Run(() =>
    {
        using var t = Build();
        var k = InboxTasks(t);
        t.Controller.Show();
        ChooseInbox(t);

        Click(ButtonNamed(LineOf(t.Window, k.Asking), TasksWindow.PlaceChipName));
        var move = t.Window.OpenedMenu!;
        Click(WorkspaceChipMenu.Chips(move).Single(c => AutomationProperties.GetName(c) == "Sparrow"));

        Assert.Equal(t.Sparrow, t.Tasks.Board.Tasks.Single(x => x.Id == k.Asking).WorkspaceId.Value);
    });

    // A task's id typed in the window's search box finds it, past the tabs.
    [Fact]
    public void An_id_typed_in_the_search_box_finds_its_task() => StaThread.Run(() =>
    {
        using var t = Build();
        var k = InboxTasks(t);
        t.Controller.Show();
        ChooseInbox(t);

        Assert.True(t.Window.Press(Key.F, ModifierKeys.Control));
        t.Window.SearchBox.Text = k.Note.ToString()[..8];

        Assert.False(t.Window.InboxShown!.Tabbed);
        Assert.Equal([k.Note], DrawnInboxLines(t));
    });

    // A note just added is followed into the Claude tab once Claude starts on it (Add and start Claude, or Start Claude
    // pressed later on the same selected line).
    [Fact]
    public void A_note_just_added_is_followed_into_the_claude_tab_when_claude_starts() => StaThread.Run(() =>
    {
        using var t = Build();
        InboxTasks(t);
        t.Controller.Show();
        ChooseInbox(t);
        Click(ButtonNamed(t.Window.InboxHeaderShown!, "New"));
        Assert.True(t.Window.TypeNote("Try the faster build", Key.Enter));
        var added = t.Tasks.Board.Tasks.Single(x => x.Title == "Try the faster build").Id;
        Assert.Equal(InboxTab.Notes, t.Window.InboxTabShown);

        Assert.True(t.Tasks.Join(added, Session("s9")).IsSuccess);

        Assert.Equal(InboxTab.Claude, t.Window.InboxTabShown);
        Assert.Contains(added, DrawnInboxLines(t));
        Assert.True(t.Window.InboxGlowing(added));
    });

    // The user taking over (a tab button) ends the follow: the tab on show stays.
    [Fact]
    public void A_tab_click_ends_the_follow() => StaThread.Run(() =>
    {
        using var t = Build();
        InboxTasks(t);
        t.Controller.Show();
        ChooseInbox(t);
        Click(ButtonNamed(t.Window.InboxHeaderShown!, "New"));
        Assert.True(t.Window.TypeNote("Try the faster build", Key.Enter));
        var added = t.Tasks.Board.Tasks.Single(x => x.Title == "Try the faster build").Id;

        Click(ButtonNamed(t.Window.InboxHeaderShown!, "Needs you"));
        Assert.True(t.Tasks.Join(added, Session("s9")).IsSuccess);

        Assert.Equal(InboxTab.NeedsYou, t.Window.InboxTabShown);
    });
}
