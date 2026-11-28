using CSharpFunctionalExtensions;
using Deskbar.Core.Goals;
using Deskbar.Core.Tasks;
// System.Threading.Tasks (an implicit using) has a TaskStatus too; the alias says which one this file means.
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests.Tasks;

// The tasks panel's Inbox view, its three tabs (InboxTabs; Deskbar task 740a2231). The input is always what the List
// draws (TaskViews.Grouped, then UnderParents), so these tests build the List's groups the way TasksBoard.View does and
// check where each line lands. The window itself is tested in Deskbar.Windows.Tests.
public class InboxTabsTests
{
    DateTimeOffset clock = new(2026, 11, 26, 10, 0, 0, TimeSpan.Zero);
    static readonly Maybe<Guid> Free = Maybe<Guid>.None;
    const string LimitLine = "Stopped on the usage limit until Thu 06:00";

    TaskBoard board = TaskBoard.Empty;

    Guid Note(string title)
    {
        var id = Guid.NewGuid();
        board = board.AddNote(title, Free, clock, id).Value.Board;
        clock = clock.AddMinutes(1);
        return id;
    }

    Guid Claudes(string title, string session, bool live = true)
    {
        var id = Guid.NewGuid();
        board = board.Create(title, Free, new TaskSession(session, @"C:\repos\Example", clock, live ? Maybe<DateTimeOffset>.None : clock), clock, id).Value.Board;
        clock = clock.AddMinutes(1);
        return id;
    }

    void Change(Func<TaskBoard, Result<TaskBoard>> change)
    {
        board = change(board).Value;
        clock = clock.AddMinutes(1);
    }

    TaskItem Task(Guid id) => board.Tasks.Single(t => t.Id == id);

    static bool Limited(TaskItem task) => task.StatusLine.StartsWith("Stopped on the usage limit until ", StringComparison.Ordinal);

    // The Inbox over the List's groups, as TasksBoard.View hands them over: grouped, then each subtask under its parent.
    InboxView Inbox(Func<TaskItem, bool>? busy = null, Func<TaskItem, bool>? hollow = null, TaskFilter filter = TaskFilter.Open, bool tabbed = true)
    {
        var (listed, nested) = TaskViews.UnderParents(TaskViews.Grouped(board, filter, TaskWorkspaceFilter.All, busy: busy, hollow: hollow));
        return InboxTabs.Split(listed, nested, board, Limited, busy, hollow, tabbed);
    }

    IReadOnlyList<Guid> Listed(Func<TaskItem, bool>? busy = null, TaskFilter filter = TaskFilter.Open) =>
        TaskViews.UnderParents(TaskViews.Grouped(board, filter, TaskWorkspaceFilter.All, busy: busy)).Groups.SelectMany(g => g.Tasks).Select(t => t.Id).ToArray();

    static IReadOnlyList<(InboxSection, Guid)> Sections(InboxView view, InboxTab tab) => view.In(tab).Select(l => (l.Section, l.Id)).ToArray();

    // A board with one task of every kind the List groups: a question, a Done task, Claude working and idle, a note in
    // each quadrant, a handed-back task, and one stopped on the usage limit.
    (Guid Asking, Guid Done, Guid Working, Guid Idle, Guid Quiet, Guid Limited, Guid DoFirst, Guid Plan, Guid Quick, Guid Someday) EveryKind()
    {
        var asking = Claudes("Docked bar jumps", "s1");
        Change(b => b.Ask(asking, "Does the bar stay?", clock));
        var done = Claudes("Lighter terminal", "s2");
        Change(b => b.MarkDone(done, clock));
        var working = Claudes("Review the diff", "s3");
        var idle = Claudes("Read the extension", "s4");
        var quiet = Claudes("Measure the strip", "s5");
        board = board.HandBack(quiet, clock);
        var limited = Claudes("Store icons", "s6");
        Change(b => b.SetStatusLine(limited, LimitLine, clock));
        var doFirst = Note("Pay the bill");
        Change(b => b.SetFlag(doFirst, TaskFlag.Important, true, clock));
        Change(b => b.SetFlag(doFirst, TaskFlag.Urgent, true, clock));
        var plan = Note("Launch checklist");
        Change(b => b.SetFlag(plan, TaskFlag.Important, true, clock));
        var quick = Note("Call the shop");
        Change(b => b.SetFlag(quick, TaskFlag.Urgent, true, clock));
        var someday = Note("Try a new font");
        return (asking, done, working, idle, quiet, limited, doFirst, plan, quick, someday);
    }

