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

    // windowTitleOf stands in for reading a real window's title; left out, every window reads as untitled, so no test
    // reads whatever the person running the suite has open.
    static TasksBar Build(Func<nint, string>? windowTitleOf = null)
    {
        var harness = Harness.Build();
        var now = Start;
        var tasks = new TaskService(new MemoryTaskStore(), () => now);
        var bar = new FloatingBar(harness.Manager)
        {
            Left = -32000, Top = -32000, SendShellKeys = _ => { }, PressSpace = () => { },
            WindowTitleOf = windowTitleOf ?? (_ => ""),
        };
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

    // A listed task's submenu: Done, Clear timer, then the four lengths (final review I4).
    static MenuItem ListedTask(ContextMenu menu, string title) =>
        menu.Items.OfType<MenuItem>().Single(i => i.Header is string h && h.StartsWith(title, StringComparison.Ordinal));

    static MenuItem Child(MenuItem item, string header) => item.Items.OfType<MenuItem>().Single(i => Equals(i.Header, header));

    static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    // Each live task is listed with its time left, as a submenu whose Done marks it done.
    [Fact]
    public void The_menu_lists_live_tasks_and_marks_one_done() => StaThread.Run(() =>
    {
        using var t = Build();
        var mail = t.Tasks.Park("Mail", t.Workspace("Sparrow"), TimeSpan.FromMinutes(5)).Value;
        t.Advance(TimeSpan.FromSeconds(28));

        var menu = t.Bar.TasksMenu();
        var item = ListedTask(menu, "Mail");
        Assert.Contains("4:32", (string)item.Header);
        Assert.Equal(["Done", "Clear timer", "1 min", "5 min", "10 min", "30 min"],
            item.Items.OfType<MenuItem>().Select(i => (string)i.Header));

        Click(Child(item, "Done"));

        Assert.True(t.Tasks.Board.Tasks.Single(x => x.Id == mail.Id).DoneAt.HasValue);
    });

    // Final review I4: a due timer can be cleared from the menu, which was the only way to answer it there without
    // marking the task done or travelling to its workspace.
    [Fact]
    public void The_menu_clears_a_due_timer() => StaThread.Run(() =>
    {
        using var t = Build();
        var mail = t.Tasks.Park("Mail", t.Workspace("Sparrow"), TimeSpan.FromMinutes(1)).Value;
        t.Advance(TimeSpan.FromMinutes(2));

        var item = ListedTask(t.Bar.TasksMenu(), "Mail");
        Assert.Contains("due", (string)item.Header);
        var clear = Child(item, "Clear timer");
        Assert.True(clear.IsEnabled);

        Click(clear);

        var task = t.Tasks.Board.Tasks.Single(x => x.Id == mail.Id);
        Assert.True(task.Timer.HasNoValue);
        Assert.Equal(TaskState.Open, task.StateAt(t.Clock()));
    });

    // A task without a timer has nothing to clear; a length sets one on it.
    [Fact]
    public void The_menu_sets_a_timer_on_a_listed_task() => StaThread.Run(() =>
    {
        using var t = Build();
        var mail = t.Tasks.Park("Mail", t.Workspace("Sparrow"), Maybe<TimeSpan>.None).Value;

        var item = ListedTask(t.Bar.TasksMenu(), "Mail");
        Assert.False(Child(item, "Clear timer").IsEnabled);

        Click(Child(item, "10 min"));

        Assert.Equal(TimeSpan.FromMinutes(10), t.Tasks.Board.Tasks.Single(x => x.Id == mail.Id).Timer.Value.Length);
    });

    // At most eight tasks are listed; more is the task window's job.
    [Fact]
    public void The_menu_lists_at_most_eight_tasks() => StaThread.Run(() =>
    {
        using var t = Build();
        Enumerable.Range(1, 10).ToList().ForEach(i => t.Tasks.Park($"Task {i}", Maybe<Guid>.None, Maybe<TimeSpan>.None));

        var listed = t.Bar.TasksMenu().Items.OfType<MenuItem>().Count(i => i.Header is string h && h.StartsWith("Task ", StringComparison.Ordinal));

        Assert.Equal(8, listed);
    });

    // With no live task on the workspace, the park takes the title of the window last typed in, when that window is
    // on the workspace being parked.
    [Fact]
    public void The_menu_parks_under_the_foreground_windows_title() => StaThread.Run(() =>
    {
        using var t = Build(hwnd => hwnd == 101 ? "Remote Desktop Manager - prod" : "");
        // 101 is the harness's Remote Desktop Manager window, on GEPHA, the workspace in front.
        t.Bar.NoteForeground(101);

        Click(t.Bar.TasksMenu().Items.OfType<MenuItem>().Single(i => Equals(i.Header, "5 min")));

        Assert.Equal("Remote Desktop Manager - prod", Assert.Single(t.Tasks.Board.Tasks).Title);
    });

    // Final review M3: the window last typed in can be on another desktop (it was focused there before a switch);
    // its title says nothing about this workspace, so the park falls back to the workspace's name.
    [Fact]
    public void A_window_on_another_desktop_does_not_title_the_park() => StaThread.Run(() =>
    {
        using var t = Build(hwnd => hwnd == 202 ? "Beeper" : "");
        // 202 is the harness's Beeper window, on Sparrow; GEPHA is in front.
        t.Bar.NoteForeground(202);

        Click(t.Bar.TasksMenu().Items.OfType<MenuItem>().Single(i => Equals(i.Header, "5 min")));

        Assert.Equal("GEPHA", Assert.Single(t.Tasks.Board.Tasks).Title);
    });

    // Final review M5: the countdown uses tabular numerals, so "4:11" and "4:00" take the same width and the strip
    // does not shift (or re-fit the clock) every second.
    [Fact]
    public void The_countdown_keeps_its_width_from_second_to_second() => StaThread.Run(() =>
    {
        using var t = Build();
        Assert.True(t.Tasks.Park("Mail", t.Workspace("Sparrow"), TimeSpan.FromMinutes(5)).IsSuccess);
        var countdown = ((StackPanel)t.Bar.StripTasks!.Content).Children.OfType<TextBlock>().Single();
        Assert.Equal(FontNumeralAlignment.Tabular, System.Windows.Documents.Typography.GetNumeralAlignment(countdown));
        // Wide enough for everything, so FitClock keeps the countdown on screen.
        t.Strip.Width = 800;

        t.Advance(TimeSpan.FromSeconds(49));
        t.Bar.PaintStopwatch();
        t.Bar.UpdateLayout();
        Assert.Equal("4:11", t.Bar.StopwatchText);
        var ones = countdown.ActualWidth;
        Assert.True(ones > 0, "the countdown is not on screen, so its width proves nothing");

        t.Advance(TimeSpan.FromSeconds(11));
        t.Bar.PaintStopwatch();
        t.Bar.UpdateLayout();
        Assert.Equal("4:00", t.Bar.StopwatchText);

        output.WriteLine($"4:11 {ones:0.00}, 4:00 {countdown.ActualWidth:0.00}");
        Assert.Equal(ones, countdown.ActualWidth, 2);
    });

    // Final review M9: the stopwatch follows the icon size setting, its glyph only; the countdown is text and stays.
    [Fact]
    public void The_stopwatch_glyph_follows_the_icon_size() => StaThread.Run(() =>
    {
        using var t = Build();
        var face = (StackPanel)t.Bar.StripTasks!.Content;
        var glyph = face.Children.OfType<Viewbox>().Single();
        var countdown = face.Children.OfType<TextBlock>().Single();

        Assert.True(t.Harness.Manager.SetIconScale(1.5).IsSuccess);
        t.Bar.UpdateLayout();

        Assert.Equal(1.5, ((System.Windows.Media.ScaleTransform)glyph.LayoutTransform).ScaleX, 3);
        Assert.True(countdown.LayoutTransform is null || countdown.LayoutTransform == System.Windows.Media.Transform.Identity);
        Assert.True(t.Bar.StripTasks.LayoutTransform is null || t.Bar.StripTasks.LayoutTransform == System.Windows.Media.Transform.Identity);
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
