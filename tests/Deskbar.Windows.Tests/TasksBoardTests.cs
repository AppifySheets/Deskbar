using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core.Tasks;

namespace Deskbar.Windows.Tests;

// The tasks window and its controller (TasksWindow, TasksBoard; docs/superpowers/specs/2026-11-22-tasks-board-design.md,
// "The window", "Comments", "Statuses", "Going to a session"). The window is display only; the controller decides what
// it shows and what each click does. Built over the shared two-workspace harness (GEPHA current, Sparrow beside it)
// with a real TaskService on an in-memory store, so every change goes through the service the App hands the board.
// The window is the docked tasks panel (plan 2026-11-23-tasks-panel, Task 6), behind a TasksPanel whose seams are all
// faked (TasksPanelRig): it docks on a made-up screen far off every real monitor, so a run never reserves a strip of
// a real screen, and it is Quiet, never activating itself, so a run neither flashes it nor takes the keyboard.
public class TasksBoardTests
{
    sealed class MemoryStore : ITaskStore
    {
        public Result<TaskBoard> Load() => TaskBoard.Empty;

        public Result Save(TaskBoard board) => Result.Success();
    }

    static readonly DateTimeOffset Start = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    // The undo window's timers, fired by the test (TasksBoard's undoTimer seam), as in the bar's task list tests.
    sealed class FakeTimers
    {
        sealed class Handle : IDisposable
        {
            public bool Stopped { get; private set; }

            public void Dispose() => Stopped = true;
        }

        readonly List<(Action Then, Handle Handle)> started = [];

        public IDisposable Start(TimeSpan after, Action then)
        {
            var handle = new Handle();
            started.Add((then, handle));
            return handle;
        }

        public void FireAll() => started.Where(t => !t.Handle.Stopped).ToList().ForEach(t =>
        {
            t.Handle.Dispose();
            t.Then();
        });
    }

    sealed class Board : IDisposable
    {
        public required Harness Harness { get; init; }
        public required TasksWindow Window { get; init; }
        public required TasksBoard Controller { get; init; }
        public required TasksPanelRig Panel { get; init; }
        public required TaskService Tasks { get; init; }
        public required FakeTimers Timers { get; init; }
        public required List<Guid> WentTo { get; init; }

        public Guid Gepha => Harness.Manager.State.Workspaces.Single(w => w.Name == "GEPHA").Id;

        public Guid Sparrow => Harness.Manager.State.Workspaces.Single(w => w.Name == "Sparrow").Id;

        public void Dispose()
        {
            Controller.Dispose();
            Window.CloseForGood();
        }
    }

    static readonly DateTime ClickStart = new(2026, 11, 22, 9, 0, 0, DateTimeKind.Utc);

    // The panel's saved width in most tests: past TasksPanelLayout.MatrixFrom (780), so the full line and the List / Matrix
    // switch both show (Task 8). It was the floating window's 720 DIP until the switch was gated on 780.
    const double Wide = 800;

    // Each read of the task clock is a minute later, so tasks are raised in the order the test makes them. Each read of
    // the click clock is a second later, so a click is never "right after a repaint" unless a test hands in a clock of
    // its own (the click guard's test does).
    // `bringForward` sees the handle the board brings forward (the App's raise and activate, TasksBoard.Arrive), and the
    // seam answers that it activated the window; `activates` overrides that answer (a panel off the desktop in front).
    // Task 9's seams: a task's sessions as its thread lists them, "Start Claude on this" and a session's Open.
    // `edge` is the edge the bar docks on, so the panel docks there too (the grip's side follows it).
    static Board Build(Func<TaskItem, Result>? goTo = null, Action<nint>? bringForward = null, Func<DateTime>? clickClock = null,
        Func<DateTimeOffset>? now = null, TaskSelection? selection = null, Func<bool>? activates = null,
        Func<TaskItem, IReadOnlyList<SessionView>>? sessionsOf = null, Action<Guid>? startClaude = null,
        Func<TaskItem, string, Result>? openSession = null, IObservable<System.Reactive.Unit>? sessionsChanged = null,
        string edge = "right", double width = Wide, Func<TaskItem, int>? workingOf = null, double? screenDips = null,
        string? stateDir = null, Action<string>? openImage = null)
    {
        var harness = Harness.Build();
        var minutes = 0;
        var seconds = 0;
        var timers = new FakeTimers();
        var went = new List<Guid>();
        var tasks = new TaskService(new MemoryStore(), () => Start.AddMinutes(minutes++));
#pragma warning disable WPF0001 // ThemeMode is experimental: the app runs under ThemeMode="System" (App.xaml), so the window tests do too
        var window = new TasksWindow
        {
            ThemeMode = ThemeMode.System,
            Quiet = true, ClickClock = clickClock ?? (() => ClickStart.AddSeconds(seconds++)), Now = now ?? (() => Start.AddHours(1)),
            // Images in tasks: a scratch state folder, and a click on a picture recorded instead of opening a viewer.
            StateDir = stateDir ?? Path.GetTempPath(), OpenImage = openImage ?? (_ => { }),
        };
#pragma warning restore WPF0001
        // A panel wide enough for everything (Wide, below): the full line and the Matrix switch both need it. The slim line
        // below 710 DIP and the switch's 780 are Task 8's, with their own tests passing a `width`.
        Assert.True(harness.Manager.SaveTasksPanel(new Deskbar.Core.Persistence.TasksPanelState(false, width)).IsSuccess);
        var panel = new TasksPanelRig(window).Docked(edge, screenDips);
        var controller = new TasksBoard(window, panel.Panel, tasks, harness.Manager, goTo ?? (t =>
        {
            went.Add(t.Id);
            return Result.Success();
        }), bringForward: bringForward is null && activates is null ? null : hwnd =>
        {
            bringForward?.Invoke(hwnd);
            return activates?.Invoke() ?? true;
        }, undoTimer: timers.Start, now: () => Start.AddHours(1), selection: selection,
            sessionsOf: sessionsOf, startClaude: startClaude, openSession: openSession, sessionsChanged: sessionsChanged,
            workingOf: workingOf);
        return new Board { Harness = harness, Window = window, Controller = controller, Panel = panel, Tasks = tasks, Timers = timers, WentTo = went };
    }

