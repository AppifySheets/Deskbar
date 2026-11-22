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
        Func<nint, bool>? onCurrentDesktop = null)
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
            SessionWindow = sessionWindow ?? (_ => Maybe<nint>.None),
            ForegroundWindow = front ?? (() => Editor),
            HandForegroundTo = hwnd => handedBack?.Add(hwnd),
            WindowAlive = hwnd => hwnd == Editor && editorAlive,
            OnCurrentDesktop = onCurrentDesktop ?? (_ => true),
            TaskClickClock = clickClock ?? (() => ClickStart.AddSeconds(seconds++)),
            TaskUndoTimer = timers.Start,
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
        t.Tasks.SetStatusLine(task.Id, "waiting for CI on #398");
        t.Tasks.AddNote("Dishwasher tablets", Maybe<Guid>.None);

        var claude = t.Lines.Single(l => ((TaskLine)l.Tag).Id == task.Id);
        Assert.True(HasClaudeTag(claude));
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
        t.Tasks.Create("Tasks redesign", t.Gepha, Session("s1"));
        t.Tasks.AddNote("Dishwasher tablets", Maybe<Guid>.None);

        var claude = t.Lines.Single(l => ((TaskLine)l.Tag).Claudes);
        Assert.Equal((nint)101, ((TaskLine)claude.Tag).Window.Value);
        Assert.Contains(Inside(claude).OfType<FrameworkElement>(), e => Equals(e.Tag, "task-window-icon"));
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

    [Fact]
    public void A_done_claude_task_has_a_close_button() => StaThread.Run(() =>
    {
        using var t = Build();
        var working = t.Tasks.Create("Still going", t.Gepha, Session("s1")).Value;
        var done = t.Tasks.Create("Merged", t.Gepha, Session("s2")).Value;
        t.Tasks.MarkDone(done.Id);

        var workingLine = t.Lines.Single(l => ((TaskLine)l.Tag).Id == working.Id);
        Assert.False(HasButtonNamed(workingLine, "Close task"));
        Assert.False(HasButtonNamed(workingLine, "Tick off"));

        var doneLine = t.Lines.Single(l => ((TaskLine)l.Tag).Id == done.Id);
        Click(ButtonNamed(doneLine, "Close task"));

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
        Click(ButtonNamed(t.List, "none"));
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
    public void Clicking_a_line_goes_to_its_workspace() => StaThread.Run(() =>
    {
        var went = new List<Guid>();
        using var t = Build(goTo: id =>
        {
            went.Add(id);
            return Result.Success();
        });
        var errand = t.Tasks.AddNote("Dishwasher tablets", Maybe<Guid>.None).Value;
        var onGepha = t.Tasks.AddNote("Review the migration", t.Gepha).Value;

        Click(t.Lines.Single(l => ((TaskLine)l.Tag).Id == onGepha.Id));
        Click(t.Lines.Single(l => ((TaskLine)l.Tag).Id == errand.Id));

        // The note on no workspace has nowhere to go, so its click does nothing.
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
        Click(ButtonNamed(t.List, "none"));
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

        Click(ButtonNamed(t.List, "none"));

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

        Click(ButtonNamed(t.List, "none"));

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
        Click(ButtonNamed(t.List, "none"));
        typeof(Window).GetMethod("OnDeactivated", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(t.Bar, [EventArgs.Empty]);
        SwitchTo(gephaDesktop);
        Assert.True(t.Bar.TaskAddTarget.HasNoValue);
    });

    // A chip reads as chosen when it is filled and has the blue edge; the rest have neither.
    static readonly Color PickedBlue = Color.FromRgb(0x4F, 0x8C, 0xFF);

    static Border FaceOf(Button chip) => (Border)chip.Content;

    static bool LooksChosen(Button chip) =>
        FaceOf(chip).BorderBrush is SolidColorBrush { Color: var edge } && edge == PickedBlue
        && FaceOf(chip).Background is SolidColorBrush { Color.A: > 0 };

    static IReadOnlyList<string> ChosenChips(ListBar t) =>
        Inside(t.Chips).OfType<Button>().Where(b => b.Visibility == Visibility.Visible && LooksChosen(b))
            .Select(b => AutomationProperties.GetName(b)).ToList();

    // Petre's live check: he picked "none", its edge went blue, but the GEPHA chip stayed filled in its colour and the
    // box still said "Add to GEPHA…". Exactly one chip reads as chosen, and the box names the chosen target.
    [Fact]
    public void Picking_none_makes_only_none_look_chosen_and_the_box_says_add() => StaThread.Run(() =>
    {
        using var t = Build();
        Assert.Equal(["GEPHA"], ChosenChips(t));
        Assert.Equal("Add to GEPHA…", t.Bar.TaskAddPlaceholder);

        ClickBox(t);
        Click(ButtonNamed(t.List, "none"));

        Assert.Equal(["none"], ChosenChips(t));
        // The workspace chip keeps no fill of its own colour once it is not the one chosen.
        Assert.Equal(Colors.Transparent, ((SolidColorBrush)FaceOf(ButtonNamed(t.List, "GEPHA")).Background).Color);
        Assert.Equal("Add…", t.Bar.TaskAddPlaceholder);

        Click(ButtonNamed(t.List, "GEPHA"));
        Assert.Equal(["GEPHA"], ChosenChips(t));
        Assert.Equal("Add to GEPHA…", t.Bar.TaskAddPlaceholder);
    });

    [Fact]
    public void A_workspace_picked_through_other_is_named_in_the_box() => StaThread.Run(() =>
    {
        using var t = Build();
        ClickBox(t);

        var menu = t.Bar.OpenOtherWorkspaces()!;
        var sparrow = menu.Items.OfType<MenuItem>().Single(i => Inside(i.Header).OfType<TextBlock>().Any(x => x.Text == "Sparrow"));
        sparrow.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.IsOpen = false;

        Assert.Equal("Add to Sparrow…", t.Bar.TaskAddPlaceholder);
        Assert.Equal(["Sparrow"], ChosenChips(t));
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

        // ...whose line stays where it was with "Undo" in the tick's place, so the second press of the double click lands
        // on Undo rather than on Note 4's tick, and is dropped there too: the note stays closed and Note 4 stays open.
        now = now.AddMilliseconds(600);
        Assert.True(Closing(t.Lines[0]));
        PressAndClick(ButtonNamed(t.Lines[0], "Undo"), clickCount: 2);
        PressAndClick(ButtonNamed(t.Lines[1], "Tick off"), clickCount: 2);
        PressAndClick(t.Lines[1], clickCount: 2);
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

        Click(ButtonNamed(t.Lines.Single(l => ((TaskLine)l.Tag).Id == done.Id), "Close task"));
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
            .Select(b => (TextBlock)b.Child).ToList();
        Assert.True(labels.Count >= 4);
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

    // A Done task's close button behaves the same: its line lingers with Undo in the close button's place, and Undo
    // brings it back Done.
    [Fact]
    public void A_done_tasks_close_button_has_the_same_undo() => StaThread.Run(() =>
    {
        using var t = Build();
        var done = t.Tasks.Create("Merged", t.Gepha, Session("s1")).Value;
        t.Tasks.MarkDone(done.Id);

        Click(ButtonNamed(t.Lines[0], "Close task"));
        Assert.Equal(TaskStatus.Closed, t.Tasks.Board.Tasks.Single(x => x.Id == done.Id).Status);
        Assert.True(Closing(t.Lines[0]));
        Assert.False(HasButtonNamed(t.Lines[0], "Close task"));

        Click(ButtonNamed(t.Lines[0], "Undo"));
        Assert.Equal(TaskStatus.Done, t.Tasks.Board.Tasks.Single(x => x.Id == done.Id).Status);
        Assert.False(Closing(Assert.Single(t.Lines)));
        Assert.True(HasButtonNamed(t.Lines[0], "Close task"));
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

    // The words, without a window: when the store says it kept the file, the line promises the .bak; when it could
    // not move the file aside, the line says to copy it somewhere safe instead.
    [Fact]
    public void The_failed_load_line_promises_a_bak_only_when_one_was_kept()
    {
        Assert.Contains(".bak", FloatingBar.LoadProblemLine("Could not read tasks.json: bad; kept tasks.json as tasks.json.20261122-090000.bak"));
        Assert.Contains("copy it somewhere safe", FloatingBar.LoadProblemLine("Could not read tasks.json: bad; could not keep C:\\x\\tasks.json aside: locked"));
    }
}