    bool Busy(TaskItem task) => task.Title is "Review the diff";

    [Fact]
    public void Every_listed_line_is_in_exactly_one_tab()
    {
        EveryKind();
        var parent = Note("Parent note");
        var child = Note("Its subtask");
        Change(b => b.SetParent(child, parent, clock));

        var view = Inbox(Busy);

        var inTabs = new[] { InboxTab.NeedsYou, InboxTab.Claude, InboxTab.Notes }.SelectMany(tab => view.In(tab)).Select(l => l.Id).ToArray();
        Assert.Equal(Listed(Busy).Order(), inTabs.Order());
        Assert.Equal(inTabs.Length, inTabs.Distinct().Count());
    }

    [Fact]
    public void Each_tabs_count_is_its_number_of_lines()
    {
        EveryKind();

        var view = Inbox(Busy);

        Assert.Equal(3, view.Count(InboxTab.NeedsYou));
        Assert.Equal(3, view.Count(InboxTab.Claude));
        Assert.Equal(4, view.Count(InboxTab.Notes));
        Assert.All(Enum.GetValues<InboxTab>(), tab => Assert.Equal(view.In(tab).Count, view.Count(tab)));
    }

    // The All tab (the owner: "let's add all tasks here as well"): every line of the other three, each once, Needs you's
    // first, then Claude's, then Notes', each in its own tab's order; its count is theirs added up. No line is sorted
    // INTO All: each line's own tab stays one of the three.
    [Fact]
    public void All_holds_the_other_tabs_lines_in_their_order_and_counts_them()
    {
        EveryKind();
        var parent = Note("Parent note");
        var child = Note("Its subtask");
        Change(b => b.SetParent(child, parent, clock));

        var view = Inbox(Busy);

        var three = new[] { InboxTab.NeedsYou, InboxTab.Claude, InboxTab.Notes };
        Assert.Equal(three.SelectMany(tab => view.In(tab)).Select(l => l.Id), view.In(InboxTab.All).Select(l => l.Id));
        Assert.Equal(three.Sum(tab => view.Count(tab)), view.Count(InboxTab.All));
        Assert.Equal(Listed(Busy).Count, view.Count(InboxTab.All));
        Assert.DoesNotContain(view.Lines, l => l.Tab == InboxTab.All);
        // A subtask still follows its parent in All, as in Notes.
        var ids = view.In(InboxTab.All).Select(l => l.Id).ToList();
        Assert.Equal(ids.IndexOf(parent) + 1, ids.IndexOf(child));
    }

    // The tab a task's line is brought on show in: the tab on show while it holds the line (All always does), else the
    // line's own tab; none for a task the Inbox has no line for.
    [Fact]
    public void The_tab_to_show_a_line_in_keeps_a_tab_that_holds_it()
    {
        var k = EveryKind();
        var view = Inbox(Busy);

        Assert.Equal(InboxTab.Claude, view.TabToShow(k.Working, InboxTab.NeedsYou).Value);
        Assert.Equal(InboxTab.Claude, view.TabToShow(k.Working, InboxTab.Claude).Value);
        Assert.Equal(InboxTab.All, view.TabToShow(k.Working, InboxTab.All).Value);
        Assert.Equal(InboxTab.Notes, view.TabToShow(k.Someday, InboxTab.Claude).Value);
        Assert.False(view.TabToShow(Guid.NewGuid(), InboxTab.All).HasValue);
    }

    [Fact]
    public void Needs_you_holds_the_questions_then_went_quiet_then_finished()
    {
        var k = EveryKind();

        Assert.Equal([(InboxSection.Questions, k.Asking), (InboxSection.WentQuiet, k.Quiet), (InboxSection.Finished, k.Done)],
            Sections(Inbox(Busy), InboxTab.NeedsYou));
    }

    // Task 1f6ae26e: a task that only waits for his deploy asks for less than a question or a quiet session, so it sits
    // below both, under its own caption, and above Finished. It is asked first here, so the List's order (oldest ask
    // first) would have put it at the top of the questions.
    [Fact]
    public void A_task_waiting_for_the_deploy_sits_below_the_questions_and_went_quiet()
    {
        var deploy = Claudes("Manager panel", "s0");
        Change(b => b.Ask(deploy, "Install v2.6.86, which has the panel.", clock, deploy: true));
        var k = EveryKind();
        var view = Inbox(Busy);

        Assert.Equal([(InboxSection.Questions, k.Asking), (InboxSection.WentQuiet, k.Quiet), (InboxSection.Deploys, deploy), (InboxSection.Finished, k.Done)],
            Sections(view, InboxTab.NeedsYou));
        Assert.Equal(InboxMark.WaitsForDeploy, view.Lines.Single(l => l.Id == deploy).Mark);
        Assert.Equal(("To deploy", "merged, not running yet"), InboxTabs.CaptionOf(InboxSection.Deploys));
    }

