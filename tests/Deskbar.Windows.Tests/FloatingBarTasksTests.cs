using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core.Persistence;
using Deskbar.Core.Tasks;
using Xunit.Abstractions;

namespace Deskbar.Windows.Tests;

// The stopwatch in the bar's strip and the pie in a workspace row's name gutter
// (docs/superpowers/specs/2026-11-19-tasks-in-the-bar-design.md, "The bar" and "When the strip runs out of room").
// Every bar here runs over a TaskService whose clock is a local the test moves, and a store that keeps nothing, so
// no test waits a real second and none touches tasks.json.
public class FloatingBarTasksTests(ITestOutputHelper output)
{
    sealed class MemoryTaskStore : ITaskStore
    {
        public Result<TaskBoard> Load() => TaskBoard.Empty;

        public Result Save(TaskBoard board) => Result.Success();
    }

    static readonly DateTimeOffset Start = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);

    // The bar over the shared two-workspace harness (GEPHA current, Sparrow beside it), with the tasks handed in
    // the way App hands them. SendShellKeys and PressSpace are no-ops so nothing reaches a real shell.
    sealed class TasksBar : IDisposable
    {
        public required Harness Harness { get; init; }
        public required FloatingBar Bar { get; init; }
        public required TaskService Tasks { get; init; }
        public required Func<DateTimeOffset> Clock { get; init; }
        public required Action<TimeSpan> Advance { get; init; }

        public Guid Workspace(string name) => Harness.Manager.State.Workspaces.Single(w => w.Name == name).Id;

        public DockPanel Strip => (DockPanel)Bar.FindName("ShellStrip")!;

        public void Dispose() => Bar.Close();
    }

    static TasksBar Build()
    {
        var harness = Harness.Build();
        var now = Start;
        var tasks = new TaskService(new MemoryTaskStore(), () => now);
        var bar = new FloatingBar(harness.Manager) { Left = -32000, Top = -32000, SendShellKeys = _ => { }, PressSpace = () => { } };
        bar.Tasks = tasks;
        bar.ShowBar();
        bar.UpdateLayout();
        return new TasksBar { Harness = harness, Bar = bar, Tasks = tasks, Clock = () => now, Advance = by => now += by };
    }

    static IEnumerable<DependencyObject> Descendants(DependencyObject node) =>
        LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()
            .SelectMany(child => new[] { child }.Concat(Descendants(child)));

    [Fact]
    public void The_stopwatch_rests_as_a_glyph() => StaThread.Run(() =>
    {
        using var t = Build();

        t.Bar.PaintStopwatch();

        Assert.Equal("", t.Bar.StopwatchText);
        Assert.NotNull(t.Bar.StripTasks);
        Assert.Equal(Visibility.Visible, t.Bar.StripTasks!.Visibility);
        Assert.Contains(t.Bar.StripTasks, t.Strip.Children.OfType<Button>());
    });

    [Fact]
    public void A_running_task_shows_its_time_left() => StaThread.Run(() =>
    {
        using var t = Build();
        Assert.True(t.Tasks.Park("Mail", t.Workspace("Sparrow"), TimeSpan.FromMinutes(5)).IsSuccess);

        t.Advance(TimeSpan.FromSeconds(28));
        t.Bar.PaintStopwatch();

        Assert.Equal("4:32", t.Bar.StopwatchText);
    });

    // The soonest of several running timers is the one shown, and minutes are not zero-padded.
    [Fact]
    public void The_soonest_timer_is_the_one_shown() => StaThread.Run(() =>
    {
        using var t = Build();
        Assert.True(t.Tasks.Park("Long", t.Workspace("Sparrow"), TimeSpan.FromMinutes(30)).IsSuccess);
        Assert.True(t.Tasks.Park("Short", t.Workspace("GEPHA"), TimeSpan.FromMinutes(10)).IsSuccess);

        t.Advance(TimeSpan.FromSeconds(5));
        t.Bar.PaintStopwatch();

        Assert.Equal("9:55", t.Bar.StopwatchText);
    });

    [Fact]
    public void A_due_task_reads_due() => StaThread.Run(() =>
    {
        using var t = Build();
        Assert.True(t.Tasks.Park("Mail", t.Workspace("Sparrow"), TimeSpan.FromMinutes(5)).IsSuccess);

        t.Advance(TimeSpan.FromMinutes(6));
        t.Bar.PaintStopwatch();

        Assert.Equal("due", t.Bar.StopwatchText);
        Assert.Same(t.Bar.AskingPulse, t.Bar.StripTasks!.Foreground);
    });

    // A free task has no row to blink, so a due one is the stopwatch's alone, and the shared pulse has to run for it.
    [Fact]
    public void A_free_due_task_runs_the_pulse() => StaThread.Run(() =>
    {
        using var t = Build();
        Assert.True(t.Tasks.Park("Call back", Maybe<Guid>.None, TimeSpan.FromMinutes(1)).IsSuccess);
        t.Bar.PaintStopwatch();
        Assert.False(t.Bar.AskingAnimated);

        t.Advance(TimeSpan.FromMinutes(2));
        t.Bar.PaintStopwatch();

        Assert.True(t.Bar.AskingAnimated);
    });

    // Narrowing the strip: the date goes first, then the countdown, and only then the clock. The glyph stays for as
    // long as the clock does. Walked down in half-DIP steps from where the date has just gone, so each transition
    // is seen in order rather than reckoned from the thresholds.
    [Fact]
    public void The_countdown_gives_way_after_the_date() => StaThread.Run(() =>
    {
        using var t = Build();
        Assert.True(t.Tasks.Park("Mail", t.Workspace("Sparrow"), TimeSpan.FromMinutes(5)).IsSuccess);
        t.Bar.PaintStopwatch();
        var clock = t.Strip.Children.OfType<Button>().Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Clock");
        var date = ((StackPanel)clock.Content).Children.OfType<TextBlock>().Last();

        t.Strip.Width = 800;
        t.Bar.UpdateLayout();
        Assert.Equal(Visibility.Visible, date.Visibility);
        Assert.True(t.Bar.CountdownShown);

        var (_, others, withDate) = t.Bar.lastClockFit;
        output.WriteLine($"others {others:0.00}, clock with date {withDate:0.00}");
        var width = others + withDate - 0.25;
        t.Strip.Width = width;
        t.Bar.UpdateLayout();
        Assert.Equal(Visibility.Collapsed, date.Visibility);
        Assert.True(t.Bar.CountdownShown);
        Assert.Equal(Visibility.Visible, clock.Visibility);

        var sawCountdownGoFirst = false;
        while (width > 20 && clock.Visibility == Visibility.Visible)
        {
            width -= 0.5;
            t.Strip.Width = width;
            t.Bar.UpdateLayout();
            Assert.Equal(Visibility.Collapsed, date.Visibility);
            // The glyph stays while the clock does.
            if (clock.Visibility == Visibility.Visible) Assert.Equal(Visibility.Visible, t.Bar.StripTasks!.Visibility);
            if (!t.Bar.CountdownShown && clock.Visibility == Visibility.Visible) sawCountdownGoFirst = true;
        }
        output.WriteLine($"clock went at {width:0.00}");

        Assert.True(sawCountdownGoFirst, "the countdown never hid while the clock still showed");
        Assert.Equal(Visibility.Collapsed, clock.Visibility);
        Assert.False(t.Bar.CountdownShown);
    });

    [Fact]
    public void Hidden_strip_item_hides_the_stopwatch() => StaThread.Run(() =>
    {
        using var t = Build();

        Assert.True(t.Harness.Manager.SetHiddenStripItems([StripItem.Tasks]).IsSuccess);
        t.Bar.UpdateLayout();

        Assert.Equal(Visibility.Collapsed, t.Bar.StripTasks!.Visibility);
        Assert.DoesNotContain(t.Bar.StripTasks, t.Strip.Children.OfType<Button>());
    });

    [Fact]
    public void A_running_workspace_row_draws_a_pie() => StaThread.Run(() =>
    {
        using var t = Build();
        var rows = (Panel)t.Bar.FindName("Rows")!;
        Assert.DoesNotContain(Descendants(rows).OfType<FrameworkElement>(), e => Equals(e.Tag, "task-pie"));

        var sparrow = t.Workspace("Sparrow");
        t.Harness.Manager.SetTaskMarks(new HashSet<Guid>(), new Dictionary<Guid, TaskTimer> { [sparrow] = new(Start, TimeSpan.FromMinutes(5)) });
        t.Bar.UpdateLayout();

        var pies = Descendants(rows).OfType<FrameworkElement>().Where(e => Equals(e.Tag, "task-pie")).ToList();
        Assert.Single(pies);
    });

    // The pie empties in place on the clock's tick: its slice shrinks, with no rebuild in between.
    [Fact]
    public void The_pie_empties_on_the_tick() => StaThread.Run(() =>
    {
        using var t = Build();
        var rows = (Panel)t.Bar.FindName("Rows")!;
        var sparrow = t.Workspace("Sparrow");
        t.Harness.Manager.SetTaskMarks(new HashSet<Guid>(), new Dictionary<Guid, TaskTimer> { [sparrow] = new(Start, TimeSpan.FromMinutes(4)) });
        t.Bar.UpdateLayout();
        var pie = (Panel)Descendants(rows).OfType<FrameworkElement>().Single(e => Equals(e.Tag, "task-pie"));
        var slice = pie.Children.OfType<System.Windows.Shapes.Path>().Single();
        var full = slice.Data.GetArea(0.001, System.Windows.Media.ToleranceType.Absolute);

        t.Advance(TimeSpan.FromMinutes(3));
        t.Bar.PaintStopwatch();

        Assert.Same(pie, Descendants(rows).OfType<FrameworkElement>().Single(e => Equals(e.Tag, "task-pie")));
        // Measured at a fine tolerance: GetArea flattens the arc, by about 2% at its default.
        Assert.Equal(full / 4, slice.Data.GetArea(0.001, System.Windows.Media.ToleranceType.Absolute), tolerance: 0.1);
    });

    // Until the task window exists, a click on the stopwatch opens a menu: park the current workspace for one of the
    // four lengths, titled after the window in front or, with none, the workspace itself.
    [Fact]
    public void The_menu_parks_the_current_workspace() => StaThread.Run(() =>
    {
        using var t = Build();
        var menu = t.Bar.TasksMenu();
        var five = menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "5 min"));
        Assert.True(five.IsEnabled);

        five.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        var task = Assert.Single(t.Tasks.Board.Tasks);
        Assert.Equal("GEPHA", task.Title);
        Assert.Equal(t.Workspace("GEPHA"), task.WorkspaceId.Value);
        Assert.Equal(TimeSpan.FromMinutes(5), task.Timer.Value.Length);
    });

    // A workspace that already has a live task parks under that task's title, so the same thread stays one row.
    [Fact]
    public void The_menu_parks_under_the_latest_live_title() => StaThread.Run(() =>
    {
        using var t = Build();
        Assert.True(t.Tasks.Park("Release notes", t.Workspace("GEPHA"), Maybe<TimeSpan>.None).IsSuccess);

        var menu = t.Bar.TasksMenu();
        menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "10 min")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        var task = Assert.Single(t.Tasks.Board.Tasks);
        Assert.Equal("Release notes", task.Title);
        Assert.Equal(TimeSpan.FromMinutes(10), task.Timer.Value.Length);
    });

    // Each live task is listed with its time left; clicking it marks it done.
    [Fact]
    public void The_menu_lists_live_tasks_and_marks_one_done() => StaThread.Run(() =>
    {
        using var t = Build();
        var mail = t.Tasks.Park("Mail", t.Workspace("Sparrow"), TimeSpan.FromMinutes(5)).Value;
        t.Advance(TimeSpan.FromSeconds(28));

        var menu = t.Bar.TasksMenu();
        var item = menu.Items.OfType<MenuItem>().Single(i => i.Header is string h && h.StartsWith("Mail"));
        Assert.Contains("4:32", (string)item.Header);

        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        Assert.True(t.Tasks.Board.Tasks.Single(x => x.Id == mail.Id).DoneAt.HasValue);
    });

    // On a desktop nobody named there is no workspace to park, so the lengths are offered but disabled.
    [Fact]
    public void An_unnamed_desktop_cannot_be_parked() => StaThread.Run(() =>
    {
        var harness = Harness.Build(withUnnamedDesktop: true);
        harness.Desktops.CurrentId = harness.UnnamedDesktop;
        ((System.Reactive.Subjects.Subject<Guid>)harness.Desktops.CurrentChanged).OnNext(harness.UnnamedDesktop);
        var tasks = new TaskService(new MemoryTaskStore(), () => Start);
        var bar = new FloatingBar(harness.Manager) { Left = -32000, Top = -32000, SendShellKeys = _ => { }, PressSpace = () => { } };
        bar.Tasks = tasks;
        bar.ShowBar();
        try
        {
            Assert.True(harness.Manager.CurrentWorkspaceId.HasNoValue);
            var menu = bar.TasksMenu();
            Assert.Contains(menu.Items.OfType<MenuItem>(), i => Equals(i.Header, "Name this desktop to park it"));
            Assert.All(menu.Items.OfType<MenuItem>().Where(i => i.Header is "1 min" or "5 min" or "10 min" or "30 min"), i => Assert.False(i.IsEnabled));
        }
        finally
        {
            bar.Close();
        }
    });

    // The stopwatch's click opens what App hands it once the task window exists; until then, the menu.
    [Fact]
    public void A_click_opens_what_the_app_handed_over() => StaThread.Run(() =>
    {
        using var t = Build();
        var opened = 0;
        t.Bar.OpenTasks = () => opened++;

        t.Bar.StripTasks!.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        Assert.Equal(1, opened);
    });
}
