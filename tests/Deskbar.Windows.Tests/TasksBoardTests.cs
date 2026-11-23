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
        var window = new TasksWindow
        {
            Quiet = true, ClickClock = clickClock ?? (() => ClickStart.AddSeconds(seconds++)), Now = now ?? (() => Start.AddHours(1)),
        };
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

        DoubleClick(t.Window, id); // a double-click goes (Ruling 54)

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

    // The App's seam, in its order: move to the desktop in front, raise to the top of the z-order, then activate
    // ("tasks should become a top window, now it's behind somewhere"). A failed move raises and activates only a window
    // already on the desktop in front; one elsewhere is traced and left where it is. Every arrival is traced, so a live
    // check can read what happened.
    [Fact]
    public void Arriving_moves_first_then_raises_then_activates() => StaThread.Run(() =>
    {
        var steps = new List<string>();
        Assert.True(TasksBoard.Arrive(0x42, () => { steps.Add("move"); return Result.Success(); }, () => { steps.Add("ask"); return false; },
            () => steps.Add("raise"), () => steps.Add("activate"), trace => steps.Add("trace")));
        Assert.Equal(["move", "raise", "activate", "trace"], steps);

        steps.Clear();
        Assert.True(TasksBoard.Arrive(0x42, () => { steps.Add("move"); return Result.Failure("refused"); }, () => { steps.Add("ask"); return true; },
            () => steps.Add("raise"), () => steps.Add("activate"), trace => steps.Add("trace")));
        Assert.Equal(["move", "trace", "ask", "raise", "activate", "trace"], steps);

        steps.Clear();
        Assert.False(TasksBoard.Arrive(0x42, () => { steps.Add("move"); return Result.Failure("refused"); }, () => { steps.Add("ask"); return false; },
            () => steps.Add("raise"), () => steps.Add("activate"), trace => steps.Add("trace")));
        Assert.Equal(["move", "trace", "ask", "trace"], steps);
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

        t.Controller.Open(Rect.Empty);

        Assert.Equal([false], activeAtSeam);
        Assert.Same(t.Window.Toolbar, FocusManager.GetFocusedElement(t.Window));
    });

    // An arrival the seam could not complete (the window left on another desktop) leaves the keyboard alone: focusing
    // the window would activate it, and Windows would follow it to that desktop.
    [Fact]
    public void A_window_left_on_another_desktop_is_not_focused() => StaThread.Run(() =>
    {
        using var t = Build(activates: () => false);

        t.Controller.Open(Rect.Empty);

        Assert.False(t.Window.IsActive);
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

    // (c) "give it a proper icon in the workspaces so i can take it to the correct place": the window is listed on the bar
    // like any app window, and the bar draws it by the icon the window answers WM_GETICON with (IconCache's own-icon
    // source). That icon is the tasks checklist, green, not Deskbar's.
    [Fact]
    public void The_window_wears_its_own_checklist_icon_on_the_bar() => StaThread.Run(() =>
    {
        using var t = Build();

        t.Controller.Open(Rect.Empty);

        Assert.Same(TasksIcon.Image, t.Window.Icon);
        var drawn = Assert.IsAssignableFrom<System.Windows.Media.Imaging.BitmapSource>(
            IconCache.For(new Deskbar.Core.Domain.WindowHandle(t.Controller.EnsureHandle()), null));
        var (r, g, b) = AverageOpaque(drawn);
        Assert.True(g > r + 40 && g > b + 20, $"the bar's icon averages R={r} G={g} B={b}, not the checklist's green");
    });

    // (b) "maybe even tile it": the tasks window takes part in tiling like any app window. Deskbar keeps its own windows
    // out of the grid (the bar, Manage, its dialogs), and the tasks window is the one exception: the tiling surface's
    // "ours" leaves it out, while any other window of this process stays out of the grid.
    [Fact]
    public void The_tasks_window_tiles_and_the_apps_other_windows_still_do_not() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Controller.Open(Rect.Empty);
        var tasksWindow = t.Controller.EnsureHandle();
        var other = new Window { Width = 200, Height = 200, Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false };
        other.Show();
        try
        {
            var surface = new Deskbar.Windows.Tiling.TileSurface(w => TasksBoard.KeptFromTiling(w.Value, tasksWindow));

            Assert.True(surface.CanTile(new Deskbar.Core.Domain.WindowHandle(tasksWindow)));
            Assert.False(surface.CanTile(new Deskbar.Core.Domain.WindowHandle(new System.Windows.Interop.WindowInteropHelper(other).Handle)));
        }
        finally
        {
            other.Close();
        }
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
    //
    // The place is sized in DIPs at the test screen's own scale, so it clears the window's minimum (560 by 260 DIP) on
    // any screen. It was a fixed 900 by 640 px, which is 600 DIP wide at 150% but only 400 at 225%: on Petre's machine,
    // once its 3840 by 2160 screen went to 216 DPI, WPF rightly held the window at its 560 DIP minimum (1260 px) and
    // the test failed for a reason that was the test's, not the window's (measured; task-12-report.md, fix round 4).
    [Fact]
    public void A_remembered_place_comes_back_in_device_pixels() => StaThread.Run(() =>
    {
        using var t = Build();
        var scale = TasksWindow.PrimaryScale();
        var place = new Deskbar.Core.Persistence.TasksWindowState(120, 90, (int)(700 * scale), (int)(480 * scale), Maximized: false);
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
        Assert.Equal("Edit, status, move, delete", ButtonNamed(lines[0], "More").ToolTip);
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
        t.Controller.Open(Rect.Empty);
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

        t.Controller.Open(Rect.Empty);

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
        t.Controller.Open(Rect.Empty);
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
        t.Controller.Open(Rect.Empty);
        t.Window.UpdateLayout();
        Assert.Equal([w, c, b, a], t.Window.Keys);

        WindowDrag(t, a, InLine(t, w, 0.5));

        Assert.False(t.Tasks.Board.Tasks.Single(x => x.Id == a).Urgent);
        Assert.Equal([w, c, a, b], t.Tasks.Board.Pile().Select(x => x.Id));
    });

    // Review of 4fc4441..a1acd60, item 1: at the window's minimum width the Matrix left a line's title 9.8 DIP (80 at
    // 720). With a cell long enough for its own scroll bar, every column stays inside the cell and the title keeps a
    // usable width, as the List's I-3 test asks.
    [Fact]
    public void At_the_minimum_width_the_matrix_keeps_room_for_titles() => StaThread.Run(() =>
    {
        using var t = Build();
        Enumerable.Range(1, 30).ToList().ForEach(i => t.Tasks.AddNote($"a note with a fairly long title, number {i}", t.Gepha));
        Assert.True(t.Harness.Manager.SetTasksMatrix(true).IsSuccess);
        t.Controller.Open(Rect.Empty);
        t.Window.Width = t.Window.MinWidth;
        t.Window.UpdateLayout();

        var line0 = LineOf(t.Window, t.Window.Keys[0]);
        var scroller = Inside(t.Window).OfType<ScrollViewer>().First(s => s.Content is StackPanel panel && panel.Children.Contains(line0));
        Assert.True(scroller.ComputedVerticalScrollBarVisibility == Visibility.Visible, "the cell's scroll bar shows");
        t.Window.Keys.Take(5).Select(id => LineOf(t.Window, id)).ToList().ForEach(line =>
        {
            var more = ButtonNamed(line, "More");
            var bounds = more.TransformToAncestor(scroller).TransformBounds(new Rect(more.RenderSize));
            Assert.True(bounds.Right <= scroller.ViewportWidth + 0.5, $"More ends at {bounds.Right} in a viewport {scroller.ViewportWidth} wide");
            var title = Inside(line).OfType<TextBlock>().First(b => b.FontWeight == FontWeights.SemiBold);
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
        t.Controller.Open(Rect.Empty);
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
        t.Controller.Open(Rect.Empty);
        t.Window.UpdateLayout();
        Assert.Equal([w, c, b, a], t.Window.Keys);

        var start = InLine(t, a, 0.5);
        t.Window.DragPress(a, start);
        t.Window.DragMove(new Point(start.X, start.Y - 8));
        t.Window.DragMove(InLine(t, w, 0.5));

        Assert.Equal(InLine(t, b, 0).Y, t.Window.DropLineY!.Value, 1);
        t.Window.DragRelease();
    });

    // --- Review of the fix round, I-1: the Matrix gives the window back its own width and stays on screen -----------

    // A place wider than the List's minimum (560 DIP) and narrower than the Matrix's (780), in device pixels at the
    // test screen's own scale, on that screen's work area.
    static Deskbar.Core.Persistence.TasksWindowState Narrow(Rect work, bool flushRight)
    {
        var scale = TasksWindow.PrimaryScale();
        var (width, height) = ((int)(660 * scale), (int)(480 * scale));
        return new(flushRight ? (int)work.Right - width : (int)work.Left + 40, (int)work.Top + 40, width, height, Maximized: false);
    }

    [Fact]
    public void List_then_matrix_then_list_gives_the_window_back_its_width() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Tasks.AddNote("a", t.Gepha);
        var narrow = Narrow(TasksWindow.PrimaryWorkArea(), flushRight: false);
        Assert.True(t.Harness.Manager.SaveTasksWindow(narrow).IsSuccess);
        t.Controller.Open(Rect.Empty);
        Assert.Equal(narrow.Width, (int)t.Window.DeviceRect.Width);

        Click(ButtonNamed(t.Window, "Matrix"));
        t.Window.UpdateLayout();
        Assert.True(t.Window.DeviceRect.Width > narrow.Width, $"the Matrix grew the window to {t.Window.DeviceRect.Width}");
        // While the Matrix has it, the place to remember is still the List's.
        Assert.Equal(narrow, t.Window.CurrentPlace());

        Click(ButtonNamed(t.Window, "List"));
        t.Window.UpdateLayout();
        Assert.Equal(narrow.Width, (int)t.Window.DeviceRect.Width);

        t.Controller.Dismiss();
        Assert.Equal(narrow, t.Harness.Manager.State.TasksWindow);
    });

    [Fact]
    public void A_window_flush_with_the_right_edge_stays_on_screen_in_the_matrix() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Tasks.AddNote("a", t.Gepha);
        var work = TasksWindow.PrimaryWorkArea();
        var narrow = Narrow(work, flushRight: true);
        Assert.True(t.Harness.Manager.SaveTasksWindow(narrow).IsSuccess);
        t.Controller.Open(Rect.Empty);

        Click(ButtonNamed(t.Window, "Matrix"));
        t.Window.UpdateLayout();

        var rect = t.Window.DeviceRect;
        Assert.True(rect.Width > narrow.Width, $"grown to {rect.Width}");
        Assert.True(rect.Right <= work.Right + 0.5 && rect.Left >= work.Left - 0.5, $"window {rect} in a work area {work}");

        Click(ButtonNamed(t.Window, "List"));
        t.Window.UpdateLayout();
        Assert.Equal(new Rect(narrow.Left, narrow.Top, narrow.Width, narrow.Height), t.Window.DeviceRect);
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
        t.Controller.Open(Rect.Empty);
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
        t.Controller.Open(Rect.Empty);
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
        t.Controller.Open(Rect.Empty);
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
        t.Controller.Open(Rect.Empty);
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
        t.Controller.Open(Rect.Empty);
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
        t.Controller.Open(Rect.Empty);

        var menu = t.Window.OpenMenuFor(id);
        var close = menu.Items.OfType<MenuItem>().Single(i => i.Header is TextBlock { Text: "Close" });
        close.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.IsOpen = false;

        Assert.Equal(TaskStatus.Closed, t.Tasks.Board.Tasks.Single().Status);
    });

    // Ruling 42 (Petre: "don't say claude; mark with claude at the beginning with the" orange edge): a Claude line has
    // no CL badge in the window either; its mark is the orange edge at its start, and its title begins where the badge
    // was.
    [Fact]
    public void A_claude_line_is_marked_by_its_edge_not_a_badge() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.Create("Tasks redesign", t.Gepha, Session("s1")).Value.Id;
        t.Controller.Open(Rect.Empty);

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
        t.Controller.Open(Rect.Empty);
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
        t.Controller.Open(Rect.Empty);

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
        t.Controller.Open(Rect.Empty);
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
        t.Controller.Open(Rect.Empty);
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
        t.Controller.Open(Rect.Empty);
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

        t.Controller.ShowTask(id, Rect.Empty);

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
        t.Controller.Open(Rect.Empty);
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
        t.Controller.Open(Rect.Empty);

        Assert.Contains(Inside(LineOf(t.Window, working)).OfType<Button>(), b => AutomationProperties.GetName(b) == "Close");
        Assert.Contains(Inside(LineOf(t.Window, done)).OfType<Button>(), b => AutomationProperties.GetName(b) == "Close");
        Assert.DoesNotContain(Inside(LineOf(t.Window, done)).OfType<TextBlock>(), b => b.Text == "✕");
        Click(ButtonNamed(LineOf(t.Window, done), "Close"));
        Assert.Equal(TaskStatus.Closed, t.Tasks.Board.Tasks.Single(x => x.Id == done).Status);
    });

    // F2 on the selected line opens it too; Enter saves through Rename; Esc cancels.
    [Fact]
    public void F2_edits_the_selected_line_Enter_saves_and_Esc_cancels() => StaThread.Run(() =>
    {
        var selection = new TaskSelection();
        using var t = Build(selection: selection);
        var id = t.Tasks.AddNote("iam.ge: transcripts", t.Gepha).Value.Id;
        t.Controller.Open(Rect.Empty);
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
        t.Controller.Open(Rect.Empty);

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
        t.Controller.Open(Rect.Empty);
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
        t.Controller.Open(Rect.Empty);

        Click(ButtonNamed(t.Window, "Workspace filter"));

        var menu = Assert.IsAssignableFrom<ContextMenu>(t.Window.OpenedMenu);
        Assert.True(menu.IsOpen);
        Click(WorkspaceChipMenu.Chips(menu).Single(c => AutomationProperties.GetName(c) == "Sparrow"));
        Assert.Equal("Workspace: Sparrow", t.Window.WorkspaceFilterLabel);
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
        t.Controller.Open(Rect.Empty);
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
        t.Controller.Open(Rect.Empty);
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
        t.Controller.Open(Rect.Empty);
        selection.Select(id);

        Assert.True(t.Controller.Close(id).IsSuccess);
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
        t.Controller.Open(Rect.Empty);

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

        t.Controller.Open(Rect.Empty);

        Assert.True(t.Window.CellScroller(Quadrant.IV).VerticalOffset > 0, $"offset {t.Window.CellScroller(Quadrant.IV).VerticalOffset}");
    });
}