    // Answer buttons make a merged task's ask a real question (DeployWait.Waits), so it stays with the questions.
    [Fact]
    public void A_merged_task_asking_with_answer_buttons_stays_a_question()
    {
        var merged = Claudes("Portal stack", "s0");
        Change(b => b.Ask(merged, "Redeploy now or after the demo?", clock, options: ["Now", "After the demo"]));
        var view = Inbox(hollow: t => t.Id == merged);

        Assert.Equal([(InboxSection.Questions, merged)], Sections(view, InboxTab.NeedsYou));
    }

    [Fact]
    public void Finished_keeps_full_discs_before_rings()
    {
        var ring = Claudes("Merged, not running", "s1");
        Change(b => b.MarkDone(ring, clock));
        var disc = Claudes("Running here", "s2");
        Change(b => b.MarkDone(disc, clock));
        var view = Inbox(hollow: t => t.Id == ring);

        Assert.Equal([disc, ring], view.In(InboxTab.NeedsYou).Select(l => l.Id));
        Assert.Equal([InboxMark.Finished, InboxMark.FinishedNotRunning], view.In(InboxTab.NeedsYou).Select(l => l.Mark));
    }

    // StalledTasks hands a quiet task back: In progress and the user's. With its session live it is in the List's Claude
    // idle part, with none in its quadrant; the Inbox puts it under "Went quiet" either way.
    [Fact]
    public void A_handed_back_task_leaves_the_claude_block_or_its_quadrant_for_went_quiet()
    {
        var live = Claudes("Live but quiet", "s1");
        var ended = Claudes("Ended and quiet", "s2", live: false);
        Change(b => b.SetStatus(ended, TaskStatus.InProgress, clock));
        board = board.HandBack(live, clock).HandBack(ended, clock);
        Assert.True(Task(live).HandedBack.HasValue);
        Assert.True(Task(ended).HandedBack.HasValue);

        var view = Inbox(_ => false);

        Assert.Equal(new[] { live, ended }.Order(), view.In(InboxTab.NeedsYou).Select(l => l.Id).Order());
        Assert.All(view.In(InboxTab.NeedsYou), l => Assert.Equal(InboxSection.WentQuiet, l.Section));
        Assert.All(view.In(InboxTab.NeedsYou), l => Assert.Equal(InboxMark.WentQuiet, l.Mark));
        Assert.Empty(view.In(InboxTab.Claude));
        Assert.Empty(view.In(InboxTab.Notes));
    }

    [Fact]
    public void Claude_holds_working_then_idle_then_the_usage_limit()
    {
        var k = EveryKind();

        Assert.Equal([(InboxSection.WorkingNow, k.Working), (InboxSection.Idle, k.Idle), (InboxSection.UsageLimit, k.Limited)],
            Sections(Inbox(Busy), InboxTab.Claude));
    }

    [Fact]
    public void Without_a_busy_test_every_live_task_is_working_as_in_the_list()
    {
        var a = Claudes("One", "s1");
        var b = Claudes("Two", "s2");

        var view = Inbox();

        Assert.All(view.In(InboxTab.Claude), l => Assert.Equal(InboxSection.WorkingNow, l.Section));
        Assert.All(view.In(InboxTab.Claude), l => Assert.Equal(InboxMark.Working, l.Mark));
        Assert.Equal(new[] { a, b }.Order(), view.In(InboxTab.Claude).Select(l => l.Id).Order());
    }

    [Fact]
    public void The_quadrants_become_the_notes_sections_in_order()
    {
        var k = EveryKind();

        Assert.Equal(
            [(InboxSection.DoFirst, k.DoFirst), (InboxSection.Plan, k.Plan), (InboxSection.QuickOnes, k.Quick), (InboxSection.Someday, k.Someday)],
            Sections(Inbox(Busy), InboxTab.Notes));
        Assert.Equal(("Do first", "urgent and important"), InboxTabs.CaptionOf(InboxSection.DoFirst));
        Assert.Equal(("Someday", "neither"), InboxTabs.CaptionOf(InboxSection.Someday));
    }

