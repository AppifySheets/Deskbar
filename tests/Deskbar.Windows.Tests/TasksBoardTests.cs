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

    // Each read of the task clock is a minute later, so tasks are raised in the order the test makes them.
    static Board Build(Func<TaskItem, Result>? goTo = null)
    {
        var harness = Harness.Build();
        var minutes = 0;
        var timers = new FakeTimers();
        var went = new List<Guid>();
        var tasks = new TaskService(new MemoryStore(), () => Start.AddMinutes(minutes++));
        var window = new TasksWindow { Quiet = true };
        var controller = new TasksBoard(window, tasks, harness.Manager, goTo ?? (t =>
        {
            went.Add(t.Id);
            return Result.Success();
        }), undoTimer: timers.Start, now: () => Start.AddHours(1));
        return new Board { Harness = harness, Window = window, Controller = controller, Tasks = tasks, Timers = timers, WentTo = went };
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

    [Fact]
    public void The_board_groups_open_tasks_by_status() => StaThread.Run(() =>
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

        Assert.Equal(["Waiting on you", "Done", "In progress", "Backlog"], t.Window.Headers);
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
        Assert.Equal(["In progress"], t.Window.Headers);
        // Closed is not a status to set: Close is the way.
        Assert.True(t.Controller.SetStatus(note, TaskStatus.Closed).IsFailure);

        // The tick closes it at once, and the line stays, struck through with Undo, for the undo window.
        Click(ButtonNamed(LineOf(t.Window, note), "Close"));
        Assert.Equal(TaskStatus.Closed, t.Tasks.Board.Tasks.Single(x => x.Id == note).Status);
        Assert.Equal([note], t.Window.Keys);
        Assert.True(t.Window.IsClosing(note));

        // Undo brings it back as it was.
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
}
