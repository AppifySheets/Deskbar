using System.IO;
using System.Reactive.Subjects;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core.Persistence;
using Deskbar.Core.Tasks;

namespace Deskbar.Windows.Tests;

// The task list at the bottom of the bar (FloatingBar.TaskList.cs; docs/superpowers/specs/2026-11-22-tasks-board-design.md,
// "The bar"). Built over the shared two-workspace harness (GEPHA current, Sparrow beside it; see Harness in
// FloatingBarRebuildTests) with a real TaskService on an in-memory store, so every change goes through the same
// service the App hands the bar. No Claude task can be made from the app yet, so Claude's tasks come from
// TaskService.Create with a session, the way a session's `task create` will make them.
public class FloatingBarTaskListTests
{
    // Keeps nothing: the board in memory is the service's own, and these tests read it there.
    sealed class MemoryStore : ITaskStore
    {
        public Result<TaskBoard> Load() => TaskBoard.Empty;

        public Result Save(TaskBoard board) => Result.Success();
    }

    sealed class ListBar : IDisposable
    {
        public required Harness Harness { get; init; }
        public required FloatingBar Bar { get; init; }
        public required TaskService Tasks { get; init; }
        public required FakeTimers Timers { get; init; }

        public TextBox Box => Inside(List).OfType<TextBox>().Single(b => Equals(b.Tag, "task-add-box"));

        public FrameworkElement Chips => Inside(List).OfType<WrapPanel>().Single(w => Equals(w.Tag, "task-add-chips"));

        public Guid Gepha => Harness.Manager.State.Workspaces.Single(w => w.Name == "GEPHA").Id;

        public FrameworkElement List => (FrameworkElement)Bar.FindName("TaskListLine")!;

        // The task lines on show, in order: the buttons that carry a TaskLine.
        public IReadOnlyList<Button> Lines => Inside(List).OfType<Button>().Where(b => b.Tag is TaskLine).ToList();

        public void Dispose() => Bar.Close();
    }

    static readonly DateTimeOffset Start = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    // The undo window's timers, started by the bar and fired by the test (TaskUndoTimer), so no test waits six seconds.
    sealed class FakeTimers
    {
        sealed class Handle : IDisposable
        {
            public bool Stopped { get; private set; }

            public void Dispose() => Stopped = true;
        }

        readonly List<(TimeSpan After, Action Then, Handle Handle)> started = [];

        public int Running => started.Count(t => !t.Handle.Stopped);

        public IReadOnlyList<TimeSpan> Afters => started.Select(t => t.After).ToList();

        public IDisposable Start(TimeSpan after, Action then)
        {
            var handle = new Handle();
            started.Add((after, then, handle));
            return handle;
        }

        // Every timer still running runs out now.
        public void FireAll() => started.Where(t => !t.Handle.Stopped).ToList().ForEach(t =>
        {
            t.Handle.Dispose();
            t.Then();
        });
    }

    // The window Petre was typing in before he clicked the bar, as the foreground seam reports it. Made up, like the
    // language tests' editor: a test must never read or move the real foreground.
    const nint Editor = 0x5150;

    static readonly DateTime ClickStart = new(2026, 11, 22, 9, 0, 0, DateTimeKind.Utc);

    // Each read of the task clock is a minute later, so every task is raised at its own time and the pile's order is
    // the order the test made them in, newest first. Each read of the click clock is a second later, so a click is
    // never "right after a repaint" unless a test hands in a clock of its own (the click guard's tests do).
    static ListBar Build(Func<Guid, Result>? goTo = null, Action? openWindow = null, TaskService? tasks = null, Func<string, Maybe<nint>>? sessionWindow = null,
        List<nint>? handedBack = null, bool editorAlive = true, Func<DateTime>? clickClock = null, Func<nint>? front = null,
        Func<nint, bool>? onCurrentDesktop = null, Func<DateTimeOffset>? taskNow = null, Action<Guid>? openTask = null,
        Func<TaskItem, Maybe<string>>? goalTitle = null, IObservable<System.Reactive.Unit>? goalChanges = null)
    {
        var harness = Harness.Build();
        var minutes = 0;
        var seconds = 0;
        var timers = new FakeTimers();
        var service = tasks ?? new TaskService(new MemoryStore(), () => Start.AddMinutes(minutes++));
        var bar = new FloatingBar(harness.Manager)
        {
            Left = -32000,
            Top = -32000,
            SendShellKeys = _ => { },
            PressSpace = () => { },
            Tasks = service,
            GoToTask = goTo ?? (_ => Result.Success()),
            OpenTasksWindow = openWindow,
            // A line's single click opens its task in the window (Ruling 54); a test that hands in a goTo probe sees that click
            // too, which is what the click-guard tests watch for. A double-click goes through goTo itself.
            OpenTaskInWindow = openTask ?? (goTo is null ? null : id => goTo(id)),
            SessionWindow = sessionWindow ?? (_ => Maybe<nint>.None),
            ForegroundWindow = front ?? (() => Editor),
            HandForegroundTo = hwnd => handedBack?.Add(hwnd),
            WindowAlive = hwnd => hwnd == Editor && editorAlive,
            OnCurrentDesktop = onCurrentDesktop ?? (_ => true),
            TaskClickClock = clickClock ?? (() => ClickStart.AddSeconds(seconds++)),
            TaskUndoTimer = timers.Start,
            TaskNow = taskNow ?? (() => Start.AddHours(1)),
            // The minute's tick of the ages is driven by the tests (RefreshTaskAges), never by a real timer.
            TaskAgeTimer = (_, _) => System.Reactive.Disposables.Disposable.Empty,
            GoalTitle = goalTitle ?? (_ => Maybe<string>.None),
            GoalChanges = goalChanges,
        };
        bar.ShowBar();
        bar.UpdateLayout();
        return new ListBar { Harness = harness, Bar = bar, Tasks = service, Timers = timers };
    }

    static IEnumerable<object> Inside(object node) =>
        new[] { node }.Concat(node is DependencyObject d ? LogicalTreeHelper.GetChildren(d).Cast<object>().SelectMany(Inside) : []);

    static Button ButtonNamed(object scope, string name) =>
        Inside(scope).OfType<Button>().Single(b => AutomationProperties.GetName(b) == name);

    static bool HasButtonNamed(object scope, string name) =>
        Inside(scope).OfType<Button>().Any(b => AutomationProperties.GetName(b) == name);