    // A subtask drawn under its parent goes where the parent goes, even when its own state would put it elsewhere; its
    // mark still says its own state.
    [Fact]
    public void A_nested_subtask_stays_with_its_parent()
    {
        var parent = Claudes("Parent at work", "s1");
        var child = Note("Plain subtask");
        Change(b => b.SetParent(child, parent, clock));

        var view = Inbox(_ => true);

        Assert.Equal([parent, child], view.In(InboxTab.Claude).Select(l => l.Id));
        var line = view.In(InboxTab.Claude)[1];
        Assert.True(line.Nested);
        Assert.Equal(InboxMark.Note, line.Mark);
        Assert.Empty(view.In(InboxTab.Notes));
    }

    [Theory]
    [InlineData(TaskFilter.Open, true, false, true)]
    [InlineData(TaskFilter.WaitingOnYou, true, false, true)]
    [InlineData(TaskFilter.Open, false, false, false)]
    [InlineData(TaskFilter.Open, true, true, false)]
    [InlineData(TaskFilter.Closed, true, false, false)]
    [InlineData(TaskFilter.Snoozed, true, false, false)]
    public void Tabs_apply_unless_words_an_id_closed_or_snoozed(TaskFilter filter, bool searchBlank, bool foundById, bool tabbed) =>
        Assert.Equal(tabbed, InboxTabs.TabsApply(filter, searchBlank, foundById));

    [Fact]
    public void Without_tabs_the_lines_are_in_the_lists_order()
    {
        EveryKind();

        var view = Inbox(Busy, tabbed: false);

        Assert.False(view.Tabbed);
        Assert.Equal(Listed(Busy), view.Lines.Select(l => l.Id));
        Assert.All(view.Lines, l => Assert.Equal(InboxSection.Matches, l.Section));
    }

    [Fact]
    public void Closed_lines_carry_the_closed_mark()
    {
        var closed = Note("Old errand");
        Change(b => b.Close(closed, clock));

        var view = Inbox(filter: TaskFilter.Closed, tabbed: false);

        Assert.Equal(InboxMark.Closed, Assert.Single(view.Lines).Mark);
    }

    [Fact]
    public void Each_state_has_its_mark()
    {
        var k = EveryKind();
        var ended = Claudes("Session ended", "s9", live: false);
        Change(b => b.SetStatus(ended, TaskStatus.InProgress, clock));
        InboxMark Mark(Guid id) => InboxTabs.MarkOf(Task(id), Limited, Busy);

        Assert.Equal(InboxMark.Asks, Mark(k.Asking));
        Assert.Equal(InboxMark.Finished, Mark(k.Done));
        Assert.Equal(InboxMark.Working, Mark(k.Working));
        Assert.Equal(InboxMark.Idle, Mark(k.Idle));
        Assert.Equal(InboxMark.WentQuiet, Mark(k.Quiet));
        Assert.Equal(InboxMark.UsageLimit, Mark(k.Limited));
        Assert.Equal(InboxMark.Note, Mark(k.Someday));
        // A Claude task with no live session is idle, not a note: the empty box is for a task no session ever worked on.
        Assert.Equal(InboxMark.Idle, Mark(ended));
    }

    [Fact]
    public void A_question_the_keeper_asked_because_it_gave_up_is_went_quiet()
    {
        var task = Claudes("Could not restart", "s1");
        Change(b => b.Ask(task, SessionKeeper.LoopGaveUp, clock));
        Assert.True(SessionKeeper.GivenUp(Task(task)));

        Assert.Equal(InboxMark.WentQuiet, InboxTabs.MarkOf(Task(task), Limited));
        // It is still a question in "Needs you", where every question is.
        Assert.Equal(InboxSection.Questions, Inbox().Lines.Single(l => l.Id == task).Section);
    }

    [Fact]
    public void Since_is_when_the_state_began()
    {
        var k = EveryKind();
        DateTimeOffset Since(Guid id) => InboxTabs.SinceOf(Task(id), InboxTabs.MarkOf(Task(id), Limited, Busy));

        Assert.Equal(Task(k.Asking).WaitingSince.GetValueOrDefault(Task(k.Asking).Raised), Since(k.Asking));
        Assert.Equal(Task(k.Quiet).HandedBack.Value, Since(k.Quiet));
        Assert.Equal(Task(k.Done).Raised, Since(k.Done));
        Assert.Equal(Task(k.Working).UpdatedAt, Since(k.Working));
        Assert.Equal(Task(k.Someday).Created, Since(k.Someday));
    }