    // A press with the click count Windows gives it (2 for the second press of a double click), then its Click, as the
    // bar's task list tests do: ClickCount has no public setter.
    static void PressAndClick(Button button, int clickCount)
    {
        var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent };
        typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!.SetValue(press, clickCount);
        button.RaiseEvent(press);
        Click(button);
    }

    static TaskSession Session(string id, bool live = true) =>
        new(id, @"C:\repos\TaskSpaces", Start, live ? Maybe<DateTimeOffset>.None : Start.AddMinutes(30));

    static IEnumerable<object> Inside(object node) =>
        new[] { node }.Concat(node is DependencyObject d ? LogicalTreeHelper.GetChildren(d).Cast<object>().SelectMany(Inside) : []);

    static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    static Button ButtonNamed(object scope, string name) =>
        Inside(scope).OfType<Button>().First(b => AutomationProperties.GetName(b) == name);

    static bool HasButtonNamed(object scope, string name) =>
        Inside(scope).OfType<Button>().Any(b => AutomationProperties.GetName(b) == name);

    // A line of the window: the button that carries its task's id.
    static Button LineOf(TasksWindow window, Guid id) =>
        Inside(window).OfType<Button>().Single(b => b.Tag is Guid tag && tag == id);

    // Task 12 (Ruling 28): the List view is grouped as the bar, Waiting on you, then Covey's quadrants under captions in
    // his words; a Done task sits in the Waiting on you block under the questions (task eefb986a). Task 93fa0b9b: the task
    // Claude is at work on sits in the Claude working block between that block and the quadrants.
    [Fact]
    public void The_board_groups_open_tasks_as_the_bar() => StaThread.Run(() =>
    {
        using var t = Build();
        var note = t.Tasks.AddNote("Dishwasher tablets", Maybe<Guid>.None).Value.Id;
        var working = t.Tasks.Create("DurableFile move guard", t.Gepha, Session("s1")).Value.Id;
        var asking = t.Tasks.Create("Tasks redesign", t.Gepha, Session("s2")).Value.Id;
        var done = t.Tasks.Create("Store screenshots", t.Gepha, Session("s3")).Value.Id;
        var closed = t.Tasks.AddNote("Old errand", Maybe<Guid>.None).Value.Id;
        t.Tasks.Ask(asking, "OK to restart Deskbar?");
        t.Tasks.MarkDone(done);
        t.Tasks.Close(closed);

        t.Controller.Show();

        Assert.Equal(["Waiting on you", "Claude working", "IV  Not urgent, not important"], t.Window.Headers);
        Assert.Equal([asking, done, working, note], t.Window.Keys);

        // Closed only under its own filter.
        t.Controller.Filter(TaskFilter.Closed);
        Assert.Equal(["Closed"], t.Window.Headers);
        Assert.Equal([closed], t.Window.Keys);
    });

    [Fact]
    public void Filters_by_status_and_workspace() => StaThread.Run(() =>
    {
        using var t = Build();
        var here = t.Tasks.AddNote("here", t.Gepha).Value.Id;
        var there = t.Tasks.AddNote("there", t.Sparrow).Value.Id;
        var nowhere = t.Tasks.AddNote("nowhere", Maybe<Guid>.None).Value.Id;
        var asking = t.Tasks.Create("asking", t.Sparrow, Session("s1")).Value.Id;
        t.Tasks.Ask(asking, "?");
        t.Controller.Show();

        // The chips count what they would show.
        Assert.Contains("Open 4", t.Window.FilterLabels);
        Assert.Contains("Waiting on you 1", t.Window.FilterLabels);

        t.Controller.Filter(TaskFilter.WaitingOnYou);
        Assert.Equal([asking], t.Window.Keys);

        t.Controller.Filter(TaskFilter.Open);
        t.Controller.FilterWorkspace(TaskWorkspaceFilter.On(t.Sparrow));
        Assert.Equal([asking, there], t.Window.Keys);
        Assert.Contains("Open 2", t.Window.FilterLabels);
        Assert.Equal("Workspace: Sparrow", t.Window.WorkspaceFilterLabel);

        t.Controller.FilterWorkspace(TaskWorkspaceFilter.On(Maybe<Guid>.None));
        Assert.Equal([nowhere], t.Window.Keys);

        t.Controller.FilterWorkspace(TaskWorkspaceFilter.All);
        Assert.Equal([asking, nowhere, there, here], t.Window.Keys);
    });

    // Task ed1a01fc, Petre: "when i change workspace, it should only show me current workspace tasks, when selected".
    // "Current workspace" follows the desktop in front: a switch narrows the list, the chips' counts and the label to
    // the new workspace without another pick, and a desktop with no name shows the tasks on no workspace.
    [Fact]
    public void The_current_workspace_filter_follows_a_workspace_switch() => StaThread.Run(() =>
    {
        using var t = Build();
        var here = t.Tasks.AddNote("here", t.Gepha).Value.Id;
        var there = t.Tasks.AddNote("there", t.Sparrow).Value.Id;
        var nowhere = t.Tasks.AddNote("nowhere", Maybe<Guid>.None).Value.Id;
        SwitchTo(t, t.Gepha);
        t.Controller.Show();

        t.Controller.FilterWorkspace(TaskWorkspaceFilter.Current);
        Assert.Equal([here], t.Window.Keys);
        Assert.Contains("Open 1", t.Window.FilterLabels);
        Assert.Equal("Workspace: GEPHA", t.Window.WorkspaceFilterLabel);

        SwitchTo(t, t.Sparrow);
        Assert.Equal([there], t.Window.Keys);
        Assert.Equal("Workspace: Sparrow", t.Window.WorkspaceFilterLabel);

        t.Controller.FilterWorkspace(TaskWorkspaceFilter.All);
        Assert.Equal([nowhere, there, here], t.Window.Keys);
    });

    // Task 07d4b83d, Petre: "current workspace takes a long time to refresh the tasks when i change workspace". Windows
    // raises its desktop change on a thread of its own (an MTA one), and the manager pulses StateChanged from there. The
    // board used to redraw on that thread: WPF threw ("The calling thread must be STA"), the manager's catch swallowed
    // it, and DistinctUntilChanged had already taken the new workspace as seen, so the list stayed on the old workspace
    // until some unrelated change (a task, a session list) redrew it. The switch below is driven from such a thread, and
    // the list must follow as soon as the dispatcher runs what was queued for it.
    [Fact]
    public void The_current_workspace_filter_follows_a_switch_raised_on_another_thread() => StaThread.Run(() =>
    {
        using var t = Build();
        var here = t.Tasks.AddNote("here", t.Gepha).Value.Id;
        var there = t.Tasks.AddNote("there", t.Sparrow).Value.Id;
        SwitchTo(t, t.Gepha);
        t.Controller.Show();
        t.Controller.FilterWorkspace(TaskWorkspaceFilter.Current);
        Assert.Equal([here], t.Window.Keys);

        var desktop = t.Harness.Manager.State.Workspaces.Single(w => w.Id == t.Sparrow).DesktopId!.Value;
        t.Harness.Desktops.CurrentId = desktop;
        var windows = new Thread(() =>
            ((System.Reactive.Subjects.Subject<Guid>)t.Harness.Desktops.CurrentChanged).OnNext(desktop));
        windows.SetApartmentState(ApartmentState.MTA);
        windows.Start();
        windows.Join();
        t.Window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

        Assert.Equal([there], t.Window.Keys);
        Assert.Equal("Workspace: Sparrow", t.Window.WorkspaceFilterLabel);
    });

    // The one filter a fresh open keeps (AppState.TasksPanel.CurrentWorkspace): it is how Petre works, and the panel
    // reopens at every start. Picking any other workspace filter forgets it.
    [Fact]
    public void The_current_workspace_filter_survives_a_fresh_open_until_another_is_picked() => StaThread.Run(() =>
    {
        using var t = Build();
        var here = t.Tasks.AddNote("here", t.Gepha).Value.Id;
        t.Tasks.AddNote("there", t.Sparrow);
        SwitchTo(t, t.Gepha);
        t.Controller.Show();
        t.Controller.FilterWorkspace(TaskWorkspaceFilter.Current);
        Assert.True(t.Harness.Manager.State.TasksPanel!.CurrentWorkspace);

        t.Controller.Hide();
        t.Controller.Show();
        Assert.Equal([here], t.Window.Keys);
        Assert.Equal("Workspace: GEPHA", t.Window.WorkspaceFilterLabel);

        t.Controller.FilterWorkspace(TaskWorkspaceFilter.On(t.Sparrow));
        Assert.False(t.Harness.Manager.State.TasksPanel!.CurrentWorkspace);
        t.Controller.Hide();
        t.Controller.Show();
        Assert.Equal("Workspace: all", t.Window.WorkspaceFilterLabel);
    });

    // Task 93fa0b9b: the Claude working chip is kept across opens like the Current workspace checkbox, in
    // AppState.TasksPanel.ClaudeWorking, and read as a checkbox's state. Showing a task it hides turns it off for good.
    [Fact]
    public void The_Claude_working_chip_survives_a_fresh_open_until_it_is_turned_off() => StaThread.Run(() =>
    {
        using var t = Build();
        var note = t.Tasks.AddNote("note", t.Gepha).Value.Id;
        var live = t.Tasks.Create("DurableFile move guard", t.Gepha, Session("s1")).Value.Id;
        t.Controller.Show();
        Assert.Equal("unchecked", AutomationProperties.GetItemStatus(ButtonNamed(t.Window, "Filter: Claude working")));

        Click(ButtonNamed(t.Window, "Filter: Claude working"));
        Assert.True(t.Harness.Manager.State.TasksPanel!.ClaudeWorking);
        Assert.Equal("checked", AutomationProperties.GetItemStatus(ButtonNamed(t.Window, "Filter: Claude working")));

        t.Controller.Hide();
        t.Controller.Show();
        Assert.True(t.Window.ClaudeWorkingOn);
        Assert.Equal([live], t.Window.Keys);

        // The note has no live session, so showing it turns the chip off, and the next open starts with it off.
        t.Controller.ShowTask(note);
        Assert.False(t.Harness.Manager.State.TasksPanel!.ClaudeWorking);
        t.Controller.Hide();
        t.Controller.Show();
        Assert.False(t.Window.ClaudeWorkingOn);
        Assert.Equal(2, t.Window.Keys.Count);
    });

    // Task 93fa0b9b: the line Claude is at work on sits in the Claude working block, after Waiting on you and before the
    // quadrants, captioned. It drags as a line of its quadrant does (not as a pinned line), since the block keeps quadrant
    // order and a drop among its lines is a place in that order.
    [Fact]
    public void A_task_Claude_works_on_sits_after_waiting_on_you_and_drags_as_its_quadrant() => StaThread.Run(() =>
    {
        using var t = Build();
        var note = t.Tasks.AddNote("note", t.Gepha).Value.Id;
        var working = t.Tasks.Create("working", t.Gepha, Session("s1")).Value.Id;
        var asking = t.Tasks.Create("asking", t.Gepha, Session("s2")).Value.Id;
        t.Tasks.Ask(asking, "Merge it?");
        t.Controller.Show();

        Assert.Equal([asking, working, note], t.Window.Keys);
        Assert.Equal(["Waiting on you", "Claude working", "IV  Not urgent, not important"], t.Window.Headers);
        Assert.False(t.Window.IsPinnedLine(working));
        Assert.True(t.Window.IsPinnedLine(asking));
        Assert.False(t.Window.IsPinnedLine(note));
    });

    // A desktop switch as the tests drive one: the fake desktops say the workspace's desktop is in front, and pulse.
    static void SwitchTo(Board t, Guid workspace)
    {
        var desktop = t.Harness.Manager.State.Workspaces.Single(w => w.Id == workspace).DesktopId!.Value;
        t.Harness.Desktops.CurrentId = desktop;
        ((System.Reactive.Subjects.Subject<Guid>)t.Harness.Desktops.CurrentChanged).OnNext(desktop);
        Assert.Equal(workspace, t.Harness.Manager.CurrentWorkspaceId.Value);
    }

    [Fact]
    public void A_line_expands_to_its_thread() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.Create("Tasks redesign", t.Gepha, Session("s1")).Value.Id;
        t.Tasks.Comment(id, "s1", "The branch is **built**.");
        t.Tasks.Comment(id, Maybe<string>.None, "yes, swap");
        t.Controller.Show();
        Assert.False(t.Window.ThreadShown(id));

        // The count on the line says how many, and its arrow opens the thread.
        Click(ButtonNamed(LineOf(t.Window, id), "Comments"));

        Assert.True(t.Window.ThreadShown(id));
        Assert.Equal(["Claude", "you"], t.Window.CommentAuthors(id));

        Click(ButtonNamed(LineOf(t.Window, id), "Comments"));
        Assert.False(t.Window.ThreadShown(id));
    });

    [Fact]
    public void Petre_can_comment_and_it_shows_as_you() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("Ask about the runner disk", t.Gepha).Value.Id;
        t.Controller.Show();
        t.Controller.ToggleThread(id);

        Assert.True(t.Window.TypeComment(id, "Check **D:** first", Key.Enter));

        var comment = Assert.Single(t.Tasks.Board.Tasks.Single(x => x.Id == id).Comments);
        Assert.True(comment.SessionId.HasNoValue);
        Assert.Equal("Check **D:** first", comment.Markdown);
        Assert.Equal(["you"], t.Window.CommentAuthors(id));
        // The box is empty again, ready for the next one, and the thread stays open.
        Assert.Equal("", t.Window.CommentDraft(id));
        Assert.True(t.Window.ThreadShown(id));
    });

    [Fact]
    public void Shift_enter_starts_a_new_line_rather_than_posting() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("note", t.Gepha).Value.Id;
        t.Controller.Show();
        t.Controller.ToggleThread(id);

        Assert.False(t.Window.TypeComment(id, "first line", Key.Enter, ModifierKeys.Shift));

        Assert.Empty(t.Tasks.Board.Tasks.Single(x => x.Id == id).Comments);
    });

    [Fact]
    public void Close_reopen_and_set_status_from_the_window() => StaThread.Run(() =>
    {
        using var t = Build();
        var note = t.Tasks.AddNote("Dishwasher tablets", t.Gepha).Value.Id;
        t.Controller.Show();

        // Set by hand.
        Assert.True(t.Controller.SetStatus(note, TaskStatus.InProgress).IsSuccess);
        Assert.Equal(["IV  Not urgent, not important"], t.Window.Headers);
        // Closed is not a status to set: Close is the way.
        Assert.True(t.Controller.SetStatus(note, TaskStatus.Closed).IsFailure);

        // The tick closes it at once, and the line stays, struck through with Undo, for the undo window.
        Click(ButtonNamed(LineOf(t.Window, note), "Close"));
        Assert.Equal(TaskStatus.Closed, t.Tasks.Board.Tasks.Single(x => x.Id == note).Status);
        Assert.Equal([note], t.Window.Keys);
        Assert.True(t.Window.IsClosing(note));

        // Undo means exactly undo (Ruling 31), by the bar's rule, so the two Undos agree: the status it had before the
        // close comes back, here In progress, set by hand on a note with no session.
        Click(ButtonNamed(LineOf(t.Window, note), "Undo"));
        Assert.Equal(TaskStatus.InProgress, t.Tasks.Board.Tasks.Single(x => x.Id == note).Status);
        Assert.False(t.Window.IsClosing(note));

        // Closed for good once the six seconds are over: it leaves the open view...
        Assert.True(t.Controller.Close(note).IsSuccess);
        t.Timers.FireAll();
        Assert.Empty(t.Window.Keys);

        // ...and the Closed filter has it, with Reopen.
        t.Controller.Filter(TaskFilter.Closed);
        Assert.Equal([note], t.Window.Keys);
        Click(ButtonNamed(LineOf(t.Window, note), "Reopen"));
        Assert.Equal(TaskStatus.Backlog, t.Tasks.Board.Tasks.Single(x => x.Id == note).Status);
        Assert.Empty(t.Window.Keys);
    });

    [Fact]
    public void Rename_move_and_delete_from_the_window() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("Dishwasher", t.Gepha).Value.Id;
        t.Controller.Show();

        Assert.True(t.Controller.Rename(id, "Dishwasher tablets").IsSuccess);
        Assert.True(t.Controller.MoveTo(id, t.Sparrow).IsSuccess);
        var task = t.Tasks.Board.Tasks.Single(x => x.Id == id);
        Assert.Equal("Dishwasher tablets", task.Title);
        Assert.Equal(t.Sparrow, task.WorkspaceId.Value);

        Assert.True(t.Controller.Delete(id).IsSuccess);
        Assert.Empty(t.Tasks.Board.Tasks);
        Assert.Empty(t.Window.Keys);
    });

    [Fact]
    public void Add_is_one_button_that_opens_a_box() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Controller.Show();
        Assert.False(t.Window.AddBoxShown);

        Click(ButtonNamed(t.Window, "Add"));
        Assert.True(t.Window.AddBoxShown);

        Assert.True(t.Window.TypeNote("Call the bank", Key.Enter));
        var task = Assert.Single(t.Tasks.Board.Tasks);
        Assert.Equal("Call the bank", task.Title);
        // On the workspace in front (GEPHA, the harness's current one).
        Assert.Equal(t.Gepha, task.WorkspaceId.Value);
        Assert.False(t.Window.AddBoxShown);
    });

    // Task ee75f046, option 3 of the mockup: the panel's add box highlights what it adds as the bar's does. The new task
    // is picked (in both views, one selection), its line glows while Windows' animations are on (TaskGlow), and a
    // search that would hide it is cleared so the line is there to see.
    [Fact]
    public void A_note_added_in_the_panel_is_selected_and_glows() => StaThread.Run(() =>
    {
        var selection = new TaskSelection();
        using var t = Build(selection: selection);
        var earlier = t.Tasks.AddNote("Pay rent", t.Gepha).Value.Id;
        t.Controller.Show();
        t.Controller.Pick(earlier);
        t.Controller.Search("rent");

        Click(ButtonNamed(t.Window, "Add"));
        Assert.True(t.Window.TypeNote("Call the bank", Key.Enter));

        var added = t.Tasks.Board.Tasks.Single(x => x.Title == "Call the bank").Id;
        Assert.True(selection.IsSelected(added));
        Assert.Contains(added, t.Window.Keys);
        Assert.True(t.Window.IsSelected(added));
        Assert.False(t.Window.IsSelected(earlier));
        Assert.True(t.Window.IsGlowing(added), "the new line glows, animations on or off");
    });

    [Fact]
    public void The_window_opens_on_the_board_not_a_form() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("note", t.Gepha).Value.Id;
        t.Controller.Show();

        Assert.True(t.Window.IsOpen);
        Assert.False(t.Window.AddBoxShown);
        Assert.DoesNotContain(Inside(t.Window).OfType<TextBox>(), b => b.IsKeyboardFocused || b.IsFocused);
        Assert.Equal([id], t.Window.Keys);

        // Opened again after typing in a thread, it is still the board, filters reset and threads closed.
        t.Controller.ToggleThread(id);
        t.Controller.Filter(TaskFilter.Closed);
        t.Controller.Hide();
        t.Controller.Show();
        Assert.Equal([id], t.Window.Keys);
        Assert.False(t.Window.ThreadShown(id));
        Assert.DoesNotContain(Inside(t.Window).OfType<TextBox>(), b => b.IsKeyboardFocused || b.IsFocused);
    });

    // Controller ruling 13: until the `vscode://` session link is measured, a Claude task goes through the same goTo
    // the bar uses (its workspace). The seam hands over the whole task, so the later change only swaps the function;
    // TaskViews.SessionToGoTo is the session it will open (the live waiting session first, else the latest).
    [Fact]
    public void Clicking_a_claude_task_goes_to_its_session() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.Create("Tasks redesign", Maybe<Guid>.None, Session("old")).Value.Id;
        t.Tasks.Join(id, Session("new"));
        t.Tasks.SessionEnded("new");
        t.Controller.Show();

        DoubleClick(t.Window, id); // a double-click goes (Ruling 54)

        Assert.Equal([id], t.WentTo);
        // A page of its own: it stays open behind the switch.
        Assert.True(t.Window.IsOpen);
        Assert.Equal("old", TaskViews.SessionToGoTo(t.Tasks.Board.Tasks.Single(x => x.Id == id)).Value.Id);
    });

    // The panel (spec 2026-11-23-tasks-panel-design.md, "The panel window"; it replaced the regular window "It_is_a_regular_
    // window" pinned): no title bar, not resizable by Windows (the grip is its own), not on the taskbar, not in Alt+Tab
    // (WS_EX_TOOLWINDOW), topmost like the bar, and with no minimum width of its own (the panel's 360 DIP floor is
    // TasksPanelLayout's, applied when it docks).
    [Fact]
    public void It_is_a_panel_not_a_regular_window() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Controller.Show();

        Assert.Equal("Tasks", t.Window.Title);
        Assert.Equal(WindowStyle.None, t.Window.WindowStyle);
        Assert.Equal(ResizeMode.NoResize, t.Window.ResizeMode);
        Assert.False(t.Window.ShowInTaskbar);
        Assert.True(t.Window.Topmost);
        Assert.Equal(0, t.Window.MinWidth);
        var exStyle = (long)Deskbar.Windows.Monitoring.NativeMethods.GetWindowLongPtr(t.Controller.EnsureHandle(),
            Deskbar.Windows.Monitoring.NativeMethods.GWL_EXSTYLE);
        Assert.NotEqual(0, exStyle & Deskbar.Windows.Monitoring.NativeMethods.WS_EX_TOOLWINDOW);
    });

    // --- Task 6 of the tasks panel plan: Show, Hide, Toggle, the saved state, the grip --------------------------------

    // The bar's "All tasks" line toggles the panel (spec "Opening and closing").
    [Fact]
    public void Toggle_shows_a_hidden_panel_and_hides_a_shown_one() => StaThread.Run(() =>
    {
        using var t = Build();
        Assert.False(t.Controller.IsOpen);

        t.Controller.Toggle();
        Assert.True(t.Controller.IsOpen);
        Assert.True(t.Window.IsOpen);
        Assert.True(t.Window.IsVisible);
        Assert.Equal(["Undock", $"DockWith Right {Px(t, Wide)}"], t.Panel.Calls);

        t.Controller.Toggle();
        Assert.False(t.Controller.IsOpen);
        Assert.False(t.Window.IsOpen);
        Assert.False(t.Window.IsVisible);
        // Hiding gives the strip back to the shell.
        Assert.Equal("Undock", t.Panel.Calls[^1]);
    });

    // Every Show and Hide saves whether the panel is open, with its width, so the next start opens it as it was left
    // (AppState.TasksPanel). The width is the one saved, never reset by opening or closing.
    [Fact]
    public void Showing_and_hiding_remember_whether_the_panel_is_open() => StaThread.Run(() =>
    {
        using var t = Build();

        t.Controller.Show();
        Assert.Equal(new Deskbar.Core.Persistence.TasksPanelState(true, Wide), t.Harness.Manager.State.TasksPanel);

        t.Controller.Hide();
        Assert.Equal(new Deskbar.Core.Persistence.TasksPanelState(false, Wide), t.Harness.Manager.State.TasksPanel);
    });

    // The app exiting is not the user closing the panel: Shutdown saves it as open when it was, before the panel's own
    // shutdown gives the strip back and hides the window, so it opens again on the next start.
    [Fact]
    public void Shutting_down_an_open_panel_keeps_it_open_for_the_next_start() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Controller.Show();

        t.Controller.Shutdown();

        Assert.Equal(new Deskbar.Core.Persistence.TasksPanelState(true, Wide), t.Harness.Manager.State.TasksPanel);
        Assert.Equal("Undock", t.Panel.Calls[^1]);
    });

    // TASK 6 REVIEW, IMPORTANT 1. Application.Shutdown closes every window, and a close of the panel's window used to
    // read as the user's Alt+F4 (OnClosing, ClosePanel, Hide), saving the panel closed: after a sign-out or the update's
    // hand-over an open panel did not come back. Every app-shutdown path (ExitApp, the update's hand-over, SessionEnding)
    // now calls the board's Shutdown before Application.Shutdown, so the close that follows is the app's, and the panel
    // stays saved as open. Here the second step is what Application.Shutdown does to the window.
    [Fact]
    public void An_open_panel_whose_window_the_app_closes_stays_saved_as_open() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Controller.Show();

        t.Controller.Shutdown();
        t.Window.Close();
        // A late Hide (a queued pipe command, the bar's toggle) cannot undo the state saved for the next start either.
        t.Controller.Hide();

        Assert.Equal(new Deskbar.Core.Persistence.TasksPanelState(true, Wide), t.Harness.Manager.State.TasksPanel);
        Assert.True(t.Panel.Panel.IsShown);
    });

    // The start-up reopen of a panel left open: drawn and docked like a Show, but nothing is brought forward or
    // activated (a start at sign-in must not take the keyboard from whatever Petre opened first).
    [Fact]
    public void Restoring_at_start_opens_the_panel_without_bringing_it_forward() => StaThread.Run(() =>
    {
        var forward = new List<nint>();
        using var t = Build(bringForward: forward.Add);
        var id = t.Tasks.AddNote("note", t.Gepha).Value.Id;

        t.Controller.Restore();

        Assert.True(t.Controller.IsOpen);
        Assert.True(t.Window.IsVisible);
        Assert.Equal([id], t.Window.Keys);
        Assert.Empty(forward);
    });

    // Every filter starts over on a fresh show, as the window's did: status, workspace, goal (TasksBoardGoalTests) and
    // assignee. The search and Claude working start over too (Task 7, A_fresh_show_clears_the_search_and_Claude_working).
    [Fact]
    public void A_fresh_show_resets_every_filter() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Tasks.AddNote("mine", t.Gepha);
        t.Tasks.Create("Claude's", t.Sparrow, Session("s1"));
        t.Controller.Show();
        t.Controller.Filter(TaskFilter.Closed);
        t.Controller.FilterWorkspace(TaskWorkspaceFilter.On(t.Sparrow));
        t.Controller.FilterAssignee(TaskAssigneeFilter.Claude);
        Assert.Empty(t.Window.Keys);

        t.Controller.Hide();
        t.Controller.Show();

        Assert.Equal(2, t.Window.Keys.Count);
        Assert.Equal("Workspace: all", t.Window.WorkspaceFilterLabel);
        Assert.Equal("Assigned: all", t.Window.AssigneeFilterLabel);
    });

    // --- Task 7 of the tasks panel plan: the search box and the Claude working chip -----------------------------------
    // Spec 2026-11-23-tasks-panel-design.md, "The new filters". Both narrow the board BEFORE it is grouped and counted,
    // as the goal filter does, so every chip's count agrees with what is shown.

    // The box narrows as Petre types: every word must match, ignoring case, in the title, the status line or the question.
    // The status chips count only what the search admits.
    [Fact]
    public void Searching_narrows_the_groups_and_the_chip_counts_alike() => StaThread.Run(() =>
    {
        using var t = Build();
        var submission = t.Tasks.AddNote("Store submission", t.Gepha).Value.Id;
        var screenshots = t.Tasks.Create("Store screenshots", t.Gepha, Session("s1")).Value.Id;
        t.Tasks.Ask(screenshots, "Which price tier?");
        var dishes = t.Tasks.AddNote("Dishwasher tablets", t.Gepha).Value.Id;
        t.Controller.Show();
        Assert.Contains("Open 3", t.Window.FilterLabels);

        t.Window.SearchBox.Text = "STORE";

        Assert.Equal([screenshots, submission], t.Window.Keys);
        Assert.Contains("Open 2", t.Window.FilterLabels);
        Assert.Contains("Waiting on you 1", t.Window.FilterLabels);

        // Two words, both must match: "tier" is only in the question.
        t.Window.SearchBox.Text = "store  tier";
        Assert.Equal([screenshots], t.Window.Keys);
        Assert.Contains("Open 1", t.Window.FilterLabels);
        Assert.Equal("Claude working 1", t.Window.ClaudeWorkingLabel);

        t.Window.SearchBox.Text = "nothing like it";
        Assert.Empty(t.Window.Keys);
        Assert.Contains("Open 0", t.Window.FilterLabels);
        Assert.Equal("Claude working 0", t.Window.ClaudeWorkingLabel);

        // Cleared, everything is back.
        t.Window.SearchBox.Text = "";
        Assert.Equal([screenshots, dishes, submission], t.Window.Keys);
        Assert.Contains("Open 3", t.Window.FilterLabels);
    });

    // The chip keeps only tasks with at least one LIVE session (an ended session does not count), and says how many it
    // would show, on or off. Its count follows every other filter; the status chips follow it.
    [Fact]
    public void Claude_working_keeps_only_tasks_with_a_live_session_and_counts_them() => StaThread.Run(() =>
    {
        using var t = Build();
        var note = t.Tasks.AddNote("Dishwasher tablets", t.Gepha).Value.Id;
        var live = t.Tasks.Create("DurableFile move guard", t.Gepha, Session("s1")).Value.Id;
        var ended = t.Tasks.Create("Store screenshots", t.Gepha, Session("s2", live: false)).Value.Id;
        t.Controller.Show();
        Assert.Equal("Claude working 1", t.Window.ClaudeWorkingLabel);
        Assert.False(t.Window.ClaudeWorkingOn);

        Click(ButtonNamed(t.Window, "Filter: Claude working"));

        Assert.True(t.Window.ClaudeWorkingOn);
        Assert.Equal([live], t.Window.Keys);
        Assert.Equal("Claude working 1", t.Window.ClaudeWorkingLabel);
        Assert.Contains("Open 1", t.Window.FilterLabels);

        // A session ending takes its task out of the filter as it happens.
        t.Tasks.SessionEnded("s1");
        Assert.Empty(t.Window.Keys);
        Assert.Equal("Claude working 0", t.Window.ClaudeWorkingLabel);

        Click(ButtonNamed(t.Window, "Filter: Claude working"));
        Assert.False(t.Window.ClaudeWorkingOn);
        Assert.Equal(3, t.Window.Keys.Count);
        Assert.Contains(note, t.Window.Keys);
        Assert.Contains(ended, t.Window.Keys);
    });

    // The two new filters combine with the status, workspace and assignee ones, and the Claude working count is what the
    // chip would show under all the others.
    [Fact]
    public void Search_and_Claude_working_combine_with_the_status_and_workspace_filters() => StaThread.Run(() =>
    {
        using var t = Build();
        var guard = t.Tasks.Create("deploy guard", t.Gepha, Session("s1")).Value.Id;
        var notes = t.Tasks.Create("deploy notes", t.Sparrow, Session("s2")).Value.Id;
        t.Tasks.Ask(notes, "Ship it?");
        var fix = t.Tasks.Create("deploy fix", t.Gepha, Session("s3", live: false)).Value.Id;
        var list = t.Tasks.AddNote("deploy list", t.Gepha).Value.Id;
        var other = t.Tasks.Create("runner disk", t.Gepha, Session("s4")).Value.Id;
        t.Controller.Show();
        Assert.Equal("Claude working 3", t.Window.ClaudeWorkingLabel);

        t.Controller.Search("deploy");
        Assert.Equal("Claude working 2", t.Window.ClaudeWorkingLabel);
        Assert.DoesNotContain(other, t.Window.Keys);

        t.Controller.FilterWorkspace(TaskWorkspaceFilter.On(t.Gepha));
        Assert.Equal("Claude working 1", t.Window.ClaudeWorkingLabel);
        Assert.Equal(3, t.Window.Keys.Count);
        Assert.Contains(fix, t.Window.Keys);
        Assert.Contains(list, t.Window.Keys);

        t.Controller.FilterClaudeWorking(true);
        Assert.Equal([guard], t.Window.Keys);
        Assert.Contains("Open 1", t.Window.FilterLabels);

        // Waiting on you, every workspace: only the live task that asks.
        t.Controller.FilterWorkspace(TaskWorkspaceFilter.All);
        t.Controller.Filter(TaskFilter.WaitingOnYou);
        Assert.Equal([notes], t.Window.Keys);
        Assert.Equal("Claude working 1", t.Window.ClaudeWorkingLabel);
        Assert.Contains("Waiting on you 1", t.Window.FilterLabels);

        // Assigned to me: the task that asks is on Petre (an ask hands it to him), the one Claude works on is not.
        t.Controller.Filter(TaskFilter.Open);
        t.Controller.FilterAssignee(TaskAssigneeFilter.Me);
        Assert.Equal([notes], t.Window.Keys);
        Assert.Equal("Claude working 1", t.Window.ClaudeWorkingLabel);

        // A search nothing matches leaves nothing, and the chip says so.
        t.Controller.FilterAssignee(TaskAssigneeFilter.All);
        t.Controller.Search("deploy nowhere");
        Assert.Empty(t.Window.Keys);
        Assert.Equal("Claude working 0", t.Window.ClaudeWorkingLabel);
    });

    // Search by id (task 6e4dc116, Petre: "searching for tasks by their id, like 173ef268 should give me that task, from all
    // sections, including closed"). The first eight characters of a closed task's id find it under the Open filter, a
    // workspace filter on another workspace and Claude working on, in its Closed group, and in the List even under the
    // Matrix setting, since the Matrix has no Closed cell.
    [Fact]
    public void An_id_finds_its_task_in_every_section_past_every_filter() => StaThread.Run(() =>
    {
        using var t = Build();
        var closed = t.Tasks.AddNote("Old store chore", t.Gepha).Value.Id;
        t.Tasks.Close(closed);
        var open = t.Tasks.Create("Live work", t.Sparrow, Session("s1")).Value.Id;
        t.Controller.Show();
        Click(ButtonNamed(t.Window, "Matrix"));
        t.Controller.FilterWorkspace(TaskWorkspaceFilter.On(t.Sparrow));
        t.Controller.FilterClaudeWorking(true);
        Assert.Equal([open], t.Window.Keys);

        t.Controller.Search(closed.ToString()[..8]);

        Assert.Equal([closed], t.Window.Keys);
        Assert.Equal(["Closed"], t.Window.Headers);
        Assert.False(t.Window.MatrixShown);

        // The whole id, dashes and all, finds it too; the Matrix comes back once the text is no id.
        t.Controller.Search(closed.ToString());
        Assert.Equal([closed], t.Window.Keys);
        t.Controller.Search("live");
        Assert.Equal([open], t.Window.Keys);
        Assert.True(t.Window.MatrixShown);
    });

    // A fresh show starts with no search, the box empty, like every other filter, but keeps Claude working (task 93fa0b9b:
    // Petre wants it kept "just like with current workspace").
    [Fact]
    public void A_fresh_show_clears_the_search_and_keeps_Claude_working() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Tasks.AddNote("Dishwasher tablets", t.Gepha);
        var live = t.Tasks.Create("DurableFile move guard", t.Gepha, Session("s1")).Value.Id;
        t.Controller.Show();
        t.Window.SearchBox.Text = "guard";
        Click(ButtonNamed(t.Window, "Filter: Claude working"));
        Assert.Equal([live], t.Window.Keys);

        t.Controller.Hide();
        t.Controller.Show();

        Assert.Equal("", t.Window.SearchBox.Text);
        Assert.True(t.Window.ClaudeWorkingOn);
        Assert.Equal([live], t.Window.Keys);
        // The box does not take the keyboard on a show: the panel opens on the board.
        Assert.False(t.Window.SearchBox.IsKeyboardFocused);
    });

    // A redraw while Petre types (a session's pulse, a task arriving) never touches the box: its text, the keyboard and
    // the caret stay as they were, as a half-typed comment's do.
    [Fact]
    public void Typing_into_the_search_box_keeps_its_text_and_caret_through_a_redraw() => StaThread.Run(() =>
    {
        using var t = Build();
        var dishes = t.Tasks.AddNote("Dishwasher tablets", t.Gepha).Value.Id;
        t.Tasks.AddNote("Runner disk", t.Gepha);
        t.Controller.Show();
        t.Window.Activate();
        var box = t.Window.SearchBox;
        box.Focus();
        box.Text = "dish";
        box.CaretIndex = 2;
        Assert.True(box.IsKeyboardFocused);
        Assert.Equal([dishes], t.Window.Keys);
        var rebuilds = t.Window.Rebuilds;

        var more = t.Tasks.AddNote("Dish soap", t.Gepha).Value.Id;

        // The redraw happened (the new match is drawn)...
        Assert.True(t.Window.Rebuilds > rebuilds);
        Assert.Equal([more, dishes], t.Window.Keys);
        // ...and the box is as it was.
        Assert.Same(box, t.Window.SearchBox);
        Assert.Equal("dish", box.Text);
        Assert.True(box.IsKeyboardFocused);
        Assert.Equal(2, box.CaretIndex);
    });

    // Ctrl+F anywhere in the panel puts the keyboard in the box; Esc in the box empties it and gives the keyboard back to
    // the board, with every task back.
    [Fact]
    public void Ctrl_f_focuses_the_search_and_esc_clears_it_and_gives_the_board_back() => StaThread.Run(() =>
    {
        using var t = Build();
        var dishes = t.Tasks.AddNote("Dishwasher tablets", t.Gepha).Value.Id;
        var disk = t.Tasks.AddNote("Runner disk", t.Gepha).Value.Id;
        t.Controller.Show();
        t.Window.Activate();
        t.Window.FocusBoard();

        Assert.True(t.Window.Press(Key.F, ModifierKeys.Control));
        Assert.True(t.Window.SearchBox.IsKeyboardFocused);
        // F alone is typing, not the shortcut.
        Assert.False(t.Window.Press(Key.F, ModifierKeys.None));

        t.Window.SearchBox.Text = "runner";
        Assert.Equal([disk], t.Window.Keys);

        PressKey(t.Window, Key.Escape);

        Assert.Equal("", t.Window.SearchBox.Text);
        Assert.False(t.Window.SearchBox.IsKeyboardFocusWithin);
        Assert.True(t.Window.Toolbar.IsKeyboardFocused);
        Assert.Equal([disk, dishes], t.Window.Keys);
    });

    // The clear button (×) in the search box: hidden while the box is empty, shown once it has text; a click empties the
    // box, brings every task back, hides itself again and leaves the keyboard in the box for the next search.
    [Fact]
    public void The_search_clear_button_shows_with_text_and_clears_the_search() => StaThread.Run(() =>
    {
        using var t = Build();
        var dishes = t.Tasks.AddNote("Dishwasher tablets", t.Gepha).Value.Id;
        var disk = t.Tasks.AddNote("Runner disk", t.Gepha).Value.Id;
        t.Controller.Show();
        t.Window.Activate();
        Assert.Equal(Visibility.Collapsed, t.Window.SearchClear.Visibility);

        t.Window.SearchBox.Text = "runner";
        Assert.Equal([disk], t.Window.Keys);
        Assert.Equal(Visibility.Visible, t.Window.SearchClear.Visibility);

        Click(t.Window.SearchClear);

        Assert.Equal("", t.Window.SearchBox.Text);
        Assert.Equal([disk, dishes], t.Window.Keys);
        Assert.Equal(Visibility.Collapsed, t.Window.SearchClear.Visibility);
        Assert.Equal(Visibility.Visible, t.Window.SearchPlaceholder.Visibility);
        Assert.True(t.Window.SearchBox.IsKeyboardFocused);
    });

    // The chip as the approved mockup draws it (Task 7 review, Important): the orange edge on or off, the normal chip fill
    // while off and the AskTint fill while on, never the other chips' blue selected edge.
    [Fact]
    public void The_Claude_working_chip_keeps_its_orange_edge_and_fills_when_on() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Tasks.Create("DurableFile move guard", t.Gepha, Session("s1"));
        t.Controller.Show();
        var orange = FloatingBar.AskingOrange;
        var normalFill = System.Windows.Media.Color.FromRgb(0x2A, 0x2C, 0x33);
        var askTint = System.Windows.Media.Color.FromRgb(0x3A, 0x2C, 0x1C);
        static System.Windows.Media.Color ColourOf(System.Windows.Media.Brush brush) =>
            Assert.IsType<System.Windows.Media.SolidColorBrush>(brush).Color;

        var off = ButtonNamed(t.Window, "Filter: Claude working");
        Assert.Equal(orange, ColourOf(off.BorderBrush));
        Assert.Equal(normalFill, ColourOf(off.Background));

        Click(off);

        var on = ButtonNamed(t.Window, "Filter: Claude working");
        Assert.True(t.Window.ClaudeWorkingOn);
        Assert.Equal(orange, ColourOf(on.BorderBrush));
        Assert.Equal(askTint, ColourOf(on.Background));
    });

    // The toolbar is the approved mockup's three rows (Task 7 fix round 2): the title line ("Tasks" and the end buttons
    // only), then the search row, then the filter chips. Measured after layout, each row wholly below the one before.
    [Fact]
    public void The_toolbar_is_the_title_line_then_the_search_row_then_the_filter_chips() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Tasks.AddNote("note", t.Gepha);
        t.Controller.Show();
        t.Window.UpdateLayout();
        double Top(FrameworkElement e) => e.TranslatePoint(new Point(0, 0), t.Window.Toolbar).Y;
        double Bottom(FrameworkElement e) => Top(e) + e.ActualHeight;

        // The title and the end buttons share the first line, and no chip is on it.
        Assert.True(Bottom(t.Window.ToolbarTitle) <= Top(t.Window.SearchRow));
        Assert.True(Bottom(t.Window.ToolbarEnd) <= Top(t.Window.SearchRow));
        Assert.True(Top(t.Window.ToolbarTitle) < Bottom(t.Window.ToolbarEnd) && Top(t.Window.ToolbarEnd) < Bottom(t.Window.ToolbarTitle));
        // The search row, then the chips, Claude working among them.
        Assert.True(Bottom(t.Window.SearchRow) <= Top(t.Window.Filters));
        Assert.True(t.Window.Filters.ActualHeight > 0);
        Assert.Contains(ButtonNamed(t.Window, "Filter: Claude working"), t.Window.Filters.Children.OfType<Button>());
        // The search row is the panel's full width inside the toolbar's padding, as wide as the title line.
        Assert.Equal(((FrameworkElement)t.Window.ToolbarTitle.Parent).ActualWidth, t.Window.SearchRow.ActualWidth, 1);
    });

    // Task 7 review, ruling on ShowTask: a task clicked on the bar that the search or the Claude working chip hides is put
    // on screen by clearing whichever of the two hides it (the box emptied too); one that only the other hides keeps the
    // first in force.
    [Fact]
    public void Showing_a_task_the_search_or_Claude_working_hides_clears_what_hides_it() => StaThread.Run(() =>
    {
        using var t = Build();
        var note = t.Tasks.AddNote("Runner disk", t.Gepha).Value.Id;
        var guard = t.Tasks.Create("DurableFile move guard", t.Gepha, Session("s1")).Value.Id;
        var ended = t.Tasks.Create("DurableFile cleanup", t.Gepha, Session("s2", live: false)).Value.Id;
        t.Controller.Show();
        t.Window.SearchBox.Text = "durablefile";
        Click(ButtonNamed(t.Window, "Filter: Claude working"));
        Assert.Equal([guard], t.Window.Keys);

        // Matches the search, but has no live session: only the chip goes off.
        t.Controller.ShowTask(ended);
        Assert.Equal("durablefile", t.Window.SearchBox.Text);
        Assert.False(t.Window.ClaudeWorkingOn);
        Assert.Contains(ended, t.Window.Keys);
        Assert.DoesNotContain(note, t.Window.Keys);

        // Turned back on, then a task hidden by both: the box empties and the chip goes off.
        Click(ButtonNamed(t.Window, "Filter: Claude working"));
        t.Controller.ShowTask(note);
        Assert.Equal("", t.Window.SearchBox.Text);
        Assert.False(t.Window.ClaudeWorkingOn);
        Assert.Contains(note, t.Window.Keys);
        Assert.True(t.Window.ThreadShown(note));

        // A live task the search admits: neither is touched.
        t.Window.SearchBox.Text = "guard";
        Click(ButtonNamed(t.Window, "Filter: Claude working"));
        t.Controller.ShowTask(guard);
        Assert.Equal("guard", t.Window.SearchBox.Text);
        Assert.True(t.Window.ClaudeWorkingOn);
        Assert.Equal([guard], t.Window.Keys);
    });

    // The toolbar's × closes the panel, as `tasks-window hide` does, and Alt+F4 on the panel does the same rather than
    // closing the WPF window for good (a closed window can never be shown again).
    [Fact]
    public void The_close_button_and_alt_f4_close_the_panel() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Controller.Show();

        Click(ButtonNamed(t.Window, "Close the panel"));
        Assert.False(t.Controller.IsOpen);
        Assert.False(t.Window.IsVisible);

        t.Controller.Show();
        t.Window.Close();
        Assert.False(t.Controller.IsOpen);
        Assert.False(t.Window.IsVisible);
        Assert.Equal(new Deskbar.Core.Persistence.TasksPanelState(false, Wide), t.Harness.Manager.State.TasksPanel);

        t.Controller.Show();
        Assert.True(t.Window.IsVisible);
    });

    // The grip sits on the panel's inner edge, the one facing the windows: its left edge when it docks on the right,
    // its right edge when it docks on the left (spec "The panel window").
    [Theory]
    [InlineData("right", Dock.Left)]
    [InlineData("left", Dock.Right)]
    public void The_grip_sits_on_the_edge_facing_the_windows(string barEdge, Dock gripSide) => StaThread.Run(() =>
    {
        using var t = Build(edge: barEdge);
        t.Controller.Show();

        Assert.Equal(gripSide, DockPanel.GetDock(t.Window.Grip));
    });

    // The grip's drag end hands the new width over: it is saved (AppState.TasksPanel) and the panel re-docks at it, in
    // place (no Undock: the registration, and so the order behind the bar, stays).
    [Fact]
    public void A_width_from_the_grip_is_saved_and_the_panel_redocks_at_it() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Controller.Show();
        t.Panel.Calls.Clear();

        t.Window.Actions!.ResizePanel(900);

        Assert.Equal(new Deskbar.Core.Persistence.TasksPanelState(true, 900), t.Harness.Manager.State.TasksPanel);
        Assert.Equal([$"DockWith Right {Px(t, 900)}"], t.Panel.Calls);
    });

    // A width in DIPs as the panel asks the shell for it: physical pixels at the window's own scale (TasksPanelRig).
    static int Px(Board t, double dips) => (int)Math.Round(dips * System.Windows.Media.VisualTreeHelper.GetDpi(t.Window).DpiScaleX);

    [Fact]
    public void A_click_into_another_app_leaves_it_open() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Controller.Show();

        typeof(Window).GetMethod("OnDeactivated", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(t.Window, [EventArgs.Empty]);

        Assert.True(t.Window.IsOpen);
        Assert.True(t.Window.IsVisible);
    });

    // "+N more" on an open window brings it forward as it is, filter and all; only a hidden window opens on the board.
    [Fact]
    public void Opening_it_again_brings_it_forward_with_its_view() => StaThread.Run(() =>
    {
        using var t = Build();
        var closed = t.Tasks.AddNote("Old errand", t.Gepha).Value.Id;
        t.Tasks.Close(closed);
        t.Controller.Show();
        t.Controller.Filter(TaskFilter.Closed);

        t.Controller.Show();

        Assert.Equal([closed], t.Window.Keys);
    });


    [Fact]
    public void A_refused_go_says_why_and_stays_open() => StaThread.Run(() =>
    {
        using var t = Build(_ => Result.Failure("That workspace has no desktop."));
        var id = t.Tasks.AddNote("note", t.Gepha).Value.Id;
        t.Controller.Show();
        DoubleClick(t.Window, id); // a double-click goes (Ruling 54)

        Assert.True(t.Window.IsOpen);
        Assert.Equal("That workspace has no desktop.", t.Window.Problem);
    });

    // The bar's #77 lesson: a redraw under an open menu pulls the rows out from under it. A change that arrives while
    // one of the window's menus is open waits for the menu to close.
    [Fact]
    public void A_rebuild_waits_while_a_menu_is_open() => StaThread.Run(() =>
    {
        using var t = Build();
        var first = t.Tasks.AddNote("first", t.Gepha).Value.Id;
        t.Controller.Show();

        var menu = t.Window.OpenMenuFor(first);
        Assert.True(menu.IsOpen);
        var second = t.Tasks.AddNote("second", t.Gepha).Value.Id;
        Assert.Equal([first], t.Window.Keys);

        // The menu's Closed arrives through the dispatcher, as it does when Petre clicks away from it.
        menu.IsOpen = false;
        t.Window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        Assert.Equal([second, first], t.Window.Keys);
    });

    [Fact]
    public void A_rebuild_keeps_a_comment_being_typed() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("note", t.Gepha).Value.Id;
        t.Controller.Show();
        t.Controller.ToggleThread(id);
        t.Window.TypeComment(id, "half a thou", key: null);

        t.Tasks.AddNote("another", t.Gepha);

        Assert.Equal("half a thou", t.Window.CommentDraft(id));
    });

    [Fact]
    public void A_change_that_shows_nothing_new_rebuilds_nothing() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("note", t.Gepha).Value.Id;
        t.Controller.Show();
        var before = t.Window.Rebuilds;

        // A refresh with the board as it was (the workspaces' StateChanged pulses for many reasons) redraws nothing;
        // a rename does.
        t.Controller.Refresh();
        Assert.Equal(before, t.Window.Rebuilds);

        t.Tasks.Rename(id, "renamed");
        Assert.Equal(before + 1, t.Window.Rebuilds);
    });

    // FIX ROUND 1, Critical 1, kept for the panel: the window never activates itself. Opening it shows it without
    // activation, and both a fresh show and a show of the open panel bring it forward through the App's seam (raise,
    // then activate; TasksBoard.Arrive). The panel is pinned to every desktop, so there is no move any more.
    [Fact]
    public void The_window_never_activates_itself_and_both_paths_go_through_the_seam() => StaThread.Run(() =>
    {
        var forward = new List<nint>();
        using var t = Build(bringForward: forward.Add);

        t.Controller.Show();
        Assert.False(t.Window.ShowActivated);
        Assert.Equal([t.Controller.EnsureHandle()], forward);

        t.Controller.Show();
        Assert.Equal(2, forward.Count);
        Assert.Equal(0, t.Window.SelfActivations);
    });

    // The App's seam, in its order: raise to the top of the z-order, then activate ("tasks should become a top window,
    // now it's behind somewhere"), and trace the arrival so a live check can read what happened. It used to move the
    // window to the desktop in front first; the panel is pinned to every desktop, so the move went (plan
    // 2026-11-23-tasks-panel, Task 6). A panel whose pin failed and that is off the desktop in front is traced and left
    // alone, since activating it would take Petre to its desktop (Task 6 review, Minor 1).
    [Fact]
    public void Arriving_raises_then_activates_only_on_the_desktop_in_front() => StaThread.Run(() =>
    {
        var steps = new List<string>();
        Assert.True(TasksBoard.Arrive(0x42, () => { steps.Add("ask"); return true; },
            () => steps.Add("raise"), () => steps.Add("activate"), trace => steps.Add("trace")));
        Assert.Equal(["ask", "raise", "activate", "trace"], steps);

        steps.Clear();
        Assert.False(TasksBoard.Arrive(0x42, () => { steps.Add("ask"); return false; },
            () => steps.Add("raise"), () => steps.Add("activate"), trace => steps.Add("trace")));
        Assert.Equal(["ask", "trace"], steps);
    });

    // An arrival the seam refused (the panel off the desktop in front after a failed pin) leaves the keyboard alone too:
    // focusing the window would activate it, and Windows would follow it to that desktop.
    [Fact]
    public void A_panel_off_the_desktop_in_front_is_not_focused() => StaThread.Run(() =>
    {
        using var t = Build(activates: () => false);

        t.Controller.Show();

        Assert.False(t.Window.IsActive);
        Assert.NotSame(t.Window.Toolbar, FocusManager.GetFocusedElement(t.Window));
    });

    // MANUAL STEP 64n, measured 2026-11-25: `tasks-window show` while a video played full screen docked the panel behind
    // the video and then brought it forward over it, raised and activated, until the next 1 s tick. A panel standing
    // behind a full-screen window is not brought forward and not focused, whether it found the window as it opened or the
    // shell said so while it was already open; once the window has gone, a show brings it forward as before.
    [Fact]
    public void A_panel_behind_a_full_screen_window_is_not_brought_forward() => StaThread.Run(() =>
    {
        var forward = new List<nint>();
        using var t = Build(bringForward: forward.Add);

        // Opened while a video is already full screen: the panel finds it as it shows.
        t.Panel.Cover = 0x77;
        t.Controller.Show();
        Assert.True(t.Window.IsVisible);
        Assert.Empty(forward);
        Assert.NotSame(t.Window.Toolbar, FocusManager.GetFocusedElement(t.Window));

        // The video ends; the next tick puts the panel back on top, and a show brings it forward again.
        t.Panel.Cover = 0;
        t.Panel.Panel.ReclaimTopmost();
        t.Controller.Show();
        Assert.Single(forward);

        // The shell says a full-screen window opened while the panel is open: a show leaves it where it is.
        t.Panel.Panel.StepAside(fullScreen: true);
        t.Controller.Show();
        Assert.Single(forward);
    });

    // THE ROOT OF "IT'S BEHIND SOMEWHERE" (2026-11-23 live check). Opening the window gave the toolbar the keyboard
    // before the seam ran, and a WPF Focus() on an element of an inactive window activates that window (measured on the
    // hidden desktop: active=T right after Focus). So the window was already the foreground window, possibly still on
    // the desktop it had been left on, before it was moved, and the seam's SetForegroundWindow on a window that already
    // is the foreground one changes nothing: it is never raised. Now nothing activates the window before the seam, and
    // the keyboard goes to the toolbar only after the seam says the window has arrived.
    [Fact]
    public void Opening_does_not_activate_the_window_before_the_seam_and_focuses_it_after() => StaThread.Run(() =>
    {
        var activeAtSeam = new List<bool>();
        TasksWindow? window = null;
        using var t = Build(bringForward: _ => activeAtSeam.Add(window!.IsActive));
        window = t.Window;

        t.Controller.Show();

        Assert.Equal([false], activeAtSeam);
        Assert.Same(t.Window.Toolbar, FocusManager.GetFocusedElement(t.Window));
    });

    // The raise itself, on real windows: a window that is active but has another window over it (the state the live
    // check left the tasks window in) comes to the top of the z-order, without anything else being activated.
    [Fact]
    public void Raising_puts_the_window_above_one_that_covered_it() => StaThread.Run(() =>
    {
        var below = new Window { Title = "below", Width = 200, Height = 200, Left = -30000, Top = -30000, ShowActivated = false };
        var cover = new Window { Title = "cover", Width = 200, Height = 200, Left = -30000, Top = -30000, ShowActivated = false };
        below.Show();
        cover.Show();
        var belowHandle = new System.Windows.Interop.WindowInteropHelper(below).Handle;
        var coverHandle = new System.Windows.Interop.WindowInteropHelper(cover).Handle;
        try
        {
            Assert.True(Above(coverHandle, belowHandle));

            TasksBoard.RaiseToTop(belowHandle);

            Assert.True(Above(belowHandle, coverHandle));
        }
        finally
        {
            below.Close();
            cover.Close();
        }
    });

    // (c) "give it a proper icon in the workspaces so i can take it to the correct place": the window answers WM_GETICON
    // with the tasks checklist, green, not Deskbar's (IconCache's own-icon source). The panel is no longer listed on the
    // bar (it opts out like the bar's own window, plan 2026-11-23-tasks-panel, Task 6), but it keeps its own icon
    // wherever Windows still draws one, a debug dump or a trace.
    [Fact]
    public void The_window_wears_its_own_checklist_icon() => StaThread.Run(() =>
    {
        using var t = Build();

        t.Controller.Show();

        Assert.Same(TasksIcon.Image, t.Window.Icon);
        var drawn = Assert.IsAssignableFrom<System.Windows.Media.Imaging.BitmapSource>(
            IconCache.For(new Deskbar.Core.Domain.WindowHandle(t.Controller.EnsureHandle()), null, null));
        var (r, g, b) = AverageOpaque(drawn);
        Assert.True(g > r + 40 && g > b + 20, $"the bar's icon averages R={r} G={g} B={b}, not the checklist's green");
    });

    // A docked panel is not a tile (spec "The panel window"): the floating window's exception ("maybe even tile it",
    // the test "The_tasks_window_tiles_and_the_apps_other_windows_still_do_not") is gone, and the panel is kept out of
    // the grid like every other window of this process, the bar, Manage and its dialogs.
    [Fact]
    public void The_panel_is_kept_out_of_tiling() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Controller.Show();
        var panel = t.Controller.EnsureHandle();

        Assert.True(TasksBoard.KeptFromTiling(panel));
        var surface = new Deskbar.Windows.Tiling.TileSurface(w => TasksBoard.KeptFromTiling(w.Value));
        Assert.False(surface.CanTile(new Deskbar.Core.Domain.WindowHandle(panel)));
    });

    // The average colour of a bitmap's opaque pixels.
    static (int R, int G, int B) AverageOpaque(System.Windows.Media.Imaging.BitmapSource source)
    {
        var bgra = new System.Windows.Media.Imaging.FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        var pixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
        bgra.CopyPixels(pixels, bgra.PixelWidth * 4, 0);
        var opaque = Enumerable.Range(0, pixels.Length / 4).Where(i => pixels[i * 4 + 3] > 200).ToList();
        return opaque.Count == 0 ? (0, 0, 0)
            : ((int)opaque.Average(i => pixels[i * 4 + 2]), (int)opaque.Average(i => pixels[i * 4 + 1]), (int)opaque.Average(i => pixels[i * 4]));
    }

    // Whether `upper` is above `lower` in the z-order: walking up from `lower` reaches it.
    static bool Above(nint upper, nint lower)
    {
        for (var h = GetWindow(lower, 3); h != 0; h = GetWindow(h, 3))
            if (h == upper) return true;
        return false;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern nint GetWindow(nint hwnd, uint command);

    // FIX ROUND 1, Important 1: the bar's click guard (#48, Ruling 10). A press that is the second of a double click, or
    // a click within half a second of a repaint that changed the lines, is dropped on a line and its buttons; Undo
    // drops only the double click, since it appears under the pointer on purpose.
    [Fact]
    public void A_double_click_or_a_click_right_after_a_repaint_is_ignored() => StaThread.Run(() =>
    {
        var now = ClickStart;
        using var t = Build(clickClock: () => now);
        var first = t.Tasks.AddNote("first", t.Gepha).Value.Id;
        var second = t.Tasks.AddNote("second", t.Gepha).Value.Id;
        t.Controller.Show();

        // Right after the draw: nothing takes the click.
        Click(ButtonNamed(LineOf(t.Window, second), "Close"));
        Click(LineOf(t.Window, second));
        Assert.Equal(2, t.Tasks.Board.Pile().Count);
        Assert.Empty(t.WentTo);

        // Later, one click closes "second"; its line stays with Undo where the tick was.
        now = now.AddMilliseconds(600);
        PressAndClick(ButtonNamed(LineOf(t.Window, second), "Close"), clickCount: 1);
        Assert.Single(t.Tasks.Board.Pile());

        // The second press of that double click lands on Undo and is dropped, and so is one on the next line's tick.
        now = now.AddMilliseconds(600);
        PressAndClick(ButtonNamed(LineOf(t.Window, second), "Undo"), clickCount: 2);
        PressAndClick(ButtonNamed(LineOf(t.Window, first), "Close"), clickCount: 2);
        Assert.Equal(TaskStatus.Closed, t.Tasks.Board.Tasks.Single(x => x.Id == second).Status);
        Assert.Equal(TaskStatus.Backlog, t.Tasks.Board.Tasks.Single(x => x.Id == first).Status);
        Assert.Empty(t.WentTo);

        // A deliberate click on Undo, however soon after the repaint, works.
        PressAndClick(ButtonNamed(LineOf(t.Window, second), "Undo"), clickCount: 1);
        Assert.Equal(TaskStatus.Backlog, t.Tasks.Board.Tasks.Single(x => x.Id == second).Status);
    });

    // FIX ROUND 1, Important 2: a half-typed comment must not freeze the window's own buttons. Every button is
    // unfocusable, so the comment box keeps the keyboard through any click; a redraw that waited for the box would
    // never come. The redraw goes ahead, and the box comes back with its text, the keyboard and its caret.
    [Fact]
    public void A_half_typed_comment_keeps_its_place_and_the_window_still_redraws() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("note", t.Gepha).Value.Id;
        var closed = t.Tasks.AddNote("closed", t.Gepha).Value.Id;
        t.Tasks.Close(closed);
        t.Controller.Show();
        t.Window.Activate();
        t.Controller.ToggleThread(id);
        var box = t.Window.CommentBoxOf(id)!;
        box.Text = "half a thought";
        box.Focus();
        box.CaretIndex = 4;
        Assert.True(box.IsKeyboardFocused);

        t.Tasks.AddNote("another", t.Gepha);

        // The redraw happened (the new note is drawn; the closed one is not in the open view)...
        var again = t.Window.CommentBoxOf(id)!;
        Assert.Equal(2, t.Window.Keys.Count);
        // ...and the box came back as it was.
        Assert.Equal("half a thought", again.Text);
        Assert.True(again.IsKeyboardFocused);
        Assert.Equal(4, again.CaretIndex);
    });

    // The floating window's remembered place is ignored now (spec "Opening and closing": an old AppState.TasksWindow is
    // ignored). The test that pinned it coming back in device pixels, "A_remembered_place_comes_back_in_device_pixels",
    // went with the place memory; this one pins that an old place neither moves the panel nor is written again.
    [Fact]
    public void An_old_floating_place_is_ignored_and_never_written() => StaThread.Run(() =>
    {
        using var t = Build();
        var old = new Deskbar.Core.Persistence.TasksWindowState(120, 90, 900, 640, Maximized: true);
        Assert.True(t.Harness.Manager.SaveTasksWindow(old).IsSuccess);

        t.Controller.Show();
        t.Controller.Hide();

        Assert.Equal(old, t.Harness.Manager.State.TasksWindow);
        Assert.Equal(WindowState.Normal, t.Window.WindowState);
    });

    // The window's Undo follows the bar's: a Done task closed from its line comes back Done.
    [Fact]
    public void Undo_brings_a_done_task_back_done() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.Create("merged", t.Gepha, Session("s1")).Value.Id;
        t.Tasks.MarkDone(id);
        t.Controller.Show();

        Click(ButtonNamed(LineOf(t.Window, id), "Close"));
        Click(ButtonNamed(LineOf(t.Window, id), "Undo"));

        Assert.Equal(TaskStatus.Done, t.Tasks.Board.Tasks.Single(x => x.Id == id).Status);
    });

    // Ruling 26: each line says how long ago its task appeared, and a minute passing rewrites only that text.
    [Fact]
    public void A_line_says_its_age_and_a_minute_rewrites_only_that() => StaThread.Run(() =>
    {
        var now = Start.AddDays(3).AddHours(2);
        using var t = Build(now: () => now);
        var id = t.Tasks.AddNote("note", t.Gepha).Value.Id;
        t.Controller.Show();
        var line = LineOf(t.Window, id);
        var rebuilds = t.Window.Rebuilds;
        Assert.Equal("3d2h", t.Window.AgeText(id));

        now = now.AddHours(1);
        t.Window.RefreshAges();

        Assert.Equal("3d3h", t.Window.AgeText(id));
        Assert.Equal(rebuilds, t.Window.Rebuilds);
        Assert.Same(line, LineOf(t.Window, id));
    });

    // Ruling 21: one selection for both views. A click on a window line picks it, and so does a pick made on the bar
    // (the same TaskSelection object); the window draws it in place and lets go when the task is closed.
    [Fact]
    public void A_pick_in_either_view_shows_in_the_window() => StaThread.Run(() =>
    {
        var selection = new TaskSelection();
        using var t = Build(selection: selection);
        var first = t.Tasks.AddNote("first", t.Gepha).Value.Id;
        var second = t.Tasks.AddNote("second", t.Gepha).Value.Id;
        t.Controller.Show();
        Click(LineOf(t.Window, first)); // picks and expands it (Ruling 54)
        Assert.True(selection.IsSelected(first));
        Assert.True(t.Window.IsSelected(first));
        var rebuilds = t.Window.Rebuilds;

        // Picked on the bar: the window follows, in place.
        selection.Select(second);
        Assert.True(t.Window.IsSelected(second));
        Assert.False(t.Window.IsSelected(first));
        Assert.Equal(rebuilds, t.Window.Rebuilds);

        t.Tasks.Close(second);
        Assert.True(selection.Current.HasNoValue);
    });

    [Fact]
    public void The_window_opens_scrolled_to_the_selected_task() => StaThread.Run(() =>
    {
        var selection = new TaskSelection();
        using var t = Build(selection: selection);
        var oldest = t.Tasks.AddNote("the oldest", t.Gepha).Value.Id;
        Enumerable.Range(1, 40).ToList().ForEach(i => t.Tasks.AddNote($"note {i}", t.Gepha));
        selection.Select(oldest);

        t.Controller.Show();

        Assert.True(t.Window.ScrollOffset > 0, $"offset {t.Window.ScrollOffset}");
        Assert.True(t.Window.IsSelected(oldest));
    });

    // Task 4846fde1: a selection made while the panel is open (a Claude task's terminal brought to the front) scrolls its
    // line into view through Reveal; selecting alone does not, and a line already in view stays put. Closed, Reveal
    // neither opens the panel nor fails.
    [Fact]
    public void Revealing_the_selected_task_scrolls_the_open_panel_to_it() => StaThread.Run(() =>
    {
        var selection = new TaskSelection();
        using var t = Build(selection: selection);
        var oldest = t.Tasks.AddNote("the oldest", t.Gepha).Value.Id;
        Enumerable.Range(1, 40).ToList().ForEach(i => t.Tasks.AddNote($"note {i}", t.Gepha));

        t.Controller.Reveal();
        Assert.False(t.Controller.IsOpen);

        t.Controller.Show();
        Assert.Equal(0, t.Window.ScrollOffset, 1);

        selection.Select(oldest);
        t.Window.UpdateLayout();
        Assert.Equal(0, t.Window.ScrollOffset, 1);

        t.Controller.Reveal();
        t.Window.UpdateLayout();
        Assert.True(t.Window.ScrollOffset > 0, $"offset {t.Window.ScrollOffset}");

        var offset = t.Window.ScrollOffset;
        t.Controller.Reveal();
        t.Window.UpdateLayout();
        Assert.Equal(offset, t.Window.ScrollOffset, 1);
    });

    // Review I-3: at the narrowest width the full line is drawn at (TasksPanelLayout.SlimBelow, 710 DIP, the floating
    // window's old minimum; narrower, Task 8 draws the slim line), with enough lines for the scroll bar, every column
    // stays inside the list's viewport and the title keeps a usable width.
    [Fact]
    public void At_the_full_lines_narrowest_width_the_columns_stay_inside_the_viewport() => StaThread.Run(() =>
    {
        using var t = Build();
        Enumerable.Range(1, 40).ToList().ForEach(i => t.Tasks.AddNote($"a note with a fairly long title, number {i}", t.Gepha));
        t.Controller.Show();
        t.Window.Width = TasksPanelLayout.SlimBelow;
        t.Window.UpdateLayout();

        var scroller = Inside(t.Window).OfType<ScrollViewer>().First(s => s.Content is StackPanel);
        Assert.True(scroller.ComputedVerticalScrollBarVisibility == Visibility.Visible, "the scroll bar shows");
        t.Window.Keys.Take(5).Select(id => LineOf(t.Window, id)).ToList().ForEach(line =>
        {
            var more = ButtonNamed(line, "More");
            var bounds = more.TransformToAncestor(scroller).TransformBounds(new Rect(more.RenderSize));
            Assert.True(bounds.Right <= scroller.ViewportWidth + 0.5, $"More ends at {bounds.Right} in a viewport {scroller.ViewportWidth} wide");
            var title = Inside(line).OfType<TextBlock>().First(b => Equals(b.Tag, "task-title"));
            Assert.True(title.ActualWidth >= 100, $"the title has {title.ActualWidth} DIP");
        });
    });

    // Review minor: a redraw that only rewrites what a line says (a session's status line) moves nothing, so it does
    // not start the click guard's half second; frequent status pulses must not eat clicks.
    [Fact]
    public void A_status_line_change_does_not_eat_the_next_click() => StaThread.Run(() =>
    {
        var now = ClickStart;
        using var t = Build(clickClock: () => now);
        var id = t.Tasks.Create("work", t.Gepha, Session("s1")).Value.Id;
        t.Controller.Show();
        now = now.AddMilliseconds(600);

        t.Tasks.SetStatusLine(id, "waiting for CI");
        DoubleClick(t.Window, id);

        Assert.Equal([id], t.WentTo);
    });

    // Where a part of the window ends, against the window's own content (what the window shows).
    static Rect BoundsIn(TasksWindow window, FrameworkElement part) =>
        part.TransformToAncestor((System.Windows.Media.Visual)window.Content).TransformBounds(new Rect(part.RenderSize));

    static double ContentWidth(TasksWindow window) => ((FrameworkElement)window.Content).ActualWidth;

    // Petre's screenshot: every line sized its own columns, so the chip, the comments and "…" sat at a different x on
    // each line, and a long title pushed the chip off the window. One column layout for every line, the title trimming
    // first: each column at the same x on every line, nothing past the right edge, at the window's minimum width too.
    [Fact]
    public void The_lines_columns_line_up_and_a_long_title_never_pushes_them_out() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Tasks.AddNote("a", t.Gepha);
        var closed = t.Tasks.Create("done one", t.Gepha, Session("s1")).Value.Id;
        t.Tasks.MarkDone(closed);
        t.Tasks.AddNote("iam.ge: transcripts, learnings and every other thing - personalized-health - Visual Studio Code", Maybe<Guid>.None);
        t.Controller.Show();
        t.Window.Width = TasksPanelLayout.SlimBelow;
        t.Window.UpdateLayout();

        var lines = t.Window.Keys.Select(id => LineOf(t.Window, id)).ToList();
        // The workspace chip; the status chip (TaskStatusMarks) is rounded the same way, so it is told apart by its tag.
        var chips = lines.Select(l => BoundsIn(t.Window, Inside(l).OfType<Border>()
            .Single(b => b.Child is TextBlock && b.CornerRadius.TopLeft == 8 && !Equals(b.Tag, TaskStatusMarks.ChipTag)))).ToList();
        var statuses = lines.Select(l => BoundsIn(t.Window, StatusChipOf(l)!)).ToList();
        var comments = lines.Select(l => BoundsIn(t.Window, ButtonNamed(l, "Comments"))).ToList();
        var mores = lines.Select(l => BoundsIn(t.Window, ButtonNamed(l, "More"))).ToList();
        Assert.True(chips.Max(b => b.Left) - chips.Min(b => b.Left) < 0.5, string.Join(", ", chips.Select(b => b.Left)));
        Assert.True(statuses.Max(b => b.Left) - statuses.Min(b => b.Left) < 0.5, string.Join(", ", statuses.Select(b => b.Left)));
        Assert.All(statuses, b => Assert.True(b.Right <= chips[0].Left, $"status chip {b} runs into the workspace column"));
        Assert.True(comments.Max(b => b.Left) - comments.Min(b => b.Left) < 0.5, string.Join(", ", comments.Select(b => b.Left)));
        Assert.True(mores.Max(b => b.Left) - mores.Min(b => b.Left) < 0.5, string.Join(", ", mores.Select(b => b.Left)));
        Assert.All(chips.Concat(comments).Concat(mores), b => Assert.True(b.Right <= ContentWidth(t.Window) + 0.5, $"{b} in {ContentWidth(t.Window)}"));
        // The icons explain themselves.
        Assert.Equal("Comments", ButtonNamed(lines[0], "Comments").ToolTip);
        Assert.Equal("Edit, status, move, delete", ButtonNamed(lines[0], "More").ToolTip);
    });

    // The status chip of a line (TaskStatusMarks.Chip), or null when it has none.
    static Border? StatusChipOf(DependencyObject line) =>
        Inside(line).OfType<Border>().SingleOrDefault(b => Equals(b.Tag, TaskStatusMarks.ChipTag));

    static string? StatusWordOf(DependencyObject line) => (StatusChipOf(line)?.Child as TextBlock)?.Text;

    static bool HasDoneTick(DependencyObject line) =>
        Inside(line).OfType<FrameworkElement>().Any(e => Equals(e.Tag, TaskStatusMarks.DoneTickTag));

    // Petre, 2026-11-23: "i can't currently see statuses". Every line says its status in a chip, and a Done line also has
    // the green tick in front of its title; no other line has the tick.
    [Fact]
    public void Every_line_shows_its_status_and_only_a_done_line_has_the_tick() => StaThread.Run(() =>
    {
        using var t = Build();
        var note = t.Tasks.AddNote("a note", t.Gepha).Value.Id;
        var working = t.Tasks.Create("working", t.Gepha, Session("s1")).Value.Id;
        var asking = t.Tasks.Create("asking", t.Gepha, Session("s2")).Value.Id;
        t.Tasks.Ask(asking, "Ship it?");
        var done = t.Tasks.Create("finished", t.Gepha, Session("s3")).Value.Id;
        t.Tasks.MarkDone(done);
        t.Controller.Show();
        t.Window.UpdateLayout();

        Assert.Equal("Backlog", StatusWordOf(LineOf(t.Window, note)));
        Assert.Equal("In progress", StatusWordOf(LineOf(t.Window, working)));
        Assert.Equal("Waiting", StatusWordOf(LineOf(t.Window, asking)));
        Assert.Equal("Done", StatusWordOf(LineOf(t.Window, done)));
        Assert.True(HasDoneTick(LineOf(t.Window, done)));
        Assert.False(new[] { note, working, asking }.Any(id => HasDoneTick(LineOf(t.Window, id))));
    });

    // Petre's screenshot: the placeholder "Comment as you, in markdow…" was cut off and the box squeezed. A short
    // placeholder, the hint in the tooltip, the box across the thread, and nothing clipped at the minimum width.
    [Fact]
    public void The_comment_box_is_short_spoken_and_never_clips() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("note", t.Gepha).Value.Id;
        t.Controller.Show();
        t.Controller.ToggleThread(id);
        t.Window.Width = TasksPanelLayout.SlimBelow;
        t.Window.UpdateLayout();

        var box = t.Window.CommentBoxOf(id)!;
        Assert.Equal(TasksWindow.CommentHint, box.ToolTip);
        var placeholder = Inside(t.Window).OfType<TextBlock>().Single(b => Equals(b.Tag, "comment-placeholder"));
        Assert.Equal("Add a comment…", placeholder.Text);
        var post = ButtonNamed(t.Window, "Post comment");
        Assert.All(new FrameworkElement[] { box, placeholder, post }, part =>
        {
            var bounds = BoundsIn(t.Window, part);
            Assert.True(bounds.Right <= ContentWidth(t.Window) + 0.5, $"{part.GetType().Name} ends at {bounds.Right} in {ContentWidth(t.Window)}");
            Assert.True(part.RenderSize.Width >= part.DesiredSize.Width - part.Margin.Left - part.Margin.Right - 0.5, $"{part.GetType().Name} squeezed");
        });
    });

    // --- Task 12: flags, quadrants and drag in the List view ---------------------------------------------------------

    static bool ToggleOn(Button toggle) => AutomationProperties.GetItemStatus(toggle) == "on";

    [Fact]
    public void The_quadrants_are_captioned_in_coveys_words_in_his_order() => StaThread.Run(() =>
    {
        using var t = Build();
        var a = t.Tasks.AddNote("a", t.Gepha).Value.Id;
        var b = t.Tasks.AddNote("b", t.Gepha).Value.Id;
        var c = t.Tasks.AddNote("c", t.Gepha).Value.Id;
        t.Tasks.AddNote("d", t.Gepha);
        t.Tasks.SetFlag(a, TaskFlag.Urgent, true);
        t.Tasks.SetFlag(b, TaskFlag.Important, true);
        t.Tasks.SetFlag(c, TaskFlag.Important, true);
        t.Tasks.SetFlag(c, TaskFlag.Urgent, true);

        t.Controller.Show();

        Assert.Equal(["I  Urgent and important", "II  Important, not urgent", "III  Urgent, not important", "IV  Not urgent, not important"],
            t.Window.Headers);
    });

    // The two toggles on each line, as on the bar: a click sets the flag and the line moves to its quadrant.
    [Fact]
    public void A_toggle_on_a_window_line_sets_its_flag() => StaThread.Run(() =>
    {
        using var t = Build();
        var a = t.Tasks.AddNote("a", t.Gepha).Value.Id;
        var b = t.Tasks.AddNote("b", t.Gepha).Value.Id;
        t.Controller.Show();
        Assert.Equal([b, a], t.Window.Keys);
        var important = ButtonNamed(LineOf(t.Window, a), "Important");
        Assert.Equal("Important", important.ToolTip);
        Assert.Equal("Urgent", ButtonNamed(LineOf(t.Window, a), "Urgent").ToolTip);
        Assert.False(ToggleOn(important));

        Click(important);

        Assert.True(t.Tasks.Board.Tasks.Single(x => x.Id == a).Important);
        Assert.Equal([a, b], t.Window.Keys);
        Assert.True(ToggleOn(ButtonNamed(LineOf(t.Window, a), "Important")));
        Assert.Equal(["II  Important, not urgent", "IV  Not urgent, not important"], t.Window.Headers);
    });

    // Task 6da8d7eb: the snooze moon sits with the two toggles, quiet until hovered. Its menu offers the four lengths; a
    // snoozed line leaves the List for the "Snoozed" chip, where its moon is lit and its menu wakes it.
    [Fact]
    public void The_snooze_moon_hides_a_line_until_it_is_woken() => StaThread.Run(() =>
    {
        using var t = Build();
        var a = t.Tasks.AddNote("a", t.Gepha).Value.Id;
        var b = t.Tasks.AddNote("b", t.Gepha).Value.Id;
        t.Controller.Show();
        var moon = ButtonNamed(LineOf(t.Window, a), "Snooze");
        Assert.False(ToggleOn(moon));
        Assert.DoesNotContain(t.Window.FilterLabels, label => label.StartsWith("Snoozed", StringComparison.Ordinal));

        static string Words(MenuItem item) => item.Header is TextBlock text ? text.Text : $"{item.Header}";
        var menu = t.Window.OpenSnoozeMenu(a, moon);
        var lengths = menu.Items.OfType<MenuItem>().ToArray();
        Assert.Equal(["1 hour", "3 hours"], lengths.Take(2).Select(Words));
        Assert.StartsWith("Tomorrow, ", Words(lengths[2]));
        Assert.StartsWith("Monday, ", Words(lengths[3]));
        lengths[0].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        // The redraw waits for the menu to close (a rebuild under an open menu), as when Petre's click closes it.
        menu.IsOpen = false;
        t.Window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

        Assert.True(t.Tasks.Board.Tasks.Single(x => x.Id == a).IsSnoozed);
        Assert.Equal([b], t.Window.Keys);
        Assert.Contains("Snoozed 1", t.Window.FilterLabels);

        t.Controller.Filter(TaskFilter.Snoozed);
        Assert.Equal([a], t.Window.Keys);
        var lit = ButtonNamed(LineOf(t.Window, a), "Snooze");
        Assert.True(ToggleOn(lit));
        // The filter's redraw just made this moon: lay it out first, as the screen has before anyone can press it, or its
        // menu's popup never opens and so never reports closing (measured: the held redraw then never came).
        t.Window.UpdateLayout();
        var again = t.Window.OpenSnoozeMenu(a, lit);
        var wake = again.Items.OfType<MenuItem>().First();
        Assert.Equal("Wake now", Words(wake));
        wake.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        again.IsOpen = false;
        t.Window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

        Assert.False(t.Tasks.Board.Tasks.Single(x => x.Id == a).IsSnoozed);
        Assert.Empty(t.Window.Keys);
        Assert.Contains("Snoozed 0", t.Window.FilterLabels);
        t.Controller.Filter(TaskFilter.Open);
        Assert.Equal([b, a], t.Window.Keys);
    });

    // Petre, 2026-11-25: "i may snooze something that's waiting". A line waiting on him has the moon too; snoozed, it
    // leaves the Waiting on you block and the chip's count, and its "⋯" menu offers Snooze as well.
    [Fact]
    public void A_line_waiting_on_you_can_be_snoozed() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.Create("asks", t.Gepha, Session("s1")).Value.Id;
        Assert.True(t.Tasks.Ask(id, "merge?").IsSuccess);
        t.Controller.Show();
        Assert.Contains("WAITING ON YOU", t.Window.Headers.Select(h => h.ToUpperInvariant()));

        // ButtonNamed fails the test when the line has no moon.
        _ = ButtonNamed(LineOf(t.Window, id), "Snooze");
        var more = t.Window.OpenMenuFor(id);
        Assert.Contains(more.Items.OfType<MenuItem>(), i => i.Header is TextBlock { Text: "Snooze" });
        more.IsOpen = false;
        t.Window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

        Assert.True(t.Controller.Snooze(id, SnoozeChoice.Hour).IsSuccess);

        Assert.Empty(t.Window.Keys);
        Assert.Contains("Waiting on you 0", t.Window.FilterLabels);
        Assert.Contains("Snoozed 1", t.Window.FilterLabels);
        Assert.Equal(TaskStatus.WaitingOnYou, t.Tasks.Board.Tasks.Single(x => x.Id == id).Status);
    });

    // A point inside a window line, `share` of the way down it, in the drag's own coordinates (the window's rows).
    static Point InLine(Board t, Guid id, double share)
    {
        var line = LineOf(t.Window, id);
        return line.TranslatePoint(new Point(10, line.ActualHeight * share), t.Window.DragSurface);
    }

    static void WindowDrag(Board t, Guid from, Point to)
    {
        var start = InLine(t, from, 0.5);
        t.Window.DragPress(from, start);
        t.Window.DragMove(new Point(start.X, start.Y + (to.Y > start.Y ? 8 : -8)));
        t.Window.DragMove(to);
        t.Window.DragRelease();
        t.Window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
    }

    // Drag a line within its group, or into another group (whose flags it then takes), as on the bar.
    [Fact]
    public void Dragging_a_window_line_reorders_and_across_a_group_sets_flags() => StaThread.Run(() =>
    {
        using var t = Build();
        var a = t.Tasks.AddNote("a", t.Gepha).Value.Id;
        var b = t.Tasks.AddNote("b", t.Gepha).Value.Id;
        var c = t.Tasks.AddNote("c", t.Gepha).Value.Id;
        t.Window.DragMouse = ArrangeMouse.Held;
        t.Controller.Show();
        t.Window.UpdateLayout();
        Assert.Equal([c, b, a], t.Window.Keys);

        WindowDrag(t, c, InLine(t, a, 0.8));
        t.Window.UpdateLayout();
        Assert.Equal([b, a, c], t.Window.Keys);

        t.Tasks.SetFlag(b, TaskFlag.Urgent, true);
        t.Window.UpdateLayout();
        Assert.Equal([b, a, c], t.Window.Keys);
        WindowDrag(t, c, InLine(t, b, 0.2));
        t.Window.UpdateLayout();
        Assert.True(t.Tasks.Board.Tasks.Single(x => x.Id == c).Urgent);
        Assert.Equal([c, b, a], t.Window.Keys);
    });

    // A press that does not travel is a click. A waiting line is dragged (task f41d7857), but only within its block:
    // released below it, it stays at the block's foot, here its own place. Nothing drops above the block.
    [Fact]
    public void A_window_press_in_place_is_a_click_and_waiting_lines_stay_in_their_block() => StaThread.Run(() =>
    {
        using var t = Build();
        var a = t.Tasks.AddNote("a", t.Gepha).Value.Id;
        var b = t.Tasks.AddNote("b", t.Gepha).Value.Id;
        var w = t.Tasks.AddNote("w", t.Gepha).Value.Id;
        t.Tasks.SetStatus(w, TaskStatus.WaitingOnYou);
        t.Window.DragMouse = ArrangeMouse.Held;
        t.Controller.Show();
        t.Window.UpdateLayout();
        var before = t.Tasks.Board;

        var at = InLine(t, b, 0.5);
        t.Window.DragPress(b, at);
        t.Window.DragMove(new Point(at.X + 1, at.Y + 1));
        Assert.False(t.Window.Dragging);
        t.Window.DragRelease();
        var waitingAt = InLine(t, w, 0.5);
        t.Window.DragPress(w, waitingAt);
        t.Window.DragMove(new Point(waitingAt.X, waitingAt.Y + 8));
        t.Window.DragMove(InLine(t, a, 0.9));
        Assert.True(t.Window.Dragging);
        t.Window.DragRelease();
        t.Window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        Assert.Same(before, t.Tasks.Board);

        WindowDrag(t, a, InLine(t, w, 0.2));
        Assert.Equal([w, a, b], t.Tasks.Board.Pile().Select(x => x.Id));
    });

    // --- Task 12: the Matrix view (Ruling 28) ------------------------------------------------------------------------

    // The toolbar's switch shows Covey's 2 by 2 and is remembered (AppState.TasksMatrix): urgent and not urgent across,
    // important and not important down, so I is top left and II top right. Each cell lists its quadrant's tasks, a task
    // waiting on Petre on top of its own quadrant's cell.
    [Fact]
    public void The_matrix_shows_four_cells_in_coveys_places() => StaThread.Run(() =>
    {
        using var t = Build();
        var a = t.Tasks.AddNote("a", t.Gepha).Value.Id;
        var b = t.Tasks.AddNote("b", t.Gepha).Value.Id;
        var c = t.Tasks.AddNote("c", t.Gepha).Value.Id;
        var w = t.Tasks.AddNote("w", t.Gepha).Value.Id;
        t.Tasks.SetFlag(a, TaskFlag.Important, true);
        t.Tasks.SetFlag(a, TaskFlag.Urgent, true);
        t.Tasks.SetFlag(b, TaskFlag.Important, true);
        t.Tasks.SetFlag(w, TaskFlag.Important, true);
        t.Tasks.SetStatus(w, TaskStatus.WaitingOnYou);
        t.Controller.Show();
        Assert.False(t.Window.MatrixShown);

        Click(ButtonNamed(t.Window, "Matrix"));

        Assert.True(t.Harness.Manager.State.TasksMatrix);
        Assert.True(t.Window.MatrixShown);
        Assert.Equal(["I  Urgent and important", "II  Important, not urgent", "III  Urgent, not important", "IV  Not urgent, not important"],
            t.Window.Headers);
        Assert.Equal([a, w, b, c], t.Window.Keys);
        Assert.Equal((0, 0), t.Window.CellPlace(Quadrant.I));
        Assert.Equal((0, 1), t.Window.CellPlace(Quadrant.II));
        Assert.Equal((1, 0), t.Window.CellPlace(Quadrant.III));
        Assert.Equal((1, 1), t.Window.CellPlace(Quadrant.IV));
        Assert.Equal(Quadrant.II, t.Window.CellOf(w));
        // Quadrant II is drawn a little stronger: Covey's quadrant to put first.
        Assert.True(t.Window.CellStronger(Quadrant.II));
        Assert.False(t.Window.CellStronger(Quadrant.I));

        Click(ButtonNamed(t.Window, "List"));
        Assert.False(t.Harness.Manager.State.TasksMatrix);
        Assert.False(t.Window.MatrixShown);
    });

    [Fact]
    public void The_matrix_is_remembered_when_the_window_opens_again() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Tasks.AddNote("a", t.Gepha);
        Assert.True(t.Harness.Manager.SetTasksMatrix(true).IsSuccess);

        t.Controller.Show();

        Assert.True(t.Window.MatrixShown);
    });

    // A point inside a line, in the Matrix's drag coordinates.
    static Point InMatrixLine(Board t, Guid id, double share)
    {
        var line = LineOf(t.Window, id);
        return line.TranslatePoint(new Point(10, line.ActualHeight * share), t.Window.DragSurface);
    }

    // Dragging between cells sets the flags of the cell it lands in; into an empty cell, it lands at its top.
    [Fact]
    public void Dragging_between_matrix_cells_sets_the_flags() => StaThread.Run(() =>
    {
        using var t = Build();
        var a = t.Tasks.AddNote("a", t.Gepha).Value.Id;
        var b = t.Tasks.AddNote("b", t.Gepha).Value.Id;
        t.Tasks.SetFlag(a, TaskFlag.Important, true);
        Assert.True(t.Harness.Manager.SetTasksMatrix(true).IsSuccess);
        t.Window.DragMouse = ArrangeMouse.Held;
        t.Controller.Show();
        t.Window.UpdateLayout();
        Assert.Equal(Quadrant.II, t.Window.CellOf(a));

        WindowDragTo(t, b, InMatrixLine(t, a, 0.2));
        t.Window.UpdateLayout();
        Assert.True(t.Tasks.Board.Tasks.Single(x => x.Id == b).Important);
        Assert.Equal([b, a], t.Window.Keys);

        // Into the empty cell III (urgent, not important).
        WindowDragTo(t, a, t.Window.CellCentre(Quadrant.III));
        t.Window.UpdateLayout();
        var moved = t.Tasks.Board.Tasks.Single(x => x.Id == a);
        Assert.True(moved.Urgent && !moved.Important);
        Assert.Equal(Quadrant.III, t.Window.CellOf(a));
    });

    // Task f41d7857 in the window: two waiting lines swap in the List; in the Matrix a waiting line dropped on another
    // cell takes that cell's flags and stays pinned on top of it.
    [Fact]
    public void Window_waiting_lines_reorder_in_the_list_and_change_cells_in_the_matrix() => StaThread.Run(() =>
    {
        using var t = Build();
        var a = t.Tasks.AddNote("a", t.Gepha).Value.Id;
        var w1 = t.Tasks.AddNote("w1", t.Gepha).Value.Id;
        var w2 = t.Tasks.AddNote("w2", t.Gepha).Value.Id;
        t.Tasks.SetStatus(w1, TaskStatus.WaitingOnYou);
        t.Tasks.SetStatus(w2, TaskStatus.WaitingOnYou);
        t.Window.DragMouse = ArrangeMouse.Held;
        t.Controller.Show();
        t.Window.UpdateLayout();
        Assert.Equal([w2, w1, a], t.Window.Keys);

        WindowDrag(t, w2, InLine(t, w1, 0.9));
        t.Window.UpdateLayout();
        Assert.Equal([w1, w2, a], t.Window.Keys);

        // The waiting a's middle takes a subtask in the window too.
        WindowDrag(t, a, InLine(t, w2, 0.5));
        t.Window.UpdateLayout();
        Assert.Equal(CSharpFunctionalExtensions.Maybe.From(w2), t.Tasks.Board.Tasks.Single(x => x.Id == a).ParentId);
        Assert.Equal([w1, w2, a], t.Window.Keys);

        Assert.True(t.Harness.Manager.SetTasksMatrix(true).IsSuccess);
        t.Window.UpdateLayout();
        WindowDragTo(t, w1, t.Window.CellCentre(Quadrant.II));
        t.Window.UpdateLayout();
        var moved = t.Tasks.Board.Tasks.Single(x => x.Id == w1);
        Assert.True(moved.Important && !moved.Urgent);
        Assert.Equal(TaskStatus.WaitingOnYou, moved.Status);
        Assert.Equal(Quadrant.II, t.Window.CellOf(w1));
    });

    static void WindowDragTo(Board t, Guid from, Point to)
    {
        var start = InMatrixLine(t, from, 0.5);
        t.Window.DragPress(from, start);
        t.Window.DragMove(new Point(start.X + 8, start.Y + 8));
        t.Window.DragMove(to);
        t.Window.DragRelease();
        t.Window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
    }

    // Review item 4, in the window: a drop on the waiting block keeps the task's own quadrant, at its top.
    [Fact]
    public void A_window_drop_on_the_waiting_block_keeps_the_tasks_own_quadrant() => StaThread.Run(() =>
    {
        using var t = Build();
        var a = t.Tasks.AddNote("a", t.Gepha).Value.Id;
        var b = t.Tasks.AddNote("b", t.Gepha).Value.Id;
        var c = t.Tasks.AddNote("c", t.Gepha).Value.Id;
        var w = t.Tasks.AddNote("w", t.Gepha).Value.Id;
        t.Tasks.SetStatus(w, TaskStatus.WaitingOnYou);
        t.Tasks.SetFlag(c, TaskFlag.Urgent, true);
        t.Window.DragMouse = ArrangeMouse.Held;
        t.Controller.Show();
        t.Window.UpdateLayout();
        Assert.Equal([w, c, b, a], t.Window.Keys);

        // On the waiting line's top edge: its middle would make a its subtask (task f41d7857).
        WindowDrag(t, a, InLine(t, w, 0.1));

        Assert.False(t.Tasks.Board.Tasks.Single(x => x.Id == a).Urgent);
        Assert.Equal([w, c, a, b], t.Tasks.Board.Pile().Select(x => x.Id));
    });

    // Review of 4fc4441..a1acd60, item 1: at the window's minimum width the Matrix left a line's title 9.8 DIP (80 at
    // 720). With a cell long enough for its own scroll bar, every column stays inside the cell and the title keeps a
    // usable width, as the List's I-3 test asks. The window used to grow to the Matrix's 780 DIP by itself; the panel
    // does not, and its Matrix switch shows only from TasksPanelLayout.MatrixFrom (780, Task 8), so that is the width here.
    [Fact]
    public void At_the_matrix_narrowest_width_it_keeps_room_for_titles() => StaThread.Run(() =>
    {
        using var t = Build();
        Enumerable.Range(1, 30).ToList().ForEach(i => t.Tasks.AddNote($"a note with a fairly long title, number {i}", t.Gepha));
        Assert.True(t.Harness.Manager.SetTasksMatrix(true).IsSuccess);
        t.Controller.Show();
        t.Window.Width = TasksPanelLayout.MatrixFrom;
        t.Window.UpdateLayout();

        var line0 = LineOf(t.Window, t.Window.Keys[0]);
        var scroller = Inside(t.Window).OfType<ScrollViewer>().First(s => s.Content is StackPanel panel && panel.Children.Contains(line0));
        Assert.True(scroller.ComputedVerticalScrollBarVisibility == Visibility.Visible, "the cell's scroll bar shows");
        t.Window.Keys.Take(5).Select(id => LineOf(t.Window, id)).ToList().ForEach(line =>
        {
            var more = ButtonNamed(line, "More");
            var bounds = more.TransformToAncestor(scroller).TransformBounds(new Rect(more.RenderSize));
            Assert.True(bounds.Right <= scroller.ViewportWidth + 0.5, $"More ends at {bounds.Right} in a viewport {scroller.ViewportWidth} wide");
            var title = Inside(line).OfType<TextBlock>().First(b => Equals(b.Tag, "task-title"));
            Assert.True(title.ActualWidth >= 100, $"the title has {title.ActualWidth} DIP at a window {t.Window.ActualWidth} wide");
        });
    });

    // --- Review of 4fc4441..a1acd60, item 2: window drags driven by real routed input --------------------------------
    // As on the bar (FloatingBarTaskListTests, the routed helpers): ButtonBase reads the real mouse button, so the
    // helper raises the Click a release that reaches the button would raise.

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

    // The line released must still be drawn: a redraw under the press (#48) would have replaced it (minor 1).
    static bool RealRelease(Board t, Button line)
    {
        Assert.Contains(line, Inside(t.Window).OfType<Button>());
        var up = ButtonArgs(UIElement.PreviewMouseUpEvent);
        line.RaiseEvent(up);
        if (up.Handled) return false;
        line.RaiseEvent(ButtonArgs(UIElement.MouseUpEvent));
        Click(line);
        return true;
    }

    static void LoseCapture(UIElement on) =>
        on.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.LostMouseCaptureEvent });

    sealed class Captures
    {
        public int Count;
    }

    static (Board Board, Pointer Pointer, Guid A, Guid B, Guid C) RoutedWindow(Captures? captures = null)
    {
        var t = Build();
        var a = t.Tasks.AddNote("a", t.Gepha).Value.Id;
        var b = t.Tasks.AddNote("b", t.Gepha).Value.Id;
        var c = t.Tasks.AddNote("c", t.Gepha).Value.Id;
        var pointer = new Pointer();
        t.Window.DragMouse = new ArrangeMouse(_ =>
        {
            if (captures is not null) captures.Count++;
            return true;
        }, _ => true, (_, _) => pointer.At);
        t.Controller.Show();
        t.Window.UpdateLayout();
        return (t, pointer, a, b, c);
    }

    [Fact]
    public void A_routed_window_press_under_the_threshold_is_still_a_click() => StaThread.Run(() =>
    {
        var captures = new Captures();
        var (t, pointer, _, _, c) = RoutedWindow(captures);
        using var board = t;
        var before = t.Tasks.Board;
        var line = LineOf(t.Window, c);
        pointer.At = InLine(t, c, 0.5);

        RealPress(line);
        pointer.At = new Point(pointer.At.X + 1, pointer.At.Y + 1);
        RealMove(line);
        Assert.False(t.Window.Dragging);
        Assert.True(RealRelease(t, line));
        Assert.Equal(0, captures.Count);

        Assert.Empty(t.WentTo); // a press that stays a click picks the task and goes nowhere (Ruling 54)
        Assert.Same(before, t.Tasks.Board);
    });

    [Fact]
    public void A_routed_window_drag_drops_and_its_release_raises_no_click() => StaThread.Run(() =>
    {
        var (t, pointer, a, b, c) = RoutedWindow();
        using var board = t;
        var line = LineOf(t.Window, c);
        pointer.At = InLine(t, c, 0.5);

        RealPress(line);
        pointer.At = new Point(pointer.At.X, pointer.At.Y + 8);
        RealMove(line);
        pointer.At = InLine(t, a, 0.8);
        RealMove(line);
        Assert.True(t.Window.Dragging);
        Assert.False(RealRelease(t, line));
        t.Window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

        Assert.Empty(t.WentTo);
        Assert.Equal([b, a, c], t.Tasks.Board.Pile().Select(x => x.Id));
    });

    [Fact]
    public void A_routed_window_lost_capture_ends_a_press_or_a_drag_and_moves_nothing() => StaThread.Run(() =>
    {
        var (t, pointer, a, b, c) = RoutedWindow();
        using var board = t;
        var line = LineOf(t.Window, c);
        pointer.At = InLine(t, c, 0.5);

        RealPress(line);
        var late = t.Tasks.AddNote("held back by the press", t.Gepha).Value.Id;
        Assert.DoesNotContain(late, t.Window.Keys);
        LoseCapture(line);
        t.Window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        Assert.Contains(late, t.Window.Keys);

        var before = t.Tasks.Board;
        t.Window.UpdateLayout();
        line = LineOf(t.Window, c);
        pointer.At = InLine(t, c, 0.5);
        RealPress(line);
        pointer.At = new Point(pointer.At.X, pointer.At.Y + 8);
        RealMove(line);
        pointer.At = InLine(t, a, 0.8);
        RealMove(line);
        Assert.True(t.Window.Dragging);
        LoseCapture(t.Window.DragSurface);
        Assert.False(t.Window.Dragging);
        RealRelease(t, line);
        t.Window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

        Assert.Same(before, t.Tasks.Board);
    });

    // Minor 2, in the List: the drop line on the waiting block is drawn where the task lands, the top of its quadrant.
    [Fact]
    public void The_window_drop_line_on_the_waiting_block_is_where_the_task_lands() => StaThread.Run(() =>
    {
        using var t = Build();
        var a = t.Tasks.AddNote("a", t.Gepha).Value.Id;
        var b = t.Tasks.AddNote("b", t.Gepha).Value.Id;
        var c = t.Tasks.AddNote("c", t.Gepha).Value.Id;
        var w = t.Tasks.AddNote("w", t.Gepha).Value.Id;
        t.Tasks.SetStatus(w, TaskStatus.WaitingOnYou);
        t.Tasks.SetFlag(c, TaskFlag.Urgent, true);
        t.Window.DragMouse = ArrangeMouse.Held;
        t.Controller.Show();
        t.Window.UpdateLayout();
        Assert.Equal([w, c, b, a], t.Window.Keys);

        var start = InLine(t, a, 0.5);
        t.Window.DragPress(a, start);
        t.Window.DragMove(new Point(start.X, start.Y - 8));
        // The top edge: the middle of a waiting line now takes the task as its subtask (task f41d7857).
        t.Window.DragMove(InLine(t, w, 0.1));

        Assert.Equal(InLine(t, b, 0).Y, t.Window.DropLineY!.Value, 1);
        t.Window.DragRelease();
    });

    // The floating window grew itself to the Matrix's 780 DIP and gave the List its width back afterwards (the tests
    // "List_then_matrix_then_list_gives_the_window_back_its_width" and "A_window_flush_with_the_right_edge_stays_on_
    // screen_in_the_matrix" pinned that). The panel never widens anything (spec "The Matrix"): the switch changes the
    // view, not the window's width nor the width saved for the panel.
    [Fact]
    public void The_matrix_never_widens_the_panel() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Tasks.AddNote("a", t.Gepha);
        t.Controller.Show();
        var before = t.Window.ActualWidth;

        Click(ButtonNamed(t.Window, "Matrix"));
        t.Window.UpdateLayout();

        Assert.True(t.Window.MatrixShown);
        Assert.Equal(before, t.Window.ActualWidth, 0.5);
        Assert.Equal(Wide, t.Harness.Manager.State.TasksPanel!.Width);
    });

    // --- Whole-PR review, I-1: a redraw keeps each Matrix cell's scroll, and a workspace switch redraws no row --------

    [Fact]
    public void A_redraw_keeps_each_matrix_cells_scroll() => StaThread.Run(() =>
    {
        using var t = Build();
        Enumerable.Range(1, 30).ToList().ForEach(i => t.Tasks.AddNote($"note {i}", t.Gepha));
        var other = t.Tasks.AddNote("in quadrant I", t.Gepha).Value.Id;
        t.Tasks.SetFlag(other, TaskFlag.Important, true);
        t.Tasks.SetFlag(other, TaskFlag.Urgent, true);
        Assert.True(t.Harness.Manager.SetTasksMatrix(true).IsSuccess);
        t.Controller.Show();
        t.Window.UpdateLayout();
        t.Window.CellScroller(Quadrant.IV).ScrollToVerticalOffset(200);
        t.Window.UpdateLayout();
        Assert.Equal(200, t.Window.CellScroller(Quadrant.IV).VerticalOffset, 1);
        var rebuilds = t.Window.Rebuilds;

        Assert.True(t.Controller.Comment(other, "a comment on another task").IsSuccess);
        t.Window.UpdateLayout();

        Assert.True(t.Window.Rebuilds > rebuilds, "the comment redrew the window");
        Assert.Equal(200, t.Window.CellScroller(Quadrant.IV).VerticalOffset, 1);
    });

    // A line's click switches workspace, which changes where a new note would go: that is the add box's chip alone,
    // painted in place, and no line is drawn again.
    [Fact]
    public void A_workspace_switch_redraws_no_row() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Tasks.AddNote("a", t.Gepha);
        t.Controller.Show();
        var rebuilds = t.Window.Rebuilds;
        var sparrow = t.Harness.Manager.State.Workspaces.Single(w => w.Name == "Sparrow");

        t.Harness.Desktops.CurrentId = sparrow.DesktopId!.Value;
        ((System.Reactive.Subjects.Subject<Guid>)t.Harness.Desktops.CurrentChanged).OnNext(sparrow.DesktopId!.Value);

        Assert.Equal(sparrow.Id, t.Harness.Manager.CurrentWorkspaceId.Value);
        Assert.Equal(rebuilds, t.Window.Rebuilds);
        Assert.Contains("on Sparrow", Inside(ButtonNamed(t.Window, "Add to workspace")).OfType<TextBlock>().Single().Text);
    });

    // (d) 2026-11-23, Petre, of the add row's "on Deskbar site ▾": "dropdown doesn't work". The chip is a flat button
    // whose own Click handler marks the click handled, and the menu was opened from a second Click handler added after
    // it, which WPF then never calls (a handler added with += is skipped once an earlier one on the same element has
    // handled the event). A routed click on the real chip opens the menu, and a pick lands on the next note.
    [Fact]
    public void The_add_rows_workspace_chip_opens_its_menu_and_a_pick_lands() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Controller.Show();
        Click(ButtonNamed(t.Window, "Add"));

        Click(ButtonNamed(t.Window, "Add to workspace"));

        var menu = Assert.IsAssignableFrom<ContextMenu>(t.Window.OpenedMenu);
        Assert.True(menu.IsOpen);
        Click(WorkspaceChipMenu.Chips(menu).Single(c => AutomationProperties.GetName(c) == "Sparrow"));
        Assert.False(menu.IsOpen);
        Assert.Contains("on Sparrow", Inside(ButtonNamed(t.Window, "Add to workspace")).OfType<TextBlock>().Single().Text);

        Assert.True(t.Window.TypeNote("encrypt data", Key.Enter));
        Assert.Equal(t.Sparrow, t.Tasks.Board.Tasks.Single(x => x.Title == "encrypt data").WorkspaceId.Value);
    });

    // Ruling 37 in the window's List: the add row has a line's two toggles; the note takes their flags and tops that
    // quadrant, and they are off again after the add.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void A_note_added_in_the_list_takes_the_toggles_flags(bool important, bool urgent) => StaThread.Run(() =>
    {
        using var t = Build();
        t.Tasks.AddNote("already there", t.Gepha, QuadrantOf.Flags(important, urgent));
        t.Controller.Show();
        Click(ButtonNamed(t.Window, "Add"));

        if (important) Click(ButtonNamed(t.Window, "Add as important"));
        if (urgent) Click(ButtonNamed(t.Window, "Add as urgent"));
        Assert.True(t.Window.TypeNote("Pay rent", Key.Enter));

        var added = t.Tasks.Board.Tasks.Single(x => x.Title == "Pay rent");
        Assert.Equal((important, urgent), (added.Important, added.Urgent));
        Assert.Equal(added.Id, t.Tasks.Board.Group(QuadrantOf.Flags(important, urgent))[0].Id);
        Click(ButtonNamed(t.Window, "Add"));
        Assert.Equal("off", AutomationProperties.GetItemStatus(ButtonNamed(t.Window, "Add as important")));
        Assert.Equal("off", AutomationProperties.GetItemStatus(ButtonNamed(t.Window, "Add as urgent")));
    });

    // Petre: "actually use the box which is active when adding through the matrix". In the Matrix the toggles are the
    // active cell: quadrant II at first, outlined; a press in a cell makes it active and turns the toggles to its flags;
    // a toggle moves the active cell; a new task lands at the top of the active cell.
    [Fact]
    public void In_the_matrix_a_note_goes_to_the_active_cell() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Tasks.AddNote("urgent one", t.Gepha, Quadrant.III);
        Assert.True(t.Harness.Manager.SetTasksMatrix(true).IsSuccess);
        t.Controller.Show();
        Click(ButtonNamed(t.Window, "Add"));

        Assert.Equal(Quadrant.II, t.Window.ActiveCell);
        Assert.Equal(Quadrant.II, t.Window.HighlightedCell);
        Assert.Equal("on", AutomationProperties.GetItemStatus(ButtonNamed(t.Window, "Add as important")));
        Assert.Equal("off", AutomationProperties.GetItemStatus(ButtonNamed(t.Window, "Add as urgent")));

        // A press in cell III makes it the active one.
        t.Window.PressInCell(Quadrant.III);
        Assert.Equal(Quadrant.III, t.Window.HighlightedCell);
        Assert.Equal("off", AutomationProperties.GetItemStatus(ButtonNamed(t.Window, "Add as important")));
        Assert.Equal("on", AutomationProperties.GetItemStatus(ButtonNamed(t.Window, "Add as urgent")));
        Assert.True(t.Window.TypeNote("next urgent", Key.Enter));
        var added = t.Tasks.Board.Tasks.Single(x => x.Title == "next urgent");
        Assert.Equal(added.Id, t.Tasks.Board.Group(Quadrant.III)[0].Id);

        // Turning important on moves the active cell to I, and the next note lands there.
        Click(ButtonNamed(t.Window, "Add"));
        Click(ButtonNamed(t.Window, "Add as important"));
        Assert.Equal(Quadrant.I, t.Window.HighlightedCell);
        Assert.True(t.Window.TypeNote("both", Key.Enter));
        Assert.Equal(Quadrant.I, t.Tasks.Board.Tasks.Single(x => x.Title == "both").Quadrant);
    });

    // Ruling 40, the window's half: "…"'s Close settles a Claude task that is still in progress, as the bar's tick does.
    [Fact]
    public void The_menus_close_settles_an_in_progress_claude_task() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.Create("Still going", t.Gepha, Session("s1")).Value.Id;
        t.Controller.Show();

        var menu = t.Window.OpenMenuFor(id);
        var close = menu.Items.OfType<MenuItem>().Single(i => i.Header is TextBlock { Text: "Close" });
        close.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.IsOpen = false;

        // Task 37cd7081: Claude is still at it, so Close offers to stop it rather than closing under it.
        Assert.Equal(TaskStatus.InProgress, t.Tasks.Board.Tasks.Single().Status);
        var stop = Assert.IsAssignableFrom<ContextMenu>(t.Window.OpenedMenu);
        Assert.Contains(stop.Items.OfType<MenuItem>(), i => i.Header is TextBlock { Text: "Claude is still working on this task." } && !i.IsEnabled);
        var item = stop.Items.OfType<MenuItem>().Single(i => i.Header is TextBlock { Text: TaskViews.StopClaudeAndClose });
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        stop.IsOpen = false;
        t.Window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

        Assert.Equal(TaskStatus.Closed, t.Tasks.Board.Tasks.Single().Status);
    });

    // Task 37cd7081: the tick on a task Claude is still working on says it will offer to stop Claude, and opens the same
    // menu; a note's tick still closes at once.
    [Fact]
    public void The_tick_on_a_task_claude_works_on_offers_to_stop_it() => StaThread.Run(() =>
    {
        using var t = Build();
        var working = t.Tasks.Create("Still going", t.Gepha, Session("s1")).Value.Id;
        t.Controller.Show();

        Assert.True(t.Controller.Close(working).IsSuccess);
        Assert.Equal(TaskStatus.InProgress, t.Tasks.Board.Tasks.Single().Status);
        var stop = Assert.IsAssignableFrom<ContextMenu>(t.Window.OpenedMenu);
        Assert.Contains(stop.Items.OfType<MenuItem>(), i => i.Header is TextBlock { Text: TaskViews.StopClaudeAndClose });
        stop.IsOpen = false;

        Assert.True(t.Controller.Close(working, stopClaude: true).IsSuccess);
        Assert.Equal(TaskStatus.Closed, t.Tasks.Board.Tasks.Single().Status);
        // The undo window holds as for any close.
        Assert.True(t.Controller.Undo(working).IsSuccess);
        Assert.Equal(TaskStatus.InProgress, t.Tasks.Board.Tasks.Single().Status);
    });

    // Ruling 42 (Petre: "don't say claude; mark with claude at the beginning with the" orange edge): a Claude line has
    // no CL badge in the window either; its mark is the orange edge at its start, and its title begins where the badge
    // was.
    [Fact]
    public void A_claude_line_is_marked_by_its_edge_not_a_badge() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.Create("Tasks redesign", t.Gepha, Session("s1")).Value.Id;
        t.Controller.Show();

        var line = LineOf(t.Window, id);
        Assert.DoesNotContain(Inside(line).OfType<TextBlock>(), b => b.Text is "CL" or "CLAUDE");
        var edge = Inside(line).OfType<Border>().First(b => b.BorderThickness.Left == 3);
        Assert.Equal(System.Windows.Media.Color.FromRgb(FloatingBar.AskingOrange.R, FloatingBar.AskingOrange.G, FloatingBar.AskingOrange.B),
            ((System.Windows.Media.SolidColorBrush)edge.BorderBrush).Color);
    });

    // Ruling 44 (Petre: "task rightclick should let me edit it first, first option"). A right-click on a line opens its
    // menu at the pointer, the same menu as its "⋯", and Edit is its first item; Edit opens the in-place editor and a
    // save renames.
    [Fact]
    public void A_right_click_opens_the_lines_menu_with_Edit_first() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("buy body shampoo", t.Gepha).Value.Id;
        t.Controller.Show();
        t.Window.UpdateLayout();

        LineOf(t.Window, id).RaiseEvent(RightRelease());

        var menu = Assert.IsAssignableFrom<ContextMenu>(t.Window.OpenedMenu);
        Assert.True(menu.IsOpen);
        var first = Assert.IsType<MenuItem>(menu.Items[0]);
        Assert.Equal("Edit", ((TextBlock)first.Header).Text);
        first.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.IsOpen = false;
        var editor = Assert.IsType<TextBox>(t.Window.Editor);
        editor.Text = "buy body shampoo and soap";
        t.Window.SaveEditor();
        Assert.Equal("buy body shampoo and soap", t.Tasks.Board.Tasks.Single().Title);
        Assert.Empty(t.WentTo);
    });

    // The "⋯" menu has Edit first too, in place of the old "Rename…".
    [Fact]
    public void The_more_menu_has_Edit_first() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("note", t.Gepha).Value.Id;
        t.Controller.Show();

        var menu = t.Window.OpenMenuFor(id);

        Assert.Equal("Edit", ((TextBlock)Assert.IsType<MenuItem>(menu.Items[0]).Header).Text);
        Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), i => i.Header is TextBlock { Text: "Rename…" });
        menu.IsOpen = false;
    });

    // The tunnelling release a real right-click makes: each element on its way down raises its own
    // PreviewMouseRightButtonUp from it, the line's included.
    static MouseButtonEventArgs RightRelease() =>
        new(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = Mouse.PreviewMouseUpEvent };

    // Ruling 38 (Petre: "let tasks be multiline and editable"). A long title wraps to show its whole text instead of
    // ending in an ellipsis, in the List and in the Matrix: its line grows taller, the title is never trimmed.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_long_title_wraps_and_is_not_trimmed(bool matrix) => StaThread.Run(() =>
    {
        using var t = Build();
        var shortOne = t.Tasks.AddNote("short", t.Gepha).Value.Id;
        var longOne = t.Tasks.AddNote(string.Join(" ", Enumerable.Repeat("a long title that goes on", 12)), t.Gepha).Value.Id;
        Assert.True(t.Harness.Manager.SetTasksMatrix(matrix).IsSuccess);
        t.Controller.Show();
        t.Window.UpdateLayout();

        var title = TitleOf(t.Window, longOne);
        Assert.Equal(TextWrapping.Wrap, title.TextWrapping);
        Assert.Equal(TextTrimming.None, title.TextTrimming);
        Assert.True(title.ActualHeight > 2 * TitleOf(t.Window, shortOne).ActualHeight,
            $"the long title is {title.ActualHeight} high, the short one {TitleOf(t.Window, shortOne).ActualHeight}");
    });

    static TextBlock TitleOf(TasksWindow window, Guid id) =>
        Inside(LineOf(window, id)).OfType<TextBlock>().Single(b => Equals(b.Tag, "task-title"));

    // RULING 54 (Petre: "clicking a task should open that task; double clicking takes me to the workspace and window").
    // A single click on a line picks it and expands its comment thread, and goes nowhere; every click acts at once now
    // (there is no wait for a second click, so no delayed go to call off).
    [Fact]
    public void A_click_on_a_line_picks_and_expands_it_and_goes_nowhere() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("buy body shampoo", t.Gepha).Value.Id;
        t.Tasks.Comment(id, Maybe<string>.None, "a comment");
        t.Controller.Show();
        t.Window.UpdateLayout();
        Assert.False(t.Window.ThreadShown(id));
        Click(LineOf(t.Window, id));

        Assert.Empty(t.WentTo);
        Assert.True(t.Window.ThreadShown(id));
        Assert.Null(t.Window.Editor);
    });

    // A double-click anywhere on the line goes to the task, and does not edit it (editing is F2 or Edit in the menu).
    [Fact]
    public void A_double_click_on_a_line_goes_to_its_task_and_does_not_edit() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("buy body shampoo", t.Gepha).Value.Id;
        t.Controller.Show();
        t.Window.UpdateLayout();

        DoubleClick(t.Window, id);

        Assert.Equal([id], t.WentTo);
        Assert.Null(t.Window.Editor);
    });

    // The second press of a double-click is the line's, handled before it clicks again, so one double-click is one go.
    static void DoubleClick(TasksWindow window, Guid id)
    {
        window.UpdateLayout();
        var line = LineOf(window, id);
        line.RaiseEvent(TitlePress(1));
        Click(line);
        line.RaiseEvent(TitlePress(2));
    }

    // The tunnelling press a real click on the title makes (see A_double_click_on_the_title_opens_the_editor).
    static MouseButtonEventArgs TitlePress(int clicks)
    {
        var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent };
        typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!.SetValue(press, clicks);
        return press;
    }

    // The bar's single click (Ruling 54): the window opens on that task, picked and expanded to its thread, and goes
    // nowhere. With the window already open the same click brings it forward on the task.
    [Fact]
    public void The_bars_click_opens_the_window_on_that_task_picked_and_expanded() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Tasks.AddNote("first", t.Gepha);
        var id = t.Tasks.AddNote("second", t.Gepha).Value.Id;
        Assert.False(t.Window.IsOpen);

        t.Controller.ShowTask(id);

        Assert.True(t.Window.IsOpen);
        Assert.True(t.Window.IsSelected(id));
        Assert.True(t.Window.ThreadShown(id));
        Assert.Empty(t.WentTo);
    });

    // RULING 52 (Petre's screenshot, "better alignment": the right-hand parts zigzagged, the lines had different widths,
    // Claude lines had no tick box and Done ones an X). In the List every line spans the list, and each column, the
    // flags, the sessions, the age, the chip, the comments and the menu button, starts at the same x on every line,
    // whatever the line holds: a short or a long title, comments, sessions, flags or none of them.
    [Fact]
    public void Every_lines_columns_start_at_the_same_x_and_every_line_spans_the_window() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Tasks.AddNote("short", t.Gepha);
        var long1 = t.Tasks.AddNote("a much longer title that wraps over a good few lines of the window because it never ends", t.Gepha).Value.Id;
        t.Tasks.SetFlag(long1, TaskFlag.Important, true);
        var withComments = t.Tasks.AddNote("commented", Maybe<Guid>.None).Value.Id;
        t.Tasks.Comment(withComments, Maybe<string>.None, "one");
        var claude = t.Tasks.Create("Tasks redesign", t.Gepha, Session("s1")).Value.Id;
        t.Tasks.Join(claude, Session("s2"));
        var done = t.Tasks.Create("Merged", t.Gepha, Session("s3")).Value.Id;
        t.Tasks.MarkDone(done);
        t.Controller.Show();
        t.Window.UpdateLayout();

        var lines = t.Tasks.Board.Pile().Select(x => LineOf(t.Window, x.Id)).ToList();
        Assert.True(lines.Count >= 5);
        double Left(FrameworkElement e) => e.TransformToAncestor(t.Window).TransformBounds(new Rect(e.RenderSize)).Left;
        double Width(FrameworkElement e) => e.TransformToAncestor(t.Window).TransformBounds(new Rect(e.RenderSize)).Width;
        var grids = lines.Select(line => Inside(line).OfType<Grid>().First(g => g.ColumnDefinitions.Count > 4)).ToList();
        Assert.All(lines, line => Assert.Equal(Width(lines[0]), Width(line), 0.5));
        Enumerable.Range(0, grids[0].ColumnDefinitions.Count).ToList().ForEach(c =>
        {
            var xs = grids.Select(g => Left(g) + g.ColumnDefinitions[c].Offset).ToList();
            Assert.True(xs.Max() - xs.Min() < 0.5, $"column {c} starts at {string.Join(", ", xs.Select(x => x.ToString("0.#")))}");
        });
    });

    // Claude lines have the tick box a note has in the window, a Done one included, and no X.
    [Fact]
    public void A_claude_line_has_a_tick_box_in_the_window_and_a_done_one_no_cross() => StaThread.Run(() =>
    {
        using var t = Build();
        var working = t.Tasks.Create("Working", t.Gepha, Session("s1")).Value.Id;
        var done = t.Tasks.Create("Merged", t.Gepha, Session("s2")).Value.Id;
        t.Tasks.MarkDone(done);
        t.Controller.Show();

        Assert.Contains(Inside(LineOf(t.Window, working)).OfType<Button>(), b => AutomationProperties.GetName(b) == "Close");
        Assert.Contains(Inside(LineOf(t.Window, done)).OfType<Button>(), b => AutomationProperties.GetName(b) == "Close");
        Assert.DoesNotContain(Inside(LineOf(t.Window, done)).OfType<TextBlock>(), b => b.Text == "✕");
        Click(ButtonNamed(LineOf(t.Window, done), "Close"));
        Assert.Equal(TaskStatus.Closed, t.Tasks.Board.Tasks.Single(x => x.Id == done).Status);
    });

    // The age is left-aligned in its column, so its x does not follow the text length ("now" and "3d3h" start together),
    // and the sessions column holds "2 sessions, 1 live" without spilling into the age's.
    [Fact]
    public void The_age_starts_at_one_x_and_the_sessions_words_stay_in_their_column() => StaThread.Run(() =>
    {
        using var t = Build(now: () => Start.AddDays(3).AddHours(3));
        var young = t.Tasks.AddNote("young", t.Gepha).Value.Id;
        var claude = t.Tasks.Create("Tasks redesign", t.Gepha, Session("s1")).Value.Id;
        t.Tasks.Join(claude, Session("s2"));
        t.Controller.Show();
        t.Window.UpdateLayout();

        double Left(FrameworkElement e) => e.TransformToAncestor(t.Window).TransformBounds(new Rect(e.RenderSize)).Left;
        double Right(FrameworkElement e) => e.TransformToAncestor(t.Window).TransformBounds(new Rect(e.RenderSize)).Right;
        TextBlock AgeIn(Guid id) => Inside(LineOf(t.Window, id)).OfType<TextBlock>().Single(b => Equals(b.Tag, "task-age"));
        Assert.Equal(Left(AgeIn(young)), Left(AgeIn(claude)), 0.5);

        var words = Inside(LineOf(t.Window, claude)).OfType<TextBlock>().Single(b => b.Text.Contains("sessions"));
        Assert.True(words.ActualWidth >= words.DesiredSize.Width - words.Margin.Left - words.Margin.Right - 0.5, $"'{words.Text}' is trimmed to {words.ActualWidth} of {words.DesiredSize.Width}");
        Assert.True(Right(words) <= Left(AgeIn(claude)) + 0.5, $"the sessions words end at {Right(words)}, the age starts at {Left(AgeIn(claude))}");
    });

    // Petre's second screenshot (2.5.238): the expanded comment panel ("Add a comment...") ran past the window's right
    // edge and was clipped. Under the app's own theme (Build puts the window under ThemeMode="System"), a thread and
    // its comment box stay inside the list's width, on a narrow window and with a long comment too.
    [Fact]
    public void An_expanded_thread_stays_inside_the_windows_width() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("a note with a title that is quite long, long enough to wrap in a narrow window of tasks", t.Gepha).Value.Id;
        t.Tasks.Comment(id, Maybe<string>.None, "A very long comment " + string.Join(" ", Enumerable.Repeat("with many words", 30)) + " `and_a_long_unbreakable_code_span_" + new string('x', 80) + "`");
        t.Controller.Show();
        t.Window.Width = TasksPanelLayout.SlimBelow;
        t.Window.UpdateLayout();
        Click(ButtonNamed(LineOf(t.Window, id), "Comments"));
        t.Window.UpdateLayout();

        var box = t.Window.CommentBoxOf(id)!;
        var scroller = Inside(t.Window).OfType<ScrollViewer>().First(s => s.Content is StackPanel);
        double Right(FrameworkElement e) => e.TransformToAncestor(scroller).TransformBounds(new Rect(e.RenderSize)).Right;
        Assert.True(Right(box) <= scroller.ViewportWidth + 0.5, $"the comment box ends at {Right(box)} in a viewport {scroller.ViewportWidth} wide");
        var thread = Inside(t.Window).OfType<Border>().Where(b => b.IsAncestorOf(box) && b.Child is StackPanel).OrderBy(b => b.ActualWidth).Last();
        Assert.True(Right(thread) <= scroller.ViewportWidth + 0.5, $"the thread ends at {Right(thread)} in a viewport {scroller.ViewportWidth} wide");
    });

    // F2 on the selected line opens it too; Enter saves through Rename; Esc cancels.
    [Fact]
    public void F2_edits_the_selected_line_Enter_saves_and_Esc_cancels() => StaThread.Run(() =>
    {
        var selection = new TaskSelection();
        using var t = Build(selection: selection);
        var id = t.Tasks.AddNote("iam.ge: transcripts", t.Gepha).Value.Id;
        t.Controller.Show();
        selection.Select(id);

        PressKey(t.Window, Key.F2);
        var editor = Assert.IsType<TextBox>(t.Window.Editor);
        editor.Text = "iam.ge: transcripts for every call";
        t.Window.SaveEditor();
        Assert.Null(t.Window.Editor);
        Assert.Equal("iam.ge: transcripts for every call", t.Tasks.Board.Tasks.Single().Title);

        PressKey(t.Window, Key.F2);
        Assert.IsType<TextBox>(t.Window.Editor).Text = "thrown away";
        PressKey(t.Window, Key.Escape);
        Assert.Null(t.Window.Editor);
        Assert.Equal("iam.ge: transcripts for every call", t.Tasks.Board.Tasks.Single().Title);
    });

    // Enter saves and Shift+Enter is a line break, which the title keeps.
    [Fact]
    public void Shift_Enter_keeps_a_line_break_in_the_title() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("first", t.Gepha).Value.Id;
        t.Controller.Show();

        Assert.True(TasksWindow.EditorSaves(Key.Enter, ModifierKeys.None));
        Assert.False(TasksWindow.EditorSaves(Key.Enter, ModifierKeys.Shift));
        t.Window.BeginRename(id);
        var editor = Assert.IsType<TextBox>(t.Window.Editor);
        Assert.True(editor.AcceptsReturn);
        editor.Text = "first\nsecond";
        t.Window.SaveEditor();

        Assert.Equal("first\nsecond", t.Tasks.Board.Tasks.Single().Title);
        Assert.Contains("second", TitleOf(t.Window, id).Text);
    });

    // A change arriving while the editor is open (a session renaming another task) waits: the editor, its text and the
    // keyboard survive, and the change is drawn once the editor closes.
    [Fact]
    public void A_redraw_does_not_kill_an_edit_in_progress() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("mine", t.Gepha).Value.Id;
        var other = t.Tasks.AddNote("theirs", t.Gepha).Value.Id;
        t.Controller.Show();
        t.Window.BeginRename(id);
        var editor = Assert.IsType<TextBox>(t.Window.Editor);
        editor.Text = "half typ";

        t.Tasks.Rename(other, "theirs, renamed");

        Assert.Same(editor, t.Window.Editor);
        Assert.Equal("half typ", editor.Text);
        PressKey(t.Window, Key.Escape);
        Assert.Equal("theirs, renamed", TitleOf(t.Window, other).Text);
    });

    static void PressKey(TasksWindow window, Key key) =>
        window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });

    // The toolbar's "Workspace: all ▾" was built the same way and had the same fault: a routed click opens its menu, and
    // a pick filters the board.
    [Fact]
    public void The_workspace_filter_chip_opens_its_menu_and_a_pick_filters() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Tasks.AddNote("here", t.Gepha);
        t.Tasks.AddNote("there", t.Sparrow);
        t.Controller.Show();

        Click(ButtonNamed(t.Window, "Workspace filter"));

        var menu = Assert.IsAssignableFrom<ContextMenu>(t.Window.OpenedMenu);
        Assert.True(menu.IsOpen);
        Click(WorkspaceChipMenu.Chips(menu).Single(c => AutomationProperties.GetName(c) == "Sparrow"));
        Assert.Equal("Workspace: Sparrow", t.Window.WorkspaceFilterLabel);
    });

    // "Current workspace" is a checkbox beside the workspace filter (Petre: "let's just do a checkbox, it would be easier
    // to tick/untick"). A click ticks it and the list follows the workspace in front; a second click unticks it and every
    // workspace shows. Picking a workspace from the menu beside it unticks it, and the menu has no entry of its own for it.
    [Fact]
    public void The_current_workspace_checkbox_ticks_and_unticks() => StaThread.Run(() =>
    {
        using var t = Build();
        var here = t.Tasks.AddNote("here", t.Gepha).Value.Id;
        var there = t.Tasks.AddNote("there", t.Sparrow).Value.Id;
        SwitchTo(t, t.Gepha);
        t.Controller.Show();
        Assert.False(t.Window.CurrentWorkspaceOn);

        Click(ButtonNamed(t.Window, "Current workspace"));
        Assert.True(t.Window.CurrentWorkspaceOn);
        Assert.Equal([here], t.Window.Keys);
        Assert.Equal("checked", AutomationProperties.GetItemStatus(ButtonNamed(t.Window, "Current workspace")));

        Click(ButtonNamed(t.Window, "Current workspace"));
        Assert.False(t.Window.CurrentWorkspaceOn);
        Assert.Equal([there, here], t.Window.Keys);
        Assert.Equal("Workspace: all", t.Window.WorkspaceFilterLabel);

        Click(ButtonNamed(t.Window, "Current workspace"));
        Click(ButtonNamed(t.Window, "Workspace filter"));
        var menu = Assert.IsAssignableFrom<ContextMenu>(t.Window.OpenedMenu);
        Assert.DoesNotContain(WorkspaceChipMenu.Chips(menu), c => AutomationProperties.GetName(c) == "Current workspace");
        Assert.DoesNotContain(WorkspaceChipMenu.Chips(menu), c => AutomationProperties.GetItemStatus(c) == WorkspaceChipMenu.CurrentStatus);
        Click(WorkspaceChipMenu.Chips(menu).Single(c => AutomationProperties.GetName(c) == "Sparrow"));
        Assert.False(t.Window.CurrentWorkspaceOn);
        Assert.Equal([there], t.Window.Keys);
    });

    // "Subtasks" (task 173ef268; Petre: "checkbox at the top to show nested tasks or not"). Ticked by default, as every
    // build before it drew subtasks. Unticked, the subtasks drawn under a parent fold into its line, which keeps counting
    // them, and the choice is saved, so a fresh open keeps it. Ticked again, they are back under the parent.
    [Fact]
    public void The_subtasks_checkbox_folds_subtasks_into_their_parent() => StaThread.Run(() =>
    {
        using var t = Build();
        var parent = t.Tasks.AddNote("Parent", t.Gepha).Value.Id;
        var child = t.Tasks.AddNote("Child", t.Gepha, Quadrant.IV, parent).Value.Id;
        t.Controller.Show();
        Assert.True(t.Window.SubtasksOn);
        Assert.Equal([parent, child], t.Window.Keys);

        Click(ButtonNamed(t.Window, "Subtasks"));
        Assert.False(t.Window.SubtasksOn);
        Assert.Equal([parent], t.Window.Keys);
        Assert.Equal("unchecked", AutomationProperties.GetItemStatus(ButtonNamed(t.Window, "Subtasks")));
        Assert.Contains("1 subtask", RunsOf(TitleOf(t.Window, parent)));
        Assert.True(t.Harness.Manager.State.TasksPanel?.HideSubtasks);

        t.Controller.Hide();
        t.Controller.Show();
        Assert.False(t.Window.SubtasksOn);
        Assert.Equal([parent], t.Window.Keys);

        Click(ButtonNamed(t.Window, "Subtasks"));
        Assert.True(t.Window.SubtasksOn);
        Assert.Equal([parent, child], t.Window.Keys);
    });

    // An id typed on the Stats view (task 6e4dc116) leaves it for the task found; a word search stays on Stats.
    [Fact]
    public void An_id_typed_on_the_stats_view_shows_its_task() => StaThread.Run(() =>
    {
        using var t = Build();
        var closed = t.Tasks.AddNote("Old chore", t.Gepha).Value.Id;
        t.Tasks.Close(closed);
        t.Tasks.AddNote("Live chore", t.Gepha);
        t.Controller.Show();
        Click(ButtonNamed(t.Window, "Stats"));

        t.Controller.Search("chore");
        Assert.True(t.Window.StatsShown);

        t.Controller.Search(closed.ToString()[..8]);
        Assert.False(t.Window.StatsShown);
        Assert.Equal([closed], t.Window.Keys);
    });

    // A subtask found by its id (task 6e4dc116) shows with the Subtasks checkbox unticked: a lookup never folds.
    [Fact]
    public void A_subtask_found_by_its_id_shows_with_subtasks_folded() => StaThread.Run(() =>
    {
        using var t = Build();
        var parent = t.Tasks.AddNote("Parent", t.Gepha).Value.Id;
        var child = t.Tasks.AddNote("Child", t.Gepha, Quadrant.IV, parent).Value.Id;
        t.Controller.Show();
        Click(ButtonNamed(t.Window, "Subtasks"));
        Assert.Equal([parent], t.Window.Keys);

        t.Controller.Search(child.ToString()[..8]);

        Assert.Equal([child], t.Window.Keys);
    });

    // The screencast shoot on v2.5.189: the toolbar's dropdowns did not open by mouse, keyboard or UI Automation. UIA's
    // Invoke on a dropdown goes through the button's own Click as a mouse click does, so it opens the menu too: what a
    // screen reader or a keyboard-driven tool does reaches the same press.
    [Theory]
    [InlineData("Workspace filter")]
    [InlineData("Add to workspace")]
    public void UI_Automation_Invoke_opens_a_toolbar_dropdown(string name) => StaThread.Run(() =>
    {
        using var t = Build();
        t.Tasks.AddNote("here", t.Gepha);
        t.Controller.Show();
        if (name == "Add to workspace") Click(ButtonNamed(t.Window, "Add"));
        var button = ButtonNamed(t.Window, name);

        var invoke = (System.Windows.Automation.Provider.IInvokeProvider)System.Windows.Automation.Peers.UIElementAutomationPeer
            .CreatePeerForElement(button).GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke);
        invoke.Invoke();
        // The peer raises the click through the dispatcher.
        t.Window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

        Assert.True(Assert.IsAssignableFrom<ContextMenu>(t.Window.OpenedMenu).IsOpen);
        t.Window.OpenedMenu!.IsOpen = false;
    });

    // Whole-PR review, minor 4: a state pulse the window does not show (here the Claude skill setting) builds no view;
    // a change of the workspaces does.
    [Fact]
    public void Only_a_state_change_the_window_shows_refreshes_it() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Tasks.AddNote("a", t.Gepha);
        t.Controller.Show();
        var presents = t.Window.Presents;

        Assert.True(t.Harness.Manager.SetClaudeSkill(false).IsSuccess);
        Assert.Equal(presents, t.Window.Presents);

        Assert.True(t.Harness.Manager.RenameWorkspace(t.Gepha, "GEPHA two").IsSuccess);
        Assert.True(t.Window.Presents > presents);
    });

    // Ruling 31 in the window: Undo brings a task waiting on you back waiting, with its question, and selected again.
    [Fact]
    public void Undo_in_the_window_brings_back_waiting_with_its_question_and_the_selection() => StaThread.Run(() =>
    {
        var selection = new TaskSelection();
        using var t = Build(selection: selection);
        var id = t.Tasks.Create("work", t.Gepha, Session("s1")).Value.Id;
        t.Tasks.Ask(id, "OK to merge?");
        t.Controller.Show();
        selection.Select(id);

        // Its session is still live, so the close is "Stop Claude and close" (task 37cd7081).
        Assert.True(t.Controller.Close(id, stopClaude: true).IsSuccess);
        Assert.True(t.Controller.Undo(id).IsSuccess);

        var task = t.Tasks.Board.Tasks.Single(x => x.Id == id);
        Assert.Equal(TaskStatus.WaitingOnYou, task.Status);
        Assert.Equal("OK to merge?", task.Question.Value);
        Assert.Equal(id, selection.Current.Value);
    });

    // Whole-PR review, minor 5: a Matrix line makes no sessions, age or close button it has no column for, and the
    // selected task is scrolled into view inside its cell when the window opens on the Matrix.
    [Fact]
    public void A_matrix_line_has_no_hidden_columns() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.Create("work", t.Gepha, Session("s1")).Value.Id;
        t.Tasks.MarkDone(id);
        Assert.True(t.Harness.Manager.SetTasksMatrix(true).IsSuccess);
        t.Controller.Show();

        var line = LineOf(t.Window, id);
        Assert.Equal("", t.Window.AgeText(id));
        Assert.DoesNotContain(Inside(line).OfType<TextBlock>(), b => b.Text == "✕"); // the tick box closes it now (Ruling 52)
        Assert.DoesNotContain(Inside(line).OfType<TextBlock>(), b => b.Text.Contains("session"));
    });

    [Fact]
    public void The_matrix_scrolls_the_selected_task_into_view_in_its_cell() => StaThread.Run(() =>
    {
        var selection = new TaskSelection();
        using var t = Build(selection: selection);
        var oldest = t.Tasks.AddNote("the oldest", t.Gepha).Value.Id;
        Enumerable.Range(1, 40).ToList().ForEach(i => t.Tasks.AddNote($"note {i}", t.Gepha));
        Assert.True(t.Harness.Manager.SetTasksMatrix(true).IsSuccess);
        selection.Select(oldest);

        t.Controller.Show();

        Assert.True(t.Window.CellScroller(Quadrant.IV).VerticalOffset > 0, $"offset {t.Window.CellScroller(Quadrant.IV).VerticalOffset}");
    });

    // --- RULING 57, the assignee: a column, a filter, and "Assign to" (in the window only, never on the bar) ---------

    static string AssigneeTextOf(TasksWindow window, Guid id) =>
        Inside(LineOf(window, id)).OfType<TextBlock>().Single(b => Equals(b.Tag, "task-assignee")).Text;

    [Fact]
    public void Each_line_names_its_assignee() => StaThread.Run(() =>
    {
        using var t = Build();
        var note = t.Tasks.AddNote("mine", t.Gepha).Value.Id;
        var working = t.Tasks.Create("Claude's", t.Gepha, Session("s1")).Value.Id;
        t.Controller.Show();

        Assert.Equal("Me", AssigneeTextOf(t.Window, note));
        Assert.Equal("Claude", AssigneeTextOf(t.Window, working));
    });

    [Fact]
    public void The_assigned_filter_narrows_the_lines_and_the_chip_counts() => StaThread.Run(() =>
    {
        using var t = Build();
        var note = t.Tasks.AddNote("mine", t.Gepha).Value.Id;
        var working = t.Tasks.Create("Claude's", t.Gepha, Session("s1")).Value.Id;
        t.Controller.Show();
        Assert.Equal("Assigned: all", t.Window.AssigneeFilterLabel);
        Assert.Contains("Open 2", t.Window.FilterLabels);

        t.Controller.FilterAssignee(TaskAssigneeFilter.Claude);
        Assert.Equal([working], t.Window.Keys);
        Assert.Equal("Assigned: Claude", t.Window.AssigneeFilterLabel);
        Assert.Contains("Open 1", t.Window.FilterLabels);

        t.Controller.FilterAssignee(TaskAssigneeFilter.Me);
        Assert.Equal([note], t.Window.Keys);
        Assert.Equal("Assigned: me", t.Window.AssigneeFilterLabel);

        t.Controller.FilterAssignee(TaskAssigneeFilter.All);
        Assert.Equal(2, t.Window.Keys.Count);
        // A fresh opening starts at "all" again, as the other filters do.
        t.Controller.FilterAssignee(TaskAssigneeFilter.Me);
        t.Controller.Hide();
        t.Controller.Show();
        Assert.Equal("Assigned: all", t.Window.AssigneeFilterLabel);
    });

    [Fact]
    public void The_assigned_filter_has_a_menu_of_me_claude_and_all() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Tasks.AddNote("mine", t.Gepha);
        t.Tasks.Create("Claude's", t.Gepha, Session("s1"));
        t.Controller.Show();

        var menu = t.Window.OpenAssigneeFilterMenu();
        Assert.Equal(["Me", "Claude", "All"], menu.Items.OfType<MenuItem>().Select(i => ((TextBlock)i.Header).Text));
        menu.Items.OfType<MenuItem>().Single(i => i.Header is TextBlock { Text: "Claude" }).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.IsOpen = false;
        t.Window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

        Assert.Equal("Assigned: Claude", t.Window.AssigneeFilterLabel);
    });

    // Both the "..." menu and the right-click menu (one menu, OpenMenuFor) have "Assign to" with Me and Claude, the
    // task's own ticked; a pick goes through TaskService.Assign.
    [Fact]
    public void Assign_to_in_the_menu_hands_the_task_over() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("mine", t.Gepha).Value.Id;
        t.Controller.Show();

        foreach (var atPointer in new[] { false, true })
        {
            var menu = t.Window.OpenMenuFor(id, atPointer);
            var assign = menu.Items.OfType<MenuItem>().Single(i => i.Header is TextBlock { Text: "Assign to" });
            var choices = assign.Items.OfType<MenuItem>().ToList();
            Assert.Equal(["Me", "Claude"], choices.Select(i => ((TextBlock)i.Header).Text));
            var wasMe = t.Tasks.Board.Tasks.Single().Assignee.IsMe;
            Assert.Equal(wasMe, choices[0].IsChecked);
            choices[wasMe ? 1 : 0].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.IsOpen = false;
            t.Window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            Assert.Equal(!wasMe, t.Tasks.Board.Tasks.Single().Assignee.IsMe);
        }
    });

    // --- RULING 58, edit covers the description and Petre's own comments -------------------------------------------------

    static TextBlock? DescriptionBlockOf(TasksWindow window, Guid id) =>
        Inside(LineOf(window, id)).OfType<RichTextBox>().FirstOrDefault(b => Equals(b.Tag, "task-description"))?.Document is { } doc
            ? new TextBlock { Text = new System.Windows.Documents.TextRange(doc.ContentStart, doc.ContentEnd).Text.Trim() }
            : null;

    static TextBlock? DescriptionPreviewOf(TasksWindow window, Guid id) =>
        Inside(LineOf(window, id)).OfType<TextBlock>().FirstOrDefault(b => Equals(b.Tag, "task-description-preview"));

    [Fact]
    public void A_description_shows_under_the_title_as_markdown_once_the_line_is_open() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("Pay rent", t.Gepha).Value.Id;
        var plain = t.Tasks.AddNote("No words", t.Gepha).Value.Id;
        t.Tasks.Describe(id, "The **landlord** wants it by Friday");
        t.Controller.Show();
        t.Controller.ToggleThread(id);

        Assert.Equal("The landlord wants it by Friday", DescriptionBlockOf(t.Window, id)!.Text);
        Assert.Null(DescriptionPreviewOf(t.Window, id));
        Assert.Null(DescriptionBlockOf(t.Window, plain));
        Assert.Null(DescriptionPreviewOf(t.Window, plain));
    });

    // Petre, 2026-11-23, of a long brief that filled the window: "i want that after i go to details". A closed line
    // shows only the description's first line, plain, cut to one line; opening the line shows the whole of it, and
    // closing it again goes back to the one line.
    [Fact]
    public void A_closed_line_shows_only_the_descriptions_first_line() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("Profile", t.Gepha).Value.Id;
        t.Tasks.Describe(id, "Parked, pick up **from here**.\n## What to do next\n1. answer the questions");
        t.Controller.Show();

        var preview = DescriptionPreviewOf(t.Window, id);
        Assert.Equal("Parked, pick up from here.", preview!.Text);
        Assert.Equal(TextWrapping.NoWrap, preview.TextWrapping);
        Assert.Equal(TextTrimming.CharacterEllipsis, preview.TextTrimming);
        Assert.Null(DescriptionBlockOf(t.Window, id));

        t.Controller.ToggleThread(id);
        Assert.Contains("answer the questions", DescriptionBlockOf(t.Window, id)!.Text);
        Assert.Null(DescriptionPreviewOf(t.Window, id));

        t.Controller.ToggleThread(id);
        Assert.NotNull(DescriptionPreviewOf(t.Window, id));
        Assert.Null(DescriptionBlockOf(t.Window, id));
    });

    // Petre, 2026-11-24, of a question cut at "Add an "O…" with the thread open: "where's the question?". A closed line
    // cuts the question to one line; an open line shows it all, wrapped, and closing it cuts it again.
    [Fact]
    public void An_open_line_shows_the_whole_question() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.Create("Tasks redesign", t.Gepha, Session("s1")).Value.Id;
        t.Tasks.Ask(id, "Open already resumes an ended session. Add an \"Open Claude session\" item to the menu, or leave it?");
        t.Controller.Show();

        TextBlock Detail() => Inside(LineOf(t.Window, id)).OfType<TextBlock>().Single(b => Equals(b.Tag, TasksWindow.DetailTag));
        Assert.Equal(TextTrimming.CharacterEllipsis, Detail().TextTrimming);
        Assert.Equal(TextWrapping.NoWrap, Detail().TextWrapping);

        t.Controller.ToggleThread(id);
        Assert.Equal(TextTrimming.None, Detail().TextTrimming);
        Assert.Equal(TextWrapping.Wrap, Detail().TextWrapping);
        Assert.StartsWith("Open already resumes", Detail().Text);

        t.Controller.ToggleThread(id);
        Assert.Equal(TextTrimming.CharacterEllipsis, Detail().TextTrimming);
    });

    // Task d560f3eb ("misaligned text"): a waiting task's question stood in the title's column, a few letters wide, in a
    // narrow panel. Before the fix, an open line's question was 29 DIP wide and 629 tall at 360 DIP, 129 wide at 460 and
    // 211 at 542 (measured here). Now it spans the line under the title, from the title's left edge to the line's right
    // end, at every width and on a closed line as on an open one, and the toggles stay with the title rather than half
    // way down the question (on a slim line, on the title's last line or just under it, task 65ab598d).
    [Theory]
    [InlineData(TasksPanelLayout.MinWidth)]
    [InlineData(TasksPanelLayout.DefaultWidth)]
    [InlineData(542)]
    public void A_question_spans_the_line_under_its_title(double width) => StaThread.Run(() =>
    {
        using var t = Build(width: width);
        var id = t.Tasks.Create("დისტრიბუციის ეკრანი", t.Gepha, Session("s1")).Value.Id;
        t.Tasks.Ask(id, "Approve Playwright for this session: type /mcp in the session and approve playwright, so I can walk the new screen (or open http://localhost:20002, menu დისტრიბუციის ეკრანი, and look yourself).");
        t.Controller.Show();

        void Check(string state)
        {
            t.Window.UpdateLayout();
            var line = LineOf(t.Window, id);
            var detail = Inside(line).OfType<TextBlock>().Single(b => Equals(b.Tag, TasksWindow.DetailTag));
            var title = Inside(line).OfType<TextBlock>().Single(b => Equals(b.Tag, "task-title"));
            var flags = Inside(line).OfType<Button>().First(b => Equals(b.Tag, "task-flag-off") || Equals(b.Tag, "task-flag-on"));
            var titleLeft = title.TranslatePoint(new Point(0, 0), line).X;
            var detailAt = detail.TranslatePoint(new Point(0, 0), line);
            // From the title's left edge (the same x, within its 2 DIP margin) to near the line's right end. The slim
            // line's title spans the line too since task 65ab598d, so the question is measured against the line, no
            // longer against the title: no narrower than the line less the tick column, the margins and the scroll bar.
            Assert.InRange(detailAt.X, titleLeft - 0.5, titleLeft + 0.5);
            Assert.True(detail.ActualWidth > line.ActualWidth - 60,
                $"{state} at {width}: the question is {detail.ActualWidth} DIP wide in a {line.ActualWidth} DIP line");
            // Under the title, and the toggles above the question rather than lower down beside it.
            Assert.True(detailAt.Y >= title.TranslatePoint(new Point(0, title.ActualHeight), line).Y - 0.5, $"{state}: the question is under the title");
            Assert.True(flags.TranslatePoint(new Point(0, 0), line).Y < detailAt.Y, $"{state}: the toggles sit beside the title");
        }

        Check("closed");
        t.Controller.ToggleThread(id);
        Check("open");
    });

    // Task 08cdb052 ("when asking for action, prs should have links, or artifacts; make that clickable"): a question's
    // markdown link and a bare address are links in the line's detail, on a closed line and an open one, and the text
    // reads as the words, never the markdown written out. Only web addresses become links.
    [Fact]
    public void A_questions_links_are_clickable_in_the_line() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.Create("Snooze", t.Gepha, Session("s1")).Value.Id;
        t.Tasks.Ask(id, "Merge [Deskbar #490](https://github.com/AppifySheets/Deskbar/pull/490), see https://claude.ai/code/artifact/abc. Not [this](file:///C:/x.exe).");
        t.Controller.Show();

        TextBlock Detail() => Inside(LineOf(t.Window, id)).OfType<TextBlock>().Single(b => Equals(b.Tag, TasksWindow.DetailTag));
        Hyperlink[] Links() => Detail().Inlines.OfType<Hyperlink>().ToArray();

        Assert.Equal(
            ["https://github.com/AppifySheets/Deskbar/pull/490", "https://claude.ai/code/artifact/abc"],
            Links().Select(l => l.NavigateUri.AbsoluteUri));
        Assert.Equal("Merge Deskbar #490, see https://claude.ai/code/artifact/abc. Not [this](file:///C:/x.exe).",
            new TextRange(Detail().ContentStart, Detail().ContentEnd).Text);

        t.Controller.ToggleThread(id);
        Assert.Equal(2, Links().Length);
    });

    [Fact]
    public void A_claude_tasks_status_line_and_his_description_both_show() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.Create("Tasks redesign", t.Gepha, Session("s1")).Value.Id;
        t.Tasks.SetStatusLine(id, "waiting for CI");
        t.Tasks.Describe(id, "Petre's brief");
        t.Controller.Show();
        t.Controller.ToggleThread(id);

        var texts = Inside(LineOf(t.Window, id)).OfType<TextBlock>().Select(b => b.Text).ToList();
        Assert.Contains("waiting for CI", texts);
        Assert.Equal("Petre's brief", DescriptionBlockOf(t.Window, id)!.Text);
    });

    [Fact]
    public void Edit_opens_the_title_and_the_description_together_and_enter_saves_both() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("Pay rent", t.Gepha).Value.Id;
        t.Tasks.Describe(id, "old words");
        t.Controller.Show();

        t.Window.BeginRename(id);
        Assert.Equal("Pay rent", t.Window.Editor!.Text);
        Assert.Equal("old words", t.Window.DescriptionEditor!.Text);
        t.Window.Editor.Text = "Pay the rent";
        t.Window.DescriptionEditor.Text = "new **words**\nsecond line";
        t.Window.SaveEditor();

        var task = t.Tasks.Board.Tasks.Single();
        Assert.Equal("Pay the rent", task.Title);
        Assert.Equal("new **words**\nsecond line", task.Description);
        Assert.Null(t.Window.Editor);
    });

    [Fact]
    public void Emptying_the_description_in_the_editor_clears_it_and_an_empty_title_keeps_the_editor_open() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("Pay rent", t.Gepha).Value.Id;
        t.Tasks.Describe(id, "old words");
        t.Controller.Show();

        t.Window.BeginRename(id);
        t.Window.Editor!.Text = "  ";
        t.Window.DescriptionEditor!.Text = "";
        t.Window.SaveEditor();
        Assert.NotNull(t.Window.Editor);
        Assert.Equal("old words", t.Tasks.Board.Tasks.Single().Description);

        t.Window.Editor!.Text = "Pay rent";
        t.Window.SaveEditor();
        Assert.Equal("", t.Tasks.Board.Tasks.Single().Description);
    });

    // His comment has Edit, a session's does not ("i should only be able to edit my own comments, not claude's").
    [Fact]
    public void Only_his_own_comments_offer_edit() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.Create("Tasks redesign", t.Gepha, Session("s1")).Value.Id;
        t.Tasks.Comment(id, "s1", "Claude's finding");
        t.Tasks.Comment(id, Maybe<string>.None, "my thought");
        // Deskbar's own line (a resume) is Deskbar's, not his: no Edit, and it says so (Petre's screenshot, 2026-11-24).
        t.Tasks.Note(id, "Resumed by Deskbar at 10:05.");
        t.Controller.Show();
        t.Controller.ToggleThread(id);

        var edits = Inside(t.Window).OfType<Button>().Where(b => AutomationProperties.GetName(b) == "Edit comment").ToList();
        Assert.Single(edits);
        Assert.Equal(["Claude", "you", "Deskbar"], t.Window.CommentAuthors(id));
    });

    [Fact]
    public void Editing_a_comment_rewrites_it_in_place_and_escape_keeps_it() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("note", t.Gepha).Value.Id;
        t.Tasks.Comment(id, Maybe<string>.None, "first draft");
        var comment = t.Tasks.Board.Tasks.Single().Comments.Single().Id;
        t.Controller.Show();
        t.Controller.ToggleThread(id);

        Click(Inside(t.Window).OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Edit comment"));
        Assert.Equal("first draft", t.Window.CommentEditor!.Text);
        t.Window.CommentEditor.Text = "second draft";
        t.Window.CancelCommentEdit();
        Assert.Equal("first draft", t.Tasks.Board.Tasks.Single().Comments.Single().Markdown);
        Assert.Null(t.Window.CommentEditor);

        t.Window.BeginEditComment(comment);
        t.Window.CommentEditor!.Text = "second draft";
        t.Window.SaveCommentEditor();
        Assert.Equal("second draft", t.Tasks.Board.Tasks.Single().Comments.Single().Markdown);
        Assert.Null(t.Window.CommentEditor);
    });

    // A session's comment is refused even when asked for by id (the board says so, and the window shows it).
    [Fact]
    public void A_sessions_comment_cannot_be_opened_for_editing() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.Create("Tasks redesign", t.Gepha, Session("s1")).Value.Id;
        t.Tasks.Comment(id, "s1", "Claude's finding");
        var comment = t.Tasks.Board.Tasks.Single().Comments.Single().Id;
        t.Controller.Show();
        t.Controller.ToggleThread(id);

        t.Window.BeginEditComment(comment);

        Assert.Null(t.Window.CommentEditor);
    });

    // --- RULING 59: an important task's title is bold, an urgent one's italic (bar, List and Matrix) -------------------

    static (FontWeight Weight, FontStyle Style) TitleLookOf(TasksWindow window, Guid id)
    {
        var title = Inside(LineOf(window, id)).OfType<TextBlock>().Single(b => Equals(b.Tag, "task-title"));
        return (title.FontWeight, title.FontStyle);
    }

    [Fact]
    public void Important_titles_are_bold_and_urgent_ones_italic_in_the_list() => StaThread.Run(() =>
    {
        using var t = Build();
        var plain = t.Tasks.AddNote("plain", t.Gepha).Value.Id;
        var important = t.Tasks.AddNote("important", t.Gepha, Quadrant.II).Value.Id;
        var urgent = t.Tasks.AddNote("urgent", t.Gepha, Quadrant.III).Value.Id;
        var both = t.Tasks.AddNote("both", t.Gepha, Quadrant.I).Value.Id;
        t.Controller.Show();

        Assert.Equal((FontWeights.Normal, FontStyles.Normal), TitleLookOf(t.Window, plain));
        Assert.Equal((FontWeights.Bold, FontStyles.Normal), TitleLookOf(t.Window, important));
        Assert.Equal((FontWeights.Normal, FontStyles.Italic), TitleLookOf(t.Window, urgent));
        Assert.Equal((FontWeights.Bold, FontStyles.Italic), TitleLookOf(t.Window, both));

        // The toggles stay: flipping one changes the look.
        t.Tasks.SetFlag(plain, TaskFlag.Important, true);
        Assert.Equal(FontWeights.Bold, TitleLookOf(t.Window, plain).Weight);
    });

    [Fact]
    public void The_matrix_titles_follow_the_flags_too() => StaThread.Run(() =>
    {
        using var t = Build();
        var both = t.Tasks.AddNote("both", t.Gepha, Quadrant.I).Value.Id;
        var plain = t.Tasks.AddNote("plain", t.Gepha).Value.Id;
        Assert.True(t.Harness.Manager.SetTasksMatrix(true).IsSuccess);
        t.Controller.Show();

        Assert.Equal((FontWeights.Bold, FontStyles.Italic), TitleLookOf(t.Window, both));
        Assert.Equal((FontWeights.Normal, FontStyles.Normal), TitleLookOf(t.Window, plain));
    });

    // --- RULING 62: settled comments fold away behind one dim line ---------------------------------------------------------

    static Button SettledFold(Board t) =>
        Inside(t.Window).OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Settled comments");

    static string TextOf(Button button) => Inside(button).OfType<TextBlock>().First().Text;

    // A thread whose question was answered: two comments settled (the session's, since the ask), one before it and one of
    // his not.
    static (Guid Task, Guid Older) Answered(Board t)
    {
        var id = t.Tasks.Create("Tasks redesign", t.Gepha, Session("s1")).Value.Id;
        t.Tasks.Comment(id, "s1", "older finding");
        t.Tasks.Ask(id, "Merge?");
        t.Tasks.Comment(id, "s1", "detail of the question");
        t.Tasks.Comment(id, "s1", "follow-up");
        t.Tasks.Comment(id, Maybe<string>.None, "my reply");
        t.Tasks.Answered("s1");
        return (id, t.Tasks.Board.Tasks.Single().Comments[0].Id);
    }

    [Fact]
    public void Settled_comments_are_hidden_behind_a_line_that_expands_them() => StaThread.Run(() =>
    {
        using var t = Build();
        var (id, _) = Answered(t);
        t.Controller.Show();
        t.Controller.ToggleThread(id);

        Assert.Equal(2, t.Window.ShownComments(id).Count); // the older one and his reply
        Assert.Equal("2 settled comments", TextOf(SettledFold(t)));

        Click(SettledFold(t));
        // Four now (RULING 63: only the latest three show; the earlier line unfolds the fourth).
        Assert.Equal(3, t.Window.ShownComments(id).Count);
        Click(ButtonsNamed(t, "Earlier comments").Single());
        Assert.Equal(4, t.Window.ShownComments(id).Count);
        Assert.Equal("Hide 2 settled comments", TextOf(SettledFold(t)));

        Click(SettledFold(t));
        Assert.Equal(2, t.Window.ShownComments(id).Count);
    });

    [Fact]
    public void A_thread_with_nothing_settled_has_no_line() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("note", t.Gepha).Value.Id;
        t.Tasks.Comment(id, Maybe<string>.None, "mine");
        t.Controller.Show();
        t.Controller.ToggleThread(id);

        Assert.DoesNotContain(Inside(t.Window).OfType<Button>(), b => AutomationProperties.GetName(b) == "Settled comments");
    });

    [Fact]
    public void Petre_settles_any_comment_from_its_button_and_unsettles_it() => StaThread.Run(() =>
    {
        using var t = Build();
        var (id, older) = Answered(t);
        t.Controller.Show();
        t.Controller.ToggleThread(id);

        var settle = Inside(t.Window).OfType<Button>().Where(b => AutomationProperties.GetName(b) == "Settle comment").ToList();
        Assert.Equal(2, settle.Count); // the older one and his reply; the settled ones offer Unsettle
        Click(settle[0]);
        Assert.True(t.Tasks.Board.Tasks.Single().Comments.Single(c => c.Id == older).Settled.HasValue);
        Assert.Equal("3 settled comments", TextOf(SettledFold(t)));

        Click(SettledFold(t));
        Click(Inside(t.Window).OfType<Button>().First(b => AutomationProperties.GetName(b) == "Unsettle comment"));
        Assert.Equal(2, t.Tasks.Board.Tasks.Single().Comments.Count(c => c.Settled.HasNoValue));
    });

    // --- RULING 63: a thread folds. Click again to collapse; long comments and old ones fold away ------------------------

    static List<Button> ButtonsNamed(Board t, string name) =>
        Inside(t.Window).OfType<Button>().Where(b => AutomationProperties.GetName(b) == name && b.Visibility == Visibility.Visible).ToList();

    [Fact]
    public void A_click_toggles_the_thread_and_escape_collapses_the_selected_one() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("note", t.Gepha).Value.Id;
        t.Controller.Show();

        Click(LineOf(t.Window, id));
        Assert.True(t.Window.ThreadShown(id));
        Click(LineOf(t.Window, id));
        Assert.False(t.Window.ThreadShown(id));
        Click(LineOf(t.Window, id));
        Assert.True(t.Window.ThreadShown(id));

        PressKey(t.Window, Key.Escape);
        Assert.False(t.Window.ThreadShown(id));
    });

    static string Long(int lines) => string.Join("\n\n", Enumerable.Range(1, lines).Select(i => $"line {i} of a long finding"));

    [Fact]
    public void A_long_comment_shows_about_three_lines_with_show_more_and_show_less() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("note", t.Gepha).Value.Id;
        t.Tasks.Comment(id, Maybe<string>.None, Long(9));
        t.Tasks.Comment(id, Maybe<string>.None, "short");
        t.Controller.Show();
        t.Controller.ToggleThread(id);

        Assert.Single(ButtonsNamed(t, "Show more")); // the short comment has none
        var body = Inside(t.Window).OfType<RichTextBox>().First(b => b.IsReadOnly && b.Tag is null);
        Assert.True(body.MaxHeight < 80, $"clamped to {body.MaxHeight}");

        Click(ButtonsNamed(t, "Show more")[0]);
        Assert.Empty(ButtonsNamed(t, "Show more"));
        Assert.Single(ButtonsNamed(t, "Show less"));
        Assert.True(double.IsPositiveInfinity(Inside(t.Window).OfType<RichTextBox>().First(b => b.IsReadOnly && b.Tag is null).MaxHeight));

        Click(ButtonsNamed(t, "Show less")[0]);
        Assert.Single(ButtonsNamed(t, "Show more"));
    });

    [Fact]
    public void Only_the_latest_three_comments_show_with_a_line_for_the_earlier_ones() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("note", t.Gepha).Value.Id;
        Enumerable.Range(1, 6).ToList().ForEach(i => t.Tasks.Comment(id, Maybe<string>.None, $"comment {i}"));
        var all = t.Tasks.Board.Tasks.Single().Comments.Select(c => c.Id).ToList();
        t.Controller.Show();
        t.Controller.ToggleThread(id);

        Assert.Equal(all.Skip(3), t.Window.ShownComments(id));
        var earlier = ButtonsNamed(t, "Earlier comments").Single();
        Assert.Equal("3 earlier comments", TextOf(earlier));

        Click(earlier);
        Assert.Equal(all, t.Window.ShownComments(id));
        Assert.Equal("Hide 3 earlier comments", TextOf(ButtonsNamed(t, "Earlier comments").Single()));
    });

    [Fact]
    public void Three_or_fewer_comments_have_no_earlier_line() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("note", t.Gepha).Value.Id;
        Enumerable.Range(1, 3).ToList().ForEach(i => t.Tasks.Comment(id, Maybe<string>.None, $"comment {i}"));
        t.Controller.Show();
        t.Controller.ToggleThread(id);

        Assert.Empty(ButtonsNamed(t, "Earlier comments"));
        Assert.Equal(3, t.Window.ShownComments(id).Count);
    });

    // Settled comments stay folded separately: they do not count toward the three.
    [Fact]
    public void Settled_comments_do_not_count_toward_the_three() => StaThread.Run(() =>
    {
        using var t = Build();
        var (id, _) = Answered(t);
        t.Controller.Show();
        t.Controller.ToggleThread(id);

        Assert.Empty(ButtonsNamed(t, "Earlier comments"));
        Assert.Single(ButtonsNamed(t, "Settled comments"));
    });

    [Fact]
    public void The_fold_state_survives_a_redraw_and_a_new_comment() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("note", t.Gepha).Value.Id;
        t.Tasks.Comment(id, Maybe<string>.None, Long(9));
        Enumerable.Range(1, 5).ToList().ForEach(i => t.Tasks.Comment(id, Maybe<string>.None, $"comment {i}"));
        t.Controller.Show();
        t.Controller.ToggleThread(id);
        Click(ButtonsNamed(t, "Earlier comments").Single());
        Click(ButtonsNamed(t, "Show more")[0]);

        t.Tasks.Comment(id, Maybe<string>.None, "a newer one");
        t.Tasks.AddNote("another task", t.Gepha);

        Assert.Equal("Hide 4 earlier comments", TextOf(ButtonsNamed(t, "Earlier comments").Single()));
        Assert.Single(ButtonsNamed(t, "Show less"));
        Assert.True(t.Window.ThreadShown(id));
    });

    // --- review fixes ----------------------------------------------------------------------------------------------------

    // Edit with nothing changed renames nothing and saves nothing: no pulse.
    [Fact]
    public void Edit_with_nothing_changed_changes_nothing() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("Pay rent", t.Gepha).Value.Id;
        t.Tasks.Describe(id, "words");
        t.Controller.Show();
        var pulses = 0;
        using var _ = t.Tasks.Changed.Subscribe(_ => pulses++);
        var before = t.Tasks.Board;

        Assert.True(t.Controller.Edit(id, "  Pay rent  ", "words").IsSuccess);

        Assert.Equal(0, pulses);
        Assert.Same(before, t.Tasks.Board);
        // A real change still lands, one pulse per field changed.
        Assert.True(t.Controller.Edit(id, "Pay rent", "new words").IsSuccess);
        Assert.Equal("new words", t.Tasks.Board.Tasks.Single().Description);
    });

    // Esc folds the selected thread, but not while a comment box has the keyboard: there it belongs to the box.
    [Fact]
    public void Escape_leaves_the_thread_alone_while_a_comment_box_has_the_keyboard() => StaThread.Run(() =>
    {
        var selection = new TaskSelection();
        using var t = Build(selection: selection);
        var id = t.Tasks.AddNote("note", t.Gepha).Value.Id;
        t.Controller.Show();
        t.Controller.Pick(id);
        Assert.True(t.Window.ThreadShown(id));
        t.Window.EscapeIgnoresThreadWhile = () => true; // a comment box with the keyboard

        PressKey(t.Window, Key.Escape);
        Assert.True(t.Window.ThreadShown(id));

        t.Window.EscapeIgnoresThreadWhile = null;
        PressKey(t.Window, Key.Escape);
        Assert.False(t.Window.ThreadShown(id));
    });

    // A re-layout of a thread (an earlier-comments line unfolded, a long comment opened) starts the half second in which a
    // click is not trusted, as any change of the lines does.
    [Fact]
    public void Unfolding_part_of_a_thread_starts_the_click_guard() => StaThread.Run(() =>
    {
        var clock = ClickStart;
        using var t = Build(clickClock: () => clock);
        var id = t.Tasks.AddNote("note", t.Gepha).Value.Id;
        var other = t.Tasks.AddNote("other", t.Gepha).Value.Id;
        Enumerable.Range(1, 5).ToList().ForEach(i => t.Tasks.Comment(id, Maybe<string>.None, $"comment {i}"));
        t.Controller.Show();
        clock = clock.AddSeconds(2);
        t.Controller.ToggleThread(id);
        clock = clock.AddSeconds(2);
        Click(ButtonsNamed(t, "Earlier comments").Single());

        // At once: the lines have just changed, so this click on another line is not trusted.
        Click(LineOf(t.Window, other));
        Assert.False(t.Window.ThreadShown(other));

        clock = clock.AddSeconds(1);
        Click(LineOf(t.Window, other));
        Assert.True(t.Window.ThreadShown(other));
    });

    // A long comment is judged by its real width once the thread is laid out: a single paragraph that the heuristic calls long
    // but that fits three lines at the window's width has no Show more.
    [Fact]
    public void Show_more_follows_the_laid_out_height() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("note", t.Gepha).Value.Id;
        // Wide glyphs: 290 characters, three lines by the character count but many more once laid out.
        t.Tasks.Comment(id, Maybe<string>.None, string.Join(" ", Enumerable.Repeat("WWWWWWWWW", 29)));
        // Narrow glyphs: 350 characters, four lines by the count but two once laid out.
        t.Tasks.Comment(id, Maybe<string>.None, string.Join(" ", Enumerable.Repeat("iiiiiiiii", 35)));
        t.Tasks.Comment(id, Maybe<string>.None, Long(9));
        t.Controller.Show();
        t.Controller.ToggleThread(id);
        static List<bool> Shown(Board t) => Inside(t.Window).OfType<Button>()
            .Where(b => AutomationProperties.GetName(b) == "Show more").Select(b => b.Visibility == Visibility.Visible).ToList();
        Assert.Equal([false, true, true], Shown(t)); // before layout, the character count decides

        t.Window.UpdateLayout();

        Assert.Equal([true, false, true], Shown(t)); // after it, the laid-out height does
    });

    // RULING 64 (Petre: "same here, make more compact", of the window's List after the bar's line gap went from 8 to 3
    // DIP): a line is as tall as its text and a few DIP, not 30. The small buttons (tick, flags, comments, "...") used
    // to be taller than the title and set the line's height, so the line is measured, not just the text: a one-line task
    // is at most its title's height + 9, a two-line task at most its two text lines + 9, in the List and in the Matrix
    // (which shares the line), and the tick still hit-tests at its size. The bound was + 6 until each line became a tile
    // (option E of the task-separation mockup, 2026-11-25): a tile has 3 DIP of its own fill above and below the text
    // (was 1.5), so the bound grew by those 3 DIP and no more. Measured then: one-line 22.3 for a 15.96 title, two-line
    // 36.59 for 30.59 of text.
    [Fact]
    public void A_line_is_as_tall_as_its_text_and_a_few_dip() => StaThread.Run(() =>
    {
        using var t = Build();
        var one = t.Tasks.AddNote("get a girl", t.Gepha).Value.Id;
        var two = t.Tasks.Create("Tighter spacing", t.Gepha, Session("s1")).Value.Id;
        t.Tasks.SetStatusLine(two, "PR green, ready to merge");
        t.Controller.Show();
        t.Window.Width = TasksPanelLayout.SlimBelow;
        t.Window.UpdateLayout();

        void Check(string view)
        {
            t.Window.UpdateLayout();
            var oneTitle = TitleOf(t.Window, one).ActualHeight;
            var oneLine = LineOf(t.Window, one).ActualHeight;
            // The title's row and, since task d560f3eb, the status line in the row under it, across the line.
            var twoTexts = ((FrameworkElement)TitleOf(t.Window, two).Parent).ActualHeight
                + Inside(LineOf(t.Window, two)).OfType<TextBlock>().Single(b => Equals(b.Tag, TasksWindow.DetailTag)).ActualHeight;
            var twoLine = LineOf(t.Window, two).ActualHeight;
            Console.WriteLine($"MEASURED {view}: one-line {oneLine} (title {oneTitle}), two-line {twoLine} (texts {twoTexts})");
            Assert.True(oneLine <= oneTitle + 9, $"{view}: a one-line task is {oneLine} high, its title {oneTitle}");
            Assert.True(twoLine <= twoTexts + 9, $"{view}: a two-line task is {twoLine} high, its texts {twoTexts}");
        }
        Check("list");
        // The tick box keeps a click target of its own size or more.
        var tick = ButtonNamed(LineOf(t.Window, one), "Close");
        Assert.True(tick.ActualHeight >= 11 && tick.ActualWidth >= 11, $"tick {tick.ActualWidth}x{tick.ActualHeight}");
        Click(ButtonNamed(t.Window, "Matrix"));
        Check("matrix");
    });

    // --- Task 9: a task's Claude sessions --------------------------------------------------------------------------

    // Task 7f61af2e (Petre: "make it possible to open claude without opening the task first"): a Claude task's menu has
    // "Open Claude" right after Edit, going where the double-click goes (goTo) once the menu has closed, without picking
    // the task or opening its thread. A note's menu has no such item.
    [Fact]
    public void Open_Claude_on_the_window_menu_goes_without_opening_the_thread() => StaThread.Run(() =>
    {
        using var t = Build(startClaude: _ => { });
        var id = t.Tasks.Create("Tasks redesign", Maybe<Guid>.None, Session("s1")).Value.Id;
        var note = t.Tasks.AddNote("Dishwasher tablets", t.Gepha).Value.Id;
        t.Controller.Show();

        var menu = t.Window.OpenMenuFor(id, atPointer: true);
        var items = menu.Items.OfType<MenuItem>().ToList();
        // "Open in window" (task dca4ebeb) comes first after Edit, then Open Claude.
        Assert.Equal(["Edit", "Open in window", "Open Claude", "Start Claude on this"], items.Take(4).Select(i => ((TextBlock)i.Header).Text));
        items[2].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.IsOpen = false;
        // Run once the menu has closed (an Input-priority dispatch): a lower priority runs after it.
        t.Window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert.Equal([id], t.WentTo);
        Assert.False(t.Window.ThreadShown(id));

        var noteMenu = t.Window.OpenMenuFor(note);
        Assert.DoesNotContain(noteMenu.Items.OfType<MenuItem>(), i => i.Header is TextBlock { Text: "Open Claude" });
        noteMenu.IsOpen = false;
    });

    // Task 7f61af2e (Petre: "add a button on the task in the tasklist for it too"): a Claude task's line, full and slim,
    // has an Open Claude button that goes where the double-click goes, without the line's own click (the task is not
    // expanded). A note has none, a closed Claude task has none (its line has Reopen there), and on a slim board of notes
    // the button's column takes no width.
    [Theory]
    [InlineData(Wide)]
    [InlineData(TasksPanelLayout.SlimBelow - 1)]
    public void A_claude_line_has_an_Open_Claude_button(double width) => StaThread.Run(() =>
    {
        using var t = Build(width: width);
        var id = t.Tasks.Create("Tasks redesign", t.Gepha, Session("s1")).Value.Id;
        var note = t.Tasks.AddNote("Dishwasher tablets", t.Gepha).Value.Id;
        var closed = t.Tasks.Create("Shipped", t.Gepha, Session("s2")).Value.Id;
        t.Tasks.MarkDone(closed);
        Assert.True(t.Tasks.Close(closed).IsSuccess);
        t.Controller.Show();
        t.Window.UpdateLayout();

        Click(ButtonNamed(LineOf(t.Window, id), "Open Claude"));
        Assert.Equal([id], t.WentTo);
        Assert.False(t.Window.ThreadShown(id));
        Assert.Equal("Open Claude: go to its session without opening the task", ButtonNamed(LineOf(t.Window, id), "Open Claude").ToolTip);
        Assert.DoesNotContain(Inside(LineOf(t.Window, note)).OfType<Button>(), b => AutomationProperties.GetName(b) == "Open Claude");

        t.Controller.Filter(TaskFilter.Closed);
        Assert.DoesNotContain(Inside(LineOf(t.Window, closed)).OfType<Button>(), b => AutomationProperties.GetName(b) == "Open Claude");
    });

    [Fact]
    public void A_slim_board_of_notes_gives_the_Open_Claude_column_no_width() => StaThread.Run(() =>
    {
        using var t = Build(width: TasksPanelLayout.SlimBelow - 1);
        var note = t.Tasks.AddNote("Dishwasher tablets", t.Gepha).Value.Id;
        t.Controller.Show();
        t.Window.UpdateLayout();

        var grid = Inside(LineOf(t.Window, note)).OfType<Grid>().First(g => g.ColumnDefinitions.Count > 4);
        Assert.Equal(0, grid.ColumnDefinitions[9].Width.Value);
    });

    // Spec, "Where the clicks are": "Start Claude on this" on the line's menu right after Edit, and as a button in the
    // task's thread. Both reach the App's ClaudeTaskActions.Offer through `startClaude`, the menu's once the menu closed.
    [Fact]
    public void Start_Claude_on_this_is_on_the_window_menu_after_Edit_and_in_the_thread() => StaThread.Run(() =>
    {
        var started = new List<Guid>();
        using var t = Build(startClaude: started.Add);
        var id = t.Tasks.AddNote("Write the docs", t.Gepha).Value.Id;
        t.Controller.Show();

        var menu = t.Window.OpenMenuFor(id);
        var items = menu.Items.OfType<MenuItem>().ToList();
        // "Open in window" (task dca4ebeb) sits between them, right after Edit.
        Assert.Equal(["Edit", "Open in window", "Start Claude on this"], items.Take(3).Select(i => ((TextBlock)i.Header).Text));
        items[2].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.IsOpen = false;
        // Run once the menu has closed (an Input-priority dispatch): a lower priority runs after it.
        t.Window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert.Equal([id], started);

        t.Controller.ToggleThread(id);
        Click(ButtonNamed(t.Window, "Start Claude on this"));
        Assert.Equal([id, id], started);

        // Without the seam (a copy without Claude sessions) neither shows.
        using var plain = Build();
        var other = plain.Tasks.AddNote("Errand", plain.Gepha).Value.Id;
        plain.Controller.Show();
        plain.Controller.ToggleThread(other);
        Assert.DoesNotContain(plain.Window.OpenMenuFor(other).Items.OfType<MenuItem>(), i => i.Header is TextBlock { Text: "Start Claude on this" });
        Assert.DoesNotContain(Inside(plain.Window).OfType<Button>(), b => AutomationProperties.GetName(b) == "Start Claude on this");
    });

    // Spec, "Task history": the thread lists the task's sessions, newest first, each with when it joined and whether it
    // is working, idle, ended or expired, and an Open that does what going to that session does; an expired one (its
    // transcript gone) has no Open. A new list from `claude agents` redraws an open thread.
    [Fact]
    public void The_thread_lists_sessions_newest_first_with_Open_except_expired() => StaThread.Run(() =>
    {
        var opened = new List<(Guid Task, string Session)>();
        var states = new Dictionary<string, SessionState> { ["aaaaaaaa-1"] = SessionState.Expired, ["bbbbbbbb-2"] = SessionState.Ended, ["cccccccc-3"] = SessionState.Working };
        var changed = new System.Reactive.Subjects.Subject<System.Reactive.Unit>();
        using var t = Build(
            sessionsOf: task => TaskViews.SessionList(task, Maybe<IReadOnlyCollection<ClaudeAgent>>.None, _ => true)
                .Select(v => v with { State = states[v.Id] }).ToList(),
            openSession: (task, session) =>
            {
                opened.Add((task.Id, session));
                return Result.Success();
            },
            sessionsChanged: changed);
        var id = t.Tasks.Create("Fix the bar", t.Gepha, new TaskSession("aaaaaaaa-1", @"C:\repos\Deskbar", Start, Maybe<DateTimeOffset>.None)).Value.Id;
        t.Tasks.Join(id, new TaskSession("bbbbbbbb-2", @"C:\repos\Deskbar", Start.AddMinutes(5), Maybe<DateTimeOffset>.None));
        t.Tasks.Join(id, new TaskSession("cccccccc-3", @"C:\repos\Deskbar", Start.AddMinutes(10), Maybe<DateTimeOffset>.None));
        t.Controller.Show();
        t.Controller.ToggleThread(id);

        var lines = Inside(t.Window).OfType<DockPanel>().Where(d => "session-line".Equals(d.Tag)).ToList();
        Assert.Equal(["cccccccc", "bbbbbbbb", "aaaaaaaa"], lines.Select(l => Inside(l).OfType<TextBlock>().Select(b => b.Text).First(x => x.StartsWith("Session ", StringComparison.Ordinal))[8..16]));
        Assert.Contains(Inside(lines[0]).OfType<TextBlock>(), b => b.Text.EndsWith("· working", StringComparison.Ordinal));
        Assert.Contains(Inside(lines[1]).OfType<TextBlock>(), b => b.Text.EndsWith("· ended", StringComparison.Ordinal));
        Assert.Contains(Inside(lines[2]).OfType<TextBlock>(), b => b.Text.EndsWith("· expired", StringComparison.Ordinal));
        Assert.True(HasButton(lines[0], "Open session cccccccc"));
        Assert.True(HasButton(lines[1], "Open session bbbbbbbb"));
        Assert.False(HasButton(lines[2], "Open session aaaaaaaa"));

        Click(ButtonNamed(lines[1], "Open session bbbbbbbb"));
        Assert.Equal([(id, "bbbbbbbb-2")], opened);

        // The working session has stopped: a new list redraws the thread with it ended.
        states["cccccccc-3"] = SessionState.Ended;
        changed.OnNext(System.Reactive.Unit.Default);
        var redrawn = Inside(t.Window).OfType<DockPanel>().First(d => "session-line".Equals(d.Tag));
        Assert.Contains(Inside(redrawn).OfType<TextBlock>(), b => b.Text.EndsWith("· ended", StringComparison.Ordinal));
    });

    static bool HasButton(object scope, string name) => Inside(scope).OfType<Button>().Any(b => AutomationProperties.GetName(b) == name);

    // --- Tasks panel plan, Task 8: lines at the panel's width, the Matrix from 780, the working halo ------------------
    // (spec docs/superpowers/specs/2026-11-23-tasks-panel-design.md, "Lines at panel width" and "The Matrix")

    // A line's session squares, in order, by what each says: "session-live", "session-ended" or "session-working".
    static IReadOnlyList<string> SquaresOf(Button line) =>
        Inside(line).OfType<Border>().Select(b => b.Tag as string).OfType<string>().Where(tag => tag.StartsWith("session-", StringComparison.Ordinal)).ToArray();

    // A line's tooltip as text: a slim line's is a TextBlock (its age is rewritten in place each minute), a full line's a string.
    static string TipOf(Button line) => line.ToolTip is TextBlock text ? text.Text : $"{line.ToolTip}";

    static IReadOnlyList<string> MenuWords(ContextMenu menu) =>
        menu.Items.OfType<MenuItem>().Select(i => i.Header is TextBlock text ? text.Text : $"{i.Header}").ToArray();

    // Below 710 DIP a line is slim: no session words, no assignee and no Reopen button on it; its sessions are squares
    // only, and the assignee moves into its tooltip with the age. The age itself stays on the slim line since it left the
    // bar (Petre, 2026-11-24: "move the time from here to the proper tasks window"), whole, never trimmed.
    [Fact]
    public void A_panel_narrower_than_710_draws_slim_lines() => StaThread.Run(() =>
    {
        using var t = Build(width: TasksPanelLayout.SlimBelow - 1);
        var id = t.Tasks.Create("work", t.Gepha, Session("s1")).Value.Id;
        t.Tasks.Join(id, Session("s2", live: false));
        var closed = t.Tasks.AddNote("old errand", t.Gepha).Value.Id;
        t.Tasks.Close(closed);
        t.Controller.Show();

        var line = LineOf(t.Window, id);
        Assert.DoesNotContain(Inside(line).OfType<TextBlock>(), b => b.Text.Contains("session", StringComparison.Ordinal));
        Assert.DoesNotContain(Inside(line).OfType<TextBlock>(), b => Equals(b.Tag, "task-assignee"));
        var task0 = t.Tasks.Board.Tasks.Single(x => x.Id == id);
        Assert.Equal(TaskAge.Format(Start.AddHours(1) - task0.Created), t.Window.AgeText(id));
        t.Window.UpdateLayout();
        var age = Inside(line).OfType<TextBlock>().Single(b => Equals(b.Tag, "task-age"));
        Assert.True(age.IsVisible, "the slim line shows its age");
        Assert.True(age.ActualWidth >= age.DesiredSize.Width - age.Margin.Left - age.Margin.Right - 0.5, $"'{age.Text}' is trimmed to {age.ActualWidth}");
        Assert.Equal(["session-live", "session-ended"], SquaresOf(line));
        var task = t.Tasks.Board.Tasks.Single(x => x.Id == id);
        Assert.Contains($"{TaskAge.Format(Start.AddHours(1) - task.Created)} · {task.Assignee.Name}", TipOf(line));

        t.Controller.Filter(TaskFilter.Closed);
        Assert.False(HasButton(LineOf(t.Window, closed), "Reopen"));
    });

    // Task 65ab598d: a slim line's title spans the whole line, where it once had a column of its own beside about 300 DIP
    // of fixed columns, about 15 DIP at the panel's narrowest, and broke one letter per line. Task 50667e10 (Petre: "give
    // buttons and status and all its own line and align on the left"): the controls' strip no longer sits on the title's
    // last line but always on a row of its own under the title, starting at the title's left edge, and a waiting task's
    // question comes under the strip. At the narrowest and the default width, for a one-word title and a long one.
    [Theory]
    [InlineData(TasksPanelLayout.MinWidth)]
    [InlineData(TasksPanelLayout.DefaultWidth)]
    public void A_slim_lines_title_spans_the_line_and_the_controls_have_their_own_row_at_its_left(double width) => StaThread.Run(() =>
    {
        using var t = Build(width: width);
        var shortOne = t.Tasks.Create("Renew", t.Gepha, Session("s1")).Value.Id;
        var longOne = t.Tasks.Create("Tasks panel at its narrowest: the slim line's fixed columns leave the title about 15 DIP, so it wraps one letter per line",
            t.Gepha, Session("s2")).Value.Id;
        t.Tasks.Ask(shortOne, "Pick how the profile shows lines nobody confirmed");
        t.Controller.Show();
        t.Window.UpdateLayout();

        (TextBlock Title, Grid Strip, Button Line) Parts(Guid id)
        {
            var line = LineOf(t.Window, id);
            return (Inside(line).OfType<TextBlock>().Single(b => Equals(b.Tag, "task-title")),
                Inside(line).OfType<Grid>().Single(g => Equals(g.Tag, TasksWindow.ControlsTag)), line);
        }
        Rect Bounds(FrameworkElement part, UIElement to) =>
            new(part.TranslatePoint(new Point(0, 0), to), new Size(part.ActualWidth, part.ActualHeight));

        var s = Parts(shortOne);
        var l = Parts(longOne);
        var sTitle = Bounds(s.Title, s.Line);
        var sStrip = Bounds(s.Strip, s.Line);
        var lTitle = Bounds(l.Title, l.Line);
        var lStrip = Bounds(l.Strip, l.Line);

        // The titles: the short one a single row, the long one nearly the line's whole width (the tick's 22 DIP and the
        // margins aside) over several rows.
        Assert.True(s.Title.ActualHeight < 20, $"at {width} the short title is {s.Title.ActualHeight} DIP tall");
        Assert.True(l.Title.ActualWidth > l.Line.ActualWidth - 60, $"at {width} the title is {l.Title.ActualWidth} DIP wide in a {l.Line.ActualWidth} DIP line");
        Assert.True(l.Title.ActualHeight > 20, "the long title wraps");

        // Each strip under its title, never beside it, starting where the title starts, and inside the line.
        Assert.True(sStrip.Top >= sTitle.Bottom - 0.5, $"at {width} the strip {sStrip} is not under the short title {sTitle}");
        Assert.True(lStrip.Top >= lTitle.Bottom - 0.5, $"at {width} the strip {lStrip} is not under the long title {lTitle}");
        Assert.InRange(sStrip.Left, sTitle.Left - 0.5, sTitle.Left + 4);
        Assert.InRange(lStrip.Left, lTitle.Left - 0.5, lTitle.Left + 4);
        Assert.True(lStrip.Right <= l.Line.ActualWidth + 0.5, $"at {width} the strip ends at {lStrip.Right} in a {l.Line.ActualWidth} DIP line");

        // The waiting task's question comes after the strip.
        var question = Inside(s.Line).OfType<TextBlock>().First(b => b.Text.Contains("nobody confirmed", StringComparison.Ordinal));
        Assert.True(Bounds(question, s.Line).Top >= sStrip.Bottom - 0.5, $"at {width} the question is not under the strip {sStrip}");
    });

    // The widest age TaskAge writes ("27d23h") fits the slim line's age column whole at the panel's narrowest width, and
    // the age's tooltip says it in words with the date, as the bar's did, so it is not misread as a timer (Ruling 46).
    [Fact]
    public void The_slim_lines_age_fits_at_its_widest_and_says_it_in_words() => StaThread.Run(() =>
    {
        var now = Start.AddDays(27).AddHours(23).AddMinutes(30);
        using var t = Build(width: TasksPanelLayout.MinWidth, now: () => now);
        var id = t.Tasks.AddNote("an old note with a title long enough to need trimming in a narrow panel", t.Gepha).Value.Id;
        t.Controller.Show();
        t.Window.UpdateLayout();

        var age = Inside(LineOf(t.Window, id)).OfType<TextBlock>().Single(b => Equals(b.Tag, "task-age"));
        Assert.Equal("27d23h", age.Text);
        Assert.True(age.ActualWidth >= age.DesiredSize.Width - age.Margin.Left - age.Margin.Right - 0.5, $"'{age.Text}' is trimmed to {age.ActualWidth}");
        Assert.StartsWith("Added 27 days 23 hours ago\n", (string)age.ToolTip);
        Assert.Contains($"{Start.ToLocalTime():dddd d MMMM yyyy}", (string)age.ToolTip);

        // A minute later the tick rewrites the text and the words in place.
        now = Start.AddDays(28);
        t.Window.RefreshAges();
        Assert.Equal("4w", age.Text);
        Assert.StartsWith("Added 4 weeks ago\n", (string)age.ToolTip);
    });

    // From 710 DIP the line is today's, and the switch between the two happens when the grip's drag ends (ResizePanel),
    // redrawing every line once.
    [Fact]
    public void A_panel_710_or_wider_draws_todays_lines() => StaThread.Run(() =>
    {
        using var t = Build(width: TasksPanelLayout.SlimBelow);
        var id = t.Tasks.Create("work", t.Gepha, Session("s1")).Value.Id;
        t.Controller.Show();

        var line = LineOf(t.Window, id);
        Assert.Contains(Inside(line).OfType<TextBlock>(), b => b.Text == "1 session");
        Assert.Contains(Inside(line).OfType<TextBlock>(), b => Equals(b.Tag, "task-assignee"));
        Assert.NotEqual("", t.Window.AgeText(id));

        var rebuilds = t.Window.Rebuilds;
        t.Window.Actions!.ResizePanel(TasksPanelLayout.DefaultWidth);
        Assert.Equal(rebuilds + 1, t.Window.Rebuilds);
        Assert.DoesNotContain(Inside(LineOf(t.Window, id)).OfType<TextBlock>(), b => b.Text == "1 session");

        t.Window.Actions!.ResizePanel(TasksPanelLayout.SlimBelow);
        Assert.Contains(Inside(LineOf(t.Window, id)).OfType<TextBlock>(), b => b.Text == "1 session");
    });

    // The status at both widths (main's status marks met the panel in the rebase onto 7df6328): a full line has the
    // status chip; a slim line has no chip column, names the status in its tooltip beside the age and the assignee, and
    // a Done line keeps the green tick in front of its title at either width.
    [Fact]
    public void A_slim_line_names_its_status_in_the_tooltip_and_keeps_the_done_tick() => StaThread.Run(() =>
    {
        using var t = Build(width: TasksPanelLayout.SlimBelow - 1);
        var done = t.Tasks.Create("finished", t.Gepha, Session("s1")).Value.Id;
        t.Tasks.MarkDone(done);
        var working = t.Tasks.Create("working", t.Gepha, Session("s2")).Value.Id;
        t.Controller.Show();

        var line = LineOf(t.Window, done);
        Assert.Null(StatusChipOf(line));
        Assert.True(HasDoneTick(line));
        var task = t.Tasks.Board.Tasks.Single(x => x.Id == done);
        Assert.StartsWith("finished\n", TipOf(line), StringComparison.Ordinal);
        Assert.Contains($"Done · {TaskAge.Format(Start.AddHours(1) - task.Created)} · {task.Assignee.Name}", TipOf(line));
        Assert.Null(StatusChipOf(LineOf(t.Window, working)));
        Assert.False(HasDoneTick(LineOf(t.Window, working)));

        t.Window.Actions!.ResizePanel(TasksPanelLayout.SlimBelow);
        Assert.Equal("Done", StatusWordOf(LineOf(t.Window, done)));
        Assert.True(HasDoneTick(LineOf(t.Window, done)));
        Assert.Equal("In progress", StatusWordOf(LineOf(t.Window, working)));
    });

    // The List / Matrix switch shows only from 780 DIP. Narrower, the List shows and the switch hides, but the setting
    // (AppState.TasksMatrix) is left as it is, so widening the panel again brings the Matrix back.
    [Fact]
    public void The_Matrix_switch_shows_only_from_780_and_the_setting_survives_a_narrow_panel() => StaThread.Run(() =>
    {
        using var t = Build(width: TasksPanelLayout.MatrixFrom);
        t.Tasks.AddNote("a", t.Gepha);
        t.Controller.Show();
        Assert.Equal(Visibility.Visible, ButtonNamed(t.Window, "Matrix").Visibility);
        Click(ButtonNamed(t.Window, "Matrix"));
        Assert.True(t.Window.MatrixShown);

        t.Window.Actions!.ResizePanel(TasksPanelLayout.MatrixFrom - 1);
        Assert.False(t.Window.MatrixShown);
        Assert.Equal(Visibility.Collapsed, ButtonNamed(t.Window, "Matrix").Visibility);
        Assert.Equal(Visibility.Collapsed, ButtonNamed(t.Window, "List").Visibility);
        Assert.True(t.Harness.Manager.State.TasksMatrix);

        // Reopened narrow, still the List.
        t.Controller.Hide();
        t.Controller.Show();
        Assert.False(t.Window.MatrixShown);

        t.Window.Actions!.ResizePanel(TasksPanelLayout.MatrixFrom);
        Assert.True(t.Window.MatrixShown);
        Assert.Equal(Visibility.Visible, ButtonNamed(t.Window, "Matrix").Visibility);
    });

    // A session Claude is working in right now (`workingOf`: the task's live sessions the latest `claude agents --json`
    // lists busy) wears a 2 DIP halo round its live blue square. A new list redraws the line even when only that count
    // changed.
    [Fact]
    public void A_session_that_is_working_draws_a_halo() => StaThread.Run(() =>
    {
        var working = 0;
        var changed = new System.Reactive.Subjects.Subject<System.Reactive.Unit>();
        using var t = Build(width: TasksPanelLayout.DefaultWidth, sessionsChanged: changed, workingOf: _ => working);
        var id = t.Tasks.Create("work", t.Gepha, Session("s1")).Value.Id;
        t.Tasks.Join(id, Session("s2"));
        t.Controller.Show();
        Assert.Equal(["session-live", "session-live"], SquaresOf(LineOf(t.Window, id)));

        working = 1;
        changed.OnNext(System.Reactive.Unit.Default);

        var line = LineOf(t.Window, id);
        Assert.Equal(["session-working", "session-live"], SquaresOf(line));
        var halo = Inside(line).OfType<Border>().First(b => Equals(b.Tag, "session-working"));
        Assert.Equal(new Thickness(2), halo.Padding);
        var square = Assert.IsType<Border>(halo.Child);
        Assert.Equal(8, square.Width);
    });

    // TASK 8 REVIEW, IMPORTANT 1: the lines and the switch follow the width the panel DOCKS at (TasksPanel.DockedWidth),
    // which is the saved width clamped to half the screen it docks on, not the saved width itself. A saved 700 on a
    // screen whose half is 546 DIP is a 546 DIP panel, and its lines are slim.
    [Fact]
    public void A_panel_clamped_below_710_by_its_screen_draws_slim_lines() => StaThread.Run(() =>
    {
        using var t = Build(width: 700, screenDips: 1092);
        var id = t.Tasks.Create("work", t.Gepha, Session("s1")).Value.Id;
        t.Controller.Show();

        var line = LineOf(t.Window, id);
        Assert.DoesNotContain(Inside(line).OfType<TextBlock>(), b => b.Text == "1 session");
        Assert.Equal(["session-live"], SquaresOf(line));
        // The saved width is the user's and stays whole, for a wider screen later.
        Assert.Equal(700, t.Harness.Manager.State.TasksPanel!.Width);
    });

    // A saved 800 on a screen whose half is 640 DIP: a 640 DIP panel, too narrow for the Matrix, so the switch hides and
    // the List shows, with the setting left on.
    [Fact]
    public void A_panel_clamped_below_780_by_its_screen_hides_the_Matrix() => StaThread.Run(() =>
    {
        using var t = Build(width: 800, screenDips: 1280);
        t.Tasks.AddNote("a", t.Gepha);
        Assert.True(t.Harness.Manager.SetTasksMatrix(true).IsSuccess);
        t.Controller.Show();

        Assert.False(t.Window.MatrixShown);
        Assert.Equal(Visibility.Collapsed, ButtonNamed(t.Window, "Matrix").Visibility);
        Assert.True(t.Harness.Manager.State.TasksMatrix);
    });

    // The bar moving to a wider screen re-docks the panel there at its whole saved width, and the full lines come back,
    // every line redrawn once.
    [Fact]
    public void A_redock_on_a_wider_screen_brings_the_full_lines_back() => StaThread.Run(() =>
    {
        // 750: above the full line's floor (TasksPanelLayout.SlimBelow, 710 since the snooze moon) and over half the
        // 1092 DIP screen, so it docks slim there and full on the wider screen.
        using var t = Build(width: 750, screenDips: 1092);
        var id = t.Tasks.Create("work", t.Gepha, Session("s1")).Value.Id;
        t.Controller.Show();
        Assert.DoesNotContain(Inside(LineOf(t.Window, id)).OfType<TextBlock>(), b => b.Text == "1 session");
        var rebuilds = t.Window.Rebuilds;

        t.Panel.Docked();

        Assert.Equal(rebuilds + 1, t.Window.Rebuilds);
        Assert.Contains(Inside(LineOf(t.Window, id)).OfType<TextBlock>(), b => b.Text == "1 session");
    });

    // Close and Reopen leave a slim line (no column for them) but stay in its "⋯" menu, which already offered both.
    [Fact]
    public void A_slim_line_keeps_close_and_reopen_in_its_menu() => StaThread.Run(() =>
    {
        using var t = Build(width: TasksPanelLayout.DefaultWidth);
        var id = t.Tasks.AddNote("note", t.Gepha).Value.Id;
        t.Controller.Show();

        var menu = t.Window.OpenMenuFor(id);
        Assert.Contains("Close", MenuWords(menu));
        menu.IsOpen = false;
        t.Window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

        t.Tasks.Close(id);
        t.Controller.Filter(TaskFilter.Closed);
        Assert.False(HasButton(LineOf(t.Window, id), "Reopen"));
        menu = t.Window.OpenMenuFor(id);
        Assert.Contains("Reopen", MenuWords(menu));
        menu.IsOpen = false;
    });

    // --- images in tasks (docs/superpowers/specs/2026-11-24-task-images-design.md) ---------------------------------------

    static string ScratchRoot() => Directory.CreateTempSubdirectory("deskbar-board-images-").FullName;

    static System.Windows.Media.Imaging.BitmapSource Picture(int width = 40, int height = 30)
    {
        var pixels = Enumerable.Repeat((byte)0x80, width * height * 4).ToArray();
        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    // One picture saved for the task in the scratch state folder; the relative path its markdown line points at.
    static string Pictured(string root, Guid task) =>
        TaskImageFiles.Save(root, task, [TaskImageFiles.FromBitmap(Picture()).Value], Start).Value.Single();

    static IEnumerable<string> RunTexts(object scope) => Inside(scope).OfType<System.Windows.Documents.Run>().Select(r => r.Text);

    static Button? PictureOf(object scope) => Inside(scope).OfType<Button>().FirstOrDefault(b => Equals(b.Tag, TaskImageRow.ImageTag));

    // A comment's picture shows as a thumbnail after its text, outside the clipped body (so "Show more" folds the text and
    // never cuts a picture), and its markdown line is not shown as text; a click opens the file itself.
    [Fact]
    public void A_comments_picture_shows_as_a_thumbnail_that_opens_the_file() => StaThread.Run(() =>
    {
        var root = ScratchRoot();
        var opened = new List<string>();
        try
        {
            using var t = Build(stateDir: root, openImage: opened.Add);
            var id = t.Tasks.AddNote("Bar looks wrong", t.Gepha).Value.Id;
            var picture = Pictured(root, id);
            t.Tasks.Comment(id, Maybe<string>.None, TaskImages.Join("Look", [picture]));
            t.Controller.Show();
            t.Controller.ToggleThread(id);

            Assert.DoesNotContain(RunTexts(t.Window), text => text.Contains("task-images"));
            Assert.Contains(RunTexts(t.Window), text => text == "Look");
            var thumbnail = PictureOf(t.Window);
            Assert.NotNull(thumbnail);
            Click(thumbnail!);
            Assert.Equal([TaskImages.Resolve(picture, root).Value], opened);
        }
        finally { Directory.Delete(root, true); }
    });

    // A picture whose file is gone shows its path as dim text: never an exception, never an empty gap.
    [Fact]
    public void A_missing_picture_shows_its_path() => StaThread.Run(() =>
    {
        var root = ScratchRoot();
        try
        {
            using var t = Build(stateDir: root);
            var id = t.Tasks.AddNote("Gone", t.Gepha).Value.Id;
            var relative = TaskImages.RelativeFor(id, Start, 0, jpeg: false);
            t.Tasks.Comment(id, Maybe<string>.None, TaskImages.Join("", [relative]));
            t.Controller.Show();
            t.Controller.ToggleThread(id);

            Assert.Null(PictureOf(t.Window));
            Assert.Contains(Inside(t.Window).OfType<TextBlock>(), b => Equals(b.Tag, TaskImageRow.MissingTag) && b.Text == relative);
        }
        finally { Directory.Delete(root, true); }
    });

    // A session's comment naming a file outside task-images is text, never a thumbnail (safety: a click must never open
    // something elsewhere).
    [Fact]
    public void A_picture_outside_task_images_stays_text() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("Crafted", t.Gepha).Value.Id;
        t.Tasks.Comment(id, "s1", "![x](task-images/../state.json)");
        t.Controller.Show();
        t.Controller.ToggleThread(id);

        Assert.Null(PictureOf(t.Window));
        Assert.Contains(RunTexts(t.Window), text => text.Contains("state.json"));
    });

    // --- pasting and dropping pictures (Ctrl+V and a drop take the same path: TaskImageFiles.FromData) -----------------

    static DataObject BitmapData() => new(DataFormats.Bitmap, Picture());

    static IEnumerable<Button> RemoveButtons(object scope) =>
        Inside(scope).OfType<Button>().Where(b => Equals(b.Tag, TasksWindow.RemovePictureTag));

    // Win+Shift+S, then Ctrl+V in the comment box, then words and Enter: one comment holding the words and the picture,
    // saved as a file under the state folder.
    [Fact]
    public void A_pasted_picture_posts_with_the_text() => StaThread.Run(() =>
    {
        var root = ScratchRoot();
        try
        {
            using var t = Build(stateDir: root);
            var id = t.Tasks.AddNote("Bar looks wrong", t.Gepha).Value.Id;
            t.Controller.Show();
            t.Controller.ToggleThread(id);

            Assert.True(t.Window.PasteImages(id, BitmapData()));
            Assert.Single(t.Window.PendingImages(id));
            Assert.True(t.Window.TypeComment(id, "See this", Key.Enter));

            var markdown = t.Tasks.Board.Tasks.Single(x => x.Id == id).Comments.Single().Markdown;
            var parts = TaskImages.Split(markdown);
            Assert.Equal("See this", parts.Text);
            Assert.True(File.Exists(TaskImages.Resolve(Assert.Single(parts.Images), root).Value));
            Assert.Empty(t.Window.PendingImages(id));
        }
        finally { Directory.Delete(root, true); }
    });

    [Fact]
    public void A_picture_alone_posts() => StaThread.Run(() =>
    {
        var root = ScratchRoot();
        try
        {
            using var t = Build(stateDir: root);
            var id = t.Tasks.AddNote("Just a screenshot", t.Gepha).Value.Id;
            t.Controller.Show();
            t.Controller.ToggleThread(id);

            t.Window.PasteImages(id, BitmapData());
            Assert.True(t.Window.TypeComment(id, "", Key.Enter));

            var parts = TaskImages.Split(t.Tasks.Board.Tasks.Single(x => x.Id == id).Comments.Single().Markdown);
            Assert.Equal("", parts.Text);
            Assert.Single(parts.Images);
        }
        finally { Directory.Delete(root, true); }
    });

    // Enter with neither words nor pictures posts nothing, as before.
    [Fact]
    public void Enter_with_nothing_posts_nothing() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("Empty", t.Gepha).Value.Id;
        t.Controller.Show();
        t.Controller.ToggleThread(id);

        Assert.False(t.Window.TypeComment(id, "", Key.Enter));
        Assert.Empty(t.Tasks.Board.Tasks.Single(x => x.Id == id).Comments);
    });

    // Ctrl+V with only words on the clipboard is the text box's own paste: nothing is taken as a picture.
    [Fact]
    public void A_text_only_clipboard_is_left_to_the_box() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("Words", t.Gepha).Value.Id;
        t.Controller.Show();
        t.Controller.ToggleThread(id);

        Assert.False(t.Window.PasteImages(id, new DataObject(DataFormats.UnicodeText, "just words")));
        Assert.Empty(t.Window.PendingImages(id));
    });

    // A session changing something mid-draft redraws the panel; a pending picture stays, as the typed text does.
    [Fact]
    public void Pending_pictures_survive_a_redraw() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("Draft", t.Gepha).Value.Id;
        var other = t.Tasks.AddNote("Elsewhere", t.Gepha).Value.Id;
        t.Controller.Show();
        t.Controller.ToggleThread(id);
        t.Window.PasteImages(id, BitmapData());

        t.Tasks.Rename(other, "Elsewhere, renamed");

        Assert.Single(t.Window.PendingImages(id));
        Assert.Single(RemoveButtons(t.Window));
    });

    [Fact]
    public void The_cross_drops_a_pending_picture() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("Draft", t.Gepha).Value.Id;
        t.Controller.Show();
        t.Controller.ToggleThread(id);
        t.Window.PasteImages(id, BitmapData());
        t.Window.PasteImages(id, BitmapData());

        Click(RemoveButtons(t.Window).First());

        Assert.Single(t.Window.PendingImages(id));
    });

    // --- pictures in the add box (task 5cc9f1d4: "paste an image into a new task ... as i can in comments") -------------

    // + Add, Ctrl+V a screenshot, type a title, Enter: one new task, the words its title and the picture its description,
    // saved as a file under the new task's own id. The box closes with nothing left waiting in it.
    [Fact]
    public void A_picture_pasted_into_the_add_box_becomes_the_new_tasks_description() => StaThread.Run(() =>
    {
        var root = ScratchRoot();
        try
        {
            using var t = Build(stateDir: root);
            t.Controller.Show();
            Click(ButtonNamed(t.Window, "Add"));

            Assert.True(t.Window.PasteIntoAdd(BitmapData()));
            Assert.Single(RemoveButtons(t.Window));
            Assert.True(t.Window.TypeNote("Bar looks wrong", Key.Enter));

            var task = Assert.Single(t.Tasks.Board.Tasks);
            Assert.Equal("Bar looks wrong", task.Title);
            var parts = TaskImages.Split(task.Description);
            Assert.Equal("", parts.Text);
            var picture = Assert.Single(parts.Images);
            Assert.StartsWith($"task-images/{task.Id}/", picture);
            Assert.True(File.Exists(TaskImages.Resolve(picture, root).Value));
            Assert.False(t.Window.AddBoxShown);
            Assert.Empty(t.Window.AddBoxImages);
        }
        finally { Directory.Delete(root, true); }
    });

    // Petre: "also let me add just the image without a title too". Enter with only pictures adds a task called "Picture".
    [Fact]
    public void A_picture_alone_adds_a_task_called_picture() => StaThread.Run(() =>
    {
        var root = ScratchRoot();
        try
        {
            using var t = Build(stateDir: root);
            t.Controller.Show();
            Click(ButtonNamed(t.Window, "Add"));

            t.Window.PasteIntoAdd(BitmapData());
            Assert.True(t.Window.TypeNote("", Key.Enter));

            var task = Assert.Single(t.Tasks.Board.Tasks);
            Assert.Equal("Picture", task.Title);
            Assert.Single(TaskImages.Split(task.Description).Images);
        }
        finally { Directory.Delete(root, true); }
    });

    // Without pictures nothing changes: Enter on an empty box adds nothing and a plain note has no description.
    [Fact]
    public void The_add_box_without_pictures_adds_as_before() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Controller.Show();
        Click(ButtonNamed(t.Window, "Add"));

        Assert.False(t.Window.TypeNote("", Key.Enter));
        Assert.Empty(t.Tasks.Board.Tasks);
        Assert.False(t.Window.PasteIntoAdd(new DataObject(DataFormats.UnicodeText, "just words")));
        Assert.True(t.Window.TypeNote("Call the bank", Key.Enter));
        Assert.Equal("", Assert.Single(t.Tasks.Board.Tasks).Description);
    });

    // A note of several lines (task 98420dbc). The box wraps and takes line breaks; Shift+Enter is left to it, so it adds
    // nothing, while Enter adds the first line as the title and the rest as the description.
    [Fact]
    public void A_note_of_several_lines_is_a_title_and_a_description() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Controller.Show();
        Click(ButtonNamed(t.Window, "Add"));

        Assert.False(t.Window.TypeNote("Fix the strip", Key.Enter, ModifierKeys.Shift));
        Assert.Empty(t.Tasks.Board.Tasks);
        Assert.True(t.Window.TypeNote("Fix the strip\nit overlaps the taskbar\non the second screen", Key.Enter));

        var task = Assert.Single(t.Tasks.Board.Tasks);
        Assert.Equal("Fix the strip", task.Title);
        Assert.Equal("it overlaps the taskbar\non the second screen", task.Description);
    });

    // Lines after the title and pasted pictures together: the text comes first in the description, then the pictures.
    [Fact]
    public void A_note_of_several_lines_keeps_its_pictures_after_the_text() => StaThread.Run(() =>
    {
        var root = ScratchRoot();
        try
        {
            using var t = Build(stateDir: root);
            t.Controller.Show();
            Click(ButtonNamed(t.Window, "Add"));

            t.Window.PasteIntoAdd(BitmapData());
            Assert.True(t.Window.TypeNote("Bar looks wrong\nsee the gap on the left", Key.Enter));

            var task = Assert.Single(t.Tasks.Board.Tasks);
            Assert.Equal("Bar looks wrong", task.Title);
            var parts = TaskImages.Split(task.Description);
            Assert.Equal("see the gap on the left", parts.Text);
            Assert.Single(parts.Images);
        }
        finally { Directory.Delete(root, true); }
    });

    // Esc closes the box and the waiting pictures go with it: the next + Add starts empty, and no file was written.
    [Fact]
    public void Esc_drops_the_add_boxs_pictures() => StaThread.Run(() =>
    {
        var root = ScratchRoot();
        try
        {
            using var t = Build(stateDir: root);
            t.Controller.Show();
            Click(ButtonNamed(t.Window, "Add"));
            t.Window.PasteIntoAdd(BitmapData());

            Assert.True(t.Window.Press(Key.Escape, ModifierKeys.None));
            Click(ButtonNamed(t.Window, "Add"));

            Assert.Empty(t.Window.AddBoxImages);
            Assert.Empty(RemoveButtons(t.Window));
            Assert.Empty(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, true); }
    });

    // "Add subtask…" takes pictures too: the subtask gets them as its description, under its own id.
    [Fact]
    public void A_subtask_takes_pasted_pictures() => StaThread.Run(() =>
    {
        var root = ScratchRoot();
        try
        {
            using var t = Build(stateDir: root);
            var parent = t.Tasks.AddNote("Ship the release", t.Gepha).Value;
            t.Controller.Show();
            t.Window.BeginSubtaskAdd(parent.Id);

            t.Window.PasteIntoAdd(BitmapData());
            Assert.True(t.Window.TypeNote("Fix the dialog", Key.Enter));

            var child = t.Tasks.Board.Tasks.Single(x => x.Title == "Fix the dialog");
            Assert.Equal(Maybe.From(parent.Id), child.ParentId);
            Assert.StartsWith($"task-images/{child.Id}/", Assert.Single(TaskImages.Split(child.Description).Images));
        }
        finally { Directory.Delete(root, true); }
    });

    // The editor shows the description's words in its box and its pictures under it; a pasted one joins them, Enter saves
    // words and every picture, and Esc saves nothing and writes no file.
    [Fact]
    public void The_description_editor_keeps_its_pictures_and_adds_pasted_ones() => StaThread.Run(() =>
    {
        var root = ScratchRoot();
        try
        {
            using var t = Build(stateDir: root);
            var id = t.Tasks.AddNote("Brief", t.Gepha).Value.Id;
            var old = Pictured(root, id);
            t.Tasks.Describe(id, TaskImages.Join("The plan", [old]));
            t.Controller.Show();
            t.Window.BeginRename(id);

            Assert.Equal("The plan", t.Window.DescriptionEditor!.Text);
            Assert.Single(RemoveButtons(t.Window));
            Assert.True(t.Window.PasteIntoDescription(BitmapData()));
            t.Window.SaveEditor();

            var parts = TaskImages.Split(t.Tasks.Board.Tasks.Single(x => x.Id == id).Description);
            Assert.Equal("The plan", parts.Text);
            Assert.Equal(2, parts.Images.Count);
            Assert.Equal(old, parts.Images[0]);
            Assert.All(parts.Images, image => Assert.True(File.Exists(TaskImages.Resolve(image, root).Value)));
        }
        finally { Directory.Delete(root, true); }
    });

    // Final review, Important 2: taking a screenshot (Win+Shift+S) or pressing on a file in Explorer to drag it takes the
    // keyboard out of the window, and the focus change says "to nowhere" (NewFocus null). That is not a click away, so the
    // editor stays open and the picture can still arrive; a click on another element of the panel still cancels.
    static void LoseFocus(TextBox box, IInputElement? to) =>
        box.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, box, to) { RoutedEvent = Keyboard.LostKeyboardFocusEvent });

    [Fact]
    public void Leaving_the_window_keeps_the_editor_open() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("Brief", t.Gepha).Value.Id;
        t.Controller.Show();
        t.Window.BeginRename(id);

        LoseFocus(t.Window.DescriptionEditor!, null);

        Assert.NotNull(t.Window.DescriptionEditor);
    });

    [Fact]
    public void A_click_elsewhere_in_the_panel_still_cancels_the_editor() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("Brief", t.Gepha).Value.Id;
        t.Controller.Show();
        t.Window.BeginRename(id);

        LoseFocus(t.Window.DescriptionEditor!, t.Window.SearchBox);

        Assert.Null(t.Window.DescriptionEditor);
    });

    [Fact]
    public void Cancelling_the_editor_writes_no_picture() => StaThread.Run(() =>
    {
        var root = ScratchRoot();
        try
        {
            using var t = Build(stateDir: root);
            var id = t.Tasks.AddNote("Brief", t.Gepha).Value.Id;
            t.Controller.Show();
            t.Window.BeginRename(id);
            t.Window.PasteIntoDescription(BitmapData());

            t.Window.CancelEditor();

            Assert.Equal("", t.Tasks.Board.Tasks.Single(x => x.Id == id).Description);
            Assert.False(Directory.Exists(Path.Combine(root, TaskImages.Folder)));
        }
        finally { Directory.Delete(root, true); }
    });

    [Fact]
    public void Removing_a_kept_picture_in_the_editor_drops_its_line() => StaThread.Run(() =>
    {
        var root = ScratchRoot();
        try
        {
            using var t = Build(stateDir: root);
            var id = t.Tasks.AddNote("Brief", t.Gepha).Value.Id;
            t.Tasks.Describe(id, TaskImages.Join("The plan", [Pictured(root, id)]));
            t.Controller.Show();
            t.Window.BeginRename(id);

            Click(RemoveButtons(t.Window).Single());
            t.Window.SaveEditor();

            Assert.Equal("The plan", t.Tasks.Board.Tasks.Single(x => x.Id == id).Description);
        }
        finally { Directory.Delete(root, true); }
    });

    // The description's pictures show under it, once the line is open, as a comment's do.
    [Fact]
    public void A_descriptions_picture_shows_under_it() => StaThread.Run(() =>
    {
        var root = ScratchRoot();
        try
        {
            using var t = Build(stateDir: root);
            var id = t.Tasks.AddNote("Brief", t.Gepha).Value.Id;
            t.Tasks.Describe(id, TaskImages.Join("The plan", [Pictured(root, id)]));
            t.Controller.Show();
            t.Controller.ToggleThread(id);

            Assert.Equal("The plan", DescriptionBlockOf(t.Window, id)!.Text);
            Assert.NotNull(PictureOf(LineOf(t.Window, id)));
        }
        finally { Directory.Delete(root, true); }
    });

    // --- subtasks (docs/superpowers/specs/2026-11-24-subtasks-design.md, "Making subtasks", Petre's pick A) ----------

    [Fact]
    public void The_menu_offers_add_subtask_after_start_claude() => StaThread.Run(() =>
    {
        using var t = Build(startClaude: _ => { });
        var id = t.Tasks.AddNote("Parent", t.Gepha).Value.Id;
        t.Controller.Show();

        var menu = t.Window.OpenMenuFor(id);
        Assert.Equal(["Edit", "Open in window", "Start Claude on this", "Add subtask…"], MenuWords(menu).Take(4));
        menu.IsOpen = false;
    });

    [Fact]
    public void Add_subtask_opens_the_box_with_the_parent_on_its_workspace() => StaThread.Run(() =>
    {
        using var t = Build();
        var parent = t.Tasks.AddNote("Ship the release", t.Sparrow).Value;
        t.Controller.Show();

        t.Window.BeginSubtaskAdd(parent.Id);

        Assert.True(t.Window.AddBoxShown);
        Assert.Equal("A subtask: Enter adds it, Esc drops the parent", t.Window.AddPlaceholderText);
        Assert.Contains("Ship the release", TextOf(ButtonNamed(t.Window, "Subtask of")));
        Assert.True(t.Window.TypeNote("Write the notes", Key.Enter));
        var child = t.Tasks.Board.Tasks.Single(x => x.Title == "Write the notes");
        Assert.Equal(Maybe.From(parent.Id), child.ParentId);
        Assert.Equal(t.Sparrow, child.WorkspaceId.Value);
        Assert.False(t.Window.AddBoxShown);
    });

    [Fact]
    public void Esc_in_a_subtask_box_drops_the_parent_first() => StaThread.Run(() =>
    {
        using var t = Build();
        var parent = t.Tasks.AddNote("Parent", t.Gepha).Value;
        t.Controller.Show();
        t.Window.BeginSubtaskAdd(parent.Id);

        Assert.True(t.Window.Press(Key.Escape, ModifierKeys.None));

        Assert.True(t.Window.AddBoxShown);
        Assert.Equal("A note: Enter adds it, Esc closes the box", t.Window.AddPlaceholderText);
        Assert.True(t.Window.TypeNote("Loose", Key.Enter));
        Assert.True(t.Tasks.Board.Tasks.Single(x => x.Title == "Loose").ParentId.HasNoValue);
    });

    // Petre's pick A in the panel: a full line names the parent in front of a subtask's title, a slim one only "↳", and a
    // parent's title is followed by its open subtasks' count. A rename of the parent redraws its subtasks' lines.
    // A title's runs joined: what it reads, whether it was set as Text (one run) or built from runs.
    static string RunsOf(TextBlock title) => string.Concat(title.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text));

    [Fact]
    public void A_full_line_names_the_parent_and_a_parent_counts_its_subtasks() => StaThread.Run(() =>
    {
        using var t = Build(width: TasksPanelLayout.SlimBelow);
        var parent = t.Tasks.AddNote("Ship the release", t.Gepha).Value;
        var child = t.Tasks.AddNote("Write the notes", t.Sparrow, parent: parent.Id).Value;
        t.Controller.Show();

        // Under its parent the arrow alone says whose it is; the count after the parent's title stays on one line.
        Assert.Equal("↳ Write the notes", RunsOf(TitleOf(t.Window, child.Id)));
        Assert.Equal("Ship the release ·\u00A01\u00A0subtask", RunsOf(TitleOf(t.Window, parent.Id)));

        // With the parent filtered out the subtask is in its own place and names its parent.
        t.Controller.FilterWorkspace(TaskWorkspaceFilter.On(t.Sparrow));
        Assert.Equal("↳ Ship the release › Write the notes", RunsOf(TitleOf(t.Window, child.Id)));
        t.Tasks.Rename(parent.Id, "Release 2.6");
        Assert.Equal("↳ Release 2.6 › Write the notes", RunsOf(TitleOf(t.Window, child.Id)));
    });

    // Task 9e85caee (Petre's screenshot: a done "research" subtask's arrow stood further right than its siblings', and
    // "what's up with research being nested further than the other tasks?"). The done tick used to be docked in front of
    // the title, so in front of its "↳". A done subtask's title now starts with the arrow, and the tick comes right after
    // it, in the title; nothing is docked before the title, so the arrow lines up with every sibling's.
    [Fact]
    public void A_done_subtasks_tick_comes_after_its_arrow_so_the_arrow_lines_up() => StaThread.Run(() =>
    {
        using var t = Build();
        var parent = t.Tasks.AddNote("Ship the release", t.Gepha).Value;
        var open = t.Tasks.AddNote("Write the notes", t.Gepha, parent: parent.Id).Value;
        var done = t.Tasks.AddNote("Research it", t.Gepha, parent: parent.Id).Value;
        Assert.True(t.Tasks.MarkDone(done.Id).IsSuccess);
        t.Controller.Show();
        t.Window.UpdateLayout();

        var title = TitleOf(t.Window, done.Id);
        var inlines = title.Inlines.ToArray();
        Assert.StartsWith("↳", Assert.IsType<System.Windows.Documents.Run>(inlines[0]).Text, StringComparison.Ordinal);
        var tick = Assert.IsType<System.Windows.Documents.InlineUIContainer>(inlines[1]);
        Assert.Equal(TaskStatusMarks.DoneTickTag, ((FrameworkElement)tick.Child).Tag);
        Assert.True(HasDoneTick(LineOf(t.Window, done.Id)));
        // Measured, not only ordered: both arrows start at the same x on the line.
        var line = LineOf(t.Window, done.Id);
        var openLine = LineOf(t.Window, open.Id);
        var arrowX = title.TranslatePoint(new Point(0, 0), line).X;
        var openArrowX = TitleOf(t.Window, open.Id).TranslatePoint(new Point(0, 0), openLine).X;
        Assert.Equal(openArrowX, arrowX, 1);
    });

    // Follow-up: the List draws a subtask right under its parent, in the parent's group, whatever its own flags.
    static IReadOnlyList<Guid> ListOrder(TasksWindow window) =>
        Inside(window).OfType<Button>().Where(b => b.Tag is Guid).Select(b => (Guid)b.Tag).ToArray();

    [Fact]
    public void The_list_draws_a_subtask_right_under_its_parent() => StaThread.Run(() =>
    {
        using var t = Build();
        var parent = t.Tasks.AddNote("Ship the release", t.Gepha).Value;
        t.Tasks.AddNote("Unrelated", t.Gepha);
        var child = t.Tasks.AddNote("Write the notes", t.Gepha, Quadrant.II, parent.Id).Value;
        t.Controller.Show();

        var order = ListOrder(t.Window).ToList();
        Assert.Equal(order.IndexOf(parent.Id) + 1, order.IndexOf(child.Id));
    });

    // Follow-up (Petre's screenshot of an open parent with no subtasks in it): the open thread lists them, each a button
    // that shows that subtask, and ends with "Add subtask".
    [Fact]
    public void An_open_parent_lists_its_subtasks_and_offers_add_subtask() => StaThread.Run(() =>
    {
        var selection = new TaskSelection();
        using var t = Build(selection: selection);
        var parent = t.Tasks.AddNote("Ship the release", t.Gepha).Value;
        var child = t.Tasks.AddNote("Write the notes", t.Gepha, parent: parent.Id).Value;
        t.Tasks.SetStatusLine(child.Id, "draft done");
        t.Controller.Show();
        t.Controller.ToggleThread(parent.Id);

        var entry = ButtonNamed(t.Window, "Subtask Write the notes");
        Assert.Contains("draft done", string.Concat(Inside(entry).OfType<TextBlock>().Select(RunsOf)));
        Click(entry);
        Assert.Equal(child.Id, selection.Current.Value);

        // The parent's thread is still open.
        Click(ButtonNamed(t.Window, "Add subtask"));
        Assert.True(t.Window.AddBoxShown);
        Assert.Equal(FloatingBar.SubtaskPlaceholder, t.Window.AddPlaceholderText);
    });

    // Task 54b39c4c (Petre: "subtask should show its parent and have a way to open the parent easily"): an open subtask's
    // thread starts with "↑ Subtask of <parent>", a button that shows the parent, picked, with its own thread open (its
    // thread lists the subtask back). A plain task's thread has no such line.
    [Fact]
    public void An_open_subtask_names_its_parent_and_opens_it() => StaThread.Run(() =>
    {
        var selection = new TaskSelection();
        using var t = Build(selection: selection);
        var parent = t.Tasks.AddNote("Ship the release", t.Gepha).Value;
        var child = t.Tasks.AddNote("Write the notes", t.Gepha, parent: parent.Id).Value;
        t.Controller.Show();
        t.Controller.ToggleThread(child.Id);

        var entry = ButtonNamed(t.Window, "Parent Ship the release");
        Assert.Equal("↑ Subtask of Ship the release", string.Concat(Inside(entry).OfType<TextBlock>().Select(RunsOf)));
        Click(entry);

        Assert.Equal(parent.Id, selection.Current.Value);
        Assert.True(HasButtonNamed(t.Window, "Subtask Write the notes"));
    });

    // The parent is shown past a filter that hides it, as a Stats line's task is (TasksBoard.OpenParent): here the
    // workspace filter shows only the subtask's workspace, and the parent lives on another.
    [Fact]
    public void Opening_the_parent_lets_it_past_the_workspace_filter() => StaThread.Run(() =>
    {
        var selection = new TaskSelection();
        using var t = Build(selection: selection);
        var parent = t.Tasks.AddNote("Ship the release", t.Sparrow).Value;
        var child = t.Tasks.AddNote("Write the notes", t.Gepha, parent: parent.Id).Value;
        t.Controller.Show();
        t.Controller.FilterWorkspace(TaskWorkspaceFilter.On(t.Gepha));
        Assert.False(HasButtonNamed(t.Window, "Subtask Write the notes"));
        t.Controller.ToggleThread(child.Id);

        Click(ButtonNamed(t.Window, "Parent Ship the release"));

        Assert.Equal(parent.Id, selection.Current.Value);
        // The parent's line is drawn with its thread open: its subtask is listed in it.
        Assert.True(HasButtonNamed(t.Window, "Subtask Write the notes"));
    });

    // Task b0526e18 (Petre: "i can't interact with the subtask. i should be able to"): with "Subtasks" unticked the
    // subtask is folded into its parent, so the thread's subtask link used to pick a line that was not drawn and nothing
    // happened. Now the picked subtask is drawn under its parent with its own thread open, and folds again once
    // another task is picked.
    [Fact]
    public void A_folded_subtask_opened_from_its_parents_thread_is_drawn() => StaThread.Run(() =>
    {
        var selection = new TaskSelection();
        using var t = Build(selection: selection);
        var parent = t.Tasks.AddNote("Ship the release", t.Gepha).Value.Id;
        var child = t.Tasks.AddNote("Write the notes", t.Gepha, Quadrant.IV, parent).Value.Id;
        t.Controller.Show();
        Click(ButtonNamed(t.Window, "Subtasks"));
        Assert.Equal([parent], t.Window.Keys);
        t.Controller.ToggleThread(parent);

        Click(ButtonNamed(t.Window, "Subtask Write the notes"));

        Assert.Equal(child, selection.Current.Value);
        Assert.Equal([parent, child], t.Window.Keys);
        // The subtask's own thread is open: it names its parent.
        Assert.True(HasButtonNamed(t.Window, "Parent Ship the release"));

        t.Controller.Pick(parent);
        Assert.Equal([parent], t.Window.Keys);
    });

    // A closed subtask is listed in its parent's thread too, and the Open filter hides it: the link lets it past the
    // filter, as "Subtask of" does for a parent (TasksBoard.ShowTaskPastFilters), so its line shows with its thread open.
    [Fact]
    public void A_closed_subtask_opened_from_its_parents_thread_is_let_past_the_filter() => StaThread.Run(() =>
    {
        using var t = Build();
        var parent = t.Tasks.AddNote("Ship the release", t.Gepha).Value.Id;
        var child = t.Tasks.AddNote("Write the notes", t.Gepha, Quadrant.IV, parent).Value.Id;
        t.Tasks.Close(child);
        t.Controller.Show();
        t.Controller.ToggleThread(parent);
        Assert.DoesNotContain(child, t.Window.Keys);

        Click(ButtonNamed(t.Window, "Subtask Write the notes"));

        Assert.Contains(child, t.Window.Keys);
        Assert.True(HasButtonNamed(t.Window, "Parent Ship the release"));
    });

    [Fact]
    public void A_plain_tasks_thread_names_no_parent() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("Ship the release", t.Gepha).Value.Id;
        t.Controller.Show();
        t.Controller.ToggleThread(id);

        Assert.DoesNotContain(Inside(t.Window).OfType<Button>(), b => AutomationProperties.GetName(b)?.StartsWith("Parent ") == true);
    });

    // The panel's line menu: "Open parent" on a subtask's line, right after "Add subtask…"; it shows the parent. None on a
    // line with no parent.
    [Fact]
    public void A_subtasks_menu_in_the_panel_offers_open_parent() => StaThread.Run(() =>
    {
        var selection = new TaskSelection();
        using var t = Build(selection: selection);
        var parent = t.Tasks.AddNote("Ship the release", t.Gepha).Value.Id;
        var child = t.Tasks.AddNote("Write the notes", t.Gepha, parent: parent).Value.Id;
        t.Controller.Show();

        var menu = t.Window.OpenMenuFor(child);
        var words = MenuWords(menu).ToList();
        Assert.Equal(words.IndexOf("Add subtask…") + 1, words.IndexOf(TasksWindow.OpenParentText));
        menu.Items.OfType<MenuItem>().ElementAt(words.IndexOf(TasksWindow.OpenParentText))
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.IsOpen = false;
        // Run once the menu has closed (an Input-priority dispatch): a lower priority runs after it.
        t.Window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert.Equal(parent, selection.Current.Value);

        var parentMenu = t.Window.OpenMenuFor(parent);
        Assert.DoesNotContain(TasksWindow.OpenParentText, MenuWords(parentMenu));
        parentMenu.IsOpen = false;
    });

    [Fact]
    public void A_slim_line_shows_only_the_arrow() => StaThread.Run(() =>
    {
        using var t = Build(width: TasksPanelLayout.DefaultWidth);
        var parent = t.Tasks.AddNote("Ship the release", t.Gepha).Value;
        var child = t.Tasks.AddNote("Write the notes", t.Gepha, parent: parent.Id).Value;
        t.Controller.Show();

        Assert.Equal("↳ Write the notes", RunsOf(TitleOf(t.Window, child.Id)));
    });

    // Drag to nest in the panel's List, the bar's twin: the middle of a line makes a subtask, the edges reorder, and a
    // nested subtask dragged out is a plain task again.
    [Fact]
    public void In_the_panel_dropping_on_the_middle_of_a_line_makes_a_subtask() => StaThread.Run(() =>
    {
        using var t = Build();
        var a = t.Tasks.AddNote("a", t.Gepha).Value.Id;
        var b = t.Tasks.AddNote("b", t.Gepha).Value.Id;
        // A held button the drag reads (no real mouse on a CI runner), and laid-out lines to aim at, as the other drags.
        t.Window.DragMouse = ArrangeMouse.Held;
        t.Controller.Show();
        t.Window.UpdateLayout();

        WindowDrag(t, a, InLine(t, b, 0.5));

        Assert.Equal(Maybe.From(b), t.Tasks.Board.Tasks.Single(x => x.Id == a).ParentId);
    });

    [Fact]
    public void In_the_panel_a_nested_subtask_dragged_out_is_freed() => StaThread.Run(() =>
    {
        using var t = Build();
        var other = t.Tasks.AddNote("other", t.Gepha).Value.Id;
        var parent = t.Tasks.AddNote("parent", t.Gepha).Value.Id;
        var child = t.Tasks.AddNote("child", t.Gepha, parent: parent).Value.Id;
        t.Window.DragMouse = ArrangeMouse.Held;
        t.Controller.Show();
        t.Window.UpdateLayout();

        WindowDrag(t, child, InLine(t, other, 0.9));

        Assert.True(t.Tasks.Board.Tasks.Single(x => x.Id == child).ParentId.HasNoValue);
    });

    [Fact]
    public void Add_subtask_on_a_subtask_adds_a_sibling_in_the_window() => StaThread.Run(() =>
    {
        using var t = Build();
        var parent = t.Tasks.AddNote("Parent", t.Gepha).Value;
        var child = t.Tasks.AddNote("Child", t.Gepha, parent: parent.Id).Value;
        t.Controller.Show();

        t.Window.BeginSubtaskAdd(child.Id);
        Assert.True(t.Window.TypeNote("Sibling", Key.Enter));

        Assert.Equal(Maybe.From(parent.Id), t.Tasks.Board.Tasks.Single(x => x.Title == "Sibling").ParentId);
    });

    // THE STATS VIEW (task 521d2b07; spec docs/superpowers/specs/2026-11-25-task-stats-design.md). The switch shows it in
    // place of the List, with the search row and the chips hidden; today is the picked day, listing what was finished and
    // added; List goes back.
    [Fact]
    public void The_Stats_switch_shows_the_days_tasks_and_List_goes_back() => StaThread.Run(() =>
    {
        using var t = Build();
        var soap = t.Tasks.AddNote("Soap", t.Gepha).Value.Id;
        var bread = t.Tasks.AddNote("Bread", t.Gepha).Value.Id;
        t.Tasks.Close(soap);
        t.Controller.Show();

        Click(ButtonNamed(t.Window, "Stats"));
        Assert.True(t.Window.StatsShown);
        Assert.Equal(Visibility.Visible, t.Window.StatsScroller.Visibility);
        Assert.Equal(Visibility.Collapsed, t.Window.Scroller.Visibility);
        Assert.Equal(Visibility.Collapsed, t.Window.SearchRow.Visibility);
        Assert.Equal(Visibility.Collapsed, t.Window.Filters.Visibility);
        // Finished first, then added, each latest first: Soap finished; Bread then Soap added.
        Assert.Equal([soap, bread, soap], t.Window.StatsLinks);
        Assert.Null(t.Window.StatsDay);

        Click(ButtonNamed(t.Window, "List"));
        Assert.False(t.Window.StatsShown);
        Assert.Equal(Visibility.Visible, t.Window.Scroller.Visibility);
        Assert.Equal(Visibility.Visible, t.Window.SearchRow.Visibility);
        Assert.Equal([bread], t.Window.Keys);
    });

    // The link: a closed task's line in Stats leaves Stats for the Closed filter, with the task picked and its thread open.
    [Fact]
    public void A_closed_tasks_link_in_Stats_shows_it_under_Closed() => StaThread.Run(() =>
    {
        using var t = Build();
        var soap = t.Tasks.AddNote("Soap", t.Gepha).Value.Id;
        t.Tasks.AddNote("Bread", t.Gepha);
        t.Tasks.Close(soap);
        t.Controller.Show();
        Click(ButtonNamed(t.Window, "Stats"));

        Click(Inside(t.Window.StatsPanel).OfType<Button>().First(b => b.Tag is Guid id && id == soap));

        Assert.False(t.Window.StatsShown);
        Assert.Equal([soap], t.Window.Keys);
        // Not selected: a closed task never stays selected (ruling 21, TaskSelection.KeepOnly); its open thread shows it.
        Assert.True(t.Window.ThreadShown(soap));
    });

    // An open task's link lands on the List with the task picked, even from a status filter that would hide it.
    [Fact]
    public void An_open_tasks_link_in_Stats_picks_it_past_a_status_filter() => StaThread.Run(() =>
    {
        using var t = Build();
        var bread = t.Tasks.AddNote("Bread", t.Gepha).Value.Id;
        t.Controller.Show();
        Click(ButtonNamed(t.Window, "Filter: Closed 0"));
        Assert.Empty(t.Window.Keys);
        Click(ButtonNamed(t.Window, "Stats"));

        Click(Inside(t.Window.StatsPanel).OfType<Button>().First(b => b.Tag is Guid id && id == bread));

        Assert.Equal([bread], t.Window.Keys);
        Assert.True(t.Window.IsSelected(bread));
        Assert.True(t.Window.ThreadShown(bread));
    });

    // On a panel too narrow for the Matrix, List still shows while Stats is on, as the way back; Stats always shows.
    [Fact]
    public void A_narrow_panel_still_offers_List_from_Stats() => StaThread.Run(() =>
    {
        using var t = Build(width: TasksPanelLayout.MatrixFrom - 1);
        t.Tasks.AddNote("a", t.Gepha);
        t.Controller.Show();
        Assert.Equal(Visibility.Collapsed, ButtonNamed(t.Window, "List").Visibility);
        Assert.Equal(Visibility.Visible, ButtonNamed(t.Window, "Stats").Visibility);

        t.Controller.ShowStats();
        Assert.Equal(Visibility.Visible, ButtonNamed(t.Window, "List").Visibility);
        Assert.Equal(Visibility.Collapsed, ButtonNamed(t.Window, "Matrix").Visibility);
    });

    // A fresh open starts on the List again: Stats is not remembered.
    [Fact]
    public void Stats_is_not_remembered_across_opens() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Controller.ShowStats();
        Assert.True(t.Window.StatsShown);
        t.Controller.Hide();
        t.Controller.Show();
        Assert.False(t.Window.StatsShown);
    });

    // Picking another day lists that day's tasks; a day with nothing lists nothing.
    [Fact]
    public void Picking_a_day_lists_its_tasks() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Tasks.AddNote("Soap", t.Gepha);
        t.Controller.ShowStats();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(Start.AddHours(1), TimeZoneInfo.Local).DateTime);

        t.Window.PickStatsDay(today.AddDays(-1));
        Assert.Equal(today.AddDays(-1), t.Window.StatsDay);
        Assert.Empty(t.Window.StatsLinks);
        t.Window.PickStatsDay(today);
        Assert.Single(t.Window.StatsLinks);
    });

    // --- the resizable columns (task 3df2d28f, TasksWindow.Columns.cs) ---------------------------------------------------

    // A full line's grid: the one inside the line with the line's columns.
    static Grid GridOf(Button line) => Inside(line).OfType<Grid>().First(g => g.ColumnDefinitions.Count > 4);

    // The header heads full lines only: shown over the List at full width, hidden over slim lines and the Matrix.
    [Fact]
    public void The_column_header_shows_over_full_lines_only() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Tasks.Create("work", t.Gepha, Session("s1"));
        t.Controller.Show();
        Assert.True(t.Window.ColumnHeaderShown);

        t.Window.Actions!.ResizePanel(TasksPanelLayout.SlimBelow - 1);
        Assert.False(t.Window.ColumnHeaderShown);

        t.Window.Actions!.ResizePanel(TasksPanelLayout.MatrixFrom);
        Assert.True(t.Window.ColumnHeaderShown);
        Assert.True(t.Harness.Manager.SetTasksMatrix(true).IsSuccess);
        t.Controller.Refresh();
        Assert.False(t.Window.ColumnHeaderShown);

        // Nor over the Stats view (task 521d2b07), which has no lines.
        Assert.True(t.Harness.Manager.SetTasksMatrix(false).IsSuccess);
        t.Controller.Refresh();
        Assert.True(t.Window.ColumnHeaderShown);
        t.Controller.ShowStats();
        Assert.False(t.Window.ColumnHeaderShown);
    });

    // A resize widens the column on every full line at once and is saved with the panel, so the next draw keeps it.
    [Fact]
    public void A_resized_column_follows_on_every_line_and_is_saved() => StaThread.Run(() =>
    {
        using var t = Build();
        var one = t.Tasks.Create("one", t.Gepha, Session("s1")).Value.Id;
        var two = t.Tasks.AddNote("two", t.Gepha).Value.Id;
        t.Controller.Show();

        t.Window.ResizeColumn(TaskColumn.Sessions, 130);

        Assert.Equal(130, t.Window.ColumnWidth(TaskColumn.Sessions));
        Assert.Equal(130, t.Harness.Manager.State.TasksPanel!.ColumnWidths.Sessions);
        new[] { one, two }.ToList().ForEach(id => Assert.Contains(GridOf(LineOf(t.Window, id)).ColumnDefinitions, c => c.Width.Value == 130));
        t.Tasks.AddNote("three", t.Gepha);
        Assert.Equal(130, t.Window.ColumnWidth(TaskColumn.Sessions));
    });

    // The buttons sit together at the right end of a full line: the flag toggles after the workspace chip, then the
    // comments and "⋯", with nothing between them but their own columns.
    [Fact]
    public void The_buttons_sit_together_after_the_workspace_chip() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("Dishwasher tablets", t.Gepha).Value.Id;
        t.Controller.Show();
        t.Window.UpdateLayout();

        var line = LineOf(t.Window, id);
        var flags = Grid.GetColumn((UIElement)LogicalTreeHelper.GetParent(ButtonNamed(line, "Important")));
        var comments = Grid.GetColumn(ButtonNamed(line, "Comments"));
        var more = Grid.GetColumn(ButtonNamed(line, "More"));
        var chip = Grid.GetColumn(Inside(line).OfType<Border>().First(b => b.Child is TextBlock { Text: "GEPHA" }));
        Assert.True(chip < flags && flags < comments && comments < more, $"chip {chip}, flags {flags}, comments {comments}, more {more}");
        // A note with no comments, on a board with no Claude task and nothing closed: the comments column is the glyph's
        // and the action column is gone.
        var grid = GridOf(line);
        Assert.Equal(20, grid.ColumnDefinitions[comments].Width.Value);
        Assert.Equal(0, grid.ColumnDefinitions[comments + 1].Width.Value);
    });
}