    static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    // A click in the text box, the way the bar sees one: the press arrives first and takes the keyboard.
    static void ClickBox(ListBar t) =>
        t.Box.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });

    // Whether a line is a closed one still in its undo window.
    static bool Closing(Button line) => ((TaskLine)line.Tag).Closing;

    // A press of the left button with the click count Windows would give it (2 for the second press of a double
    // click), then the Click it ends in. ClickCount has no public setter, so it is set the way WPF's own input code
    // sets it.
    static void PressAndClick(Button button, int clickCount)
    {
        var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent };
        typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!.SetValue(press, clickCount);
        button.RaiseEvent(press);
        Click(button);
    }

    static void Press(FloatingBar bar, UIElement target, Key key) =>
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(bar), 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });

    // What a line says, its runs joined: the title, then its status line or question.
    static string TextOf(Button line) =>
        string.Concat(Inside(line).OfType<TextBlock>().Where(t => Equals(t.Tag, "task-text"))
            .SelectMany(t => t.Inlines.OfType<Run>()).Select(r => r.Text));

    static Border EdgeOf(Button line) => Inside(line).OfType<Border>().Single(b => Equals(b.Tag, "task-line"));

    static bool HasClaudeTag(Button line) => Inside(line).OfType<FrameworkElement>().Any(e => Equals(e.Tag, "task-claude-tag"));

    static TaskSession Session(string id) => new(id, @"C:\repos\Deskbar", Start, Maybe<DateTimeOffset>.None);

    static readonly Color Orange = Color.FromRgb(0xFF, 0xA7, 0x26);

    [Fact]
    public void Three_lines_show_and_the_rest_wait_behind_more() => StaThread.Run(() =>
    {
        using var t = Build();
        Enumerable.Range(1, 5).ToList().ForEach(i => t.Tasks.AddNote($"Note {i}", Maybe<Guid>.None));

        Assert.True(t.Bar.TaskListShown);
        Assert.Equal(["Note 5", "Note 4", "Note 3"], t.Lines.Select(l => ((TaskLine)l.Tag).Title));
        Assert.Equal("+2 more", t.Bar.TaskListMoreText);
    });

    // Waiting on you sorts first, so hidden waiting tasks only exist when more than three wait; the button says so,
    // in the asking orange, so none is out of sight without a sign.
    [Fact]
    public void Hidden_waiting_tasks_are_counted_in_orange() => StaThread.Run(() =>
    {
        using var t = Build();
        Enumerable.Range(1, 5).ToList().ForEach(i =>
        {
            var made = t.Tasks.Create($"Task {i}", t.Gepha, Session($"s{i}")).Value;
            t.Tasks.Ask(made.Id, $"Question {i}?");
        });

        Assert.Equal(3, t.Lines.Count);
        Assert.Equal("+2 waiting", t.Bar.TaskListMoreText);
        var label = Inside(ButtonNamed(t.List, "More tasks")).OfType<TextBlock>().Single();
        Assert.Equal(Orange, ((SolidColorBrush)label.Foreground).Color);
    });

    [Fact]
    public void A_claude_line_is_marked() => StaThread.Run(() =>
    {
        using var t = Build();
        var task = t.Tasks.Create("Tasks redesign", t.Gepha, Session("s1")).Value;
        t.Tasks.MarkDone(task.Id); // on the bar while it needs him (Ruling 48)
        t.Tasks.SetStatusLine(task.Id, "waiting for CI on #398");
        t.Tasks.AddNote("Dishwasher tablets", Maybe<Guid>.None);

        var claude = t.Lines.Single(l => ((TaskLine)l.Tag).Id == task.Id);
        // Ruling 42 (Petre: "don't say claude"): no CLAUDE tag, the orange edge at the line's start is the mark.
        Assert.False(HasClaudeTag(claude));
        Assert.DoesNotContain(Inside(claude).OfType<TextBlock>(), b => b.Text == "CLAUDE");
        var edge = EdgeOf(claude);
        Assert.Equal(Orange, ((SolidColorBrush)edge.BorderBrush).Color);
        Assert.Equal(3, edge.BorderThickness.Left);
        Assert.Contains("Tasks redesign", TextOf(claude));
        Assert.Contains("waiting for CI on #398", TextOf(claude));

        // A note is plain: no tag, and the same edge width drawn in no colour, so the two kinds line up.
        var note = t.Lines.Single(l => ((TaskLine)l.Tag).Title == "Dishwasher tablets");
        Assert.False(HasClaudeTag(note));
        Assert.Equal(3, EdgeOf(note).BorderThickness.Left);
        Assert.Equal(0, ((SolidColorBrush)EdgeOf(note).BorderBrush).Color.A);

        // Waiting on you: the question takes the status line's place.
        t.Tasks.Ask(task.Id, "OK to restart Deskbar for the live check?");
        claude = t.Lines.Single(l => ((TaskLine)l.Tag).Id == task.Id);
        Assert.Contains("OK to restart Deskbar for the live check?", TextOf(claude));
        Assert.DoesNotContain("waiting for CI", TextOf(claude));
    });

    // The VS Code icon of the session's window comes from the App's lookup by folder; no window, no icon.
    [Fact]
    public void A_claude_line_carries_its_session_window() => StaThread.Run(() =>
    {
        var asked = new List<string>();
        using var t = Build(sessionWindow: folder =>
        {
            asked.Add(folder);
            return Maybe.From((nint)101);
        });
        t.Tasks.MarkDone(t.Tasks.Create("Tasks redesign", t.Gepha, Session("s1")).Value.Id); // Done: a Claude task shows on the bar while it needs him (Ruling 48)
        t.Tasks.AddNote("Dishwasher tablets", Maybe<Guid>.None);

        var claude = t.Lines.Single(l => ((TaskLine)l.Tag).Claudes);
        Assert.Equal((nint)101, ((TaskLine)claude.Tag).Window.Value);
        Assert.DoesNotContain(Inside(claude).OfType<FrameworkElement>(), e => Equals(e.Tag, "task-window-icon"));
        Assert.Contains(@"C:\repos\Deskbar", asked);
        var note = t.Lines.Single(l => !((TaskLine)l.Tag).Claudes);
        Assert.DoesNotContain(Inside(note).OfType<FrameworkElement>(), e => Equals(e.Tag, "task-window-icon"));
    });

    [Fact]
    public void A_note_has_a_tick_box_that_closes_it() => StaThread.Run(() =>
    {
        using var t = Build();
        var note = t.Tasks.AddNote("Dishwasher tablets", Maybe<Guid>.None).Value;

        var line = Assert.Single(t.Lines);
        Assert.False(HasButtonNamed(line, "Close task"));
        Click(ButtonNamed(line, "Tick off"));

        // Closed in the store at once; the line stays for the undo window and then leaves.
        Assert.Equal(TaskStatus.Closed, t.Tasks.Board.Tasks.Single(x => x.Id == note.Id).Status);
        Assert.True(Closing(Assert.Single(t.Lines)));
        t.Timers.FireAll();
        Assert.Empty(t.Lines);
    });

    // Ruling 40 (Petre: "how do i mark claude as done?"): a Claude line has the note's tick box. Ticking it closes the
    // task at once, with the six seconds of Undo; Undo gives back its status and its question exactly (Ruling 31); and
    // the line keeps its Claude look throughout: the orange edge and the session's icon.
    [Fact]
    public void Ticking_a_claude_line_closes_it_and_undo_brings_it_back_as_it_was() => StaThread.Run(() =>
    {
        using var t = Build(sessionWindow: _ => Maybe.From((nint)0x4242));
        var task = t.Tasks.Create("Testing the release", t.Gepha, Session("s1")).Value;
        t.Tasks.Ask(task.Id, "Ship it?");
        var line = t.Lines.Single();
        Assert.Equal(Orange, ((SolidColorBrush)EdgeOf(line).BorderBrush).Color);

        Click(ButtonNamed(line, "Tick off"));

        Assert.Equal(TaskStatus.Closed, t.Tasks.Board.Tasks.Single().Status);
        var closing = t.Lines.Single();
        Assert.True(Closing(closing));
        Assert.Equal(Orange, ((SolidColorBrush)EdgeOf(closing).BorderBrush).Color);
        Click(ButtonNamed(closing, "Undo"));

        var back = t.Tasks.Board.Tasks.Single();
        Assert.Equal(TaskStatus.WaitingOnYou, back.Status);
        Assert.Equal("Ship it?", back.Question.Value);
        var again = t.Lines.Single();
        Assert.False(Closing(again));
        Assert.Equal(Orange, ((SolidColorBrush)EdgeOf(again).BorderBrush).Color);
        Assert.DoesNotContain(Inside(again).OfType<FrameworkElement>(), e => Equals(e.Tag, "task-window-icon"));
        Assert.True(HasButtonNamed(again, "Tick off"));
    });

    // Every Claude line, a Done one included, has the tick box and no close button: all lines look alike.
    [Fact]
    public void A_done_claude_task_closes_from_its_tick_box() => StaThread.Run(() =>
    {
        using var t = Build();
        var working = t.Tasks.Create("Still going", t.Gepha, Session("s1")).Value;
        var done = t.Tasks.Create("Merged", t.Gepha, Session("s2")).Value;
        t.Tasks.MarkDone(done.Id);

        // A Claude task still in progress is not on the bar (Ruling 48); the Done one has the same tick box as a note.
        Assert.DoesNotContain(t.Lines, line => ((TaskLine)line.Tag).Id == working.Id);

        var doneLine = t.Lines.Single(l => ((TaskLine)l.Tag).Id == done.Id);
        Assert.False(HasButtonNamed(doneLine, "Close task"));
        Click(ButtonNamed(doneLine, "Tick off"));

        Assert.Equal(TaskStatus.Closed, t.Tasks.Board.Tasks.Single(x => x.Id == done.Id).Status);
        Assert.True(Closing(t.Lines.Single(l => ((TaskLine)l.Tag).Id == done.Id)));
        t.Timers.FireAll();
        Assert.DoesNotContain(t.Lines, l => ((TaskLine)l.Tag).Id == done.Id);
    });

    [Fact]
    public void A_claude_task_with_no_live_session_says_so() => StaThread.Run(() =>
    {
        using var t = Build();
        var task = t.Tasks.Create("Portal last seen", t.Gepha, Session("s1")).Value;
        t.Tasks.MarkDone(task.Id); // on the bar while it needs him (Ruling 48)
        t.Tasks.SetStatusLine(task.Id, "migration ready");
        t.Tasks.SessionEnded("s1");

        var line = Assert.Single(t.Lines);
        Assert.Equal("migration ready · no live session", ((TaskLine)line.Tag).Detail);
        Assert.Contains("migration ready · no live session", TextOf(line));
    });

    [Fact]
    public void Add_puts_a_note_on_the_current_workspace() => StaThread.Run(() =>
    {
        using var t = Build();
        var box = () => Inside(t.List).OfType<TextBox>().Single(b => Equals(b.Tag, "task-add-box"));
        Assert.False(t.Bar.TaskListAdding);

        // The box is there before anything is pressed. A click in it: the current workspace's chip is the one ticked.
        Assert.Equal(Visibility.Visible, box().Visibility);
        ClickBox(t);
        Assert.True(t.Bar.TaskListAdding);
        Assert.Equal(t.Gepha, t.Bar.TaskAddTarget.Value);
        box().Text = "Call the accountant";
        Press(t.Bar, box(), Key.Enter);

        var first = Assert.Single(t.Tasks.Board.Tasks);
        Assert.Equal("Call the accountant", first.Title);
        Assert.Equal(t.Gepha, first.WorkspaceId.Value);
        Assert.False(t.Bar.TaskListAdding);

        // "none" makes it a free note.
        ClickBox(t);
        box().Text = "Dishwasher tablets";
        PickTarget(t, "none");
        Press(t.Bar, box(), Key.Enter);

        var free = t.Tasks.Board.Tasks.Single(x => x.Title == "Dishwasher tablets");
        Assert.True(free.WorkspaceId.HasNoValue);
        Assert.Equal(TaskStatus.Backlog, free.Status);
        Assert.False(free.IsClaudes);

        // Esc clears what was typed and adds nothing.
        ClickBox(t);
        box().Text = "Never mind";
        Press(t.Bar, box(), Key.Escape);

        Assert.Equal(2, t.Tasks.Board.Tasks.Count);
        Assert.False(t.Bar.TaskListAdding);
        Assert.Equal("", box().Text);
    });

    [Fact]
    // RULING 54 (Petre: "clicking a task should open that task; double clicking takes me to the workspace and window").
    // A single click on a line opens the task in the tasks window and goes nowhere; a double-click goes: to the
    // workspace of a note, or the session's window of a Claude task. A note on no workspace has nowhere to go.
    public void Clicking_a_line_opens_its_task_and_a_double_click_goes_to_its_workspace() => StaThread.Run(() =>
    {
        var went = new List<Guid>();
        var opened = new List<Guid>();
        using var t = Build(openTask: opened.Add, goTo: id =>
        {
            went.Add(id);
            return Result.Success();
        });
        var errand = t.Tasks.AddNote("Dishwasher tablets", Maybe<Guid>.None).Value;
        var onGepha = t.Tasks.AddNote("Review the migration", t.Gepha).Value;

        Click(t.Lines.Single(l => ((TaskLine)l.Tag).Id == onGepha.Id));
        Assert.Equal([onGepha.Id], opened);
        Assert.Empty(went);

        PressAndClick(t.Lines.Single(l => ((TaskLine)l.Tag).Id == onGepha.Id), clickCount: 2);
        PressAndClick(t.Lines.Single(l => ((TaskLine)l.Tag).Id == errand.Id), clickCount: 2);

        // The note on no workspace has nowhere to go, so its double-click does nothing.
        Assert.Equal([onGepha.Id], went);
    });

    [Fact]
    public void More_shows_everything_until_the_window_exists() => StaThread.Run(() =>
    {
        using (var t = Build())
        {
            Enumerable.Range(1, 5).ToList().ForEach(i => t.Tasks.AddNote($"Note {i}", Maybe<Guid>.None));

            Click(ButtonNamed(t.List, "More tasks"));
            Assert.Equal(5, t.Lines.Count);

            Click(ButtonNamed(t.List, "More tasks"));
            Assert.Equal(3, t.Lines.Count);
            Assert.Equal("+2 more", t.Bar.TaskListMoreText);
        }

        // Once the App has a window to open, the button opens it and the list stays at three.
        var opened = 0;
        using (var t = Build(openWindow: () => opened++))
        {
            Enumerable.Range(1, 5).ToList().ForEach(i => t.Tasks.AddNote($"Note {i}", Maybe<Guid>.None));

            Click(ButtonNamed(t.List, "More tasks"));
            Assert.Equal(1, opened);
            Assert.Equal(3, t.Lines.Count);
        }
    });

    // With the window there, the line under the list is always there, since the window is where the closed tasks and
    // the comments are (Petre: "can i see task list in deskbar? all of them, new and old"): "All tasks" when nothing is
    // out of sight, even with no open task at all.
    [Fact]
    public void With_the_window_the_list_always_offers_all_tasks() => StaThread.Run(() =>
    {
        var opened = 0;
        using var t = Build(openWindow: () => opened++);
        Assert.Equal("All tasks", t.Bar.TaskListMoreText);

        t.Tasks.AddNote("One", Maybe<Guid>.None);
        Assert.Equal("All tasks", t.Bar.TaskListMoreText);

        Click(ButtonNamed(t.List, "More tasks"));
        Assert.Equal(1, opened);
    });

    [Fact]
    public void The_list_hides_with_its_strip_item() => StaThread.Run(() =>
    {
        using var t = Build();
        Assert.True(t.Bar.TaskListShown);

        t.Harness.Manager.SetHiddenStripItems([StripItem.Tasks]);
        Assert.False(t.Bar.TaskListShown);

        t.Harness.Manager.SetHiddenStripItems([]);
        Assert.True(t.Bar.TaskListShown);
    });

    // A store whose load fails, for the failed-load line without a file on disk.
    sealed class UnreadableStore : ITaskStore
    {
        public Result<TaskBoard> Load() => Result.Failure<TaskBoard>("Could not read tasks.json: bad");

        public Result Save(TaskBoard board) => Result.Success();
    }

    // Review minor 3 of 187cf4a: a change of text size moves every line (each grows or shrinks), and so does the
    // failed-load line going away above them, although neither changes which tasks are on show. Each restarts the
    // click guard's half second, as a change of the lines themselves does.
    [Fact]
    public void A_text_size_change_restarts_the_click_guard() => StaThread.Run(() =>
    {
        var now = ClickStart;
        var went = new List<Guid>();
        using var t = Build(clickClock: () => now, goTo: id =>
        {
            went.Add(id);
            return Result.Success();
        });
        Enumerable.Range(1, 3).ToList().ForEach(i => t.Tasks.AddNote($"Note {i}", t.Gepha));
        now = now.AddSeconds(1);

        Assert.True(t.Harness.Manager.SetBarTextOffset(3).IsSuccess);
        t.Bar.RequestRebuild();
        Click(t.Lines[0]);
        Assert.Empty(went);

        now = now.AddSeconds(1);
        Click(t.Lines[0]);
        Assert.Single(went);
    });

    [Fact]
    public void The_load_problem_line_going_restarts_the_click_guard() => StaThread.Run(() =>
    {
        var now = ClickStart;
        var went = new List<Guid>();
        var service = new TaskService(new UnreadableStore(), () => Start);
        service.Load();
        using var t = Build(tasks: service, clickClock: () => now, goTo: id =>
        {
            went.Add(id);
            return Result.Success();
        });
        Enumerable.Range(1, 3).ToList().ForEach(i => service.AddNote($"Note {i}", t.Gepha));
        now = now.AddSeconds(1);

        Click(ButtonNamed(t.List, "Dismiss"));
        Click(t.Lines[0]);
        Assert.Empty(went);

        now = now.AddSeconds(1);
        Click(t.Lines[0]);
        Assert.Single(went);
    });

    // A tasks.json that could not be read must not pass for an empty list. The real store is used, so the line's
    // promise (a .bak kept beside tasks.json) is checked against what the store really did.
    [Fact]
    public void A_failed_load_shows_one_line_with_a_dismiss() => StaThread.Run(() =>
    {
        var folder = Path.Combine(Path.GetTempPath(), "deskbar-tasklist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "tasks.json"), "{ not json");
            var service = new TaskService(new JsonTaskStore(folder), () => Start);
            service.Load();
            var reason = service.LoadProblem.Value;

            using var t = Build(tasks: service);
            var problem = Inside(t.List).OfType<TextBlock>().First(b => Equals(b.Tag, "task-load-problem"));
            Assert.StartsWith("Tasks could not be read.", problem.Text);
            Assert.Contains(".bak", problem.Text);
            Assert.Equal(reason, problem.ToolTip);

            Click(ButtonNamed(t.List, "Dismiss"));
            Assert.True(service.LoadProblem.HasNoValue);
            Assert.DoesNotContain(Inside(t.List).OfType<TextBlock>(), b => Equals(b.Tag, "task-load-problem"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    });

    // FIX ROUND 1, Important 1: a paint waits under the same conditions as the bar's rebuild, a press or a drag anywhere
    // on the bar and not only on the list, because a paint that adds a line changes the bar's height and moves the
    // rows under a bottom-anchored bar's press. A row being dragged in arrange mode holds the paint; the cancel serves
    // the deferred rebuild, which paints the list. The press is synthetic, so the bar gets a mouse that is held down and
    // captured (ArrangeMouse.Held, as FloatingBarArrangeTests.Held explains): with the runner's real mouse, whose button
    // is up, the press was called off before the paint could wait on it, and the test passed locally but failed on CI.
    [Fact]
    public void A_paint_waits_while_a_row_is_being_dragged() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Bar.ArrangeMouseInput = ArrangeMouse.Held;
        Assert.True(t.Bar.SetArranging(true).IsSuccess);
        t.Bar.UpdateLayout();
        var rows = (Panel)t.Bar.FindName("Rows")!;
        var sparrow = rows.Children.OfType<Border>().Select(b => b.Child).OfType<Grid>()
            .Single(row => Inside(row).OfType<TextBlock>().Any(text => text.Text == "Sparrow"));
        sparrow.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
        sparrow.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseDownEvent });

        t.Tasks.AddNote("Dishwasher tablets", Maybe<Guid>.None);
        Assert.Empty(t.Lines);

        t.Bar.CancelArrangeDrag("the test is over");
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        Assert.Single(t.Lines);
    });

    // Ruling 9, replacing Ruling 5's draft: the box is the draft. The bar losing the foreground while a note is typed
    // lets the box go (the bar may fade again, nothing is handed back) and leaves the text and its chip where they are,
    // for a later click in the box.
    [Fact]
    public void Clicking_away_leaves_the_text_in_the_box() => StaThread.Run(() =>
    {
        var handed = new List<nint>();
        using var t = Build(handedBack: handed);

        ClickBox(t);
        t.Box.Text = "Half a thought";
        PickTarget(t, "none");
        Assert.True(t.Bar.HoldsFullStrength);

        // Window.OnDeactivated is what WPF calls when another window takes the foreground.
        typeof(Window).GetMethod("OnDeactivated", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(t.Bar, [EventArgs.Empty]);

        Assert.False(t.Bar.TaskListAdding);
        Assert.False(t.Bar.HoldsFullStrength);
        Assert.Empty(handed);
        Assert.Empty(t.Tasks.Board.Tasks);
        Assert.Equal("Half a thought", t.Box.Text);
        Assert.True(t.Bar.TaskAddTarget.HasNoValue);

        ClickBox(t);
        Assert.True(t.Bar.TaskListAdding);
        Assert.Equal("Half a thought", t.Box.Text);
        Assert.True(t.Bar.TaskAddTarget.HasNoValue);

        // Enter adds it, and the box is empty again with the current workspace's chip.
        Press(t.Bar, t.Box, Key.Enter);
        Assert.True(Assert.Single(t.Tasks.Board.Tasks).WorkspaceId.HasNoValue);
        Assert.Equal("", t.Box.Text);
        Assert.Equal(t.Gepha, t.Bar.TaskAddTarget.Value);
    });

    // Text left in the box keeps the chips on show, so after clicking away a chip can be the first thing pressed. That
    // press takes the keyboard as a click in the box does: the bar holds full strength, and Esc hands the keyboard back
    // to the window Petre was typing in.
    [Fact]
    public void Picking_a_chip_with_text_in_the_box_takes_the_keyboard() => StaThread.Run(() =>
    {
        var handed = new List<nint>();
        using var t = Build(handedBack: handed);
        ClickBox(t);
        t.Box.Text = "Half a thought";
        typeof(Window).GetMethod("OnDeactivated", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(t.Bar, [EventArgs.Empty]);
        Assert.False(t.Bar.TaskListAdding);
        Assert.Equal(Visibility.Visible, t.Chips.Visibility);

        PickTarget(t, "none");

        Assert.True(t.Bar.TaskListAdding);
        Assert.True(t.Bar.HoldsFullStrength);
        Assert.True(t.Bar.TaskAddTarget.HasNoValue);
        Assert.Equal("Half a thought", t.Box.Text);
        Press(t.Bar, t.Box, Key.Escape);
        Assert.Equal([Editor], handed);
    });

    // Ruling 12 (Petre: "present always"): the chips are under the box all the time, idle or typing, empty or not, so
    // the list never jumps. The bar stays at full strength while the box has the keyboard, not merely while it holds
    // text.
    [Fact]
    public void The_chips_are_always_under_the_box() => StaThread.Run(() =>
    {
        using var t = Build();
        Assert.Equal(Visibility.Visible, t.Chips.Visibility);

        ClickBox(t);
        Assert.Equal(Visibility.Visible, t.Chips.Visibility);
        Press(t.Bar, t.Box, Key.Escape);
        Assert.Equal(Visibility.Visible, t.Chips.Visibility);

        ClickBox(t);
        t.Box.Text = "Later";
        typeof(Window).GetMethod("OnDeactivated", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(t.Bar, [EventArgs.Empty]);
        Assert.Equal(Visibility.Visible, t.Chips.Visibility);
        Assert.False(t.Bar.HoldsFullStrength);

        t.Box.Text = "";
        Assert.Equal(Visibility.Visible, t.Chips.Visibility);
    });

    // A chip pressed on an idle, empty box starts typing, as a click in the box does, with that chip chosen.
    [Fact]
    public void A_chip_on_an_idle_box_starts_typing() => StaThread.Run(() =>
    {
        using var t = Build();
        Assert.False(t.Bar.TaskListAdding);

        PickTarget(t, "none");

        Assert.True(t.Bar.TaskListAdding);
        Assert.True(t.Bar.HoldsFullStrength);
        Assert.True(t.Bar.TaskAddTarget.HasNoValue);
    });

    // The empty box says where a note would go, and follows a desktop switch; on an unnamed desktop it says "Add…".
    // While the box is idle and empty the chosen chip follows the workspace in front too; a chip picked for text that
    // is still in the box stays.
    [Fact]
    public void The_placeholder_and_the_chip_follow_the_workspace_in_front() => StaThread.Run(() =>
    {
        using var t = Build();
        Assert.Equal("Add to GEPHA…", t.Bar.TaskAddPlaceholder);
        Assert.Equal(t.Gepha, t.Bar.TaskAddTarget.Value);
        var sparrowWorkspace = t.Harness.Manager.State.Workspaces.Single(w => w.Name == "Sparrow");
        var gephaDesktop = t.Harness.Manager.State.Workspaces.Single(w => w.Name == "GEPHA").DesktopId!.Value;
        void SwitchTo(Guid desktop)
        {
            t.Harness.Desktops.CurrentId = desktop;
            ((Subject<Guid>)t.Harness.Desktops.CurrentChanged).OnNext(desktop);
            t.Bar.RequestRebuild();
        }

        SwitchTo(sparrowWorkspace.DesktopId!.Value);
        Assert.Equal("Add to Sparrow…", t.Bar.TaskAddPlaceholder);
        Assert.Equal(sparrowWorkspace.Id, t.Bar.TaskAddTarget.Value);
        Assert.Equal("Add…", FloatingBar.PlaceholderFor(Maybe<string>.None));

        // Text held with "none" picked: a switch leaves the choice alone.
        ClickBox(t);
        t.Box.Text = "Dishwasher tablets";
        PickTarget(t, "none");
        typeof(Window).GetMethod("OnDeactivated", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(t.Bar, [EventArgs.Empty]);
        SwitchTo(gephaDesktop);
        Assert.True(t.Bar.TaskAddTarget.HasNoValue);
    });

    // RULING 45 (Petre, of the add box's chip row, "on [Deskbar] [none] [other…]": "those buttons could be only one, or
    // two - selected or nothing"). The row is "on" and ONE chip, the chosen target, with a small arrow: the current
    // workspace's by default, or "none". Its press opens the compact workspace menu, none first; a pick changes the chip.
    const string AddChipName = "Add to workspace";

    static Button AddChip(ListBar t) => ButtonNamed(t.Chips, AddChipName);

    // The chip's own words: the first text in it (the arrow is the second).
    static string ChipText(ListBar t) => Inside(AddChip(t)).OfType<TextBlock>().First().Text;

    // Presses the chip as a click does and picks `name` from the menu it opens, then lets the menu go.
    static void PickTarget(ListBar t, string name)
    {
        Click(AddChip(t));
        var menu = t.Bar.TaskAddMenu!;
        Click(WorkspaceChipMenu.Chips(menu).Single(c => AutomationProperties.GetName(c) == name));
        menu.IsOpen = false;
    }

    [Fact]
    public void The_chip_row_is_on_and_one_chip_naming_the_current_workspace() => StaThread.Run(() =>
    {
        using var t = Build();

        // One chip, no "none" or "other…" beside it; the toggles are not chips.
        var chips = Inside(t.Chips).OfType<Button>().Where(b => b.Content is Border { Tag: "task-chip" }).ToList();
        Assert.Single(chips);
        Assert.Equal("GEPHA", ChipText(t));
        Assert.Single(Inside(t.Chips).OfType<TextBlock>(), b => b.Text == "on");
        Assert.Contains("▾", Inside(AddChip(t)).OfType<TextBlock>().Select(b => b.Text));
        Assert.False(HasButtonNamed(t.List, "none"));
        Assert.False(HasButtonNamed(t.List, "other…"));
        Assert.Equal("Add to GEPHA…", t.Bar.TaskAddPlaceholder);
    });

    // A click routed to the chip opens the menu (it opens from the chip button's own press, never from a second Click
    // handler: a handled click does not reach one, which broke the tasks window's dropdown on 2026-11-23). The menu
    // lists none first, then every workspace, the chosen one ticked.
    [Fact]
    public void A_click_on_the_chip_opens_the_workspace_menu() => StaThread.Run(() =>
    {
        using var t = Build();

        Click(AddChip(t));

        var menu = Assert.IsAssignableFrom<ContextMenu>(t.Bar.TaskAddMenu);
        Assert.True(menu.IsOpen);
        var items = WorkspaceChipMenu.Chips(menu);
        Assert.Equal(["none", "GEPHA", "Sparrow"], items.Select(i => AutomationProperties.GetName(i)));
        Assert.Equal(["GEPHA"], items.Where(i => AutomationProperties.GetItemStatus(i) == WorkspaceChipMenu.CurrentStatus)
            .Select(i => AutomationProperties.GetName(i)));
        menu.IsOpen = false;
    });

    // Petre's earlier live check, still true: the box names the chosen target ("none" must not leave "Add to GEPHA…").
    [Fact]
    public void Picking_none_changes_the_chip_and_the_placeholder() => StaThread.Run(() =>
    {
        using var t = Build();
        Assert.Equal("GEPHA", ChipText(t));
        Assert.Equal("Add to GEPHA…", t.Bar.TaskAddPlaceholder);

        PickTarget(t, "none");

        Assert.Equal("none", ChipText(t));
        Assert.Equal("Add…", t.Bar.TaskAddPlaceholder);
        Assert.True(t.Bar.TaskAddTarget.HasNoValue);

        PickTarget(t, "GEPHA");
        Assert.Equal("GEPHA", ChipText(t));
        Assert.Equal("Add to GEPHA…", t.Bar.TaskAddPlaceholder);
    });

    [Fact]
    public void Picking_another_workspace_changes_the_chip_and_the_placeholder() => StaThread.Run(() =>
    {
        using var t = Build();
        ClickBox(t);

        PickTarget(t, "Sparrow");

        Assert.Equal("Sparrow", ChipText(t));
        Assert.Equal("Add to Sparrow…", t.Bar.TaskAddPlaceholder);
    });

    // A note added lands where the chip says.
    [Fact]
    public void A_note_lands_where_the_chip_says() => StaThread.Run(() =>
    {
        using var t = Build();
        var sparrow = t.Harness.Manager.State.Workspaces.Single(w => w.Name == "Sparrow").Id;

        PickTarget(t, "Sparrow");
        t.Box.Text = "Water the plants";
        Press(t.Bar, t.Box, Key.Enter);
        Assert.Equal(sparrow, t.Tasks.Board.Tasks.Single(x => x.Title == "Water the plants").WorkspaceId.Value);
        Assert.Equal("GEPHA", ChipText(t));

        PickTarget(t, "none");
        t.Box.Text = "Call the plumber";
        Press(t.Bar, t.Box, Key.Enter);
        Assert.True(t.Tasks.Board.Tasks.Single(x => x.Title == "Call the plumber").WorkspaceId.HasNoValue);
    });

    // FIX ROUND 1, Important 2: after Enter or Esc the keyboard goes back to the window Petre was typing in, so his next
    // keystrokes do not land in a bar with nothing to type into; never to a window that has closed meanwhile.
    [Fact]
    public void Enter_and_Esc_hand_the_keyboard_back() => StaThread.Run(() =>
    {
        var handed = new List<nint>();
        using (var t = Build(handedBack: handed))
        {
            var box = () => Inside(t.List).OfType<TextBox>().Single(b => Equals(b.Tag, "task-add-box"));

            ClickBox(t);
            box().Text = "Call the accountant";
            Press(t.Bar, box(), Key.Enter);
            Assert.Equal([Editor], handed);

            ClickBox(t);
            Press(t.Bar, box(), Key.Escape);
            Assert.Equal([Editor, Editor], handed);
        }

        handed.Clear();
        using (var t = Build(handedBack: handed, editorAlive: false))
        {
            ClickBox(t);
            Press(t.Bar, Inside(t.List).OfType<TextBox>().Single(b => Equals(b.Tag, "task-add-box")), Key.Escape);
            Assert.Empty(handed);
        }
    });

    // PR A FIX WAVE, Critical 1: on a bar whose width SizeToContent still owns (a floating bar never resized, a new
    // install's), a line wider than the bar made TakesNoWidth measure at no width and arrange at the real one, the
    // child's size flipped between the two, and layout never settled. A 120-character title must leave the layout
    // valid and the bar as wide as it was.
    [Fact]
    public void A_long_title_settles_the_layout_and_does_not_widen_the_bar() => StaThread.Run(() =>
    {
        using var t = Build();
        Assert.True(t.Bar.SizeToContent.HasFlag(SizeToContent.Width));
        var width = t.Bar.ActualWidth;

        t.Tasks.AddNote(string.Concat(Enumerable.Repeat("Check the Store certification email ", 4))[..120], Maybe<Guid>.None);
        t.Bar.UpdateLayout();

        var holder = ((Border)t.List).Child;
        Assert.True(t.List.IsMeasureValid && t.List.IsArrangeValid);
        Assert.True(holder.IsMeasureValid && holder.IsArrangeValid);
        Assert.InRange(t.Bar.ActualWidth, 0, width + 1);
    });

    // PR A FIX WAVE, Important 2: a closed note leaves the list and the next line slides under the pointer, so a second
    // click must not close that one too. A press that is the second of a double click, or a click within half a
    // second of a repaint that changed the lines, is ignored on a line, its tick, its close button and on "+N".
    [Fact]
    public void A_double_click_or_a_click_right_after_a_repaint_is_ignored() => StaThread.Run(() =>
    {
        var now = ClickStart;
        var went = new List<Guid>();
        using var t = Build(clickClock: () => now, goTo: id =>
        {
            went.Add(id);
            return Result.Success();
        });
        Enumerable.Range(1, 5).ToList().ForEach(i => t.Tasks.AddNote($"Note {i}", t.Gepha));

        // Right after the repaint that drew them: nothing takes the click.
        Click(ButtonNamed(t.Lines[0], "Tick off"));
        Click(t.Lines[0]);
        Click(ButtonNamed(t.List, "More tasks"));
        Assert.Equal(5, t.Tasks.Board.Pile().Count);
        Assert.Empty(went);
        Assert.Equal(3, t.Lines.Count);

        // Half a second on, a single click works and closes Note 5...
        now = now.AddMilliseconds(600);
        PressAndClick(ButtonNamed(t.Lines[0], "Tick off"), clickCount: 1);
        Assert.Equal(4, t.Tasks.Board.Pile().Count);

        // ...whose line stays where it was with the Undo sign in the tick's place, so the second press of the double click lands
        // on Undo rather than on Note 4's tick, and is dropped there too: the note stays closed and Note 4 stays open.
        now = now.AddMilliseconds(600);
        Assert.True(Closing(t.Lines[0]));
        PressAndClick(ButtonNamed(t.Lines[0], "Undo"), clickCount: 2);
        PressAndClick(ButtonNamed(t.Lines[1], "Tick off"), clickCount: 2);
        PressAndClick(ButtonNamed(t.List, "More tasks"), clickCount: 2);
        Assert.Equal(4, t.Tasks.Board.Pile().Count);
        Assert.Empty(went);
        Assert.Equal(3, t.Lines.Count);
    });

    // The tick and the close button sit inside a line, which is itself a button; their click must never also go to the
    // task.
    [Fact]
    public void A_tick_or_a_close_never_goes_to_the_task() => StaThread.Run(() =>
    {
        var went = new List<Guid>();
        using var t = Build(goTo: id =>
        {
            went.Add(id);
            return Result.Success();
        });
        t.Tasks.AddNote("On GEPHA", t.Gepha);
        var done = t.Tasks.Create("Merged", t.Gepha, Session("s1")).Value;
        t.Tasks.MarkDone(done.Id);

        Click(ButtonNamed(t.Lines.Single(l => ((TaskLine)l.Tag).Id == done.Id), "Tick off"));
        Click(ButtonNamed(t.Lines.Single(l => !Closing(l)), "Tick off"));

        Assert.All(t.Lines, line => Assert.True(Closing(line)));
        Assert.Empty(t.Tasks.Board.Pile());
        Assert.Empty(went);
    });

    // PR A FIX WAVE, Important 3: expanded in place, the list shows at most eight lines and scrolls the rest, so dozens
    // of migrated notes cannot push the workspace rows off a docked bar. Folded, it is the three lines as before.
    [Fact]
    public void The_expanded_list_shows_at_most_eight_lines_and_scrolls() => StaThread.Run(() =>
    {
        using var t = Build();
        Enumerable.Range(1, 12).ToList().ForEach(i => t.Tasks.AddNote($"Note {i}", Maybe<Guid>.None));
        var viewer = Inside(t.List).OfType<ScrollViewer>().Single(v => Equals(v.Tag, "task-lines"));
        Assert.True(double.IsPositiveInfinity(viewer.MaxHeight));

        Click(ButtonNamed(t.List, "More tasks"));
        t.Bar.UpdateLayout();

        Assert.Equal(12, t.Lines.Count);
        var eight = t.Lines.Take(8).Sum(l => l.ActualHeight + l.Margin.Top + l.Margin.Bottom);
        Assert.Equal(eight, viewer.ViewportHeight, 1);
        Assert.True(viewer.ExtentHeight > viewer.ViewportHeight + 1, $"the rest scrolls: extent {viewer.ExtentHeight}, viewport {viewer.ViewportHeight}");

        Click(ButtonNamed(t.List, "More tasks"));
        t.Bar.UpdateLayout();
        Assert.Equal(3, t.Lines.Count);
        Assert.True(double.IsPositiveInfinity(viewer.MaxHeight));
    });

    // PR A FIX WAVE, Minor 7: a long workspace name is trimmed in a line's chip and in the add box's chips, so it cannot
    // squeeze the title away or push "other…" out of the row.
    [Fact]
    public void A_long_workspace_name_is_trimmed_in_the_chips() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Tasks.AddNote("Review the migration", t.Gepha);
        ClickBox(t);

        var labels = Inside(t.List).OfType<Border>().Where(b => Equals(b.Tag, "task-workspace") || Equals(b.Tag, "task-chip"))
            // The add chip holds its name and an arrow (Ruling 45): the name is the first text in it.
            .Select(b => b.Child as TextBlock ?? Inside(b).OfType<TextBlock>().First()).ToList();
        Assert.True(labels.Count >= 2);
        Assert.All(labels, label =>
        {
            Assert.True(label.MaxWidth < 200, $"'{label.Text}' has a ceiling");
            Assert.Equal(TextTrimming.CharacterEllipsis, label.TextTrimming);
        });
    });

    // PR A FIX WAVE, Minor 8: the keyboard goes back only to a window on the desktop in front. The window in front at
    // "+ Add" is the first choice; when that was the bar itself, the last window typed in, but only while it is on the
    // current desktop. A window on another desktop is refused either way, since giving it the foreground would switch
    // desktops under Petre.
    [Fact]
    public void The_keyboard_goes_back_only_to_a_window_on_this_desktop() => StaThread.Run(() =>
    {
        // The remembered window (the bar was in front at "+ Add") is on another desktop: refused.
        var handed = new List<nint>();
        nint inFront = Editor;
        using (var t = Build(handedBack: handed, front: () => inFront, onCurrentDesktop: _ => false))
        {
            t.Bar.NoteForeground(Editor);
            inFront = new System.Windows.Interop.WindowInteropHelper(t.Bar).Handle;
            ClickBox(t);
            Press(t.Bar, Inside(t.List).OfType<TextBox>().Single(b => Equals(b.Tag, "task-add-box")), Key.Escape);
            Assert.Empty(handed);
        }

        // The window in front at "+ Add", whose desktop is no longer the one in front by the time of Esc: refused.
        var here = true;
        using (var t = Build(handedBack: handed, onCurrentDesktop: _ => here))
        {
            ClickBox(t);
            here = false;
            Press(t.Bar, Inside(t.List).OfType<TextBox>().Single(b => Equals(b.Tag, "task-add-box")), Key.Escape);
            Assert.Empty(handed);
        }

        // And the remembered window, on this desktop, is the one handed back.
        inFront = Editor;
        using (var t = Build(handedBack: handed, front: () => inFront, onCurrentDesktop: _ => true))
        {
            t.Bar.NoteForeground(Editor);
            inFront = new System.Windows.Interop.WindowInteropHelper(t.Bar).Handle;
            ClickBox(t);
            Press(t.Bar, Inside(t.List).OfType<TextBox>().Single(b => Equals(b.Tag, "task-add-box")), Key.Escape);
            Assert.Equal([Editor], handed);
        }
    });

    // LIVE CHECK (Ruling 10, "when i click on done, there should be undo"): a tick closes the note in the store at once,
    // and its line stays where it was for the undo window, struck through, with Undo where the tick was.
    [Fact]
    public void A_tick_leaves_a_struck_through_line_with_undo() => StaThread.Run(() =>
    {
        using var t = Build();
        var first = t.Tasks.AddNote("First", Maybe<Guid>.None).Value;
        var second = t.Tasks.AddNote("Second", Maybe<Guid>.None).Value;

        Click(ButtonNamed(t.Lines[0], "Tick off"));

        Assert.Equal(TaskStatus.Closed, t.Tasks.Board.Tasks.Single(x => x.Id == second.Id).Status);
        Assert.Equal([second.Id, first.Id], t.Lines.Select(l => ((TaskLine)l.Tag).Id));
        var closing = t.Lines[0];
        Assert.True(Closing(closing));
        Assert.True(HasButtonNamed(closing, "Undo"));
        Assert.False(HasButtonNamed(closing, "Tick off"));
        var text = Inside(closing).OfType<TextBlock>().Single(b => Equals(b.Tag, "task-text"));
        Assert.Equal(TextDecorations.Strikethrough, text.TextDecorations);
        Assert.Equal([FloatingBar.UndoWindow], t.Timers.Afters);
        Assert.Equal(TimeSpan.FromSeconds(6), FloatingBar.UndoWindow);
    });

    [Fact]
    public void Undo_reopens_the_note_and_restores_its_line() => StaThread.Run(() =>
    {
        using var t = Build();
        var note = t.Tasks.AddNote("Dishwasher tablets", t.Gepha).Value;
        Click(ButtonNamed(t.Lines[0], "Tick off"));

        Click(ButtonNamed(t.Lines[0], "Undo"));

        Assert.Equal(TaskStatus.Backlog, t.Tasks.Board.Tasks.Single(x => x.Id == note.Id).Status);
        var line = Assert.Single(t.Lines);
        Assert.False(Closing(line));
        Assert.True(HasButtonNamed(line, "Tick off"));
        Assert.Equal(0, t.Timers.Running);
    });

    [Fact]
    public void After_six_seconds_the_closed_line_leaves() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Tasks.AddNote("First", Maybe<Guid>.None);
        t.Tasks.AddNote("Second", Maybe<Guid>.None);
        Click(ButtonNamed(t.Lines[0], "Tick off"));
        Assert.Equal(2, t.Lines.Count);

        t.Timers.FireAll();

        Assert.Equal(["First"], t.Lines.Select(l => ((TaskLine)l.Tag).Title));
        Assert.False(Closing(t.Lines[0]));
    });

    // Two ticks in a row: both lines stay in their places, each with its own Undo and its own timer, so nothing moves up
    // under the pointer; and "+N more" counts only the open tasks out of sight.
    [Fact]
    public void Two_ticks_in_a_row_keep_both_lines_in_place() => StaThread.Run(() =>
    {
        using var t = Build();
        Enumerable.Range(1, 4).ToList().ForEach(i => t.Tasks.AddNote($"Note {i}", Maybe<Guid>.None));
        var before = t.Lines.Select(l => ((TaskLine)l.Tag).Id).ToList();

        Click(ButtonNamed(t.Lines[0], "Tick off"));
        Click(ButtonNamed(t.Lines[1], "Tick off"));

        Assert.Equal(before, t.Lines.Select(l => ((TaskLine)l.Tag).Id));
        Assert.True(Closing(t.Lines[0]) && Closing(t.Lines[1]) && !Closing(t.Lines[2]));
        Assert.Equal(2, t.Timers.Running);
        Assert.Equal("+1 more", t.Bar.TaskListMoreText);

        t.Timers.FireAll();
        Assert.Equal(["Note 2", "Note 1"], t.Lines.Select(l => ((TaskLine)l.Tag).Title));
    });

    // A Done task's tick behaves the same: its line lingers with Undo in the tick box's place, and Undo brings it back
    // Done.
    [Fact]
    public void A_done_tasks_tick_has_the_same_undo() => StaThread.Run(() =>
    {
        using var t = Build();
        var done = t.Tasks.Create("Merged", t.Gepha, Session("s1")).Value;
        t.Tasks.MarkDone(done.Id);

        Click(ButtonNamed(t.Lines[0], "Tick off"));
        Assert.Equal(TaskStatus.Closed, t.Tasks.Board.Tasks.Single(x => x.Id == done.Id).Status);
        Assert.True(Closing(t.Lines[0]));
        Assert.False(HasButtonNamed(t.Lines[0], "Tick off"));

        Click(ButtonNamed(t.Lines[0], "Undo"));
        Assert.Equal(TaskStatus.Done, t.Tasks.Board.Tasks.Single(x => x.Id == done.Id).Status);
        Assert.False(Closing(Assert.Single(t.Lines)));
        Assert.True(HasButtonNamed(t.Lines[0], "Tick off"));
    });

    // LIVE CHECK, Petre's 320 px screenshot: nothing in the list clips at the narrowest width the bar allows. The box,
    // the chips (wrapping, a long workspace name among them), "+N more, K waiting" and the lines' chips all lie inside
    // the list, each at least as wide as it asked to be.
    [Fact]
    public void Nothing_in_the_list_clips_at_the_narrowest_bar() => StaThread.Run(() =>
    {
        using var t = Build();
        var gepha = t.Gepha; // looked up by name, so taken before the rename
        Assert.True(t.Harness.Manager.RenameWorkspace(gepha, "Customer onboarding portal").IsSuccess);
        // Four waiting and two more: three waiting lines show, and the hidden ones are one waiting and two more.
        Enumerable.Range(1, 2).ToList().ForEach(i => t.Tasks.AddNote($"Note {i}", gepha));
        Enumerable.Range(1, 4).ToList().ForEach(i => t.Tasks.Ask(t.Tasks.Create($"Ask {i}", gepha, Session($"s{i}")).Value.Id, "OK?"));
        t.Bar.ApplyWidth(176); // MinimumWidth at the default scale: (18 + 150 + 8) DIP
        t.Bar.RequestRebuild();
        ClickBox(t);
        t.Bar.UpdateLayout();

        Assert.Equal("+3 more, 1 waiting", t.Bar.TaskListMoreText);
        Assert.Contains("Customer onboarding portal", Inside(t.Chips).OfType<TextBlock>().Select(b => b.Text));
        var width = t.List.ActualWidth;
        var checkedParts = Inside(t.List).OfType<FrameworkElement>()
            .Where(e => e.IsVisible && (e is Button || e is TextBox || Equals(e.Tag, "task-workspace")))
            .Where(e => e is not Button { Tag: TaskLine })
            .ToList();
        Assert.True(checkedParts.Count >= 8, $"{checkedParts.Count} parts checked");
        Assert.All(checkedParts, part =>
        {
            var bounds = part.TransformToAncestor(t.List).TransformBounds(new Rect(part.RenderSize));
            var name = AutomationProperties.GetName(part) is { Length: > 0 } n ? n : part.GetType().Name;
            Assert.True(bounds.Left >= -0.5 && bounds.Right <= width + 0.5, $"{name} at {bounds} in a list {width} wide");
            Assert.True(part.RenderSize.Width >= part.DesiredSize.Width - part.Margin.Left - part.Margin.Right - 0.5,
                $"{name} squeezed: {part.RenderSize.Width} of {part.DesiredSize.Width}");
        });
    });

    // Petre, at the live check: "make workspace changeable". A line's workspace chip opens a menu of every workspace and
    // none, the task's own ticked; picking one moves the task, and the line's chip follows.
    static string ChipTextOf(Button line) =>
        Inside(line).OfType<Border>().Single(b => Equals(b.Tag, "task-workspace")).Child is TextBlock text ? text.Text : "";

    static string MenuNameOf(Button chip) => AutomationProperties.GetName(chip);

    [Fact]
    public void A_lines_workspace_chip_moves_the_task() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("Dishwasher tablets", t.Gepha).Value.Id;
        var sparrow = t.Harness.Manager.State.Workspaces.Single(w => w.Name == "Sparrow").Id;

        var menu = t.Bar.OpenTaskMoveMenu(id)!;
        var items = WorkspaceChipMenu.Chips(menu);
        Assert.Equal(["none", "GEPHA", "Sparrow"], items.Select(MenuNameOf));
        Assert.Equal(["GEPHA"], items.Where(i => AutomationProperties.GetItemStatus(i) == WorkspaceChipMenu.CurrentStatus).Select(MenuNameOf));

        Click(items.Single(i => MenuNameOf(i) == "Sparrow"));
        menu.IsOpen = false;
        t.Bar.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        t.Bar.RequestRebuild();

        Assert.Equal(sparrow, t.Tasks.Board.Tasks.Single(x => x.Id == id).WorkspaceId.Value);
        Assert.Equal("Sparrow", ChipTextOf(t.Lines.Single()));

        // And to none.
        var again = t.Bar.OpenTaskMoveMenu(id)!;
        Click(WorkspaceChipMenu.Chips(again).Single(i => MenuNameOf(i) == "none"));
        again.IsOpen = false;
        Assert.True(t.Tasks.Board.Tasks.Single(x => x.Id == id).WorkspaceId.HasNoValue);
    });

    // The chip is a button inside the line, like the tick: its click is its own and never goes to the workspace.
    [Fact]
    public void A_chip_click_never_goes_to_the_workspace() => StaThread.Run(() =>
    {
        var went = new List<Guid>();
        using var t = Build(goTo: id =>
        {
            went.Add(id);
            return Result.Success();
        });
        t.Tasks.AddNote("Dishwasher tablets", t.Gepha);

        Click(ButtonNamed(t.Lines.Single(), "Move to workspace"));

        Assert.Empty(went);
        Assert.NotNull(t.Bar.TaskMoveMenu);
        t.Bar.TaskMoveMenu!.IsOpen = false;
    });

    // (m) A RELEASED-BUILD BUG (2026-11-23, the site's screenshot at 1920x1080 and 125%, the bar docked at 198 px): the
    // titles read "Ca…", "M…", "S.", with a wide empty gap between the title and the age. The gap was the line's two
    // OFF flag toggles: transparent until hover (Opacity 0) but still laid out, docked between the title and the age, so
    // they took the room the title should have had. At the default docked width (158 DIP: 176 at the default 0.9 scale)
    // and at a floating width, real titles of 20 to 60 characters now show at least eight characters, and nothing
    // empty sits between the title and what follows it.
    [Theory]
    [InlineData(158.4)]
    [InlineData(240)]
    public void A_title_takes_the_free_width_of_its_line(double width) => StaThread.Run(() =>
    {
        using var t = Build();
        var gepha = t.Gepha;
        Assert.True(t.Harness.Manager.RenameWorkspace(gepha, "Website").IsSuccess);
        string[] titles =
        [
            "Call the accountant about Q4",
            "Make the hero video for the home page",
            "Send the invoices for November to every client, with the late fee",
            "Check the screenshots",
        ];
        titles.ToList().ForEach(title => t.Tasks.AddNote(title, gepha));
        t.Bar.ApplyWidth(width);
        t.Bar.RequestRebuild();
        t.Bar.UpdateLayout();

        t.Lines.ToList().ForEach(line =>
        {
            var title = Inside(line).OfType<TextBlock>().Single(b => Equals(b.Tag, "task-text"));
            var name = AutomationProperties.GetName(line);
            var eight = new FormattedText(name[..8], System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(title.FontFamily, title.FontStyle, title.FontWeight, title.FontStretch), title.FontSize, Brushes.White, 1).Width;
            Assert.True(title.ActualWidth >= eight, $"at {width}: '{name}' has {title.ActualWidth:0.#} DIP, eight characters need {eight:0.#}");
            // What follows the title on the line: the next element laid out to its right that takes room and shows.
            var right = title.TransformToAncestor(line).TransformBounds(new Rect(title.RenderSize)).Right;
            title.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (title.DesiredSize.Width <= title.ActualWidth + 0.5) return; // a title that fits needs no more room
            var next = Inside(line).OfType<FrameworkElement>()
                .Where(e => e != title && e.IsVisible && e.Opacity > 0 && e.ActualWidth > 0 && !e.IsAncestorOf(title) && !title.IsAncestorOf(e))
                .Select(e => e.TransformToAncestor(line).TransformBounds(new Rect(e.RenderSize)))
                .Where(b => b.Left >= right - 0.5)
                .Select(b => b.Left).DefaultIfEmpty(line.ActualWidth).Min();
            Assert.True(next - right <= 8, $"at {width}: '{name}' ends at {right:0.#}, the next thing starts at {next:0.#}");
        });
    });

    // When the chip would leave the title less than its share of the line (TaskLineFace.TitleShare), the chip gives way to
    // a dot in the workspace's colour, its name in the tooltip; on a wide bar the chip stays.
    [Fact]
    public void A_narrow_line_shows_a_dot_for_its_workspace_and_a_wide_one_the_chip() => StaThread.Run(() =>
    {
        using var t = Build();
        var gepha = t.Gepha;
        Assert.True(t.Harness.Manager.RenameWorkspace(gepha, "Deskbar site").IsSuccess);
        t.Tasks.AddNote("Make the hero video for the home page", gepha);

        t.Bar.ApplyWidth(158.4);
        t.Bar.RequestRebuild();
        t.Bar.UpdateLayout();
        var dot = Inside(t.Lines.Single()).OfType<FrameworkElement>().Single(e => Equals(e.Tag, "task-workspace-dot"));
        Assert.True(dot.IsVisible, "the dot shows on a narrow line");
        Assert.Equal("On Deskbar site", dot.ToolTip);
        Assert.False(Inside(t.Lines.Single()).OfType<Border>().Single(b => Equals(b.Tag, "task-workspace")).IsVisible);

        t.Bar.ApplyWidth(400);
        t.Bar.RequestRebuild();
        t.Bar.UpdateLayout();
        Assert.True(Inside(t.Lines.Single()).OfType<Border>().Single(b => Equals(b.Tag, "task-workspace")).IsVisible, "the chip shows on a wide line");
    });

    // Petre's screenshot: on "something else" the chip at the line's end was cut off ("Deskba"). The title trims first,
    // so the chip is always whole, at the narrowest bar too.
    [Theory]
    [InlineData(0)]
    [InlineData(Deskbar.Core.Domain.BarText.Maximum)]
    public void A_long_title_trims_and_leaves_the_chip_whole(double textOffset) => StaThread.Run(() =>
    {
        using var t = Build();
        var gepha = t.Gepha; // looked up by name, so taken before the rename
        t.Harness.Manager.SetBarTextOffset(textOffset);
        Assert.True(t.Harness.Manager.RenameWorkspace(gepha, "Deskbar").IsSuccess);
        t.Tasks.AddNote("something else that runs on far longer than any bar is wide, and then some more", gepha);
        t.Bar.ApplyWidth(176);
        t.Bar.RequestRebuild();
        t.Bar.UpdateLayout();

        var line = t.Lines.Single();
        var chip = Inside(line).OfType<Border>().Single(b => Equals(b.Tag, "task-workspace"));
        var title = Inside(line).OfType<TextBlock>().Single(b => Equals(b.Tag, "task-text"));
        var chipBounds = chip.TransformToAncestor(line).TransformBounds(new Rect(chip.RenderSize));
        Assert.True(chip.RenderSize.Width >= chip.DesiredSize.Width - chip.Margin.Left - chip.Margin.Right - 0.5,
            $"chip squeezed: {chip.RenderSize.Width} of {chip.DesiredSize.Width}");
        Assert.True(chipBounds.Right <= line.ActualWidth + 0.5, $"chip at {chipBounds} in a line {line.ActualWidth} wide");
        title.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Assert.True(title.RenderSize.Width < title.DesiredSize.Width, "the title trims");
    });

    // The words, without a window: when the store says it kept the file, the line promises the .bak; when it could
    // not move the file aside, the line says to copy it somewhere safe instead.
    [Fact]
    public void The_failed_load_line_promises_a_bak_only_when_one_was_kept()
    {
        Assert.Contains(".bak", FloatingBar.LoadProblemLine("Could not read tasks.json: bad; kept tasks.json as tasks.json.20261122-090000.bak"));
        Assert.Contains("copy it somewhere safe", FloatingBar.LoadProblemLine("Could not read tasks.json: bad; could not keep C:\\x\\tasks.json aside: locked"));
    }

    // Where the bar's content ends: its width less the root's padding (FloatingBar.xaml, Root Padding="4").
    static double VisibleEdge(ListBar t) => t.Bar.ActualWidth - 4;

    // What Petre's bar has and the harness's does not, measured from his trace (2026-11-22 18:32): "stats geometry:
    // footer=226 ... pins=222" on a bar whose geometry lines read width=140 to 197. His footer is laid out wider than
    // the bar (its line of pinned apps asks for 222 DIP), and the task list, which asks for no width of its own, took
    // the footer's width: the chip row never wrapped at the edge the bar shows, and a line's chip, docked at the line's
    // right end, sat past it. The harness's footer has no such line, so it is made that wide here, as his was.
    static void WidenTheFooter(ListBar t) => ((FrameworkElement)t.Bar.FindName("ShellFooter")!).MinWidth = 300;

    // Petre, of his narrow bar: "chip on the right side", then "edge". Every line's workspace chip ends flush at the
    // line's right edge (only the line's own inner padding inside it), all at the same x, whatever the title's length;
    // the title fills the space between the tick and the chip.
    [Fact]
    public void Every_lines_chip_sits_at_the_right_edge() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Tasks.AddNote("a", t.Gepha);
        t.Tasks.AddNote("a somewhat longer title", t.Gepha);
        t.Tasks.AddNote("mid", Maybe<Guid>.None);
        WidenTheFooter(t);
        t.Bar.ApplyWidth(200);
        t.Bar.RequestRebuild();
        t.Bar.UpdateLayout();

        var rights = t.Lines.Select(line =>
        {
            // The chip, or the dot that stands for it on a line too narrow for it (TaskLineFace): whichever is shown.
            var chip = Inside(line).OfType<FrameworkElement>().Single(b => (Equals(b.Tag, "task-workspace") || Equals(b.Tag, "task-workspace-dot")) && b.IsVisible);
            var bounds = chip.TransformToAncestor(t.Bar).TransformBounds(new Rect(chip.RenderSize));
            var lineBounds = line.TransformToAncestor(t.Bar).TransformBounds(new Rect(line.RenderSize));
            Assert.True(lineBounds.Right - bounds.Right <= 10, $"chip ends at {bounds.Right}, its line at {lineBounds.Right}");
            Assert.True(lineBounds.Right <= VisibleEdge(t) + 0.5, $"the line ends at {lineBounds.Right}, the bar shows up to {VisibleEdge(t)}");
            return bounds.Right;
        }).ToList();
        Assert.True(rights.Max() - rights.Min() < 0.5, $"chips end at {string.Join(", ", rights)}");
    });

    // Petre's screenshot: the chips under the box read "on [Deskbar] [none] [oth", "other…" cut at the bar's right edge.
    // They must wrap at the width the bar really shows, checked against the bar's own window, not against the list
    // (which can be laid out wider than the window and so never sees the clip).
    [Fact]
    public void The_chip_row_wraps_inside_the_bars_own_width() => StaThread.Run(() =>
    {
        using var t = Build();
        var gepha = t.Gepha; // looked up by name, so taken before the rename
        Assert.True(t.Harness.Manager.RenameWorkspace(gepha, "Deskbar").IsSuccess);
        t.Tasks.AddNote("a note", gepha);
        WidenTheFooter(t);
        t.Bar.ApplyWidth(140); // the narrowest Petre's trace shows (geometry: width=140 scale=0.90)
        t.Bar.RequestRebuild();
        t.Bar.UpdateLayout();

        // The visible edge: the bar's width less its root's 4 DIP padding. The chips that wrap inside the list must end
        // there, and so must the lines' chips.
        var edge = VisibleEdge(t);
        var parts = Inside(t.Chips).OfType<Button>().Where(b => b.IsVisible).Cast<FrameworkElement>()
            .Concat(Inside(t.List).OfType<Border>().Where(b => Equals(b.Tag, "task-workspace")))
            .ToList();
        Assert.True(parts.Count >= 3);
        Assert.All(parts, part =>
        {
            var bounds = part.TransformToAncestor(t.Bar).TransformBounds(new Rect(part.RenderSize));
            Assert.True(bounds.Right <= edge + 0.5, $"{AutomationProperties.GetName(part)} ends at {bounds.Right}, the bar shows up to {edge}");
        });
    });

    // Petre: "undo sign instead of text". The Undo on a line just closed is the Undo glyph, not the word, found by its
    // automation name and tooltip.
    [Fact]
    public void Undo_is_a_sign_not_a_word() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Tasks.AddNote("note", t.Gepha);
        Click(ButtonNamed(t.Lines[0], "Tick off"));

        var undo = ButtonNamed(t.Lines[0], "Undo");
        Assert.Equal("Undo", undo.ToolTip);
        Assert.Equal("↶", Inside(undo).OfType<TextBlock>().Single(b => Equals(b.Tag, "undo-sign")).Text);
        Assert.DoesNotContain(Inside(undo).OfType<TextBlock>(), b => b.Text == "Undo");
    });

    // Ruling 46 (reverses 43; Petre: "bring back the times on tasks.."). Each line says its age just before its chip,
    // dim, in the compact form; a minute passing rewrites that text and nothing else (the line is the same element).
    // The age has its own tooltip in words ("Added 2 hours 3 minutes ago", then the date), since Petre "misread them for
    // what they were"; the line's tooltip keeps the created date.
    [Fact]
    public void A_line_says_its_age_and_a_minute_rewrites_only_that() => StaThread.Run(() =>
    {
        var now = Start.AddHours(2).AddMinutes(3);
        using var t = Build(taskNow: () => now);
        t.Tasks.AddNote("note", t.Gepha);
        var line = t.Lines.Single();
        TextBlock Age() => Inside(t.Lines.Single()).OfType<TextBlock>().Single(b => Equals(b.Tag, "task-age"));
        Assert.Equal("2h3m", Age().Text);
        Assert.Equal(TaskListFor_DimInk(), ((SolidColorBrush)Age().Foreground).Color);
        var tip = (string)Age().ToolTip;
        Assert.StartsWith("Added 2 hours 3 minutes ago\n", tip);
        Assert.Contains($"{Start.ToLocalTime():dddd d MMMM yyyy}", tip);
        Assert.Contains($"Created {Start.ToLocalTime():dddd d MMMM yyyy}", (string)line.ToolTip);

        now = now.AddMinutes(1);
        t.Bar.RefreshTaskAges();

        Assert.Equal("2h4m", Age().Text);
        Assert.StartsWith("Added 2 hours 4 minutes ago\n", (string)Age().ToolTip);
        Assert.Same(line, t.Lines.Single());
    });

    static Color TaskListFor_DimInk() => Color.FromRgb(0xA9, 0xAD, 0xB8);

    // The age sits on the first row, right before the chip, after the flags; and it is counted in the title's share, so
    // at the default docked width the title still keeps its room and the chip still gives way to a dot when it must.
    [Fact]
    public void The_age_sits_right_before_the_chip_and_the_title_keeps_its_share() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Tasks.AddNote("Make the hero video for the home page", t.Gepha);
        t.Bar.ApplyWidth(158.4);
        t.Bar.RequestRebuild();
        t.Bar.UpdateLayout();

        var line = t.Lines.Single();
        var age = Inside(line).OfType<TextBlock>().Single(b => Equals(b.Tag, "task-age"));
        var end = Inside(line).OfType<FrameworkElement>().Single(b => (Equals(b.Tag, "task-workspace") || Equals(b.Tag, "task-workspace-dot")) && b.IsVisible);
        var title = Inside(line).OfType<TextBlock>().Single(b => Equals(b.Tag, "task-text"));
        Rect In(FrameworkElement e) => e.TransformToAncestor(line).TransformBounds(new Rect(e.RenderSize));
        Assert.True(In(age).Right <= In(end).Left + 6, $"age {In(age)} then chip {In(end)}");
        Assert.True(In(title).Right <= In(age).Left + 0.5, $"title {In(title)} then age {In(age)}");
        Assert.True(title.ActualWidth >= TaskLineFace.TitleShare * line.ActualWidth * 0.5, $"title has {title.ActualWidth} of {line.ActualWidth}");
    });

    // Petre: "highlight selected task" (ruling 21). A click on a line picks its task: drawn with the blue edge, kept
    // through a repaint, let go when the task is closed.
    [Fact]
    public void Clicking_a_line_selects_it_until_it_is_closed() => StaThread.Run(() =>
    {
        using var t = Build();
        var first = t.Tasks.AddNote("first", t.Gepha).Value.Id;
        var second = t.Tasks.AddNote("second", t.Gepha).Value.Id;

        Click(t.Lines.Single(l => ((TaskLine)l.Tag).Id == first));
        Assert.True(t.Bar.Selection.IsSelected(first));
        Assert.True(t.Bar.TaskLineSelected(first));
        Assert.False(t.Bar.TaskLineSelected(second));

        t.Tasks.AddNote("third", t.Gepha);
        Assert.True(t.Bar.TaskLineSelected(first));

        t.Tasks.Close(first);
        Assert.True(t.Bar.Selection.Current.HasNoValue);
    });

    // Ruling 23 (Petre: "use entire height when available for tasks"). A bar sized by hand (docked, or across the
    // seam) shows as many lines as fit in the height its rows leave free, at least three. The pass runs after layout;
    // the test runs it the way the dispatcher would, until it settles.
    static void Settle(ListBar t)
    {
        Enumerable.Range(0, 6).ToList().ForEach(_ =>
        {
            t.Bar.UpdateLayout();
            t.Bar.FitTaskListToFreeHeight();
            t.Bar.UpdateLayout();
        });
    }

    static Panel RowsArea(ListBar t) => ((DockPanel)t.Bar.FindName("BarContent")!).Children.OfType<Panel>().Last();

    static double FreeHeight(ListBar t) =>
        RowsArea(t).ActualHeight - RowsArea(t).Children.OfType<UIElement>().Sum(c => c.DesiredSize.Height);

    static void Dock(ListBar t, double height)
    {
        t.Bar.SizeToContent = SizeToContent.Manual;
        t.Bar.Height = height;
    }

    [Fact]
    public void A_docked_bar_fills_its_free_height_with_lines() => StaThread.Run(() =>
    {
        using var t = Build(openWindow: () => { });
        Enumerable.Range(1, 30).ToList().ForEach(i => t.Tasks.AddNote($"Note {i}", t.Gepha));
        Dock(t, 900);
        Settle(t);

        var lineHeight = t.Lines[0].ActualHeight + t.Lines[0].Margin.Top + t.Lines[0].Margin.Bottom;
        Assert.True(t.Lines.Count > 3, $"{t.Lines.Count} lines");
        // The list took the free height, less than one more line of it is left, and the layout has settled.
        Assert.InRange(FreeHeight(t), -0.5, lineHeight);
        Assert.True(t.Bar.IsMeasureValid && t.Bar.IsArrangeValid);
        Assert.Equal(t.Lines.Count, t.Bar.TaskListSlotCount);
        Assert.Equal($"+{30 - t.Lines.Count} more", t.Bar.TaskListMoreText);
    });

    [Fact]
    public void Less_free_height_means_fewer_lines_but_never_under_three() => StaThread.Run(() =>
    {
        using var t = Build(openWindow: () => { });
        Enumerable.Range(1, 30).ToList().ForEach(i => t.Tasks.AddNote($"Note {i}", t.Gepha));
        Dock(t, 900);
        Settle(t);
        var tall = t.Lines.Count;

        // Rows added take height from the list: the rows keep priority.
        Enumerable.Range(1, 4).ToList().ForEach(i => Assert.True(t.Harness.Manager.AddWorkspace($"Extra {i}").IsSuccess));
        t.Bar.RequestRebuild();
        Settle(t);
        var withRows = t.Lines.Count;
        Assert.True(withRows < tall, $"{withRows} lines with more rows, {tall} before; rows {((Panel)t.Bar.FindName("Rows")!).Children.Count} workspaces {t.Harness.Manager.State.Workspaces.Count} free {FreeHeight(t)} area {RowsArea(t).ActualHeight}");

        // A bar too short for more than the rows keeps three.
        Dock(t, 200);
        Settle(t);
        Assert.Equal(3, t.Lines.Count);
    });

    // Review C-1 of 187cf4a: the pass took one line's height from the FIRST line only. With notes (shorter) above Claude
    // lines carrying a session window icon (taller), the count flipped 7, 6, 7, 6 for ever, and since each flip queues
    // another pass above Input priority, the whole UI thread froze. Swept over heights and two text sizes: once settled,
    // one more pass must change nothing.
    [Fact]
    public void A_docked_list_of_mixed_line_heights_settles_at_every_height() => StaThread.Run(() =>
    {
        var unsettled = new List<string>();
        new[] { -2.0, 0.0 }.ToList().ForEach(offset =>
        {
            using var t = Build(openWindow: () => { }, sessionWindow: _ => Maybe<nint>.From((nint)0x1234));
            t.Harness.Manager.SetBarTextOffset(offset);
            Enumerable.Range(1, 20).ToList().ForEach(i => t.Tasks.Create($"Claude {i}", t.Gepha, Session($"s{i}")));
            Enumerable.Range(1, 3).ToList().ForEach(i => t.Tasks.AddNote($"Note {i}", t.Gepha));
            t.Bar.RequestRebuild();
            t.Bar.SizeToContent = SizeToContent.Manual;
            for (var height = 450.0; height < 650; height += 0.5)
            {
                t.Bar.Height = height;
                Settle(t);
                var settled = t.Bar.TaskListSlotCount;
                t.Bar.UpdateLayout();
                t.Bar.FitTaskListToFreeHeight();
                t.Bar.UpdateLayout();
                if (t.Bar.TaskListSlotCount != settled) unsettled.Add($"offset {offset} height {height}: {settled} then {t.Bar.TaskListSlotCount}");
            }
        });

        Assert.True(unsettled.Count == 0, string.Join("\n", unsettled.Take(10)));
    });

    // Review I-1 of 187cf4a: undocking laid the bar out with the docked line count before the pass after layout could
    // shrink it, so the position saved on undock was clamped against a bar as tall as the screen. Letting go of the
    // strip draws three lines before its own layout, and the bar is as tall as a floating bar right then.
    [Fact]
    public void Undocking_lays_out_three_lines_from_the_start() => StaThread.Run(() =>
    {
        using var t = Build(openWindow: () => { });
        Enumerable.Range(1, 30).ToList().ForEach(i => t.Tasks.AddNote($"Note {i}", t.Gepha));
        t.Bar.UpdateLayout();
        Dock(t, 900);
        Settle(t);
        Assert.True(t.Lines.Count > 3, $"{t.Lines.Count} lines docked");
        var laidOut = new List<int>();
        t.Bar.LayoutUpdated += (_, _) => laidOut.Add(t.Lines.Count);

        t.Bar.LetGoOfStrip();

        Assert.NotEmpty(laidOut);
        Assert.All(laidOut, count => Assert.Equal(3, count));
        Assert.Equal(3, t.Bar.TaskListSlotCount);
        // The list asks for its floating height as it lets go: the passes that follow change nothing, so the height the
        // undock places and saves from is already the floating bar's. (The bar's own height is no witness in a test:
        // the test window, parked off screen, keeps its docked 900 whatever it is asked, measured with an empty task
        // list too.)
        var asItLetGo = t.List.DesiredSize.Height;
        Settle(t);
        Assert.Equal(3, t.Lines.Count);
        Assert.Equal(asItLetGo, t.List.DesiredSize.Height, 1);
    });

    [Fact]
    public void A_floating_bar_keeps_three_lines() => StaThread.Run(() =>
    {
        using var t = Build(openWindow: () => { });
        Enumerable.Range(1, 30).ToList().ForEach(i => t.Tasks.AddNote($"Note {i}", t.Gepha));
        Settle(t);

        Assert.Equal(3, t.Lines.Count);
        Assert.Equal(3, t.Bar.TaskListSlotCount);
    });

    // --- Task 12: the two flags and Covey's quadrants on the bar (Rulings 25 and 28) ----------------------------------

    static Button Toggle(Button line, string name) => ButtonNamed(line, name);

    static TextBlock GlyphOf(Button toggle) => Inside(toggle).OfType<TextBlock>().Single();

    // The toggle is on when it says so to automation and its glyph wears the flag's colour at full strength; off it is
    // quiet (invisible until hover or selection, still clickable) and dim.
    static bool IsOn(Button toggle) =>
        AutomationProperties.GetItemStatus(toggle) == "on" && toggle.Opacity >= 1
        && GlyphOf(toggle).Foreground is SolidColorBrush { Color: var c } && c != ((SolidColorBrush)FloatingBar.TaskFlagOffInk).Color;

    static string TitleOf(Button line) => ((TaskLine)line.Tag).Title;

    // Waiting on you first, then quadrant I (important and urgent), II (important), III (urgent), IV (neither), each
    // in its own order, a newer task on top until dragged.
    [Fact]
    public void The_lines_are_waiting_then_covey_quadrants() => StaThread.Run(() =>
    {
        using var t = Build(openWindow: () => { });
        t.Bar.SizeToContent = SizeToContent.Manual;
        t.Bar.Height = 900;
        var neither = t.Tasks.AddNote("neither", t.Gepha).Value.Id;
        var urgent = t.Tasks.AddNote("urgent", t.Gepha).Value.Id;
        var important = t.Tasks.AddNote("important", t.Gepha).Value.Id;
        var both = t.Tasks.AddNote("both", t.Gepha).Value.Id;
        var waiting = t.Tasks.AddNote("waiting", t.Gepha).Value.Id;
        t.Tasks.SetFlag(urgent, TaskFlag.Urgent, true);
        t.Tasks.SetFlag(important, TaskFlag.Important, true);
        t.Tasks.SetFlag(both, TaskFlag.Important, true);
        t.Tasks.SetFlag(both, TaskFlag.Urgent, true);
        t.Tasks.SetFlag(waiting, TaskFlag.Important, true);
        t.Tasks.SetStatus(waiting, Deskbar.Core.Tasks.TaskStatus.WaitingOnYou);
        Settle(t);

        Assert.Equal(["waiting", "both", "important", "urgent", "neither"], t.Lines.Select(TitleOf));
        // A waiting task shows its flags though it sits in the pinned block.
        Assert.True(IsOn(Toggle(t.Lines[0], "Important")));
        Assert.False(IsOn(Toggle(t.Lines[0], "Urgent")));
    });

    // Each line has the two toggles, with their tooltips; on, each is drawn in its colour, off it is quiet until the
    // line is hovered or selected. A click sets the flag and the task goes to the top of its new quadrant.
    [Fact]
    public void A_toggle_sets_its_flag_and_the_line_moves_to_its_quadrant() => StaThread.Run(() =>
    {
        using var t = Build();
        var first = t.Tasks.AddNote("first", t.Gepha).Value.Id;
        t.Tasks.AddNote("second", t.Gepha);
        Assert.Equal(["second", "first"], t.Lines.Select(TitleOf));
        var important = Toggle(t.Lines[1], "Important");
        Assert.Equal("Important", important.ToolTip);
        Assert.Equal("Urgent", Toggle(t.Lines[1], "Urgent").ToolTip);
        Assert.False(IsOn(important));
        Assert.Equal(0, important.Opacity);

        Click(important);

        Assert.True(t.Tasks.Board.Tasks.Single(x => x.Id == first).Important);
        Assert.Equal(["first", "second"], t.Lines.Select(TitleOf));
        Assert.True(IsOn(Toggle(t.Lines[0], "Important")));
        Assert.False(IsOn(Toggle(t.Lines[0], "Urgent")));

        // Clicked again, off: back to quadrant IV, on top of it.
        Click(Toggle(t.Lines[0], "Important"));
        Assert.False(t.Tasks.Board.Tasks.Single(x => x.Id == first).Important);
        Assert.Equal(["first", "second"], t.Lines.Select(TitleOf));
    });

    [Fact]
    public void The_quiet_toggles_show_on_the_selected_line() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("a note", t.Gepha).Value.Id;
        Assert.Equal(0, Toggle(t.Lines[0], "Urgent").Opacity);

        t.Bar.Selection.Select(id);

        Assert.True(Toggle(t.Lines[0], "Urgent").Opacity > 0);
        Assert.False(IsOn(Toggle(t.Lines[0], "Urgent")));
    });

    // The groups are told apart by a little space above the first line of each, and no caption: the bar stays compact.
    [Fact]
    public void A_little_space_separates_the_groups_and_nothing_names_them() => StaThread.Run(() =>
    {
        using var t = Build(openWindow: () => { });
        t.Bar.SizeToContent = SizeToContent.Manual;
        t.Bar.Height = 900;
        var a = t.Tasks.AddNote("a", t.Gepha).Value.Id;
        t.Tasks.AddNote("b", t.Gepha);
        var c = t.Tasks.AddNote("c", t.Gepha).Value.Id;
        t.Tasks.SetFlag(a, TaskFlag.Urgent, true);
        t.Tasks.SetFlag(c, TaskFlag.Urgent, true);
        Settle(t);

        Assert.Equal(["c", "a", "b"], t.Lines.Select(TitleOf));
        Assert.Equal([false, false, true], t.Lines.Select(l => ((TaskLine)l.Tag).StartsGroup));
        Assert.True(t.Lines[2].Margin.Top > t.Lines[1].Margin.Top);
        Assert.DoesNotContain(Inside(t.List).OfType<TextBlock>(), b => b.Text.Contains("Urgent") || b.Text.Contains("Important"));
    });

    // A closed line in its undo window has no toggles: its task is closed and takes no flag.
    [Fact]
    public void A_closing_line_has_no_toggles() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Tasks.AddNote("a note", t.Gepha);

        Click(ButtonNamed(t.Lines[0], "Tick off"));

        Assert.True(Closing(t.Lines[0]));
        Assert.False(HasButtonNamed(t.Lines[0], "Important"));
    });

    // --- Task 12: dragging a line up or down (Rulings 24, 25) --------------------------------------------------------

    static Panel LinesPanel(ListBar t) => (Panel)t.Lines[0].Parent;

    // A point inside a line, `share` of the way down it, in the lines panel's own coordinates (what the drag reads).
    static Point In(ListBar t, Button line, double share) =>
        line.TranslatePoint(new Point(10, line.ActualHeight * share), LinesPanel(t));

    // Press on a line, move to a point (in two steps, the first past the drag distance), release, and let the queued
    // drop run, the way a real drag ends.
    static void Drag(ListBar t, Button from, Point to)
    {
        var start = In(t, from, 0.5);
        t.Bar.TaskDragPress(((TaskLine)from.Tag).Id, start);
        t.Bar.TaskDragMove(new Point(start.X, start.Y + (to.Y > start.Y ? 8 : -8)));
        t.Bar.TaskDragMove(to);
        t.Bar.TaskDragRelease();
        PumpBar(t);
    }

    static void PumpBar(ListBar t) => t.Bar.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

    static ListBar DragBar(int notes)
    {
        var t = Build(openWindow: () => { });
        t.Bar.ArrangeMouseInput = ArrangeMouse.Held;
        t.Bar.SizeToContent = SizeToContent.Manual;
        t.Bar.Height = 900;
        Enumerable.Range(1, notes).ToList().ForEach(i => t.Tasks.AddNote($"n{i}", t.Gepha));
        Settle(t);
        return t;
    }

    [Fact]
    public void Dragging_a_line_down_puts_it_below_the_line_it_is_dropped_on() => StaThread.Run(() =>
    {
        using var t = DragBar(4);
        Assert.Equal(["n4", "n3", "n2", "n1"], t.Lines.Select(TitleOf));

        Drag(t, t.Lines[0], In(t, t.Lines[2], 0.8));

        Assert.Equal(["n3", "n2", "n4", "n1"], t.Tasks.Board.Pile().Select(x => x.Title));
        Settle(t);
        Assert.Equal(["n3", "n2", "n4", "n1"], t.Lines.Select(TitleOf));
        Assert.False(t.Bar.TaskDragging);
    });

    [Fact]
    public void Dragging_a_line_up_puts_it_above_the_line_it_is_dropped_on() => StaThread.Run(() =>
    {
        using var t = DragBar(4);

        Drag(t, t.Lines[3], In(t, t.Lines[1], 0.2));

        Assert.Equal(["n4", "n1", "n3", "n2"], t.Tasks.Board.Pile().Select(x => x.Title));
    });

    // A press that does not travel the drag distance is a click, not a drag: nothing moves and no preview shows.
    [Fact]
    public void A_press_that_does_not_travel_is_no_drag() => StaThread.Run(() =>
    {
        using var t = DragBar(3);
        var before = t.Tasks.Board;
        var at = In(t, t.Lines[0], 0.5);

        t.Bar.TaskDragPress(((TaskLine)t.Lines[0].Tag).Id, at);
        t.Bar.TaskDragMove(new Point(at.X + 1, at.Y + 1));
        Assert.False(t.Bar.TaskDragging);
        Assert.Null(t.Bar.TaskDropShown);
        t.Bar.TaskDragRelease();
        PumpBar(t);

        Assert.Same(before, t.Tasks.Board);
        // Let go, the press holds nothing back: a change paints at once.
        t.Tasks.AddNote("after the press", t.Gepha);
        Assert.Equal("after the press", TitleOf(t.Lines[0]));
    });

    // While the drag shows where the line would go, the list does not repaint under it (#48: no repaint during a
    // press or a drag); the change that waited is drawn once the drop is done.
    [Fact]
    public void The_list_does_not_repaint_during_a_drag_and_shows_the_drop_line() => StaThread.Run(() =>
    {
        using var t = DragBar(3);
        var lines = t.Lines.ToList();
        var start = In(t, lines[0], 0.5);
        t.Bar.TaskDragPress(((TaskLine)lines[0].Tag).Id, start);
        t.Bar.TaskDragMove(new Point(start.X, start.Y + 8));
        t.Bar.TaskDragMove(In(t, lines[2], 0.8));
        Assert.True(t.Bar.TaskDragging);
        var shown = t.Bar.TaskDropShown!;
        Assert.Equal(TaskPlace.After(((TaskLine)lines[2].Tag).Id), shown.Place);
        Assert.Equal(Quadrant.IV, shown.Quadrant);

        t.Tasks.AddNote("arrives mid-drag", t.Gepha);
        Assert.Equal(lines, t.Lines.ToList());

        t.Bar.TaskDragRelease();
        PumpBar(t);
        Settle(t);
        Assert.Equal(["arrives mid-drag", "n2", "n1", "n3"], t.Lines.Select(TitleOf));
        Assert.Null(t.Bar.TaskDropShown);
    });

    // Dropping a line among another quadrant's lines sets that quadrant's flags.
    [Fact]
    public void Dropping_into_another_group_sets_its_flags() => StaThread.Run(() =>
    {
        using var t = DragBar(3);
        var n1 = t.Tasks.Board.Tasks.Single(x => x.Title == "n1").Id;
        t.Tasks.SetFlag(n1, TaskFlag.Important, true);
        Settle(t);
        Assert.Equal(["n1", "n3", "n2"], t.Lines.Select(TitleOf));

        Drag(t, t.Lines[2], In(t, t.Lines[0], 0.8));

        var n2 = t.Tasks.Board.Tasks.Single(x => x.Title == "n2");
        Assert.True(n2.Important);
        Assert.False(n2.Urgent);
        Assert.Equal(["n1", "n2", "n3"], t.Tasks.Board.Pile().Select(x => x.Title));
    });

    // Waiting on you lines are pinned: a press on one arms no drag, and nothing drops above them (a drop on one lands
    // under the block, at the top of the first group below it).
    [Fact]
    public void Waiting_lines_cannot_be_dragged_and_nothing_drops_above_them() => StaThread.Run(() =>
    {
        using var t = DragBar(3);
        var n3 = t.Tasks.Board.Tasks.Single(x => x.Title == "n3").Id;
        t.Tasks.SetStatus(n3, Deskbar.Core.Tasks.TaskStatus.WaitingOnYou);
        Settle(t);
        Assert.Equal(["n3", "n2", "n1"], t.Lines.Select(TitleOf));

        var start = In(t, t.Lines[0], 0.5);
        t.Bar.TaskDragPress(n3, start);
        t.Bar.TaskDragMove(new Point(start.X, start.Y + 30));
        Assert.False(t.Bar.TaskDragging);
        t.Bar.TaskDragRelease();

        Drag(t, t.Lines[2], In(t, t.Lines[0], 0.2));

        Assert.Equal(["n3", "n1", "n2"], t.Tasks.Board.Pile().Select(x => x.Title));
    });

    // Lost capture (another window took the mouse) calls the drag off: nothing moves.
    [Fact]
    public void A_cancelled_drag_moves_nothing() => StaThread.Run(() =>
    {
        using var t = DragBar(3);
        var before = t.Tasks.Board;
        var start = In(t, t.Lines[0], 0.5);
        t.Bar.TaskDragPress(((TaskLine)t.Lines[0].Tag).Id, start);
        t.Bar.TaskDragMove(new Point(start.X, start.Y + 8));
        t.Bar.TaskDragMove(In(t, t.Lines[2], 0.8));

        t.Bar.CancelTaskDrag("test");
        t.Bar.TaskDragRelease();
        PumpBar(t);

        Assert.Same(before, t.Tasks.Board);
        Assert.False(t.Bar.TaskDragging);
    });

    // Review of 4fc4441..a1acd60, item 4: a drop on the pinned Waiting on you block took the quadrant of the first line
    // under it, so dragging a plain task to the top quietly made it important and urgent. It goes to the top of its
    // OWN quadrant instead, its flags unchanged.
    [Fact]
    public void A_drop_on_the_waiting_block_keeps_the_tasks_own_quadrant() => StaThread.Run(() =>
    {
        using var t = DragBar(3);
        var n3 = t.Tasks.Board.Tasks.Single(x => x.Title == "n3").Id;
        var n2 = t.Tasks.Board.Tasks.Single(x => x.Title == "n2").Id;
        t.Tasks.SetStatus(n3, Deskbar.Core.Tasks.TaskStatus.WaitingOnYou);
        t.Tasks.SetFlag(n2, TaskFlag.Important, true);
        t.Tasks.SetFlag(n2, TaskFlag.Urgent, true);
        Settle(t);
        Assert.Equal(["n3", "n2", "n1"], t.Lines.Select(TitleOf));

        Drag(t, t.Lines[2], In(t, t.Lines[0], 0.5));

        var n1 = t.Tasks.Board.Tasks.Single(x => x.Title == "n1");
        Assert.False(n1.Important || n1.Urgent);
        Assert.Equal(["n3", "n2", "n1"], t.Tasks.Board.Pile().Select(x => x.Title));
    });

    // --- Review of 4fc4441..a1acd60, item 2: drags driven by real routed input ---------------------------------------

    sealed class Pointer
    {
        public Point At;
    }

    // The tunnelling and bubbling events WPF raises; each element on the route re-raises its own direct
    // Preview/MouseLeftButtonDown/Up from them, which is where the line and the panel listen.
    static MouseButtonEventArgs ButtonArgs(RoutedEvent routed) => new(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = routed };

    static void RealPress(UIElement line)
    {
        line.RaiseEvent(ButtonArgs(UIElement.PreviewMouseDownEvent));
        line.RaiseEvent(ButtonArgs(UIElement.MouseDownEvent));
    }

    static void RealMove(UIElement line) =>
        line.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.PreviewMouseMoveEvent });

    // The release, and the Click the button would raise when the release reaches it (see the note above the helpers).
    // The line released must still be one of the lines drawn: a repaint under the press (#48) would have replaced it,
    // and a real release would then land on nothing (review of the fix round, minor 1).
    static bool RealRelease(ListBar t, Button line)
    {
        Assert.Contains(line, t.Lines);
        var up = ButtonArgs(UIElement.PreviewMouseUpEvent);
        line.RaiseEvent(up);
        if (up.Handled) return false;
        line.RaiseEvent(ButtonArgs(UIElement.MouseUpEvent));
        Click(line);
        return true;
    }

    static void LoseCapture(UIElement on) =>
        on.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.LostMouseCaptureEvent });

    // How many times a drag took the mouse capture, through the seam (minor 1: a press under the threshold takes none).
    sealed class Captures
    {
        public int Count;
    }

    static (ListBar Bar, Pointer Pointer, List<Guid> Went) RoutedBar(Captures? captures = null)
    {
        var went = new List<Guid>();
        var t = Build(openWindow: () => { }, goTo: id =>
        {
            went.Add(id);
            return Result.Success();
        });
        var pointer = new Pointer();
        t.Bar.ArrangeMouseInput = new ArrangeMouse(_ =>
        {
            if (captures is not null) captures.Count++;
            return true;
        }, _ => true, (_, _) => pointer.At);
        t.Bar.SizeToContent = SizeToContent.Manual;
        t.Bar.Height = 900;
        Enumerable.Range(1, 3).ToList().ForEach(i => t.Tasks.AddNote($"n{i}", t.Gepha));
        Settle(t);
        return (t, pointer, went);
    }

    [Fact]
    public void A_routed_press_under_the_threshold_is_still_a_click() => StaThread.Run(() =>
    {
        var captures = new Captures();
        var (t, pointer, went) = RoutedBar(captures);
        using var _ = t;
        var before = t.Tasks.Board;
        var line = t.Lines[0];
        pointer.At = In(t, line, 0.5);

        RealPress(line);
        pointer.At = new Point(pointer.At.X + 1, pointer.At.Y + 1);
        RealMove(line);
        Assert.False(t.Bar.TaskDragging);
        Assert.True(RealRelease(t, line));
        Assert.Equal(0, captures.Count);

        Assert.Equal([((TaskLine)line.Tag).Id], went);
        Assert.Same(before, t.Tasks.Board);
    });

    [Fact]
    public void A_routed_drag_drops_and_its_release_raises_no_click() => StaThread.Run(() =>
    {
        var (t, pointer, went) = RoutedBar();
        using var _ = t;
        var line = t.Lines[0];
        pointer.At = In(t, line, 0.5);

        RealPress(line);
        pointer.At = new Point(pointer.At.X, pointer.At.Y + 8);
        RealMove(line);
        pointer.At = In(t, t.Lines[2], 0.8);
        RealMove(line);
        Assert.True(t.Bar.TaskDragging);
        Assert.False(RealRelease(t, line));
        PumpBar(t);

        Assert.Empty(went);
        Assert.Equal(["n2", "n1", "n3"], t.Tasks.Board.Pile().Select(x => x.Title));
    });

    // A lost capture during an armed press ends it, and the paint the press held back is drawn (item 3); during a
    // started drag it calls the drag off. Either way nothing moves.
    [Fact]
    public void A_routed_lost_capture_ends_a_press_or_a_drag_and_moves_nothing() => StaThread.Run(() =>
    {
        var (t, pointer, went) = RoutedBar();
        using var _ = t;
        var line = t.Lines[0];
        pointer.At = In(t, line, 0.5);

        RealPress(line);
        t.Tasks.AddNote("held back by the press", t.Gepha);
        Assert.NotEqual("held back by the press", TitleOf(t.Lines[0]));
        LoseCapture(line);
        PumpBar(t);
        Assert.Equal("held back by the press", TitleOf(t.Lines[0]));

        var before = t.Tasks.Board;
        line = t.Lines[0];
        pointer.At = In(t, line, 0.5);
        RealPress(line);
        pointer.At = new Point(pointer.At.X, pointer.At.Y + 8);
        RealMove(line);
        pointer.At = In(t, t.Lines[2], 0.8);
        RealMove(line);
        Assert.True(t.Bar.TaskDragging);
        LoseCapture(LinesPanel(t));
        Assert.False(t.Bar.TaskDragging);
        RealRelease(t, line);
        PumpBar(t);

        Assert.Same(before, t.Tasks.Board);
    });

    // Review of the fix round, minor 2: a drop on the waiting block lands at the top of the task's own quadrant, so the
    // drop line is drawn there, not at the foot of the block.
    [Fact]
    public void The_drop_line_on_the_waiting_block_is_where_the_task_lands() => StaThread.Run(() =>
    {
        using var t = DragBar(3);
        var n3 = t.Tasks.Board.Tasks.Single(x => x.Title == "n3").Id;
        var n2 = t.Tasks.Board.Tasks.Single(x => x.Title == "n2").Id;
        t.Tasks.SetStatus(n3, Deskbar.Core.Tasks.TaskStatus.WaitingOnYou);
        t.Tasks.SetFlag(n2, TaskFlag.Important, true);
        Settle(t);
        Assert.Equal(["n3", "n2", "n1"], t.Lines.Select(TitleOf));
        var landing = In(t, t.Lines[2], 0).Y;

        var start = In(t, t.Lines[2], 0.5);
        t.Bar.TaskDragPress(((TaskLine)t.Lines[2].Tag).Id, start);
        t.Bar.TaskDragMove(new Point(start.X, start.Y - 8));
        t.Bar.TaskDragMove(In(t, t.Lines[0], 0.5));

        Assert.Equal(TaskPlace.Top, t.Bar.TaskDropShown!.Place);
        Assert.Equal(landing, t.Bar.TaskDropShown!.LineY, 1);
        t.Bar.CancelTaskDrag("test");
    });

    // Ruling 31 on the bar: Undo brings a note back with the status it had (here In progress, set by hand), and the
    // line Petre had selected is selected again.
    [Fact]
    public void Undo_on_the_bar_brings_back_the_status_and_the_selection() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("set by hand", t.Gepha).Value.Id;
        t.Tasks.SetStatus(id, Deskbar.Core.Tasks.TaskStatus.InProgress);
        t.Bar.Selection.Select(id);

        Click(ButtonNamed(t.Lines[0], "Tick off"));
        Assert.True(t.Bar.Selection.Current.HasNoValue);
        Click(ButtonNamed(t.Lines[0], "Undo"));

        Assert.Equal(Deskbar.Core.Tasks.TaskStatus.InProgress, t.Tasks.Board.Tasks.Single(x => x.Id == id).Status);
        Assert.Equal(id, t.Bar.Selection.Current.Value);
    });

    // --- goals plan, Task 7: a task's tooltip names its goal, and the bar gains nothing else -------------------------

    sealed class MemoryGoals : Deskbar.Core.Goals.IGoalStore
    {
        public Result<Deskbar.Core.Goals.GoalBoard> Load() => Deskbar.Core.Goals.GoalBoard.Empty;

        public Result Save(Deskbar.Core.Goals.GoalBoard board) => Result.Success();
    }

    // The bar wired as App wires it: the goal's title for a task through GoalService.GoalOf, and its Changed to repaint.
    // The click clock is held still, so a test can tell whether a paint restarted the click guard.
    static (ListBar Bar, Deskbar.Core.Goals.GoalService Goals) GoalBar(Func<DateTime> clickClock, Func<Guid, Result>? goTo = null)
    {
        var minutes = 0;
        var tasks = new TaskService(new MemoryStore(), () => Start.AddMinutes(minutes++));
        var goals = new Deskbar.Core.Goals.GoalService(new MemoryGoals(), tasks, () => Start, () => new DateOnly(2026, 11, 24));
        var t = Build(tasks: tasks, clickClock: clickClock, goTo: goTo,
            goalTitle: task => goals.GoalOf(task).Map(g => g.Title), goalChanges: goals.Changed);
        return (t, goals);
    }

    static string TipOf(Button line) => line.ToolTip as string ?? "";

    [Fact]
    public void A_lines_tooltip_names_its_goal() => StaThread.Run(() =>
    {
        var (t, goals) = GoalBar(() => ClickStart);
        using var _ = t;
        var goal = goals.Add("Ship the goals planner", goals.ThisWeek).Value;
        t.Tasks.AddNote("free", t.Gepha);
        var linked = t.Tasks.AddNote("linked", t.Gepha).Value.Id;
        goals.LinkTask(linked, goal.Id);

        var line = t.Lines.Single(l => ((TaskLine)l.Tag).Id == linked);
        Assert.Equal("Goal: Ship the goals planner", TipOf(line).Split('\n').Last());
        Assert.DoesNotContain("Goal:", TipOf(t.Lines.Single(l => ((TaskLine)l.Tag).Title == "free")));
    });

    // A goal renamed from the pipe repaints the line (its tooltip says the new title) without moving it: the goal is in
    // the line's shape, not in the places key, so the click guard does not restart and the next click still goes.
    [Fact]
    public void Renaming_the_goal_updates_the_tooltip_without_moving_lines() => StaThread.Run(() =>
    {
        var now = ClickStart;
        var went = new List<Guid>();
        var (t, goals) = GoalBar(() => now, id =>
        {
            went.Add(id);
            return Result.Success();
        });
        using var _ = t;
        var goal = goals.Add("Ship it", goals.ThisWeek).Value;
        var id = t.Tasks.AddNote("linked", t.Gepha).Value.Id;
        goals.LinkTask(id, goal.Id);
        now = now.AddSeconds(1);

        goals.Rename(goal.Id, "Ship the goals planner");

        Assert.EndsWith("Goal: Ship the goals planner", TipOf(t.Lines[0]));
        PressAndClick(t.Lines[0], clickCount: 1);
        Assert.Equal([id], went);
    });

    // The anti-goal "a busier bar": linking a task adds nothing to its line but the tooltip.
    [Fact]
    public void The_bar_shows_no_goal_chip() => StaThread.Run(() =>
    {
        var (t, goals) = GoalBar(() => ClickStart);
        using var _ = t;
        var goal = goals.Add("Ship it", goals.ThisWeek).Value;
        var id = t.Tasks.AddNote("linked", t.Gepha).Value.Id;
        var before = Inside(t.Lines[0]).Count();

        goals.LinkTask(id, goal.Id);

        Assert.Equal(before, Inside(t.Lines[0]).Count());
        Assert.DoesNotContain(Inside(t.Lines[0]).OfType<TextBlock>(), b => b.Text.Contains("Ship it"));
    });

    // Ruling 37: flags when adding. The add box's chip row carries a task line's two toggles; the note takes the flags
    // they say and lands at the top of that quadrant, and the toggles are off again after the add.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void A_note_added_on_the_bar_takes_the_toggles_flags(bool important, bool urgent) => StaThread.Run(() =>
    {
        using var t = Build();
        t.Tasks.AddNote("already there", t.Gepha, QuadrantOf.Flags(important, urgent));

        ClickBox(t);
        t.Box.Text = "Pay rent";
        if (important) Click(ButtonNamed(t.List, "Add as important"));
        if (urgent) Click(ButtonNamed(t.List, "Add as urgent"));
        Assert.Equal(important ? "on" : "off", AutomationProperties.GetItemStatus(ButtonNamed(t.List, "Add as important")));
        Press(t.Bar, t.Box, Key.Enter);

        var added = t.Tasks.Board.Tasks.Single(x => x.Title == "Pay rent");
        Assert.Equal((important, urgent), (added.Important, added.Urgent));
        Assert.Equal(added.Id, t.Tasks.Board.Group(QuadrantOf.Flags(important, urgent))[0].Id);
        Assert.Equal("off", AutomationProperties.GetItemStatus(ButtonNamed(t.List, "Add as important")));
        Assert.Equal("off", AutomationProperties.GetItemStatus(ButtonNamed(t.List, "Add as urgent")));
    });

    // The toggles sit in the chip row's wrap, inside the list that takes no width: on a narrow bar they wrap, and the bar
    // is no wider for them.
    [Fact]
    public void The_add_toggles_wrap_with_the_chips_and_take_no_width() => StaThread.Run(() =>
    {
        using var t = Build();
        var chips = Inside(t.List).OfType<WrapPanel>().Single(w => Equals(w.Tag, "task-add-chips"));

        Assert.Contains(ButtonNamed(t.List, "Add as important"), chips.Children.OfType<Button>());
        Assert.Contains(ButtonNamed(t.List, "Add as urgent"), chips.Children.OfType<Button>());
    });
    // Ruling 44 on the bar: a right-click on a task line opens its menu, Edit first, then the rest of the line's actions.
    // Edit opens an editor in the line itself with the whole title; Enter saves through Rename, the keyboard goes back
    // to the window Petre was typing in, and the line shows the new title.
    [Fact]
    public void A_right_click_on_a_bar_line_opens_its_menu_with_Edit_first_and_Edit_renames() => StaThread.Run(() =>
    {
        var handed = new List<nint>();
        using var t = Build(handedBack: handed);
        var id = t.Tasks.AddNote("iam.ge: transcripts", t.Gepha).Value.Id;
        t.Bar.UpdateLayout();

        t.Lines.Single().RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = Mouse.PreviewMouseUpEvent });

        var menu = Assert.IsAssignableFrom<ContextMenu>(t.Bar.TaskLineMenu);
        var items = menu.Items.OfType<MenuItem>().Select(i => ((TextBlock)i.Header).Text).ToList();
        Assert.Equal("Edit", items[0]);
        Assert.Contains("Move to workspace…", items);
        Assert.Contains("Close", items);
        menu.Items.OfType<MenuItem>().First().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.IsOpen = false;

        var editor = Assert.IsType<TextBox>(t.Bar.TaskEditor);
        Assert.Equal("iam.ge: transcripts", editor.Text);
        Assert.True(t.Bar.HoldsFullStrength);
        editor.Text = "iam.ge: transcripts for every call";
        t.Bar.SaveTaskEdit();

        Assert.Null(t.Bar.TaskEditor);
        Assert.Equal("iam.ge: transcripts for every call", t.Tasks.Board.Tasks.Single(x => x.Id == id).Title);
        Assert.Equal([Editor], handed);
        Assert.False(t.Bar.HoldsFullStrength);
    });

    // While the editor is open no paint runs under it: a task arriving from a session waits, the editor and its text
    // survive, and the list is painted when the editor closes. Esc cancels and keeps the old title.
    [Fact]
    public void A_bar_paint_waits_while_a_line_is_edited_and_Esc_cancels() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("mine", t.Gepha).Value.Id;
        t.Bar.BeginTaskEdit(id);
        var editor = Assert.IsType<TextBox>(t.Bar.TaskEditor);
        editor.Text = "half typ";

        t.Tasks.AddNote("arrived meanwhile", t.Gepha);

        Assert.Single(t.Lines);
        Assert.Same(editor, t.Bar.TaskEditor);
        Assert.Equal("half typ", editor.Text);
        t.Bar.CancelTaskEdit();
        Assert.Null(t.Bar.TaskEditor);
        Assert.Equal("mine", t.Tasks.Board.Tasks.Single(x => x.Id == id).Title);
        Assert.Equal(2, t.Lines.Count);
    });

    // REVIEW OF db2b993..fa395f8, CRITICAL 1: while the editor was open the bar rebuilt without end (the rebuild ended by
    // painting the list, which waited and queued another rebuild) and input starved: no Input tick in two seconds. A
    // rebuild requested during an edit waits, and the dispatcher keeps serving input.
    [Fact]
    public void A_rebuild_requested_during_an_edit_waits_and_input_keeps_running() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("mine", t.Gepha).Value.Id;
        t.Bar.BeginTaskEdit(id);
        t.Bar.RequestRebuild();

        var ticks = InputTicksWhilePumping(t.Bar.Dispatcher, TimeSpan.FromMilliseconds(300));

        Assert.True(ticks > 5, $"input ticks {ticks}");
        Assert.NotNull(t.Bar.TaskEditor);
        t.Bar.CancelTaskEdit();
    });

    // The Edit menu's own close serves the deferred rebuild (FlushDeferredRebuild); with the editor open that must not
    // loop either, and closing the editor serves it once.
    [Fact]
    public void A_rebuild_flushed_by_the_edit_menus_close_does_not_loop() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("mine", t.Gepha).Value.Id;
        var menu = t.Bar.OpenTaskLineMenu(id)!;
        t.Bar.RequestRebuild();
        menu.Items.OfType<MenuItem>().First().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.IsOpen = false;

        var ticks = InputTicksWhilePumping(t.Bar.Dispatcher, TimeSpan.FromMilliseconds(300));
        Assert.True(ticks > 5, $"input ticks {ticks}");

        t.Bar.CancelTaskEdit();
        Assert.True(InputTicksWhilePumping(t.Bar.Dispatcher, TimeSpan.FromMilliseconds(300)) > 5);
        Assert.Single(t.Lines);
    });

    // I-1: a press inside the editor is the editor's; it must not arm a drag of the line, so selecting text with a move
    // of more than the drag distance never drags the line.
    [Fact]
    public void A_press_inside_the_editor_does_not_arm_a_line_drag() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("mine", t.Gepha).Value.Id;
        t.Tasks.AddNote("other", t.Gepha);
        t.Bar.BeginTaskEdit(id);
        var editor = Assert.IsType<TextBox>(t.Bar.TaskEditor);

        editor.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent, Source = editor });

        Assert.False(t.Bar.TaskDragInFlight);
        t.Bar.CancelTaskEdit();
    });

    // M-3: a refused rename (an empty title) keeps the editor open with what was typed, as the window's editor does.
    [Fact]
    public void A_refused_rename_keeps_the_bar_editor_open() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("mine", t.Gepha).Value.Id;
        t.Bar.BeginTaskEdit(id);
        Assert.IsType<TextBox>(t.Bar.TaskEditor).Text = "   ";

        t.Bar.SaveTaskEdit();

        Assert.NotNull(t.Bar.TaskEditor);
        Assert.Equal("mine", t.Tasks.Board.Tasks.Single(x => x.Id == id).Title);
        t.Bar.CancelTaskEdit();
    });

    // M-1: the off flags are invisible until the line is pointed at, and while invisible they take no clicks.
    [Fact]
    public void The_hidden_flag_toggles_are_not_hit_testable() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Tasks.AddNote("mine", t.Gepha);
        var toggles = Inside(t.Lines.Single()).OfType<Button>().Where(b => Equals(b.Tag, "task-flag-off") && b.Opacity == 0).ToList();
        Assert.NotEmpty(toggles);
        Assert.All(toggles, toggle => Assert.False(toggle.IsHitTestVisible));
    });

    // Petre's screenshot: a right-click on a task line opened the bar's own menu (New workspace, Window list, Arrange,
    // Dock, Manage, Exit). In the list a right-click opens the line's own menu, or the box's text menu, or nothing, and
    // the ContextMenuOpening that would reach the bar's menu is handled before it gets there.
    static ContextMenuEventArgs Opening(UIElement source) =>
        (ContextMenuEventArgs)Activator.CreateInstance(typeof(ContextMenuEventArgs),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public,
            null, [source, true, 0d, 0d], null)!;

    [Fact]
    public void A_right_click_in_the_list_never_opens_the_bars_menu() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("mine", t.Gepha).Value.Id;
        var barMenu = ((FrameworkElement)t.Bar.FindName("Root")!).ContextMenu!;
        var line = t.Lines.Single();

        // The line: a routed right-click opens its own menu, Edit first, and the bar's stays shut.
        line.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent, Source = line });
        Assert.True(t.Bar.TaskLineMenu is { IsOpen: true });
        Assert.Equal("Edit", ((TextBlock)t.Bar.TaskLineMenu!.Items.OfType<MenuItem>().First().Header).Text);
        t.Bar.TaskLineMenu.IsOpen = false;
        Assert.False(barMenu.IsOpen);

        // Anything else in the list: the add box (its own text menu), the chip row, the lines: the event ends in the list.
        new UIElement[] { t.Chips, AddChip(t), ButtonNamed(t.Chips, "Add as important"), line }.ToList().ForEach(source =>
        {
            var opening = Opening(source);
            source.RaiseEvent(opening);
            Assert.True(opening.Handled, $"{source.GetType().Name} let a right-click reach the bar's menu");
        });
        Assert.False(barMenu.IsOpen);
    });

    // RULING 50 (Petre: "move all tasks to the top to save vertical space, next to workspace selection"). The button that
    // says "All tasks" or "+N more" sits in the chip row, right-aligned after the chip and the toggles; there is no
    // bottom line, so the footer is the box, the chip row and the lines, and the docked fit pass (which measures the
    // footer) gives the line's height to the task lines.
    [Fact]
    public void All_tasks_sits_in_the_chip_row_and_there_is_no_bottom_line() => StaThread.Run(() =>
    {
        using var t = Build(openWindow: () => { });
        t.Tasks.AddNote("one", t.Gepha);
        t.Bar.UpdateLayout();
        var more = ButtonNamed(t.List, "More tasks");
        var row = Assert.IsType<Grid>(more.Parent);
        Assert.Contains(t.Chips, row.Children.Cast<UIElement>());
        Assert.Equal("All tasks", t.Bar.TaskListMoreText);

        // Right-aligned, on the chip row's own line, after the chip and the toggles.
        Rect In(FrameworkElement e) => e.TransformToAncestor(t.List).TransformBounds(new Rect(e.RenderSize));
        var chip = In(AddChip(t));
        var urgent = In(ButtonNamed(t.Chips, "Add as urgent"));
        Assert.True(In(more).Left >= urgent.Right - 0.5, $"button {In(more)} after the toggles {urgent}");
        Assert.True(In(more).Top < chip.Bottom, $"button {In(more)} on the chip's row {chip}");
        Assert.True(In(more).Right >= t.List.ActualWidth - 8, $"button {In(more)} at the right of a list {t.List.ActualWidth} wide");

        // Nothing under the lines: the footer is its box, the chip row and the lines, to the pixel.
        var lines = Inside(t.List).OfType<ScrollViewer>().Single(s => Equals(s.Tag, "task-lines"));
        Assert.True(t.List.ActualHeight <= In(lines).Bottom + 8, $"footer {t.List.ActualHeight} high, the lines end at {In(lines).Bottom}");
    });

    // The "+N more" count is the same button, in the same place, and clicking it opens the window from its own press.
    [Fact]
    public void The_more_count_is_in_the_chip_row_and_opens_the_window() => StaThread.Run(() =>
    {
        var opened = 0;
        using var t = Build(openWindow: () => opened++);
        Enumerable.Range(1, 5).ToList().ForEach(i => t.Tasks.AddNote($"Note {i}", t.Gepha));
        t.Bar.UpdateLayout();

        Assert.Equal("+2 more", t.Bar.TaskListMoreText);
        var more = ButtonNamed(t.List, "More tasks");
        Assert.Same(((FrameworkElement)t.Chips).Parent, more.Parent);

        Click(more);
        Assert.Equal(1, opened);
    });

    // At a narrow width the button shortens (to "All", or "+2") instead of wrapping to a line of its own or widening
    // the bar; its tooltip keeps the whole words, and the count the tests read stays the full sentence.
    [Fact]
    public void At_a_narrow_width_the_button_shortens_and_stays_on_the_row() => StaThread.Run(() =>
    {
        using var t = Build(openWindow: () => { });
        Enumerable.Range(1, 5).ToList().ForEach(i => t.Tasks.AddNote($"Note {i}", t.Gepha));
        t.Bar.ApplyWidth(140);
        t.Bar.RequestRebuild();
        t.Bar.UpdateLayout();

        Assert.Equal("+2 more", t.Bar.TaskListMoreText);
        Assert.Equal("+2", t.Bar.TaskListMoreShown);
        var more = ButtonNamed(t.List, "More tasks");
        t.Bar.UpdateLayout(); // the text changed by the row's own size change is laid out
        Assert.Contains("+2 more", (string)more.ToolTip);
        var bounds = more.TransformToAncestor(t.List).TransformBounds(new Rect(more.RenderSize));
        Assert.True(bounds.Right <= t.List.ActualWidth + 0.5, $"button {bounds} in a list {t.List.ActualWidth} wide");
        Assert.True(more.RenderSize.Width >= more.DesiredSize.Width - more.Margin.Left - more.Margin.Right - 0.5, $"the button is squeezed: {more.RenderSize.Width} of {more.DesiredSize.Width}");
    });

    // RULING 47 (Petre: "i'm not sure on the vscode icon and surely the space between icon and text is huge"). A Claude
    // line has no icon element on the bar, and its title starts at the same x as a note line's: nothing is reserved
    // between the tick box and the title.
    [Fact]
    public void A_claude_lines_title_starts_where_a_notes_does_and_it_has_no_icon() => StaThread.Run(() =>
    {
        using var t = Build(sessionWindow: _ => Maybe.From((nint)101));
        t.Tasks.MarkDone(t.Tasks.Create("Tasks redesign", t.Gepha, Session("s1")).Value.Id); // Done: a Claude task shows on the bar while it needs him (Ruling 48)
        t.Tasks.AddNote("Dishwasher tablets", t.Gepha);
        t.Bar.UpdateLayout();

        var claude = t.Lines.Single(l => ((TaskLine)l.Tag).Claudes);
        var note = t.Lines.Single(l => !((TaskLine)l.Tag).Claudes);
        Assert.DoesNotContain(Inside(claude).OfType<Image>(), _ => true);
        double TitleX(Button line)
        {
            var title = Inside(line).OfType<TextBlock>().Single(b => Equals(b.Tag, "task-text"));
            return title.TransformToAncestor(line).TransformBounds(new Rect(title.RenderSize)).Left;
        }
        Assert.Equal(TitleX(note), TitleX(claude), 0.5);
    });

    // RULING 48 (Petre: "when i have responded to a claude session that wanted my attention, i think it's okay if it gets
    // removed from the tasklist"; "not when i have activated the window, but when i have responded to it"). The bar lists
    // a Claude task only while it needs him: Waiting on you, or Done. In progress and Backlog Claude tasks leave the bar
    // and stay in the tasks window (the board holds them all). His notes are unaffected.
    static IReadOnlyList<string> OnTheBar(ListBar t) => t.Lines.Select(l => ((TaskLine)l.Tag).Title).ToList();

    [Fact]
    public void Only_a_claude_task_that_needs_him_is_on_the_bar_and_notes_always_are() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Tasks.AddNote("a note", t.Gepha);
        var working = t.Tasks.Create("working", t.Gepha, Session("s1")).Value;
        var waiting = t.Tasks.Create("waiting", t.Gepha, Session("s2")).Value;
        t.Tasks.Ask(waiting.Id, "OK?");
        var done = t.Tasks.Create("done", t.Gepha, Session("s3")).Value;
        t.Tasks.MarkDone(done.Id);

        Assert.Equal(["waiting", "a note", "done"], OnTheBar(t).OrderBy(x => x == "waiting" ? 0 : x == "a note" ? 1 : 2));
        Assert.DoesNotContain("working", OnTheBar(t));
        // The window shows everything: the board still holds the task, open.
        Assert.True(t.Tasks.Board.Tasks.Single(x => x.Id == working.Id).IsOpen);
    });

    // Answering a waiting task (his next prompt to the session, UserPromptSubmit) takes it off the bar but not out of the
    // board; a new ask brings it back at the top. Arriving at its workspace or clicking its line does not take it off.
    [Fact]
    public void Answering_removes_a_waiting_task_from_the_bar_and_a_new_ask_brings_it_back() => StaThread.Run(() =>
    {
        var went = new List<Guid>();
        using var t = Build(goTo: id =>
        {
            went.Add(id);
            return Result.Success();
        });
        t.Tasks.AddNote("a note", t.Gepha);
        var task = t.Tasks.Create("Ship it", t.Gepha, Session("s1")).Value;
        t.Tasks.Ask(task.Id, "Ship it?");
        Assert.Equal("Ship it", OnTheBar(t)[0]);

        // Activating: a click on the line goes to its workspace and the line stays.
        Click(t.Lines.Single(l => ((TaskLine)l.Tag).Id == task.Id));
        Assert.Contains("Ship it", OnTheBar(t));

        // His answer: the next prompt to that session.
        t.Tasks.Answered("s1");
        Assert.DoesNotContain("Ship it", OnTheBar(t));
        Assert.Equal(["a note"], OnTheBar(t));
        Assert.Equal(TaskStatus.InProgress, t.Tasks.Board.Tasks.Single(x => x.Id == task.Id).Status);

        // The session asks again: back on the bar, above the note.
        t.Tasks.Ask(task.Id, "And now?");
        Assert.Equal(["Ship it", "a note"], OnTheBar(t));
    });

    // "+N more" counts only what the bar lists: a Claude task in progress is not hidden by the cap, it is not on the bar.
    [Fact]
    public void The_more_count_ignores_claude_tasks_that_are_not_on_the_bar() => StaThread.Run(() =>
    {
        using var t = Build(openWindow: () => { });
        Enumerable.Range(1, 4).ToList().ForEach(i => t.Tasks.AddNote($"Note {i}", t.Gepha));
        Enumerable.Range(1, 3).ToList().ForEach(i => t.Tasks.Create($"Working {i}", t.Gepha, Session($"w{i}")));

        Assert.Equal("+1 more", t.Bar.TaskListMoreText);
    });

    // #48's class: a double-click goes only to the line its first click landed on. A reshuffle between the two presses
    // puts another task under the pointer, and the second press must not send him there.
    [Fact]
    public void A_double_click_after_a_reshuffle_does_not_go_to_the_other_line() => StaThread.Run(() =>
    {
        var went = new List<Guid>();
        using var t = Build(openTask: _ => { }, goTo: id =>
        {
            went.Add(id);
            return Result.Success();
        });
        var a = t.Tasks.AddNote("A", t.Gepha).Value.Id;
        var b = t.Tasks.AddNote("B", t.Gepha).Value.Id;
        Button LineFor(Guid id) => t.Lines.Single(l => ((TaskLine)l.Tag).Id == id);

        Click(LineFor(a)); // the first click lands on A...
        PressAndClick(LineFor(b), clickCount: 2); // ...the list reshuffles, and the second press lands on B
        Assert.Empty(went);

        PressAndClick(LineFor(a), clickCount: 2); // the same line twice goes
        Assert.Equal([a], went);
    });

    // The shortening of "All tasks" follows what fits (the measured widths), not a fixed width: at a large text size
    // the full words stop fitting at a width a fixed cut would have called wide, and the button must never clip.
    [Theory]
    [InlineData(200)]
    [InlineData(230)]
    [InlineData(260)]
    [InlineData(300)]
    public void The_all_tasks_button_never_clips_at_a_large_text_size(double width) => StaThread.Run(() =>
    {
        using var t = Build(openWindow: () => { });
        Enumerable.Range(1, 5).ToList().ForEach(i => t.Tasks.AddNote($"Note {i}", t.Gepha));
        Assert.True(t.Harness.Manager.SetBarTextOffset(3.5).IsSuccess);
        t.Bar.ApplyWidth(width);
        t.Bar.RequestRebuild();
        t.Bar.UpdateLayout();
        t.Bar.UpdateLayout();

        var more = ButtonNamed(t.List, "More tasks");
        var bounds = more.TransformToAncestor(t.List).TransformBounds(new Rect(more.RenderSize));
        Assert.True(bounds.Right <= t.List.ActualWidth + 0.5, $"button {bounds} in a list {t.List.ActualWidth} wide at {width}");
        var label = Inside(more).OfType<TextBlock>().Single();
        Assert.True(label.ActualWidth >= label.DesiredSize.Width - 0.5, $"'{label.Text}' is clipped: {label.ActualWidth} of {label.DesiredSize.Width}");
        var chips = Inside(t.Chips).OfType<Button>().Where(b => b.IsVisible).Select(b => b.TransformToAncestor(t.List).TransformBounds(new Rect(b.RenderSize))).ToList();
        Assert.All(chips, c => Assert.True(c.Right <= t.List.ActualWidth + 0.5 || c.Top > bounds.Top, "a chip is cut at the right"));
    });
    // --- Ruling 41: a title wraps to a second line when it does not fit, never a third ------------------------------

    static TextBlock TextBlockOf(Button line) => Inside(line).OfType<TextBlock>().Single(b => Equals(b.Tag, "task-text"));

    // A long title takes two lines and ends in an ellipsis after the second; a short one stays one line. The parts
    // beside the text stay in the first line's row.
    [Theory]
    [InlineData(158.4)]
    [InlineData(240)]
    public void A_long_title_wraps_to_two_lines_at_most_and_a_short_one_stays_one(double width) => StaThread.Run(() =>
    {
        using var t = Build();
        var shortId = t.Tasks.AddNote("Call the bank", t.Gepha).Value.Id;
        var longId = t.Tasks.AddNote("Send the invoices for November to every client, with the late fee, and chase the ones from October as well", t.Gepha).Value.Id;
        t.Bar.ApplyWidth(width);
        t.Bar.RequestRebuild();
        t.Bar.UpdateLayout();

        var shortText = TextBlockOf(t.Lines.Single(l => ((TaskLine)l.Tag).Id == shortId));
        var longText = TextBlockOf(t.Lines.Single(l => ((TaskLine)l.Tag).Id == longId));
        var oneLine = shortText.LineHeight;
        Assert.InRange(shortText.ActualHeight, oneLine - 0.5, oneLine + 0.5);
        Assert.InRange(longText.ActualHeight, 2 * oneLine - 0.5, 2 * oneLine + 0.5);
        Assert.Equal(TextTrimming.CharacterEllipsis, longText.TextTrimming);
        // Laid out at its width with no height limit it would need more than two lines: the second is cut, with "…".
        var unbounded = new TextBlock { FontSize = longText.FontSize, LineHeight = longText.LineHeight, LineStackingStrategy = longText.LineStackingStrategy,
            TextWrapping = TextWrapping.Wrap, Text = "Send the invoices for November to every client, with the late fee, and chase the ones from October as well" };
        unbounded.Measure(new Size(longText.ActualWidth, double.PositiveInfinity));
        Assert.True(unbounded.DesiredSize.Height > 2 * oneLine + 0.5, $"the probe title needs {unbounded.DesiredSize.Height}, two lines are {2 * oneLine}");
        // The chip (or its dot) stays in the first line's row of the two-line line.
        var line = t.Lines.Single(l => ((TaskLine)l.Tag).Id == longId);
        var end = Inside(line).OfType<FrameworkElement>().Single(e => (Equals(e.Tag, "task-workspace") || Equals(e.Tag, "task-workspace-dot")) && e.IsVisible);
        var endBounds = end.TransformToAncestor(longText.Parent as Visual ?? line).TransformBounds(new Rect(end.RenderSize));
        Assert.True(endBounds.Bottom <= oneLine + 1, $"the chip sits at {endBounds}, the first row is {oneLine} high");
    });

    // Two-line lines drag like one-line ones: a drop on the bottom part of a two-line line puts the dragged one below it.
    [Fact]
    public void A_two_line_line_drags_and_takes_a_drop_like_any_other() => StaThread.Run(() =>
    {
        var t = Build(openWindow: () => { });
        t.Bar.ArrangeMouseInput = ArrangeMouse.Held;
        t.Bar.ApplyWidth(158.4);
        t.Bar.SizeToContent = SizeToContent.Manual;
        t.Bar.Height = 900;
        t.Tasks.AddNote("n1", t.Gepha);
        t.Tasks.AddNote("a much longer note that will need two lines on the narrow bar for sure", t.Gepha);
        t.Tasks.AddNote("n3", t.Gepha);
        Settle(t);
        Assert.True(t.Lines[1].ActualHeight > t.Lines[0].ActualHeight + 4, "the middle line is two lines");

        Drag(t, t.Lines[0], In(t, t.Lines[1], 0.85));

        Assert.Equal(["a much longer note that will need two lines on the narrow bar for sure", "n3", "n1"], t.Tasks.Board.Pile().Select(x => x.Title));
        Assert.False(t.Bar.TaskDragging);
        t.Dispose();
    });

    // Review C-1's oscillation, again with one-line and two-line lines mixed (Claude lines with an icon, short notes, long
    // notes), at text offsets -2, 0, +1 and 3.5: once settled, one more pass changes nothing; and with the real queued
    // pass running on the dispatcher, Input-priority work still runs (a pass that kept re-queuing itself above Input
    // would starve it, the freeze C-1 was).
    [Fact]
    public void A_docked_list_of_one_and_two_line_lines_settles_and_lets_input_run() => StaThread.Run(() =>
    {
        var unsettled = new List<string>();
        var starved = new List<string>();
        new[] { -2.0, 0.0, 1.0, 3.5 }.ToList().ForEach(offset =>
        {
            using var t = Build(openWindow: () => { }, sessionWindow: _ => Maybe<nint>.From((nint)0x1234));
            t.Harness.Manager.SetBarTextOffset(offset);
            t.Bar.ApplyWidth(158.4);
            Enumerable.Range(1, 8).ToList().ForEach(i => t.Tasks.Create($"Claude {i}", t.Gepha, Session($"s{i}")));
            Enumerable.Range(1, 6).ToList().ForEach(i => t.Tasks.AddNote(i % 2 == 0 ? $"Note {i}" : $"Note {i}, which runs on long enough to need a second line on this bar", t.Gepha));
            t.Bar.RequestRebuild();
            t.Bar.SizeToContent = SizeToContent.Manual;
            for (var height = 400.0; height < 640; height += 1)
            {
                t.Bar.Height = height;
                Settle(t);
                var settled = t.Bar.TaskListSlotCount;
                t.Bar.UpdateLayout();
                t.Bar.FitTaskListToFreeHeight();
                t.Bar.UpdateLayout();
                if (t.Bar.TaskListSlotCount != settled) unsettled.Add($"offset {offset} height {height}: {settled} then {t.Bar.TaskListSlotCount}");
                if (height % 20 == 0)
                {
                    // The real path: a paint queues the pass at Loaded priority; the dispatcher runs it, and input with it.
                    t.Bar.PaintTaskList(evenUnderPress: true);
                    var ticks = InputTicksWhilePumping(t.Bar.Dispatcher, TimeSpan.FromMilliseconds(60));
                    if (ticks == 0) starved.Add($"offset {offset} height {height}");
                }
            }
        });

        Assert.True(unsettled.Count == 0, string.Join("\n", unsettled.Take(10)));
        Assert.True(starved.Count == 0, "input starved at " + string.Join(", ", starved.Take(10)));
    });

    // Pumps the dispatcher for `span`, posting an Input-priority tick that re-posts itself, and answers how many ran. A
    // safety timer above every other priority ends the pump after two seconds, so a starved dispatcher fails the test
    // instead of hanging it.
    static int InputTicksWhilePumping(Dispatcher dispatcher, TimeSpan span)
    {
        var ticks = 0;
        var frame = new DispatcherFrame();
        var end = DateTime.UtcNow + span;
        void Tick() => dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            ticks++;
            if (DateTime.UtcNow < end) Tick();
            else frame.Continue = false;
        });
        var safety = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Send, (_, _) => frame.Continue = false, dispatcher);
        Tick();
        Dispatcher.PushFrame(frame);
        safety.Stop();
        return ticks;
    }
}
