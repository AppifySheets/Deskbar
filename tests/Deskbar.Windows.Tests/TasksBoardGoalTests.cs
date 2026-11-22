using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core.Goals;
using Deskbar.Core.Tasks;

namespace Deskbar.Windows.Tests;

// The tasks window links a task to a goal and filters by goal (goals plan, Task 6). Design:
// docs/superpowers/specs/2026-11-22-goals-planner-design.md ("Linking a task to a goal elsewhere") and the goals
// ledger's ruling G1: the More menu's "Goal" submenu and the goal filter appear only while at least one goal exists,
// so a copy with no goals looks exactly as before. Built like TasksBoardTests (the shared two-workspace harness, a real
// TaskService on an in-memory store) plus a real GoalService on an in-memory goals store; the local date is Tuesday
// 24 November 2026.
public class TasksBoardGoalTests
{
    sealed class MemoryTasks : ITaskStore
    {
        public Result<TaskBoard> Load() => TaskBoard.Empty;

        public Result Save(TaskBoard board) => Result.Success();
    }

    sealed class MemoryGoals : IGoalStore
    {
        public Result<GoalBoard> Load() => GoalBoard.Empty;

        public Result Save(GoalBoard board) => Result.Success();
    }

    static readonly DateTimeOffset Start = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);
    static readonly DateOnly Today = new(2026, 11, 24);
    static readonly DateTime ClickStart = new(2026, 11, 24, 9, 0, 0, DateTimeKind.Utc);

    sealed class Board : IDisposable
    {
        public required Harness Harness { get; init; }
        public required TasksWindow Window { get; init; }
        public required TasksBoard Controller { get; init; }
        public required TaskService Tasks { get; init; }
        public required GoalService Goals { get; init; }

        public Guid Gepha => Harness.Manager.State.Workspaces.Single(w => w.Name == "GEPHA").Id;

        public void Dispose()
        {
            Controller.Dispose();
            Window.CloseForGood();
        }
    }

    // Each read of the clocks is later than the one before, as in TasksBoardTests, so tasks are raised in the order the
    // test makes them and a click is never "right after a repaint".
    static Board Build(bool withGoals = true)
    {
        var harness = Harness.Build();
        var minutes = 0;
        var seconds = 0;
        var tasks = new TaskService(new MemoryTasks(), () => Start.AddMinutes(minutes++));
        var goals = new GoalService(new MemoryGoals(), tasks, () => Start.AddMinutes(minutes++), () => Today);
        var window = new TasksWindow { Quiet = true, ClickClock = () => ClickStart.AddSeconds(seconds++), Now = () => Start.AddHours(1) };
        var controller = new TasksBoard(window, tasks, harness.Manager, _ => Result.Success(), now: () => Start.AddHours(1),
            goals: withGoals ? goals : null);
        return new Board { Harness = harness, Window = window, Controller = controller, Tasks = tasks, Goals = goals };
    }

    static IEnumerable<object> Inside(object node) =>
        new[] { node }.Concat(node is DependencyObject d ? LogicalTreeHelper.GetChildren(d).Cast<object>().SelectMany(Inside) : []);

    static string TextOf(MenuItem item) => item.Header is TextBlock text ? text.Text : $"{item.Header}";

    static IReadOnlyList<MenuItem> ItemsOf(ItemsControl menu) => menu.Items.OfType<MenuItem>().ToArray();

    static MenuItem Named(ItemsControl menu, string text) => ItemsOf(menu).Single(i => TextOf(i) == text);

    static Button? GoalFilterButton(TasksWindow window) =>
        Inside(window).OfType<Button>().FirstOrDefault(b => AutomationProperties.GetName(b) == "Goal filter");

    static void Close(ContextMenu menu, TasksWindow window)
    {
        // The menu's Closed arrives through the dispatcher, as it does when Petre clicks away from it.
        menu.IsOpen = false;
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
    }

    // Ruling G1: no goals, no "Goal" item and no goal filter; the window looks as it did before goals.
    [Fact]
    public void Without_goals_there_is_no_goal_item_and_no_goal_filter() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("note", t.Gepha).Value.Id;
        t.Controller.Open(Rect.Empty);

        var more = t.Window.OpenMenuFor(id);
        Assert.DoesNotContain(ItemsOf(more), i => TextOf(i) == "Goal");
        Assert.Null(GoalFilterButton(t.Window));
        Assert.Equal("", t.Window.GoalFilterLabel);
        Close(more, t.Window);

        // The first goal, added from the pipe, brings both.
        t.Goals.Add("Ship it", t.Goals.ThisWeek);
        Assert.NotNull(GoalFilterButton(t.Window));
        Assert.Equal("Goal: all", t.Window.GoalFilterLabel);
        Assert.Contains(ItemsOf(t.Window.OpenMenuFor(id)), i => TextOf(i) == "Goal");
    });

    [Fact]
    public void Without_a_goal_service_there_is_no_goal_item_either() => StaThread.Run(() =>
    {
        using var t = Build(withGoals: false);
        var id = t.Tasks.AddNote("note", t.Gepha).Value.Id;
        t.Goals.Add("Ship it", t.Goals.ThisWeek);
        t.Controller.Open(Rect.Empty);

        Assert.DoesNotContain(ItemsOf(t.Window.OpenMenuFor(id)), i => TextOf(i) == "Goal");
        Assert.Null(GoalFilterButton(t.Window));
    });

    [Fact]
    public void The_goal_menu_lists_this_weeks_and_months_open_goals_and_none() => StaThread.Run(() =>
    {
        using var t = Build();
        var weekly = t.Goals.Add("Ship it", t.Goals.ThisWeek).Value;
        t.Goals.Add("Done this week", t.Goals.ThisWeek).Tap(g => t.Goals.MarkDone(g.Id));
        t.Goals.Add("Dropped this month", t.Goals.ThisMonth).Tap(g => t.Goals.Drop(g.Id));
        t.Goals.Add("Last week", t.Goals.ThisWeek.Previous());
        t.Goals.Add("Next month", t.Goals.ThisMonth.Next());
        var monthly = t.Goals.Add("Store listing", t.Goals.ThisMonth).Value;
        var id = t.Tasks.AddNote("note", t.Gepha).Value.Id;
        t.Goals.LinkTask(id, weekly.Id);
        t.Controller.Open(Rect.Empty);

        var menu = t.Window.OpenGoalMenu(id);

        Assert.Equal(["None", "Store listing", "Ship it"], ItemsOf(menu).Select(TextOf));
        Assert.True(Named(menu, "Ship it").IsChecked);
        Assert.False(Named(menu, "None").IsChecked);
        Assert.False(Named(menu, "Store listing").IsChecked);
        Assert.Equal("month", Named(menu, "Store listing").InputGestureText);
        Assert.Equal("week", Named(menu, "Ship it").InputGestureText);
        Close(menu, t.Window);

        // The More menu carries the same choices under "Goal".
        var more = t.Window.OpenMenuFor(id);
        Assert.Equal(["None", "Store listing", "Ship it"], ItemsOf(Named(more, "Goal")).Select(TextOf));
        Assert.Equal(monthly.Id, t.Goals.Board.In(t.Goals.ThisMonth).Single(g => g.IsOpen).Id);
    });

    [Fact]
    public void Choosing_a_goal_links_the_task_and_none_unlinks_it() => StaThread.Run(() =>
    {
        using var t = Build();
        var goal = t.Goals.Add("Ship it", t.Goals.ThisWeek).Value;
        var first = t.Tasks.AddNote("first", t.Gepha).Value.Id;
        var id = t.Tasks.AddNote("note", t.Gepha).Value.Id;
        t.Tasks.SetFlag(id, TaskFlag.Important, true);
        var before = t.Tasks.Board.Tasks.Single(x => x.Id == id);
        t.Controller.Open(Rect.Empty);
        var keys = t.Window.Keys.ToArray();

        Named(t.Window.OpenGoalMenu(id), "Ship it").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        var after = t.Tasks.Board.Tasks.Single(x => x.Id == id);
        Assert.Equal(goal.Id, after.GoalId.Value);
        Assert.Equal((before.Important, before.Urgent, before.Rank, before.Status), (after.Important, after.Urgent, after.Rank, after.Status));
        Assert.Equal(keys, t.Window.Keys);

        Assert.True(t.Controller.SetGoal(id, Maybe<Guid>.None).IsSuccess);
        Assert.True(t.Tasks.Board.Tasks.Single(x => x.Id == id).GoalId.HasNoValue);
        Assert.True(t.Tasks.Board.Tasks.Single(x => x.Id == first).GoalId.HasNoValue);
    });

    // A done goal is not offered, and linking to one directly says why on the problem line.
    [Fact]
    public void A_done_goal_is_refused_on_the_problem_line() => StaThread.Run(() =>
    {
        using var t = Build();
        var goal = t.Goals.Add("Ship it", t.Goals.ThisWeek).Value;
        t.Goals.MarkDone(goal.Id);
        var id = t.Tasks.AddNote("note", t.Gepha).Value.Id;
        t.Controller.Open(Rect.Empty);

        Assert.Equal("The goal is done", t.Controller.SetGoal(id, goal.Id).Error);
        Assert.Equal("The goal is done", t.Window.ProblemText.Text);
    });

    // G1: goals exist, but none open this week or month (only last week's): "None" and a disabled line saying so.
    [Fact]
    public void With_no_open_goal_this_week_or_month_the_menu_says_so() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Goals.Add("Last week", t.Goals.ThisWeek.Previous());
        var id = t.Tasks.AddNote("note", t.Gepha).Value.Id;
        t.Controller.Open(Rect.Empty);

        var menu = t.Window.OpenGoalMenu(id);

        Assert.Equal(["None", "No goals this week or month"], ItemsOf(menu).Select(TextOf));
        Assert.True(Named(menu, "None").IsChecked);
        Assert.False(Named(menu, "No goals this week or month").IsEnabled);
    });

    [Fact]
    public void The_goal_filter_narrows_the_list_and_the_matrix() => StaThread.Run(() =>
    {
        using var t = Build();
        var monthly = t.Goals.Add("Store listing", t.Goals.ThisMonth).Value;
        var weekly = t.Goals.Add("Ship it", t.Goals.ThisWeek).Value;
        var other = t.Goals.Add("Read Covey", t.Goals.ThisWeek).Value;
        t.Goals.SetParent(weekly.Id, monthly.Id);
        var onMonth = t.Tasks.AddNote("on month", t.Gepha).Value.Id;
        var onWeek = t.Tasks.AddNote("on week", t.Gepha).Value.Id;
        var onOther = t.Tasks.AddNote("on other", t.Gepha).Value.Id;
        var free = t.Tasks.AddNote("free", t.Gepha).Value.Id;
        t.Goals.LinkTask(onMonth, monthly.Id);
        t.Goals.LinkTask(onWeek, weekly.Id);
        t.Goals.LinkTask(onOther, other.Id);
        t.Controller.Open(Rect.Empty);
        Assert.Contains("Open 4", t.Window.FilterLabels);

        var filterMenu = t.Window.OpenGoalFilterMenu();
        Assert.Equal(["All goals", "No goal", "Store listing", "Ship it", "Read Covey"], ItemsOf(filterMenu).Select(TextOf));
        Assert.True(Named(filterMenu, "All goals").IsChecked);
        Close(filterMenu, t.Window);

        t.Controller.FilterGoal(TaskGoalFilter.On(weekly));
        Assert.Equal([onWeek], t.Window.Keys);
        Assert.Equal("Goal: Ship it", t.Window.GoalFilterLabel);
        Assert.Contains("Open 1", t.Window.FilterLabels);

        t.Controller.FilterGoal(TaskGoalFilter.On(monthly));
        Assert.Equal([onWeek, onMonth], t.Window.Keys);
        Assert.Contains("Open 2", t.Window.FilterLabels);

        t.Controller.FilterGoal(TaskGoalFilter.NoGoal);
        Assert.Equal([free], t.Window.Keys);
        Assert.Equal("Goal: none", t.Window.GoalFilterLabel);

        // The Matrix shows the same narrowing.
        t.Controller.FilterGoal(TaskGoalFilter.On(weekly));
        t.Harness.Manager.SetTasksMatrix(true);
        Assert.Equal([onWeek], t.Window.Keys);

        t.Controller.FilterGoal(TaskGoalFilter.All);
        Assert.Equal(4, t.Window.Keys.Count);
    });

    // A goal deleted from the pipe while the window filters by it: the filter falls back to every goal rather than
    // showing an empty list under a filter nobody can see any more.
    [Fact]
    public void A_filter_on_a_deleted_goal_falls_back_to_all() => StaThread.Run(() =>
    {
        using var t = Build();
        var goal = t.Goals.Add("Ship it", t.Goals.ThisWeek).Value;
        t.Goals.Add("Keep one", t.Goals.ThisWeek);
        t.Tasks.AddNote("note", t.Gepha);
        t.Controller.Open(Rect.Empty);
        t.Controller.FilterGoal(TaskGoalFilter.On(goal));
        Assert.Empty(t.Window.Keys);

        t.Goals.Delete(goal.Id);

        Assert.Single(t.Window.Keys);
        Assert.Equal("Goal: all", t.Window.GoalFilterLabel);
    });

    [Fact]
    public void A_goal_renamed_from_the_pipe_redraws_the_filter_label() => StaThread.Run(() =>
    {
        using var t = Build();
        var goal = t.Goals.Add("Ship it", t.Goals.ThisWeek).Value;
        t.Controller.Open(Rect.Empty);
        t.Controller.FilterGoal(TaskGoalFilter.On(goal));
        Assert.Equal("Goal: Ship it", t.Window.GoalFilterLabel);

        t.Goals.Rename(goal.Id, "Ship the goals planner");

        Assert.Equal("Goal: Ship the goals planner", t.Window.GoalFilterLabel);
    });

    // The #77 lesson: a change arriving while the goal menu is open waits for the menu to close.
    [Fact]
    public void The_goal_menu_holds_a_redraw_until_it_closes() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Goals.Add("Ship it", t.Goals.ThisWeek);
        var id = t.Tasks.AddNote("note", t.Gepha).Value.Id;
        t.Controller.Open(Rect.Empty);
        var rebuilds = t.Window.Rebuilds;

        var menu = t.Window.OpenGoalMenu(id);
        Assert.True(menu.IsOpen);
        var second = t.Tasks.AddNote("second", t.Gepha).Value.Id;
        Assert.Equal(rebuilds, t.Window.Rebuilds);
        Assert.Equal([id], t.Window.Keys);

        Close(menu, t.Window);
        Assert.Equal(rebuilds + 1, t.Window.Rebuilds);
        Assert.Equal([second, id], t.Window.Keys);
    });

    // Review M1: both goal menus are built from the latest view when they open, so a goal edit that changes nothing
    // the window draws (the filter shows "Goal: all") redraws no row. A redraw for it reset the Matrix's scrolls.
    [Fact]
    public void A_goal_edit_redraws_no_row() => StaThread.Run(() =>
    {
        using var t = Build();
        var goal = t.Goals.Add("Ship it", t.Goals.ThisWeek).Value;
        var id = t.Tasks.AddNote("note", t.Gepha).Value.Id;
        t.Controller.Open(Rect.Empty);
        var rebuilds = t.Window.Rebuilds;

        t.Goals.Rename(goal.Id, "Ship the goals planner");
        t.Goals.Add("Store listing", t.Goals.ThisMonth);
        t.Goals.MarkDone(goal.Id);

        Assert.Equal(rebuilds, t.Window.Rebuilds);
        // The menus still show the goals as they are now.
        Assert.Equal(["None", "Store listing"], ItemsOf(t.Window.OpenGoalMenu(id)).Select(TextOf));
    });

    // Review M6: a task linked to a goal of another period (or one done since) still shows its goal, ticked, at the end
    // of the menu, so the menu never hides what the task counts toward.
    [Fact]
    public void A_tasks_own_goal_from_another_period_is_listed_and_ticked() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Goals.Add("Ship it", t.Goals.ThisWeek);
        var old = t.Goals.Add("Last week's", t.Goals.ThisWeek.Previous()).Value;
        var id = t.Tasks.AddNote("note", t.Gepha).Value.Id;
        t.Goals.LinkTask(id, old.Id);
        t.Controller.Open(Rect.Empty);

        var menu = t.Window.OpenGoalMenu(id);

        Assert.Equal(["None", "Ship it", "Last week's"], ItemsOf(menu).Select(TextOf));
        Assert.True(Named(menu, "Last week's").IsChecked);
        Assert.False(Named(menu, "None").IsChecked);
    });

    // Opening the window again starts at every goal, as it starts at every workspace.
    [Fact]
    public void Opening_again_resets_the_goal_filter() => StaThread.Run(() =>
    {
        using var t = Build();
        var goal = t.Goals.Add("Ship it", t.Goals.ThisWeek).Value;
        t.Controller.Open(Rect.Empty);
        t.Controller.FilterGoal(TaskGoalFilter.On(goal));
        t.Controller.Dismiss();

        t.Controller.Open(Rect.Empty);

        Assert.Equal("Goal: all", t.Window.GoalFilterLabel);
    });
}