    [Fact]
    public void A_line_with_a_link_is_marked()
    {
        var a = Note("One");
        var b = Note("Two");
        var c = Note("Three");
        Change(x => x.Relate(a, b, clock));

        var view = Inbox();

        Assert.True(view.Lines.Single(l => l.Id == a).Linked);
        Assert.True(view.Lines.Single(l => l.Id == b).Linked);
        Assert.False(view.Lines.Single(l => l.Id == c).Linked);
    }

    [Fact]
    public void Filters_on_counts_each_filter_once()
    {
        Assert.Equal(0, InboxTabs.FiltersOn(TaskFilter.Open, ClaudeSessionFilter.Off, TaskWorkspaceFilter.All, TaskAssigneeFilter.All, TaskGoalFilter.All, false));
        // "This workspace" is the header's own switch, not a filter in the sheet.
        Assert.Equal(0, InboxTabs.FiltersOn(TaskFilter.Open, ClaudeSessionFilter.Off, TaskWorkspaceFilter.Current, TaskAssigneeFilter.All, TaskGoalFilter.All, false));
        Assert.Equal(6, InboxTabs.FiltersOn(TaskFilter.Done, ClaudeSessionFilter.Idle, TaskWorkspaceFilter.On(Guid.NewGuid()), TaskAssigneeFilter.Me,
            TaskGoalFilter.NoGoal, true));
        Assert.Equal(1, InboxTabs.FiltersOn(TaskFilter.Open, ClaudeSessionFilter.Off, TaskWorkspaceFilter.On(Free), TaskAssigneeFilter.All, TaskGoalFilter.All, false));
    }

    [Fact]
    public void A_task_the_inbox_lists_names_its_tab()
    {
        var k = EveryKind();
        var view = Inbox(Busy);

        Assert.Equal(InboxTab.NeedsYou, view.TabOf(k.Asking).Value);
        Assert.Equal(InboxTab.Claude, view.TabOf(k.Limited).Value);
        Assert.Equal(InboxTab.Notes, view.TabOf(k.Plan).Value);
        Assert.True(view.TabOf(Guid.NewGuid()).HasNoValue);
    }

    // --- a permission question (Allow / Deny from a task's page) ---------------------------------------------------------

    // The task whose session waits on a permission question leads Needs you, in a section of its own, with the shield,
    // whatever its group: working, idle or even waiting on a question of its own.
    [Fact]
    public void A_permission_question_leads_needs_you_with_its_own_mark()
    {
        var kinds = EveryKind();
        bool Asks(TaskItem task) => task.Id == kinds.Working || task.Id == kinds.Asking;
        var (listed, nested) = TaskViews.UnderParents(TaskViews.Grouped(board, TaskFilter.Open, TaskWorkspaceFilter.All, busy: Busy));

        var view = InboxTabs.Split(listed, nested, board, Limited, Busy, asksPermission: Asks);

        var needsYou = view.In(InboxTab.NeedsYou);
        Assert.Equal(InboxSection.Permissions, needsYou[0].Section);
        Assert.Equal(InboxSection.Permissions, needsYou[1].Section);
        Assert.Equal(new[] { kinds.Asking, kinds.Working }.Order(), needsYou.Take(2).Select(l => l.Id).Order());
        Assert.All(needsYou.Take(2), l => Assert.Equal(InboxMark.AsksPermission, l.Mark));
        Assert.DoesNotContain(view.In(InboxTab.Claude), l => l.Id == kinds.Working);
        Assert.Equal(("Asks permission", "a yes or a no"), InboxTabs.CaptionOf(InboxSection.Permissions));
    }

    // Closed and snoozed keep their own marks: no question can be answered from them.
    [Fact]
    public void A_closed_task_keeps_its_mark()
    {
        var id = Claudes("Closed one", "s9");
        Change(b => b.Close(id, clock, stoppingClaude: true));

        Assert.Equal(InboxMark.Closed, InboxTabs.MarkOf(Task(id), Limited, asksPermission: _ => true));
    }

    [Fact]
    public void Without_a_question_nothing_changes()
    {
        EveryKind();
        var (listed, nested) = TaskViews.UnderParents(TaskViews.Grouped(board, TaskFilter.Open, TaskWorkspaceFilter.All, busy: Busy));

        Assert.Equal(Inbox(Busy).Lines, InboxTabs.Split(listed, nested, board, Limited, Busy, asksPermission: _ => false).Lines);
    }}
