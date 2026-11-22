using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core.Tasks;

namespace Deskbar.Windows.Tests;

// The tasks window and its controller (TasksWindow, TasksBoard; docs/superpowers/specs/2026-11-22-tasks-board-design.md,
// "The window", "Comments", "Statuses", "Going to a session"). The window is display only; the controller decides what
// it shows and what each click does. Built over the shared two-workspace harness (GEPHA current, Sparrow beside it)
// with a real TaskService on an in-memory store, so every change goes through the service the App hands the board.
// The window is Quiet: shown off screen and never activated, so a run neither flashes it nor takes the keyboard.
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

    // Each read of the task clock is a minute later, so tasks are raised in the order the test makes them. Each read of
    // the click clock is a second later, so a click is never "right after a repaint" unless a test hands in a clock of
    // its own (the click guard's test does).
    // `bringForward` sees the handle and the seam answers that it activated the window; `activates` overrides that
    // answer (the seam that left the window on its own desktop).
    static Board Build(Func<TaskItem, Result>? goTo = null, Action<nint>? bringForward = null, Func<DateTime>? clickClock = null,
        Func<DateTimeOffset>? now = null, TaskSelection? selection = null, Func<bool>? activates = null)
    {
        var harness = Harness.Build();
        var minutes = 0;
        var seconds = 0;
        var timers = new FakeTimers();
        var went = new List<Guid>();
        var tasks = new TaskService(new MemoryStore(), () => Start.AddMinutes(minutes++));
        var window = new TasksWindow { Quiet = true, ClickClock = clickClock ?? (() => ClickStart.AddSeconds(seconds++)), Now = now ?? (() => Start.AddHours(1)) };
        var controller = new TasksBoard(window, tasks, harness.Manager, goTo ?? (t =>
        {
            went.Add(t.Id);
            return Result.Success();
        }), bringForward: bringForward is null && activates is null ? null : hwnd =>
        {
            bringForward?.Invoke(hwnd);
            return activates?.Invoke() ?? true;
        }, undoTimer: timers.Start, now: () => Start.AddHours(1), selection: selection);
        return new Board { Harness = harness, Window = window, Controller = controller, Tasks = tasks, Timers = timers, WentTo = went };
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

    // A line of the window: the button that carries its task's id.
    static Button LineOf(TasksWindow window, Guid id) =>
        Inside(window).OfType<Button>().Single(b => b.Tag is Guid tag && tag == id);

    // Task 12 (Ruling 28): the List view is grouped as the bar, Waiting on you, then Covey's quadrants under captions in
    // his words; a Done task keeps its place in its quadrant.
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

        t.Controller.Open(Rect.Empty);

        Assert.Equal(["Waiting on you", "IV  Not urgent, not important"], t.Window.Headers);
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
        t.Controller.Open(Rect.Empty);

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

    [Fact]
    public void A_line_expands_to_its_thread() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.Create("Tasks redesign", t.Gepha, Session("s1")).Value.Id;
        t.Tasks.Comment(id, "s1", "The branch is **built**.");
        t.Tasks.Comment(id, Maybe<string>.None, "yes, swap");
        t.Controller.Open(Rect.Empty);
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
        t.Controller.Open(Rect.Empty);
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
        t.Controller.Open(Rect.Empty);
        t.Controller.ToggleThread(id);

        Assert.False(t.Window.TypeComment(id, "first line", Key.Enter, ModifierKeys.Shift));

        Assert.Empty(t.Tasks.Board.Tasks.Single(x => x.Id == id).Comments);
    });

    [Fact]
    public void Close_reopen_and_set_status_from_the_window() => StaThread.Run(() =>
    {
        using var t = Build();
        var note = t.Tasks.AddNote("Dishwasher tablets", t.Gepha).Value.Id;
        t.Controller.Open(Rect.Empty);

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

        // Undo brings it back by the bar's rule, so the two Undos agree: Reopen (Backlog for a note with no live
        // session), Done only for a task that was Done.
        Click(ButtonNamed(LineOf(t.Window, note), "Undo"));
        Assert.Equal(TaskStatus.Backlog, t.Tasks.Board.Tasks.Single(x => x.Id == note).Status);
        Assert.False(t.Window.IsClosing(note));
        Assert.True(t.Controller.SetStatus(note, TaskStatus.InProgress).IsSuccess);

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
        t.Controller.Open(Rect.Empty);

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
        t.Controller.Open(Rect.Empty);
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

    [Fact]
    public void The_window_opens_on_the_board_not_a_form() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("note", t.Gepha).Value.Id;
        t.Controller.Open(Rect.Empty);

        Assert.True(t.Window.IsOpen);
        Assert.False(t.Window.AddBoxShown);
        Assert.DoesNotContain(Inside(t.Window).OfType<TextBox>(), b => b.IsKeyboardFocused || b.IsFocused);
        Assert.Equal([id], t.Window.Keys);

        // Opened again after typing in a thread, it is still the board, filters reset and threads closed.
        t.Controller.ToggleThread(id);
        t.Controller.Filter(TaskFilter.Closed);
        t.Window.Dismiss();
        t.Controller.Open(Rect.Empty);
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
        t.Controller.Open(Rect.Empty);

        Click(LineOf(t.Window, id));

        Assert.Equal([id], t.WentTo);
        // A page of its own: it stays open behind the switch.
        Assert.True(t.Window.IsOpen);
        Assert.Equal("old", TaskViews.SessionToGoTo(t.Tasks.Board.Tasks.Single(x => x.Id == id)).Value.Id);
    });

    // Petre: "can i have a separate page for all tasks?". A regular window: a title bar saying Tasks, resizable, in
    // Alt+Tab and on the taskbar, not topmost.
    [Fact]
    public void It_is_a_regular_window() => StaThread.Run(() =>
    {
        using var t = Build();

        Assert.Equal("Tasks", t.Window.Title);
        Assert.Equal(WindowStyle.SingleBorderWindow, t.Window.WindowStyle);
        Assert.Equal(ResizeMode.CanResize, t.Window.ResizeMode);
        Assert.True(t.Window.ShowInTaskbar);
        Assert.False(t.Window.Topmost);
    });

    [Fact]
    public void A_click_into_another_app_leaves_it_open() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Controller.Open(Rect.Empty);

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
        t.Controller.Open(Rect.Empty);
        t.Controller.Filter(TaskFilter.Closed);

        t.Controller.Open(Rect.Empty);

        Assert.Equal([closed], t.Window.Keys);
    });

    // Closing it (the title bar's X) hides it, ready to come back, and remembers where it was for next time, across
    // restarts (AppState.TasksWindow).
    [Fact]
    public void Closing_it_hides_it_and_remembers_its_place() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Controller.Open(Rect.Empty);

        t.Window.Close();

        Assert.False(t.Window.IsOpen);
        Assert.False(t.Window.IsVisible);
        var place = t.Harness.Manager.State.TasksWindow;
        Assert.NotNull(place);
        Assert.True(place.Width > 0 && place.Height > 0);
        Assert.False(place.Maximized);

        // And it opens again.
        t.Controller.Open(Rect.Empty);
        Assert.True(t.Window.IsOpen);
    });

    [Fact]
    public void A_refused_go_says_why_and_stays_open() => StaThread.Run(() =>
    {
        using var t = Build(_ => Result.Failure("That workspace has no desktop."));
        var id = t.Tasks.AddNote("note", t.Gepha).Value.Id;
        t.Controller.Open(Rect.Empty);

        Click(LineOf(t.Window, id));

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
        t.Controller.Open(Rect.Empty);

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
        t.Controller.Open(Rect.Empty);
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
        t.Controller.Open(Rect.Empty);
        var before = t.Window.Rebuilds;

        // A refresh with the board as it was (the workspaces' StateChanged pulses for many reasons) redraws nothing;
        // a rename does.
        t.Controller.Refresh();
        Assert.Equal(before, t.Window.Rebuilds);

        t.Tasks.Rename(id, "renamed");
        Assert.Equal(before + 1, t.Window.Rebuilds);
    });

    // FIX ROUND 1, Critical 1: activating a window on another desktop makes Windows follow it there
    // (WorkspaceManager's own note), so "+N more" on a window left behind on another desktop dragged Petre back to it.
    // The window never activates itself: opening it shows it without activation, bringing it forward only restores it,
    // and the App's seam moves it to the desktop in front and only then activates it. Both paths go through the seam.
    [Fact]
    public void The_window_never_activates_itself_and_both_paths_go_through_the_seam() => StaThread.Run(() =>
    {
        var forward = new List<nint>();
        using var t = Build(bringForward: forward.Add);

        t.Controller.Open(Rect.Empty);
        Assert.False(t.Window.ShowActivated);
        Assert.Equal([t.Controller.EnsureHandle()], forward);

        t.Controller.Open(Rect.Empty);
        Assert.Equal(2, forward.Count);
        Assert.Equal(0, t.Window.SelfActivations);
    });

    // The App's seam, in its order: move to the desktop in front, then activate. A failed move activates only a window
    // already on the desktop in front; one elsewhere is traced and left where it is.
    [Fact]
    public void Arriving_moves_first_then_activates() => StaThread.Run(() =>
    {
        var steps = new List<string>();
        Assert.True(TasksBoard.Arrive(0x42, () => { steps.Add("move"); return Result.Success(); }, () => { steps.Add("ask"); return false; },
            () => steps.Add("activate"), trace => steps.Add("trace")));
        Assert.Equal(["move", "activate"], steps);

        steps.Clear();
        Assert.True(TasksBoard.Arrive(0x42, () => { steps.Add("move"); return Result.Failure("refused"); }, () => { steps.Add("ask"); return true; },
            () => steps.Add("activate"), trace => steps.Add("trace")));
        Assert.Equal(["move", "trace", "ask", "activate"], steps);

        steps.Clear();
        Assert.False(TasksBoard.Arrive(0x42, () => { steps.Add("move"); return Result.Failure("refused"); }, () => { steps.Add("ask"); return false; },
            () => steps.Add("activate"), trace => steps.Add("trace")));
        Assert.Equal(["move", "trace", "ask", "trace"], steps);
    });

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
        t.Controller.Open(Rect.Empty);

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
        PressAndClick(LineOf(t.Window, first), clickCount: 2);
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
        t.Controller.Open(Rect.Empty);
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

    // FIX ROUND 1, Important 3: the remembered place is device pixels, and the window is put there in device pixels
    // (SetWindowPos on the hidden handle before Show), not through Left/Top, which WPF reads at the DPI of the screen it
    // was created on. On the screen that holds it, the window's own rectangle is exactly the one remembered.
    [Fact]
    public void A_remembered_place_comes_back_in_device_pixels() => StaThread.Run(() =>
    {
        using var t = Build();
        var place = new Deskbar.Core.Persistence.TasksWindowState(120, 90, 900, 640, Maximized: false);
        Assert.True(t.Harness.Manager.SaveTasksWindow(place).IsSuccess);

        t.Controller.Open(Rect.Empty);

        Assert.Equal(place, t.Window.CurrentPlace());
    });

    // The window's Undo follows the bar's: a Done task closed from its line comes back Done.
    [Fact]
    public void Undo_brings_a_done_task_back_done() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.Create("merged", t.Gepha, Session("s1")).Value.Id;
        t.Tasks.MarkDone(id);
        t.Controller.Open(Rect.Empty);

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
        t.Controller.Open(Rect.Empty);
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
        t.Controller.Open(Rect.Empty);
        var rebuilds = t.Window.Rebuilds;

        Click(LineOf(t.Window, first));
        Assert.True(selection.IsSelected(first));
        Assert.True(t.Window.IsSelected(first));

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

        t.Controller.Open(Rect.Empty);

        Assert.True(t.Window.ScrollOffset > 0, $"offset {t.Window.ScrollOffset}");
        Assert.True(t.Window.IsSelected(oldest));
    });

    // Review I-2: restoring a minimized window or showing it maximized activates it, so neither may run before the
    // seam has moved the window to the desktop in front. The seam sees the window as it was; a remembered maximize
    // comes after.
    [Fact]
    public void A_minimized_or_maximized_window_changes_no_state_before_the_move() => StaThread.Run(() =>
    {
        var seen = new List<WindowState>();
        TasksWindow? window = null;
        using var t = Build(bringForward: _ => seen.Add(window!.WindowState));
        window = t.Window;
        Assert.True(t.Harness.Manager.SaveTasksWindow(new Deskbar.Core.Persistence.TasksWindowState(120, 90, 900, 640, Maximized: true)).IsSuccess);

        t.Controller.Open(Rect.Empty);
        Assert.Equal([WindowState.Normal], seen);
        Assert.Equal(WindowState.Maximized, t.Window.WindowState);

        t.Window.WindowState = WindowState.Minimized;
        t.Controller.Open(Rect.Empty);
        Assert.Equal(WindowState.Minimized, seen[1]);
        // The seam here is a fake; the App's activator restores an iconic window after the move.
        Assert.Equal(WindowState.Minimized, t.Window.WindowState);
    });

    // Review minor 1 of 187cf4a: showing a window maximized activates it, so a window the seam left on its own desktop
    // (not activated) is not maximized there, which would take Petre to it. It stays due: the next arrival that does
    // activate it maximizes it.
    [Fact]
    public void A_window_left_on_its_own_desktop_is_not_maximized_there() => StaThread.Run(() =>
    {
        var activates = false;
        using var t = Build(activates: () => activates);
        Assert.True(t.Harness.Manager.SaveTasksWindow(new Deskbar.Core.Persistence.TasksWindowState(120, 90, 900, 640, Maximized: true)).IsSuccess);

        t.Controller.Open(Rect.Empty);
        Assert.Equal(WindowState.Normal, t.Window.WindowState);

        activates = true;
        t.Controller.Open(Rect.Empty);
        Assert.Equal(WindowState.Maximized, t.Window.WindowState);
    });

    // Review I-3: at the window's minimum width, with enough lines for the scroll bar, every column stays inside the
    // list's viewport and the title keeps a usable width.
    [Fact]
    public void At_the_minimum_width_the_columns_stay_inside_the_viewport() => StaThread.Run(() =>
    {
        using var t = Build();
        Enumerable.Range(1, 40).ToList().ForEach(i => t.Tasks.AddNote($"a note with a fairly long title, number {i}", t.Gepha));
        t.Controller.Open(Rect.Empty);
        t.Window.Width = t.Window.MinWidth;
        t.Window.UpdateLayout();

        var scroller = Inside(t.Window).OfType<ScrollViewer>().First(s => s.Content is StackPanel);
        Assert.True(scroller.ComputedVerticalScrollBarVisibility == Visibility.Visible, "the scroll bar shows");
        t.Window.Keys.Take(5).Select(id => LineOf(t.Window, id)).ToList().ForEach(line =>
        {
            var more = ButtonNamed(line, "More");
            var bounds = more.TransformToAncestor(scroller).TransformBounds(new Rect(more.RenderSize));
            Assert.True(bounds.Right <= scroller.ViewportWidth + 0.5, $"More ends at {bounds.Right} in a viewport {scroller.ViewportWidth} wide");
            var title = Inside(line).OfType<TextBlock>().First(b => b.FontWeight == FontWeights.SemiBold);
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
        t.Controller.Open(Rect.Empty);
        now = now.AddMilliseconds(600);

        t.Tasks.SetStatusLine(id, "waiting for CI");
        Click(LineOf(t.Window, id));

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
        t.Controller.Open(Rect.Empty);
        t.Window.Width = t.Window.MinWidth;
        t.Window.UpdateLayout();

        var lines = t.Window.Keys.Select(id => LineOf(t.Window, id)).ToList();
        var chips = lines.Select(l => BoundsIn(t.Window, Inside(l).OfType<Border>().Single(b => b.Child is TextBlock && b.CornerRadius.TopLeft == 8))).ToList();
        var comments = lines.Select(l => BoundsIn(t.Window, ButtonNamed(l, "Comments"))).ToList();
        var mores = lines.Select(l => BoundsIn(t.Window, ButtonNamed(l, "More"))).ToList();
        Assert.True(chips.Max(b => b.Left) - chips.Min(b => b.Left) < 0.5, string.Join(", ", chips.Select(b => b.Left)));
        Assert.True(comments.Max(b => b.Left) - comments.Min(b => b.Left) < 0.5, string.Join(", ", comments.Select(b => b.Left)));
        Assert.True(mores.Max(b => b.Left) - mores.Min(b => b.Left) < 0.5, string.Join(", ", mores.Select(b => b.Left)));
        Assert.All(chips.Concat(comments).Concat(mores), b => Assert.True(b.Right <= ContentWidth(t.Window) + 0.5, $"{b} in {ContentWidth(t.Window)}"));
        // The icons explain themselves.
        Assert.Equal("Comments", ButtonNamed(lines[0], "Comments").ToolTip);
        Assert.Equal("Status, move, rename, delete", ButtonNamed(lines[0], "More").ToolTip);
    });

    // Petre's screenshot: the placeholder "Comment as you, in markdow…" was cut off and the box squeezed. A short
    // placeholder, the hint in the tooltip, the box across the thread, and nothing clipped at the minimum width.
    [Fact]
    public void The_comment_box_is_short_spoken_and_never_clips() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("note", t.Gepha).Value.Id;
        t.Controller.Open(Rect.Empty);
        t.Controller.ToggleThread(id);
        t.Window.Width = t.Window.MinWidth;
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

    // PlaceNear: clear of the bar, inside the work area (the model is commit 183d85e's PlaceUnder).
    [Fact]
    public void The_window_opens_below_a_bar_at_the_top() =>
        Assert.Equal(new Point(100, 304), TasksWindow.PlaceNear(new Rect(100, 0, 300, 300), new Rect(0, 0, 1920, 1040), new Size(720, 560)));

    [Fact]
    public void The_window_opens_above_a_bar_at_the_bottom() =>
        Assert.Equal(new Point(100, 1040 - 300 - 4 - 560), TasksWindow.PlaceNear(new Rect(100, 740, 300, 300), new Rect(0, 0, 1920, 1040), new Size(720, 560)));

    [Fact]
    public void The_window_opens_beside_a_bar_docked_down_an_edge() =>
        Assert.Equal(new Point(1920 - 320 - 4 - 720, 0), TasksWindow.PlaceNear(new Rect(1600, 0, 320, 1040), new Rect(0, 0, 1600, 1040), new Size(720, 560)));

    [Fact]
    public void The_window_stays_inside_the_work_area_on_the_right() =>
        Assert.Equal(new Point(1920 - 720, 304), TasksWindow.PlaceNear(new Rect(1700, 0, 200, 300), new Rect(0, 0, 1920, 1040), new Size(720, 560)));

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

        t.Controller.Open(Rect.Empty);

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
        t.Controller.Open(Rect.Empty);
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
        t.Controller.Open(Rect.Empty);
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

    // A press that does not travel is a click, and waiting lines cannot be dragged; nothing drops above them.
    [Fact]
    public void A_window_press_in_place_is_a_click_and_waiting_lines_stay_pinned() => StaThread.Run(() =>
    {
        using var t = Build();
        var a = t.Tasks.AddNote("a", t.Gepha).Value.Id;
        var b = t.Tasks.AddNote("b", t.Gepha).Value.Id;
        var w = t.Tasks.AddNote("w", t.Gepha).Value.Id;
        t.Tasks.SetStatus(w, TaskStatus.WaitingOnYou);
        t.Window.DragMouse = ArrangeMouse.Held;
        t.Controller.Open(Rect.Empty);
        t.Window.UpdateLayout();
        var before = t.Tasks.Board;

        var at = InLine(t, b, 0.5);
        t.Window.DragPress(b, at);
        t.Window.DragMove(new Point(at.X + 1, at.Y + 1));
        Assert.False(t.Window.Dragging);
        t.Window.DragRelease();
        var waitingAt = InLine(t, w, 0.5);
        t.Window.DragPress(w, waitingAt);
        t.Window.DragMove(new Point(waitingAt.X, waitingAt.Y + 30));
        Assert.False(t.Window.Dragging);
        t.Window.DragRelease();
        Assert.Same(before, t.Tasks.Board);

        WindowDrag(t, a, InLine(t, w, 0.2));
        Assert.Equal([w, a, b], t.Tasks.Board.Pile().Select(x => x.Id));
    });
}
