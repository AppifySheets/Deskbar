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

        // The task lines in sight, in order: the buttons that carry a TaskLine and that the viewer shows. Every line is
        // drawn and the ones past the viewer's height scroll (the bar's THE LIST SCROLLS), so a line drawn but scrolled
        // away is not "on show".
        public IReadOnlyList<Button> Lines
        {
            get
            {
                var inSight = Bar.TaskLinesInSight.Select(l => l.Id).ToHashSet();
                return Drawn.Where(b => inSight.Contains(((TaskLine)b.Tag).Id)).ToList();
            }
        }

        // Every task line drawn, in sight or scrolled away.
        public IReadOnlyList<Button> Drawn => Inside(List).OfType<Button>().Where(b => b.Tag is TaskLine).ToList();

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
        Func<TaskItem, Maybe<string>>? goalTitle = null, IObservable<System.Reactive.Unit>? goalChanges = null,
        Func<Guid, ContextMenu?>? startClaude = null, IObservable<bool>? panelShown = null, string? stateDir = null,
        Action<Guid>? openParent = null, Action? openManager = null, Func<ManagerAsk?>? managerAsks = null,
        Func<TaskItem, bool>? opensClaudeSession = null)
    {
        var harness = Harness.Build();
        var minutes = 0;
        var seconds = 0;
        var timers = new FakeTimers();
        var service = tasks ?? new TaskService(new MemoryStore(), () => Start.AddMinutes(minutes++));
#pragma warning disable WPF0001 // ThemeMode is experimental: the app runs under ThemeMode="System" (App.xaml), so the bar tests do too
        var bar = new FloatingBar(harness.Manager)
        {
            ThemeMode = ThemeMode.System,
#pragma warning restore WPF0001
            Left = -32000,
            Top = -32000,
            SendShellKeys = _ => { },
            PressSpace = () => { },
            Tasks = service,
            GoToTask = goTo ?? (_ => Result.Success()),
            ToggleTasksPanel = openWindow,
            OpenManager = openManager,
            ManagerAsks = managerAsks,
            TasksPanelShown = panelShown,
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
            GoalTitle = goalTitle ?? (_ => Maybe<string>.None),
            GoalChanges = goalChanges,
            StartClaude = startClaude,
            OpensClaudeSession = opensClaudeSession,
            OpenParentTask = openParent,
            // Pictures pasted into the add box are written here, never into the real profile.
            TaskStateDir = stateDir ?? Path.GetTempPath(),
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

    // Waiting on you sorts first, and on a floating bar (three lines) the block keeps ONE line (task 39f4cc8a,
    // FloatingWaitingShown): the newest question, then the group line holding the rest in the asking orange, so none is
    // out of sight without a sign, then the important task, all three in view.
    [Fact]
    public void Hidden_waiting_tasks_are_counted_in_orange() => StaThread.Run(() =>
    {
        using var t = Build();
        var important = t.Tasks.AddNote("important", t.Gepha).Value.Id;
        t.Tasks.SetFlag(important, TaskFlag.Important, true);
        Enumerable.Range(1, 5).ToList().ForEach(i =>
        {
            var made = t.Tasks.Create($"Task {i}", t.Gepha, Session($"s{i}")).Value;
            t.Tasks.Ask(made.Id, $"Question {i}?");
        });

        Assert.Equal(["Task 5", "important"], t.Lines.Select(TitleOf));
        var words = Inside(ButtonNamed(t.List, "More waiting tasks")).OfType<TextBlock>().Single();
        Assert.Equal("+4 more waiting on you", words.Text);
        Assert.Equal(Orange, ((SolidColorBrush)words.Foreground).Color);
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

    // Task 08cdb052: a session asks for a merge with the pull request as a markdown link. The bar's line is one click
    // target, so it shows the link's words, never the markdown written out; the tasks panel is where it is clicked.
    [Fact]
    public void A_questions_link_shows_as_its_words_on_the_bar() => StaThread.Run(() =>
    {
        using var t = Build();
        var task = t.Tasks.Create("Snooze", t.Gepha, Session("s1")).Value;
        t.Tasks.Ask(task.Id, "Merge [Deskbar #490](https://github.com/AppifySheets/Deskbar/pull/490): CI is green");

        var line = t.Lines.Single(l => ((TaskLine)l.Tag).Id == task.Id);
        Assert.Contains("Merge Deskbar #490: CI is green", TextOf(line));
        Assert.DoesNotContain("https://", TextOf(line));
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

        // Task 37cd7081: its session is still live (it is waiting on Petre), so the tick offers to stop Claude first.
        Assert.Equal(TaskStatus.WaitingOnYou, t.Tasks.Board.Tasks.Single().Status);
        var stop = Assert.IsAssignableFrom<ContextMenu>(t.Bar.StopClaudeMenu);
        var item = stop.Items.OfType<MenuItem>().Single(i => i.Header is TextBlock { Text: TaskViews.StopClaudeAndClose });
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        stop.IsOpen = false;
        PumpBar(t);

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

    // Petre, 2026-11-23: "i can't currently see statuses". A Done task's line carries the green tick in front of its title
    // (TaskStatusMarks.DoneTick); a note does not, and the tick goes once the line is closed (in its undo window).
    [Fact]
    public void A_done_task_has_the_done_tick_and_a_note_does_not() => StaThread.Run(() =>
    {
        using var t = Build();
        var note = t.Tasks.AddNote("A note", t.Gepha).Value.Id;
        var done = t.Tasks.Create("Merged", t.Gepha, Session("s1")).Value.Id;
        t.Tasks.MarkDone(done);
        static bool Ticked(Button line) => Inside(line).OfType<FrameworkElement>().Any(e => Equals(e.Tag, TaskStatusMarks.DoneTickTag));
        Button LineFor(Guid id) => t.Lines.Single(l => ((TaskLine)l.Tag).Id == id);

        Assert.True(Ticked(LineFor(done)));
        Assert.False(Ticked(LineFor(note)));
        // The title's text is unchanged: the tick is a picture, not a character.
        Assert.StartsWith("Merged", TextOf(LineFor(done)));

        Click(ButtonNamed(LineFor(done), "Tick off"));
        Assert.False(Ticked(LineFor(done)));
    });

    // Task 8470d176, Petre's pick A ("ring fills on arrival"): a Done task whose pull request merged but is not in the
    // running Deskbar has a hollow ring in place of the disc, and its tooltip says so; once GitHub says the running build
    // holds the merge, the disc is back.
    [Fact]
    public void A_done_change_not_running_yet_has_the_ring_until_it_runs() => StaThread.Run(() =>
    {
        using var t = Build();
        var done = t.Tasks.Create("Subtask tick in thread", t.Gepha, Session("s1")).Value.Id;
        var pr = new PullRequestLink("AppifySheets", "Deskbar", 573);
        t.Tasks.AddPullRequest(done, pr);
        t.Tasks.MarkDone(done);
        var build = new RunningBuild("AppifySheets", "Deskbar", "2.6.23", new string('a', 40));
        bool Has(string tag) => Inside(t.Lines.Single()).OfType<FrameworkElement>().Any(e => Equals(e.Tag, tag));

        // Nothing known yet: the full disc, as before.
        Assert.True(Has(TaskStatusMarks.DoneTickTag));

        t.Tasks.Arrived(build, new Dictionary<string, PullRequestArrival> { [pr.Key] = new(PullRequestState.Merged, false) });
        Assert.True(Has(TaskStatusMarks.ShippingTickTag));
        Assert.False(Has(TaskStatusMarks.DoneTickTag));
        Assert.Contains("not in this Deskbar (2.6.23) yet", (string)t.Lines.Single().ToolTip);

        t.Tasks.Arrived(build, new Dictionary<string, PullRequestArrival> { [pr.Key] = new(PullRequestState.Merged, true) });
        Assert.True(Has(TaskStatusMarks.DoneTickTag));
        Assert.False(Has(TaskStatusMarks.ShippingTickTag));
        Assert.Contains("Running in this Deskbar (2.6.23).", (string)t.Lines.Single().ToolTip);
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

    // Task ee75f046 (Petre: "adding a new task in the bar should highlight it when added", option A of the mockup): the
    // note just added becomes the selected task, taking the selection from the one picked before, and its line glows
    // (only while Windows' animations are on; TaskGlow). A note refused for an empty title selects nothing.
    [Fact]
    public void A_note_just_added_is_selected_and_glows() => StaThread.Run(() =>
    {
        using var t = Build();
        var earlier = t.Tasks.AddNote("Pay the hosting invoice", t.Gepha).Value.Id;
        t.Bar.Selection.Select(earlier);
        Assert.True(t.Bar.TaskLineSelected(earlier));
        Assert.False(t.Bar.TaskLineGlowing(earlier));

        ClickBox(t);
        t.Box.Text = "Call the accountant";
        Press(t.Bar, t.Box, Key.Enter);

        var added = t.Tasks.Board.Tasks.Single(x => x.Title == "Call the accountant").Id;
        Assert.True(t.Bar.Selection.IsSelected(added));
        Assert.True(t.Bar.TaskLineSelected(added));
        Assert.False(t.Bar.TaskLineSelected(earlier));
        Assert.True(t.Bar.TaskLineGlowing(added), "the new line glows, animations on or off");

        // Picking another line ends the glow: the selection's paint puts the plain fills back.
        t.Bar.Selection.Select(earlier);
        Assert.False(t.Bar.TaskLineGlowing(added));
        Assert.True(t.Bar.TaskLineSelected(earlier));

        // An empty title is refused: nothing is added and the selection stays where it was.
        ClickBox(t);
        t.Box.Text = "   ";
        Press(t.Bar, t.Box, Key.Enter);
        Assert.Equal(2, t.Tasks.Board.Tasks.Count);
        Assert.True(t.Bar.Selection.IsSelected(earlier));
    });

    // Live on v2.5.417 (Petre: "it kind of highlights it and then immediately loses focus"; the add's trace showed the
    // selection back on his terminal's task 230 ms after the note was selected). After Enter the bar hands the keyboard
    // back to the window he was typing in, and the App selects the task of a Claude terminal that comes to the front.
    // That foreground change is the bar's doing, so KeyboardHandedBackTo excuses it: once, for that window, and only
    // within two seconds. A window the bar did not hand the keyboard to is never excused.
    [Fact]
    public void The_keyboard_going_back_after_an_add_is_excused_once() => StaThread.Run(() =>
    {
        var now = ClickStart;
        var handedBack = new List<nint>();
        using var t = Build(handedBack: handedBack, clickClock: () => now);
        const nint other = 0x6262;

        ClickBox(t);
        t.Box.Text = "Call the accountant";
        Press(t.Bar, t.Box, Key.Enter);

        Assert.Equal([Editor], handedBack);
        Assert.False(t.Bar.KeyboardHandedBackTo(other));
        now = now.AddMilliseconds(230);
        Assert.True(t.Bar.KeyboardHandedBackTo(Editor));
        // Spent: the next time the editor comes to the front, it is Petre bringing it.
        Assert.False(t.Bar.KeyboardHandedBackTo(Editor));

        // A hand-back whose foreground event never comes (the window was in front already) lapses after two seconds.
        ClickBox(t);
        t.Box.Text = "Pay rent";
        Press(t.Bar, t.Box, Key.Enter);
        now = now.AddSeconds(3);
        Assert.False(t.Bar.KeyboardHandedBackTo(Editor));
    });

    // With Windows' animations off (Petre's machine), the glow is still there: the glow's blue held, then the selected
    // fill in one step. Either way the brush ends at the selected colour.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_glow_ends_as_the_selected_fill_with_or_without_animations(bool animate) => StaThread.Run(() =>
    {
        var selectedColour = Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF);
        var selected = new SolidColorBrush(selectedColour);
        selected.Freeze();

        // The brush a line gets is a glow (unfrozen, animated) whichever way it runs.
        var glow = TaskGlow.Fading(selected, animate);
        Assert.True(TaskGlow.IsGlow(glow));
        Assert.True(glow.HasAnimatedProperties);

        // Its colour at the start, halfway and at the end, read off the timeline as WPF would evaluate it.
        var timeline = TaskGlow.Timeline(selectedColour, animate);
        var clock = timeline.CreateClock();
        Color At(TimeSpan when)
        {
            clock.Controller!.SeekAlignedToLastTick(when, System.Windows.Media.Animation.TimeSeekOrigin.BeginTime);
            return (Color)timeline.GetCurrentValue(Colors.Transparent, Colors.Transparent, clock);
        }

        Assert.Equal(TaskGlow.StartColor, At(TimeSpan.Zero));
        var halfway = At(TaskGlow.Length / 2);
        // Faded: somewhere between the two. Off: still the glow's blue, no motion.
        if (animate) Assert.True(halfway != TaskGlow.StartColor && halfway != selectedColour, $"halfway {halfway}");
        else Assert.Equal(TaskGlow.StartColor, halfway);
        Assert.Equal(selectedColour, At(TaskGlow.Length));
    });

    // Petre: "it shouldn't land out of sight ... it should take me to that task". A plain note lands at the top of
    // quadrant IV, here under five important ones and so below the three lines in sight; the list scrolls to it (the
    // selection's scroll, THE LIST SCROLLS) and it is in sight, selected and glowing.
    [Fact]
    public void A_note_added_below_the_lines_in_sight_is_scrolled_to() => StaThread.Run(() =>
    {
        using var t = Build();
        Enumerable.Range(1, 5).ToList().ForEach(i => t.Tasks.AddNote($"Important {i}", t.Gepha, Quadrant.II));
        t.Bar.UpdateLayout();
        Assert.Equal(0, t.Bar.TaskLinesScroller!.VerticalOffset, 1);

        ClickBox(t);
        t.Box.Text = "Call the accountant";
        Press(t.Bar, t.Box, Key.Enter);
        t.Bar.UpdateLayout();
        PumpBar(t);
        t.Bar.UpdateLayout();

        var added = t.Tasks.Board.Tasks.Single(x => x.Title == "Call the accountant").Id;
        Assert.Contains(t.Lines, l => ((TaskLine)l.Tag).Id == added);
        Assert.True(t.Bar.TaskLineSelected(added));
        Assert.True(t.Bar.TaskLineGlowing(added), "the new line glows, animations on or off");
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

    // The tasks panel (plan 2026-11-23-tasks-panel, Task 6): "All tasks" toggles it, and while it is shown the button
    // looks on, a light fill like a picked line, so the line that opened the panel is the one that closes it. The App
    // says when the panel is shown (TasksPanelShown, the panel's Shown); the bar only draws it.
    [Fact]
    public void The_all_tasks_line_looks_on_while_the_panel_is_shown() => StaThread.Run(() =>
    {
        var shown = new System.Reactive.Subjects.BehaviorSubject<bool>(false);
        using var t = Build(openWindow: () => { }, panelShown: shown);
        var button = ButtonNamed(t.List, "More tasks");
        Assert.False(t.Bar.TaskListMoreOn);
        Assert.Equal("off", AutomationProperties.GetItemStatus(button));

        shown.OnNext(true);
        Assert.True(t.Bar.TaskListMoreOn);
        Assert.Equal("on", AutomationProperties.GetItemStatus(button));
        Assert.Equal("Hide the tasks panel", button.ToolTip);

        // A repaint of the list (a new task) keeps the look.
        t.Tasks.AddNote("One", Maybe<Guid>.None);
        Assert.True(t.Bar.TaskListMoreOn);

        shown.OnNext(false);
        Assert.False(t.Bar.TaskListMoreOn);
        Assert.NotEqual("Hide the tasks panel", button.ToolTip);
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

    // A note of several lines (task 98420dbc, Petre: "multiline should be allowed; longer text should increase the size
    // of the box"). The box wraps and takes line breaks, grows with its text, and Enter still adds: the first line is
    // the title and the rest the description.
    [Fact]
    public void A_note_of_several_lines_grows_the_box_and_is_a_title_and_a_description() => StaThread.Run(() =>
    {
        using var t = Build();
        Assert.True(t.Box.AcceptsReturn);
        Assert.Equal(TextWrapping.Wrap, t.Box.TextWrapping);
        ClickBox(t);
        // The box's height for a text. One line is the theme's minimum height (32 DIP, measured), so growth is read
        // against more lines, not against a multiple of it.
        double HeightFor(string text)
        {
            t.Box.Text = text;
            t.Bar.UpdateLayout();
            return t.Box.ActualHeight;
        }
        var lines = Enumerable.Range(1, 12).Select(n => $"line {n}").ToArray();
        var three = HeightFor(string.Join("\n", lines.Take(3)));
        var four = HeightFor(string.Join("\n", lines.Take(4)));
        var six = HeightFor(string.Join("\n", lines.Take(6)));
        var twelve = HeightFor(string.Join("\n", lines));
        Assert.True(three > HeightFor("one line"), $"three lines {three}");
        Assert.True(four > three, $"three lines {three}, four {four}");
        // Past six lines it stops growing and scrolls inside, so a long paste cannot push the task lines off the bar.
        Assert.Equal(six, twelve, 1);
        // A long line wraps rather than running out of sight, so it grows the box too.
        Assert.True(HeightFor(string.Join(" ", Enumerable.Repeat("a long title that keeps going", 20))) > HeightFor("short"));

        t.Box.Text = "Fix the strip\nit overlaps the taskbar\non the second screen";
        Press(t.Bar, t.Box, Key.Enter);

        var task = Assert.Single(t.Tasks.Board.Tasks);
        Assert.Equal("Fix the strip", task.Title);
        Assert.Equal("it overlaps the taskbar\non the second screen", task.Description);
        Assert.Equal("", t.Box.Text);
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
    // two - selected or nothing"). The row is ONE chip, with no "on" before it (task 47398c0f), the chosen target, with a small arrow: the current
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
    public void The_chip_row_is_one_chip_naming_the_current_workspace_with_no_on_before_it() => StaThread.Run(() =>
    {
        using var t = Build();

        // One chip, no "none" or "other…" beside it; the toggles are not chips.
        var chips = Inside(t.Chips).OfType<Button>().Where(b => b.Content is Border { Tag: "task-chip" }).ToList();
        Assert.Single(chips);
        Assert.Equal("GEPHA", ChipText(t));
        Assert.DoesNotContain(Inside(t.Chips).OfType<TextBlock>(), b => b.Text == "on");
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
        // Folded, the viewer has a ceiling too now: three lines, the rest scroll (THE LIST SCROLLS).
        Assert.False(double.IsPositiveInfinity(viewer.MaxHeight));

        Click(ButtonNamed(t.List, "More tasks"));
        t.Bar.UpdateLayout();

        Assert.Equal(12, t.Drawn.Count);
        Assert.Equal(8, t.Lines.Count);
        var eight = t.Drawn.Take(8).Sum(l => l.ActualHeight + l.Margin.Top + l.Margin.Bottom);
        Assert.Equal(eight, viewer.ViewportHeight, 1);
        Assert.True(viewer.ExtentHeight > viewer.ViewportHeight + 1, $"the rest scrolls: extent {viewer.ExtentHeight}, viewport {viewer.ViewportHeight}");

        // Folded again, the viewer is three lines tall and the other nine still scroll (THE LIST SCROLLS).
        Click(ButtonNamed(t.List, "More tasks"));
        t.Bar.UpdateLayout();
        Assert.Equal(3, t.Lines.Count);
        var three = t.Drawn.Take(3).Sum(l => l.ActualHeight + l.Margin.Top + l.Margin.Bottom);
        Assert.Equal(three, viewer.ViewportHeight, 1);
    });

    // THE LIST SCROLLS (Petre: "when there are more tasks than can fit in the bar, it should let me scroll"). Every line
    // is drawn, the viewer is as tall as the lines that fit, and "+N more" counts what is out of sight as the view moves:
    // scrolled to the end, the lines above are the ones out of sight.
    [Fact]
    public void The_lines_past_the_viewer_scroll_and_more_counts_what_is_out_of_sight() => StaThread.Run(() =>
    {
        using var t = Build();
        Enumerable.Range(1, 10).ToList().ForEach(i => t.Tasks.AddNote($"Note {i}", Maybe<Guid>.None));
        t.Bar.UpdateLayout();
        var viewer = t.Bar.TaskLinesScroller!;

        Assert.Equal(10, t.Drawn.Count);
        Assert.Equal(["Note 10", "Note 9", "Note 8"], t.Lines.Select(l => ((TaskLine)l.Tag).Title));
        Assert.Equal("+7 more", t.Bar.TaskListMoreText);

        viewer.ScrollToEnd();
        t.Bar.UpdateLayout();

        Assert.Equal(["Note 3", "Note 2", "Note 1"], t.Lines.Select(l => ((TaskLine)l.Tag).Title));
        Assert.Equal("+7 more", t.Bar.TaskListMoreText);

        // A repaint that changes a line's words keeps the place in the list.
        var offset = viewer.VerticalOffset;
        Assert.True(t.Tasks.Rename(t.Tasks.Board.Pile().Single(x => x.Title == "Note 2").Id, "Note two").IsSuccess);
        t.Bar.UpdateLayout();
        Assert.Equal(offset, viewer.VerticalOffset, 1);
        Assert.Contains("Note two", t.Lines.Select(l => ((TaskLine)l.Tag).Title));
    });

    // A task waiting on Petre scrolled above the view still says so, in "+N waiting": no ask is out of sight without a
    // sign (spec, "The bar's order").
    [Fact]
    public void A_waiting_task_scrolled_above_the_view_is_counted_as_waiting() => StaThread.Run(() =>
    {
        using var t = Build();
        Enumerable.Range(1, 6).ToList().ForEach(i => t.Tasks.AddNote($"Note {i}", Maybe<Guid>.None));
        var asked = t.Tasks.Create("Asks", t.Gepha, Session("s1")).Value;
        Assert.True(t.Tasks.Ask(asked.Id, "Merge it?").IsSuccess);
        t.Bar.UpdateLayout();
        Assert.Equal("Asks", ((TaskLine)t.Lines[0].Tag).Title);

        t.Bar.TaskLinesScroller!.ScrollToEnd();
        t.Bar.UpdateLayout();

        Assert.DoesNotContain(t.Lines, l => ((TaskLine)l.Tag).Id == asked.Id);
        // Seven open tasks less the lines in view. Counted rather than written out: how many fit depends on whether the
        // waiting line's "Asks · Merge it?" wraps, which follows the chip's width (2026-11-25: with the age gone from the
        // line its chip no longer shrinks to a dot, and the line wraps onto two).
        Assert.InRange(t.Lines.Count, 3, 4);
        Assert.Equal($"+{7 - t.Lines.Count} more, 1 waiting", t.Bar.TaskListMoreText);
    });

    // A line selected from elsewhere (a Claude terminal brought to the front selects its task, #477) that sits out of
    // sight is scrolled into view; a line already in view moves nothing.
    [Fact]
    public void A_line_selected_out_of_sight_is_scrolled_into_view() => StaThread.Run(() =>
    {
        using var t = Build();
        Enumerable.Range(1, 10).ToList().ForEach(i => t.Tasks.AddNote($"Note {i}", Maybe<Guid>.None));
        t.Bar.UpdateLayout();
        var viewer = t.Bar.TaskLinesScroller!;

        t.Bar.Selection.Select(t.Tasks.Board.Pile().Single(x => x.Title == "Note 9").Id);
        t.Bar.UpdateLayout();
        Assert.Equal(0, viewer.VerticalOffset, 1);

        var far = t.Tasks.Board.Pile().Single(x => x.Title == "Note 1").Id;
        t.Bar.Selection.Select(far);
        t.Bar.UpdateLayout();
        Assert.Contains(t.Lines, l => ((TaskLine)l.Tag).Id == far);
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
        // Four waiting and two more: on a floating bar one waiting line shows, then the group line holding the other
        // three (task 39f4cc8a), then Note 2; Note 1 is the one line out of sight. The group line is checked below too.
        Enumerable.Range(1, 2).ToList().ForEach(i => t.Tasks.AddNote($"Note {i}", gepha));
        Enumerable.Range(1, 4).ToList().ForEach(i => t.Tasks.Ask(t.Tasks.Create($"Ask {i}", gepha, Session($"s{i}")).Value.Id, "OK?"));
        t.Bar.ApplyWidth(176); // MinimumWidth at the default scale: (18 + 150 + 8) DIP
        t.Bar.RequestRebuild();
        ClickBox(t);
        t.Bar.UpdateLayout();

        Assert.Equal("+1 more", t.Bar.TaskListMoreText);
        Assert.True(ButtonNamed(t.List, "More waiting tasks").IsVisible);
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

    // NO AGE ON THE BAR (reverses Ruling 46; Petre, 2026-11-24, of the bar's "26m": "move the time from here to the proper
    // tasks window"). A line has no age text; its tooltip still says when the task was created. The tasks panel has the
    // age (TasksBoardTests.The_slim_lines_age_fits_at_its_widest_and_says_it_in_words).
    [Fact]
    public void A_line_has_no_age_and_its_tooltip_keeps_the_created_date() => StaThread.Run(() =>
    {
        using var t = Build(taskNow: () => Start.AddHours(2).AddMinutes(3));
        t.Tasks.AddNote("note", t.Gepha);

        var line = t.Lines.Single();
        Assert.DoesNotContain(Inside(line).OfType<TextBlock>(), b => Equals(b.Tag, "task-age"));
        Assert.DoesNotContain(Inside(line).OfType<TextBlock>(), b => b.Text is "2h3m" or "1h");
        Assert.Contains($"Created {Start.ToLocalTime():dddd d MMMM yyyy}", (string)line.ToolTip);
    });

    // With nothing between the title and the chip, the title runs up to the chip and keeps its share of the line at the
    // default docked width, and the chip still gives way to a dot when it must.
    [Fact]
    public void The_title_runs_up_to_the_chip_and_keeps_its_share() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Tasks.AddNote("Make the hero video for the home page", t.Gepha);
        t.Bar.ApplyWidth(158.4);
        t.Bar.RequestRebuild();
        t.Bar.UpdateLayout();

        var line = t.Lines.Single();
        var end = Inside(line).OfType<FrameworkElement>().Single(b => (Equals(b.Tag, "task-workspace") || Equals(b.Tag, "task-workspace-dot")) && b.IsVisible);
        var title = Inside(line).OfType<TextBlock>().Single(b => Equals(b.Tag, "task-text"));
        Rect In(FrameworkElement e) => e.TransformToAncestor(line).TransformBounds(new Rect(e.RenderSize));
        Assert.True(In(title).Right <= In(end).Left + 6, $"title {In(title)} then chip {In(end)}");
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

    // Petre, live on v2.5.412: "doesn't work, test tasks were not highlighted", adding from the bar of his docked bar (the
    // panel's add worked). On a docked bar the add's paint queues the fit pass (QueueTaskListFit), and the dispatcher
    // runs it as it would live, before the test looks: the new note must still end in sight, selected and glowing.
    [Fact]
    public void A_note_added_on_a_docked_bar_is_scrolled_to_and_glows() => StaThread.Run(() =>
    {
        using var t = Build(openWindow: () => { });
        Enumerable.Range(1, 30).ToList().ForEach(i => t.Tasks.AddNote($"Important {i}", t.Gepha, Quadrant.II));
        Dock(t, 600);
        Settle(t);
        Assert.Equal(0, t.Bar.TaskLinesScroller!.VerticalOffset, 1);

        ClickBox(t);
        t.Box.Text = "Call the accountant";
        Press(t.Bar, t.Box, Key.Enter);
        // Everything the dispatcher has queued runs, as it would live: the fit pass and any retry, then layout.
        PumpBar(t);
        t.Bar.UpdateLayout();
        PumpBar(t);
        t.Bar.UpdateLayout();

        var added = t.Tasks.Board.Tasks.Single(x => x.Title == "Call the accountant").Id;
        Assert.True(t.Bar.Selection.IsSelected(added));
        Assert.True(t.Bar.TaskLineSelected(added), "the new line is drawn as selected");
        Assert.Contains(t.Lines, l => ((TaskLine)l.Tag).Id == added);
        Assert.True(t.Bar.TaskLineGlowing(added), "the new line glows, animations on or off");
    });

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

    // A line's title run, which says its flags (Ruling 59): bold for important, italic for urgent.
    static System.Windows.Documents.Run TitleRunOf(Button line) =>
        Inside(line).OfType<TextBlock>().Single(b => Equals(b.Tag, "task-text")).Inlines.OfType<System.Windows.Documents.Run>()
            .First(r => r.Text == ((TaskLine)line.Tag).Title);

    static bool LooksImportant(Button line) => TitleRunOf(line).FontWeight == FontWeights.Bold;
    static bool LooksUrgent(Button line) => TitleRunOf(line).FontStyle == FontStyles.Italic;

    // No flag sign of any kind on a line (Petre, 2026-11-24: "no need for extra signs"): no Important or Urgent button,
    // no glyph tagged as a flag, and no quiet overlay over the title's end.
    static void HasNoFlagSigns(Button line)
    {
        Assert.False(HasButtonNamed(line, "Important"));
        Assert.False(HasButtonNamed(line, "Urgent"));
        Assert.DoesNotContain(Inside(line).OfType<FrameworkElement>(),
            e => e.Tag is "task-flag-on" or "task-flag-off" or "task-quiet-flags");
    }

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
        // A waiting task shows its flags though it sits in the pinned block: in its title, and with no sign.
        Assert.True(LooksImportant(t.Lines[0]));
        Assert.False(LooksUrgent(t.Lines[0]));
        Assert.True(LooksImportant(t.Lines[1]) && LooksUrgent(t.Lines[1]));
        t.Lines.ToList().ForEach(HasNoFlagSigns);
    });

    // No toggles on the line, hovered, selected or not (Petre, 2026-11-24: "important is bold; urgent is italic; no need
    // for extra signs"). The line's right-click menu sets a flag, its item ticked while the flag is on, and the task goes
    // to the top of its new quadrant with its title bold.
    [Fact]
    public void A_line_has_no_flag_toggles_and_its_menu_sets_the_flags() => StaThread.Run(() =>
    {
        using var t = Build();
        var first = t.Tasks.AddNote("first", t.Gepha).Value.Id;
        t.Tasks.AddNote("second", t.Gepha);
        Assert.Equal(["second", "first"], t.Lines.Select(TitleOf));
        t.Bar.Selection.Select(first);
        t.Lines.ToList().ForEach(HasNoFlagSigns);

        MenuItem Item(ContextMenu menu, string name) => menu.Items.OfType<MenuItem>().Single(i => ((TextBlock)i.Header).Text == name);
        var menu = t.Bar.OpenTaskLineMenu(first)!;
        Assert.False(Item(menu, "Important").IsChecked);
        Item(menu, "Important").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        // An open menu holds the list's paint back (TaskListMustWait). In the app the rebuild that follows the menu's close
        // paints the list; here the paint is asked for once the menu is shut.
        menu.IsOpen = false;
        t.Bar.PaintTaskList();

        Assert.True(t.Tasks.Board.Tasks.Single(x => x.Id == first).Important);
        Assert.Equal(["first", "second"], t.Lines.Select(TitleOf));
        Assert.True(LooksImportant(t.Lines[0]));
        HasNoFlagSigns(t.Lines[0]);

        var again = t.Bar.OpenTaskLineMenu(first)!;
        Assert.True(Item(again, "Important").IsChecked);
        again.IsOpen = false;
    });

    // Task 4b6cc9a9: a menu opened at the pointer with no placement target is created on the primary screen and keeps the
    // primary's scaling, so beside a 150% primary it was drawn one and a half times too large (OwnMenus.AtPointer). The
    // line's menu therefore names the bar as its target and still opens at the pointer.
    [Fact]
    public void A_line_menu_opens_at_the_pointer_with_the_bar_as_its_target() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.AddNote("first", t.Gepha).Value.Id;

        var menu = t.Bar.OpenTaskLineMenu(id)!;
        Assert.Same(t.Bar, menu.PlacementTarget);
        Assert.Equal(System.Windows.Controls.Primitives.PlacementMode.MousePoint, menu.Placement);
        menu.IsOpen = false;
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

    // Task eefb986a (Petre: "tasks marked as done should be in the waiting on me section"): a Done line joins the pinned
    // block under the questions, above every quadrant, whatever its flags, with no group break between the two.
    [Fact]
    public void A_done_line_sits_in_the_waiting_block_under_the_questions() => StaThread.Run(() =>
    {
        using var t = Build(openWindow: () => { });
        t.Bar.SizeToContent = SizeToContent.Manual;
        t.Bar.Height = 900;
        var done = t.Tasks.AddNote("done", t.Gepha).Value.Id;
        var asked = t.Tasks.AddNote("asked", t.Gepha).Value.Id;
        var urgent = t.Tasks.AddNote("urgent", t.Gepha).Value.Id;
        t.Tasks.SetFlag(urgent, TaskFlag.Urgent, true);
        t.Tasks.SetFlag(urgent, TaskFlag.Important, true);
        t.Tasks.SetStatus(done, Deskbar.Core.Tasks.TaskStatus.Done);
        t.Tasks.SetStatus(asked, Deskbar.Core.Tasks.TaskStatus.WaitingOnYou);
        Settle(t);

        Assert.Equal(["asked", "done", "urgent"], t.Lines.Select(TitleOf));
        Assert.Equal([false, false, true], t.Lines.Select(l => ((TaskLine)l.Tag).StartsGroup));
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

    // Task f41d7857 ("drag and drop of tasks waiting on me doesn't work ... it should"): a waiting line is dragged within
    // the Waiting on you block, and anywhere below the block it lands at the block's foot, since the block stays on top
    // until answered. Nothing else drops above the block (a drop on its edge lands at the top of the task's quadrant).
    [Fact]
    public void Waiting_lines_are_dragged_within_their_block_and_nothing_drops_above_it() => StaThread.Run(() =>
    {
        using var t = DragBar(4);
        var n4 = t.Tasks.Board.Tasks.Single(x => x.Title == "n4").Id;
        var n3 = t.Tasks.Board.Tasks.Single(x => x.Title == "n3").Id;
        t.Tasks.SetStatus(n4, Deskbar.Core.Tasks.TaskStatus.WaitingOnYou);
        t.Tasks.SetStatus(n3, Deskbar.Core.Tasks.TaskStatus.WaitingOnYou);
        Settle(t);
        Assert.Equal(["n3", "n4", "n2", "n1"], t.Lines.Select(TitleOf));

        // Within the block: n3 after n4, on n4's bottom edge.
        Drag(t, t.Lines[0], In(t, t.Lines[1], 0.9));
        Settle(t);
        Assert.Equal(["n4", "n3", "n2", "n1"], t.Lines.Select(TitleOf));

        // Below the block: n4 goes to its foot, not among the plain lines, and keeps its flags.
        Drag(t, t.Lines[0], In(t, t.Lines[3], 0.9));
        Settle(t);
        Assert.Equal(["n3", "n4", "n2", "n1"], t.Lines.Select(TitleOf));
        Assert.Equal(Deskbar.Core.Tasks.TaskStatus.WaitingOnYou, t.Tasks.Board.Tasks.Single(x => x.Id == n4).Status);

        // A plain line dropped on the block's top edge goes to the top of its own quadrant.
        Drag(t, t.Lines[3], In(t, t.Lines[0], 0.1));

        Assert.Equal(["n3", "n4", "n1", "n2"], t.Tasks.Board.Pile().Select(x => x.Title));
    });

    // Task f41d7857 ("neither moving them into each-other as subtasks. it should"): a waiting line takes a subtask
    // dropped on its middle, and draws it under itself in the pinned block; a waiting line dropped on a plain line's
    // middle becomes its subtask and stays in the pinned block, where a question is found.
    [Fact]
    public void Waiting_lines_nest_and_take_subtasks() => StaThread.Run(() =>
    {
        using var t = DragBar(3);
        var n3 = t.Tasks.Board.Tasks.Single(x => x.Title == "n3").Id;
        var n2 = t.Tasks.Board.Tasks.Single(x => x.Title == "n2").Id;
        t.Tasks.SetStatus(n3, Deskbar.Core.Tasks.TaskStatus.WaitingOnYou);
        t.Tasks.SetStatus(n2, Deskbar.Core.Tasks.TaskStatus.WaitingOnYou);
        Settle(t);
        Assert.Equal(["n2", "n3", "n1"], t.Lines.Select(TitleOf));

        // The plain n1 into the waiting n3: drawn right under it, in the block.
        Drag(t, t.Lines[2], In(t, t.Lines[1], 0.5));
        Settle(t);
        Assert.Equal(Maybe.From(n3), t.Tasks.Board.Tasks.Single(x => x.Title == "n1").ParentId);
        Assert.Equal(["n2", "n3", "n1"], t.Lines.Select(TitleOf));
        Assert.Equal("waiting", ((TaskLine)t.Lines[2].Tag).GroupKey);

        // The waiting n2 into the waiting n3 (whose subtask n1 is drawn under it): both under n3.
        Drag(t, t.Lines[0], In(t, t.Lines[1], 0.5));
        Settle(t);
        Assert.Equal(Maybe.From(n3), t.Tasks.Board.Tasks.Single(x => x.Id == n2).ParentId);
        Assert.Equal("n3", TitleOf(t.Lines[0]));
        Assert.Equal(["n1", "n2"], t.Lines.Skip(1).Select(TitleOf).OrderBy(x => x));
    });

    // Task 7378433e (Petre: "subtasks can only be moved into other tasks as subtasks; i should be able to move them as
    // non-subtasks"). A subtask whose parent is Claude's is on the bar without its parent, on a line of its own, so it
    // was never drawn under it and a drop beside a line only reordered it. Freed like any subtask now; over the middle of
    // its own line nothing happens, so a drag that comes back frees nothing.
    [Fact]
    public void A_subtask_whose_parent_is_off_the_bar_dragged_beside_a_line_is_freed() => StaThread.Run(() =>
    {
        using var t = DragBar(2);
        var parent = t.Tasks.AddNote("Claude's parent", t.Gepha).Value.Id;
        t.Tasks.Assign(parent, Assignee.Claude);
        var child = t.Tasks.AddNote("child", t.Gepha, parent: parent).Value.Id;
        Settle(t);
        Assert.DoesNotContain(parent, t.Lines.Select(l => ((TaskLine)l.Tag).Id));
        Button Line(Guid id) => t.Lines.Single(l => ((TaskLine)l.Tag).Id == id);
        Assert.Null(((TaskLine)Line(child).Tag).ShownIn);

        // Back onto its own line's middle: still a subtask.
        Drag(t, Line(child), In(t, Line(child), 0.5));
        Assert.Equal(Maybe.From(parent), t.Tasks.Board.Tasks.Single(x => x.Id == child).ParentId);

        // Beside n1: a plain task, placed after it.
        Settle(t);
        var n1 = t.Tasks.Board.Tasks.Single(x => x.Title == "n1").Id;
        Drag(t, Line(child), In(t, Line(n1), 0.9));
        Assert.True(t.Tasks.Board.Tasks.Single(x => x.Id == child).ParentId.HasNoValue);
        Assert.Equal(["n2", "n1", "child"], t.Tasks.Board.Pile().Where(x => x.Id != parent).Select(x => x.Title));
    });

    // Done lines sit in the pinned block under the questions (task eefb986a) and are dragged among themselves too (task
    // f41d7857): never above a question, and to the foot of the done lines when let go below them.
    [Fact]
    public void Done_lines_are_dragged_among_the_done_lines_under_the_questions() => StaThread.Run(() =>
    {
        using var t = DragBar(4);
        var id = new Func<string, Guid>(title => t.Tasks.Board.Tasks.Single(x => x.Title == title).Id);
        t.Tasks.SetStatus(id("n1"), Deskbar.Core.Tasks.TaskStatus.Done);
        t.Tasks.SetStatus(id("n2"), Deskbar.Core.Tasks.TaskStatus.Done);
        t.Tasks.SetStatus(id("n3"), Deskbar.Core.Tasks.TaskStatus.WaitingOnYou);
        Settle(t);
        Assert.Equal(["n3", "n2", "n1", "n4"], t.Lines.Select(TitleOf));

        // n1 above n2, on n2's top edge.
        Drag(t, t.Lines[2], In(t, t.Lines[1], 0.1));
        Settle(t);
        Assert.Equal(["n3", "n1", "n2", "n4"], t.Lines.Select(TitleOf));

        // n2 onto the question's top edge: it goes to the top of the done lines, under the question.
        Drag(t, t.Lines[2], In(t, t.Lines[0], 0.1));
        Settle(t);
        Assert.Equal(["n3", "n2", "n1", "n4"], t.Lines.Select(TitleOf));

        // n2 below everything: the foot of the done lines.
        Drag(t, t.Lines[1], In(t, t.Lines[3], 0.9));
        Settle(t);
        Assert.Equal(["n3", "n1", "n2", "n4"], t.Lines.Select(TitleOf));
        Assert.Equal(Deskbar.Core.Tasks.TaskStatus.Done, t.Tasks.Board.Tasks.Single(x => x.Title == "n2").Status);
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

        // On the waiting line's top edge: its middle would make n1 its subtask (task f41d7857).
        Drag(t, t.Lines[2], In(t, t.Lines[0], 0.1));

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
        // The top edge: the middle of a waiting line now takes the task as its subtask (task f41d7857).
        t.Bar.TaskDragMove(In(t, t.Lines[0], 0.1));

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

    // The Manager button (manager session, Task 9): shown when the App wired OpenManager, one click one call, and absent
    // otherwise, so no button ever does nothing.
    [Fact]
    public void Manager_button_shows_when_wired_and_opens_the_manager() => StaThread.Run(() =>
    {
        var opened = 0;
        using var t = Build(openManager: () => opened++);
        // A paint under the test's click clock, as every click test has: the list was first built on the real clock, which
        // the click guard would read as "a repaint just now".
        t.Tasks.AddNote("A note", t.Gepha);
        var button = ButtonNamed(t.List, "Manager");
        Assert.Equal(Visibility.Visible, button.Visibility);
        Assert.Equal("Manager", Inside(button).OfType<TextBlock>().Single().Text);
        Assert.Equal("Open the manager: one Claude session that coordinates your tasks", (string)button.ToolTip);

        Click(button);
        Assert.Equal(1, opened);
    });

    // The manager line (task 61e4d143): the manager's open question above the task lines, a click on it opens the manager,
    // and no line while it asks nothing or while there is no manager to open.
    [Fact]
    public void Manager_line_shows_the_question_and_a_click_opens_the_manager() => StaThread.Run(() =>
    {
        var opened = 0;
        using var t = Build(openManager: () => opened++, managerAsks: () => new ManagerAsk("Which comes first?", Start));
        t.Tasks.AddNote("A note", t.Gepha);

        var line = Inside(t.Bar).OfType<Border>().Single(b => Equals(b.Tag, "manager-line"));
        var text = Inside(line).OfType<TextBlock>().Single(b => Equals(b.Tag, "manager-line-text"));
        Assert.Contains("Which comes first?", string.Concat(text.Inlines.OfType<Run>().Select(r => r.Text)));

        Click(ButtonNamed(t.Bar, "Manager asks: Which comes first?"));
        Assert.Equal(1, opened);
    });

    [Fact]
    public void No_manager_line_when_the_manager_asks_nothing() => StaThread.Run(() =>
    {
        using var t = Build(openManager: () => { }, managerAsks: () => null);
        t.Tasks.AddNote("A note", t.Gepha);

        Assert.DoesNotContain(Inside(t.Bar).OfType<Border>(), b => Equals(b.Tag, "manager-line"));
    });

    [Fact]
    public void No_manager_line_without_the_open_action() => StaThread.Run(() =>
    {
        using var t = Build(managerAsks: () => new ManagerAsk("Which comes first?", Start));
        t.Tasks.AddNote("A note", t.Gepha);

        Assert.DoesNotContain(Inside(t.Bar).OfType<Border>(), b => Equals(b.Tag, "manager-line"));
    });

    [Fact]
    public void No_manager_button_without_the_action() => StaThread.Run(() =>
    {
        using var t = Build();
        Assert.Equal(Visibility.Collapsed, ButtonNamed(t.List, "Manager").Visibility);
    });

    // The Manager button takes room beside "All tasks": the words still shorten instead of clipping. 140 is the narrowest
    // width in Petre's geometry trace and 158.4 the default docked width (176 at the 0.9 scale), the two real narrow ones.
    [Theory]
    [InlineData(140)]
    [InlineData(158.4)]
    [InlineData(200)]
    [InlineData(230)]
    [InlineData(260)]
    [InlineData(300)]
    public void All_tasks_still_shortens_with_the_manager_button_beside_it(double width) => StaThread.Run(() =>
    {
        using var t = Build(openWindow: () => { }, openManager: () => { });
        Enumerable.Range(1, 5).ToList().ForEach(i => t.Tasks.AddNote($"Note {i}", t.Gepha));
        Assert.True(t.Harness.Manager.SetBarTextOffset(3.5).IsSuccess);
        t.Bar.ApplyWidth(width);
        t.Bar.RequestRebuild();
        t.Bar.UpdateLayout();
        t.Bar.UpdateLayout();

        var more = ButtonNamed(t.List, "More tasks");
        var manager = ButtonNamed(t.List, "Manager");
        var moreBounds = more.TransformToAncestor(t.List).TransformBounds(new Rect(more.RenderSize));
        var managerBounds = manager.TransformToAncestor(t.List).TransformBounds(new Rect(manager.RenderSize));
        Assert.True(managerBounds.Right <= t.List.ActualWidth + 0.5, $"manager {managerBounds} in a list {t.List.ActualWidth} wide at {width}");
        Assert.True(moreBounds.Right <= managerBounds.Left + 0.5 || moreBounds.Top < managerBounds.Top, "the two buttons overlap");
        var label = Inside(more).OfType<TextBlock>().Single();
        Assert.True(label.ActualWidth >= label.DesiredSize.Width - 0.5, $"'{label.Text}' is clipped: {label.ActualWidth} of {label.DesiredSize.Width}");
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

    // --- RULING 57, the assignee: the bar lists only the tasks assigned to Petre -----------------------------------------

    [Fact]
    public void The_bar_lists_the_tasks_assigned_to_him_whoever_made_them() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Tasks.AddNote("mine", t.Gepha);
        var handedOver = t.Tasks.AddNote("handed to Claude", t.Gepha).Value;
        t.Tasks.Assign(handedOver.Id, Assignee.Claude);
        var working = t.Tasks.Create("Claude is on it", t.Gepha, Session("s1")).Value;
        var takenBack = t.Tasks.Create("taken back", t.Gepha, Session("s2")).Value;
        t.Tasks.Assign(takenBack.Id, Assignee.Me);

        Assert.Equal(["taken back", "mine"], OnTheBar(t).OrderBy(x => x == "taken back" ? 0 : 1));
        Assert.DoesNotContain("handed to Claude", OnTheBar(t));
        Assert.DoesNotContain("Claude is on it", OnTheBar(t));
        // They stay open on the board: the window shows them.
        Assert.True(t.Tasks.Board.Tasks.Single(x => x.Id == handedOver.Id).IsOpen);
    });

    [Fact]
    public void The_more_count_follows_the_assignee_rule() => StaThread.Run(() =>
    {
        using var t = Build(openWindow: () => { });
        Enumerable.Range(1, 4).ToList().ForEach(i => t.Tasks.AddNote($"Note {i}", t.Gepha));
        Enumerable.Range(1, 3).ToList().ForEach(i => t.Tasks.Assign(t.Tasks.AddNote($"Claude's {i}", t.Gepha).Value.Id, Assignee.Claude));

        Assert.Equal("+1 more", t.Bar.TaskListMoreText);
    });

    // Ruling 58: a note's description shows on the bar where a Claude task shows its status line (its first line); a status
    // line or a question, when there is one, takes that place.
    [Fact]
    public void A_notes_description_shows_in_the_detail_and_a_status_line_wins() => StaThread.Run(() =>
    {
        using var t = Build();
        var note = t.Tasks.AddNote("Pay rent", t.Gepha).Value.Id;
        t.Tasks.Describe(note, "Use the November invoice\nand the late fee");
        var claude = t.Tasks.Create("Tasks redesign", t.Gepha, Session("s1")).Value.Id;
        t.Tasks.Describe(claude, "Petre's brief");
        t.Tasks.SetStatusLine(claude, "waiting for CI");
        t.Tasks.MarkDone(claude);

        var lines = t.Lines.Select(l => (TaskLine)l.Tag).ToDictionary(l => l.Id);
        Assert.Equal("Use the November invoice", lines[note].Detail);
        Assert.Equal("waiting for CI", lines[claude].Detail);
    });

    // Ruling 59 (Petre: important bold, urgent italic): the title's run on the bar.
    [Fact]
    public void An_important_title_is_bold_and_an_urgent_one_italic_on_the_bar() => StaThread.Run(() =>
    {
        using var t = Build();
        t.Tasks.AddNote("plain", t.Gepha);
        t.Tasks.AddNote("important", t.Gepha, Quadrant.II);
        t.Tasks.AddNote("urgent", t.Gepha, Quadrant.III);

        (FontWeight, FontStyle) Look(string title)
        {
            var line = t.Lines.Single(l => ((TaskLine)l.Tag).Title == title);
            var run = Inside(line).OfType<TextBlock>().Single(b => Equals(b.Tag, "task-text")).Inlines.OfType<System.Windows.Documents.Run>().First();
            return (run.FontWeight, run.FontStyle);
        }

        Assert.Equal((FontWeights.Normal, FontStyles.Normal), Look("plain"));
        Assert.Equal((FontWeights.Bold, FontStyles.Normal), Look("important"));
        Assert.Equal((FontWeights.Normal, FontStyles.Italic), Look("urgent"));
        // Both flags: bold and italic (the bar lists three lines, so the third task takes the second flag).
        t.Tasks.SetFlag(t.Tasks.Board.Tasks.Single(x => x.Title == "urgent").Id, TaskFlag.Important, true);
        Assert.Equal((FontWeights.Bold, FontStyles.Italic), Look("urgent"));
    });

    // Review: the bar's description line is plain text, without markdown markers.
    [Fact]
    public void The_bars_description_line_strips_markdown_markers() => StaThread.Run(() =>
    {
        using var t = Build();
        var note = t.Tasks.AddNote("Pay rent", t.Gepha).Value.Id;
        t.Tasks.Describe(note, "- **Use** the _November_ `invoice`");

        Assert.Equal("Use the November invoice", t.Lines.Select(l => (TaskLine)l.Tag).Single().Detail);
    });

    // Task 9 (spec, "Where the clicks are"): "Start Claude on this" on a line's menu right after Edit. It reaches the App's
    // ClaudeTaskActions.Offer once the menu has closed; a bar the App gave no such seam shows no such item.
    [Fact]
    public void Start_Claude_on_this_is_on_the_bar_menu_after_Edit() => StaThread.Run(() =>
    {
        var started = new List<Guid>();
        using var t = Build(startClaude: id =>
        {
            started.Add(id);
            return null;
        });
        var id = t.Tasks.AddNote("Write the docs", t.Gepha).Value.Id;

        var menu = t.Bar.OpenTaskLineMenu(id)!;
        var items = menu.Items.OfType<MenuItem>().ToList();
        Assert.Equal(["Edit", "Start Claude on this"], items.Take(2).Select(i => ((TextBlock)i.Header).Text));
        items[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.IsOpen = false;
        // Run once the menu has closed (an Input-priority dispatch): a lower priority runs after it.
        t.Bar.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert.Equal([id], started);

        using var plain = Build();
        var other = plain.Tasks.AddNote("Errand", plain.Gepha).Value.Id;
        var plainMenu = plain.Bar.OpenTaskLineMenu(other)!;
        Assert.DoesNotContain(plainMenu.Items.OfType<MenuItem>(), i => i.Header is TextBlock { Text: "Start Claude on this" });
        plainMenu.IsOpen = false;
    });

    // Task 3c40dec9 (Petre: "when adding a task, have a button to start immediately with claude"): the add row's Start
    // Claude button, after the flag toggles, adds the typed note as Enter does and hands it to "Start Claude on this"
    // (the App's ClaudeTaskActions.Offer) once the add has settled. An empty box adds and starts nothing. A bar the App
    // gave no such seam shows no such button.
    [Fact]
    public void The_add_rows_Start_Claude_button_adds_the_note_and_starts_Claude_on_it() => StaThread.Run(() =>
    {
        var started = new List<Guid>();
        var handedBack = new List<nint>();
        using var t = Build(handedBack: handedBack, startClaude: id =>
        {
            started.Add(id);
            return null;
        });
        var button = t.Bar.TaskAddStartClaudeButton!;
        Assert.Equal(Visibility.Visible, button.Visibility);
        Assert.Equal(FloatingBar.AddAndStartClaudeText, AutomationProperties.GetName(button));

        // Nothing typed: the board refuses the note, and nothing starts.
        ClickBox(t);
        Click(button);
        t.Bar.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.Empty(t.Tasks.Board.Tasks);
        Assert.Empty(started);

        t.Box.Text = "Fix the build";
        Click(button);
        var task = Assert.Single(t.Tasks.Board.Tasks);
        Assert.Equal("Fix the build", task.Title);
        Assert.Equal(t.Gepha, task.WorkspaceId.Value);
        Assert.Equal("", t.Box.Text);
        // The keyboard stays with the bar until "Start Claude on this" has had its say, so a pick list it opens has it.
        Assert.Empty(handedBack);
        // Run once the add has settled (an Input-priority dispatch): a lower priority runs after it.
        t.Bar.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.Equal([task.Id], started);
        // No pick list (one folder): the keyboard goes back to the editor.
        Assert.Equal([Editor], handedBack);

        // A second press on the idle, empty box (a double-click's): nothing added, nothing started, keyboard left alone.
        Click(button);
        t.Bar.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.Single(t.Tasks.Board.Tasks);
        Assert.Equal([task.Id], started);
        Assert.Equal([Editor], handedBack);

        using var plain = Build();
        Assert.Equal(Visibility.Collapsed, plain.Bar.TaskAddStartClaudeButton!.Visibility);
    });

    // Task 7f61af2e (Petre: "make it possible to open claude without opening the task first"): a Claude line's menu has
    // "Open Claude" right after Edit. It goes where the double-click goes (GoToTask), once the menu has closed, and the
    // tasks panel never opens on the way: no single click on the line was made. A note's menu has no such item.
    // Task b6f068b1 (Petre: "open claude and start claude , no need for both"): the menu has ONE Claude item, so a Claude
    // task's has no "Start Claude on this" and a note's has no "Open Claude".
    [Fact]
    public void Open_Claude_on_the_bar_menu_goes_without_opening_the_task() => StaThread.Run(() =>
    {
        var went = new List<Guid>();
        var opened = new List<Guid>();
        using var t = Build(goTo: id =>
        {
            went.Add(id);
            return Result.Success();
        }, openTask: opened.Add, startClaude: _ => null);
        var id = t.Tasks.Create("Tasks redesign", t.Gepha, Session("s1")).Value.Id;
        t.Tasks.MarkDone(id); // Done: a Claude task shows on the bar while it needs him (Ruling 48)
        var note = t.Tasks.AddNote("Dishwasher tablets", t.Gepha).Value.Id;

        var menu = t.Bar.OpenTaskLineMenu(id)!;
        var items = menu.Items.OfType<MenuItem>().ToList();
        Assert.Equal(["Edit", "Open Claude", "Add subtask…"], items.Take(3).Select(i => ((TextBlock)i.Header).Text));
        items[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.IsOpen = false;
        // Run once the menu has closed (an Input-priority dispatch): a lower priority runs after it.
        t.Bar.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert.Equal([id], went);
        Assert.Empty(opened);

        var noteMenu = t.Bar.OpenTaskLineMenu(note)!;
        Assert.DoesNotContain(noteMenu.Items.OfType<MenuItem>(), i => i.Header is TextBlock { Text: "Open Claude" });
        Assert.Contains(noteMenu.Items.OfType<MenuItem>(), i => i.Header is TextBlock { Text: "Start Claude on this" });
        noteMenu.IsOpen = false;
    });

    // Task 6a57f59b (Petre: "doesn't open claude", on a task whose session's worktree had been removed): a Claude task
    // whose session cannot be opened (OpensClaudeSession, the App's ClaudeTaskActions.CanOpen) has Start Claude on this
    // in its menu, as a note has, never an Open Claude whose click opens nothing.
    [Fact]
    public void A_bar_line_whose_session_cannot_be_opened_offers_Start_Claude() => StaThread.Run(() =>
    {
        var started = new List<Guid>();
        using var t = Build(startClaude: id =>
        {
            started.Add(id);
            return null;
        }, opensClaudeSession: _ => false);
        var id = t.Tasks.Create("background sleep probe", t.Gepha, Session("s1")).Value.Id;
        t.Tasks.MarkDone(id); // Done: a Claude task shows on the bar while it needs him (Ruling 48)

        var menu = t.Bar.OpenTaskLineMenu(id)!;
        var items = menu.Items.OfType<MenuItem>().ToList();
        Assert.DoesNotContain(items, i => i.Header is TextBlock { Text: "Open Claude" });
        var start = items.Single(i => i.Header is TextBlock { Text: "Start Claude on this" });
        start.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.IsOpen = false;
        t.Bar.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert.Equal([id], started);
    });

    // --- subtasks (docs/superpowers/specs/2026-11-24-subtasks-design.md, "Making subtasks", Petre's pick A) ----------

    // The parent chip in the add box, found by its tag; collapsed while the box adds a plain note.
    static Button ParentChip(ListBar t) =>
        Inside(t.Chips).OfType<Button>().Single(b => b.Content is Border { Tag: "task-add-parent" });

    static TextBlock Placeholder(ListBar t) =>
        Inside(t.List).OfType<TextBlock>().Single(b => Equals(b.Tag, "task-add-placeholder"));

    [Fact]
    public void A_lines_menu_offers_add_subtask_after_start_claude() => StaThread.Run(() =>
    {
        string[] Firsts(ListBar bar, Guid id)
        {
            var menu = bar.Bar.OpenTaskLineMenu(id)!;
            var texts = menu.Items.OfType<MenuItem>().Select(i => ((TextBlock)i.Header).Text).Take(3).ToArray();
            menu.IsOpen = false;
            return texts;
        }

        using var claude = Build(startClaude: _ => null);
        Assert.Equal(["Edit", "Start Claude on this", "Add subtask…"], Firsts(claude, claude.Tasks.AddNote("Parent", claude.Gepha).Value.Id));

        // Without Claude, right after Edit.
        using var plain = Build();
        Assert.Equal(["Edit", "Add subtask…"], Firsts(plain, plain.Tasks.AddNote("Parent", plain.Gepha).Value.Id).Take(2));
    });

    // Task 54b39c4c (Petre: "subtask should show its parent and have a way to open the parent easily"): a subtask's line,
    // which shows only "↳" on the bar, has "Open parent" right after "Add subtask…". It hands the PARENT's id to the App
    // (TasksBoard.OpenParent) once the menu has closed. A plain task's line has no such item.
    [Fact]
    public void A_subtasks_menu_offers_open_parent() => StaThread.Run(() =>
    {
        var opened = new List<Guid>();
        using var t = Build(openParent: opened.Add);
        var parent = t.Tasks.AddNote("Ship the release", t.Gepha).Value.Id;
        var child = t.Tasks.AddNote("Write the notes", t.Gepha, parent: parent).Value.Id;

        var menu = t.Bar.OpenTaskLineMenu(child)!;
        var items = menu.Items.OfType<MenuItem>().ToList();
        Assert.Equal(["Edit", "Add subtask…", "Open parent"], items.Take(3).Select(i => ((TextBlock)i.Header).Text));
        items[2].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.IsOpen = false;
        // Run once the menu has closed (an Input-priority dispatch): a lower priority runs after it.
        t.Bar.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert.Equal([parent], opened);

        var parentMenu = t.Bar.OpenTaskLineMenu(parent)!;
        Assert.DoesNotContain(parentMenu.Items.OfType<MenuItem>(), i => i.Header is TextBlock { Text: "Open parent" });
        parentMenu.IsOpen = false;
    });

    [Fact]
    public void Add_subtask_shows_the_parent_chip_on_the_parents_workspace() => StaThread.Run(() =>
    {
        using var t = Build();
        var other = t.Harness.Manager.State.Workspaces.First(w => w.Id != t.Gepha).Id;
        var parent = t.Tasks.AddNote("Ship the release", other).Value;

        t.Bar.BeginSubtaskAdd(parent.Id);

        Assert.True(t.Bar.TaskListAdding);
        Assert.Equal(Visibility.Visible, ParentChip(t).Visibility);
        Assert.Contains("Ship the release", string.Concat(Inside(ParentChip(t)).OfType<TextBlock>().Select(b => b.Text)));
        Assert.Equal(other, t.Bar.TaskAddTarget.Value);
        Assert.Equal("A subtask: Enter adds it, Esc drops the parent", Placeholder(t).Text);
    });

    // Final review, finding 2: an empty subtask box keeps the parent's workspace through a click away and a desktop
    // switch; only a plain note box follows the workspace in front.
    [Fact]
    public void An_empty_subtask_box_keeps_the_parents_workspace_after_clicking_away() => StaThread.Run(() =>
    {
        using var t = Build();
        var other = t.Harness.Manager.State.Workspaces.First(w => w.Id != t.Gepha).Id;
        var parent = t.Tasks.AddNote("Ship the release", other).Value;
        t.Bar.BeginSubtaskAdd(parent.Id);

        typeof(Window).GetMethod("OnDeactivated", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(t.Bar, [EventArgs.Empty]);
        t.Bar.RequestRebuild();
        t.Bar.UpdateLayout();
        ClickBox(t);

        Assert.Equal(other, t.Bar.TaskAddTarget.Value);
        t.Box.Text = "Write the notes";
        Press(t.Bar, t.Box, Key.Enter);
        var child = t.Tasks.Board.Tasks.Single(x => x.Title == "Write the notes");
        Assert.Equal(Maybe.From(parent.Id), child.ParentId);
        Assert.Equal(other, child.WorkspaceId.Value);
    });

    [Fact]
    public void Enter_adds_the_subtask_under_its_parent() => StaThread.Run(() =>
    {
        using var t = Build();
        var parent = t.Tasks.AddNote("Parent", t.Gepha).Value;

        t.Bar.BeginSubtaskAdd(parent.Id);
        t.Box.Text = "Child";
        Press(t.Bar, t.Box, Key.Enter);

        Assert.Equal(Maybe.From(parent.Id), t.Tasks.Board.Tasks.Single(x => x.Title == "Child").ParentId);
        // The box is a plain note box again.
        Assert.Equal(Visibility.Collapsed, ParentChip(t).Visibility);
    });

    // The tasks panel's "Subtasks" checkbox applies to the bar too (task 173ef268, Petre: "same checkbox"). Unticked,
    // the plain subtask drawn under its parent goes and the parent still counts it; the subtask asking a question stays
    // in view. Ticked again, both are back under the parent.
    [Fact]
    public void The_subtasks_checkbox_folds_subtasks_on_the_bar_too() => StaThread.Run(() =>
    {
        using var t = Build();
        // Docked, where the Waiting on you block keeps three lines: on a floating bar it keeps one (task 39f4cc8a), and
        // the parent's two subtasks would fold into the group line whatever the checkbox says.
        t.Bar.SizeToContent = SizeToContent.Manual;
        t.Bar.Height = 900;
        var parent = t.Tasks.Create("Parent", t.Gepha, Session("p")).Value.Id;
        t.Tasks.Ask(parent, "Parent question?");
        var plain = t.Tasks.AddNote("Plain", t.Gepha, Quadrant.IV, parent).Value.Id;
        var asking = t.Tasks.Create("Asking", t.Gepha, Session("a")).Value.Id;
        Assert.True(t.Tasks.SetParent(asking, parent).IsSuccess);
        t.Tasks.Ask(asking, "Child question?");
        Assert.Contains(plain, t.Bar.TaskListLines.Select(l => l.Id));

        Assert.True(t.Harness.Manager.SaveTasksPanel(Deskbar.Core.Persistence.TasksPanelState.Default with { HideSubtasks = true }).IsSuccess);
        var ids = t.Bar.TaskListLines.Select(l => l.Id).ToArray();
        Assert.DoesNotContain(plain, ids);
        Assert.Contains(asking, ids);
        Assert.Equal(2, t.Bar.TaskListLines.Single(l => l.Id == parent).SubtaskCount);

        Assert.True(t.Harness.Manager.SaveTasksPanel(Deskbar.Core.Persistence.TasksPanelState.Default).IsSuccess);
        Assert.Contains(plain, t.Bar.TaskListLines.Select(l => l.Id));
    });

    [Fact]
    public void Esc_drops_the_parent_and_keeps_the_text() => StaThread.Run(() =>
    {
        using var t = Build();
        var parent = t.Tasks.AddNote("Parent", t.Gepha).Value;
        t.Bar.BeginSubtaskAdd(parent.Id);
        t.Box.Text = "Loose note";

        Press(t.Bar, t.Box, Key.Escape);

        Assert.True(t.Bar.TaskListAdding);
        Assert.Equal("Loose note", t.Box.Text);
        Assert.Equal(Visibility.Collapsed, ParentChip(t).Visibility);
        Press(t.Bar, t.Box, Key.Enter);
        Assert.True(t.Tasks.Board.Tasks.Single(x => x.Title == "Loose note").ParentId.HasNoValue);
    });

    [Fact]
    public void The_parent_chips_press_drops_the_parent() => StaThread.Run(() =>
    {
        using var t = Build();
        var parent = t.Tasks.AddNote("Parent", t.Gepha).Value;
        t.Bar.BeginSubtaskAdd(parent.Id);

        Click(ParentChip(t));

        Assert.Equal(Visibility.Collapsed, ParentChip(t).Visibility);
        Assert.True(t.Bar.TaskListAdding);
    });

    // Petre's pick A on the bar: a subtask's line starts with "↳" (no room for the parent's name; the tooltip has it),
    // and a parent's line says how many open subtasks it has after its title.
    [Fact]
    public void A_subtask_line_shows_the_arrow_and_its_tip_names_the_parent() => StaThread.Run(() =>
    {
        using var t = Build();
        var parent = t.Tasks.AddNote("Ship the release", t.Gepha).Value;
        var child = t.Tasks.AddNote("Write the notes", t.Gepha, parent: parent.Id).Value;
        t.Bar.UpdateLayout();

        var line = t.Lines.Single(l => ((TaskLine)l.Tag).Id == child.Id);

        Assert.Equal("↳ Write the notes", TextOf(line));
        Assert.Contains("Subtask of Ship the release", (string)line.ToolTip);
    });

    // Task 9e85caee: the done tick went in front of the "↳", which pushed a done subtask's arrow right of its siblings'
    // and read as deeper nesting. The arrow comes first and the tick right after it.
    [Fact]
    public void A_done_subtasks_tick_comes_after_its_arrow() => StaThread.Run(() =>
    {
        using var t = Build();
        var parent = t.Tasks.AddNote("Ship the release", t.Gepha).Value;
        var child = t.Tasks.AddNote("Research it", t.Gepha, parent: parent.Id).Value;
        Assert.True(t.Tasks.MarkDone(child.Id).IsSuccess);
        t.Bar.UpdateLayout();

        var line = t.Lines.Single(l => ((TaskLine)l.Tag).Id == child.Id);
        var text = Inside(line).OfType<TextBlock>().Single(b => Equals(b.Tag, "task-text"));
        var inlines = text.Inlines.ToArray();

        Assert.StartsWith("↳", Assert.IsType<Run>(inlines[0]).Text, StringComparison.Ordinal);
        var tick = Assert.IsType<InlineUIContainer>(inlines[1]);
        Assert.Equal(TaskStatusMarks.DoneTickTag, ((FrameworkElement)tick.Child).Tag);
    });

    [Fact]
    public void A_parent_line_counts_its_open_subtasks() => StaThread.Run(() =>
    {
        using var t = Build();
        var parent = t.Tasks.AddNote("Ship the release", t.Gepha).Value;
        t.Tasks.AddNote("Write the notes", t.Gepha, parent: parent.Id);
        var done = t.Tasks.AddNote("Tag it", t.Gepha, parent: parent.Id).Value;
        t.Tasks.Close(done.Id);
        t.Bar.UpdateLayout();

        var line = t.Lines.Single(l => ((TaskLine)l.Tag).Id == parent.Id);

        Assert.Equal("Ship the release ·\u00A01\u00A0subtask", TextOf(line));
    });

    // Follow-up (Petre's screenshot of "↳ test1" four lines above its parent): a subtask is drawn right under its parent,
    // in the parent's group, whatever its own flags; and a change only the parent's line shows (its count, its title in
    // a subtask's tooltip) repaints the bar.
    static int IndexOf(ListBar t, Guid id) => t.Lines.Select((l, i) => (((TaskLine)l.Tag).Id, i)).First(x => x.Id == id).i;

    [Fact]
    public void A_subtask_is_drawn_right_under_its_parent() => StaThread.Run(() =>
    {
        using var t = Build();
        var parent = t.Tasks.AddNote("Ship the release", t.Gepha).Value;
        t.Tasks.AddNote("Unrelated", t.Gepha);
        var child = t.Tasks.AddNote("Write the notes", t.Gepha, parent: parent.Id).Value;
        t.Bar.UpdateLayout();

        Assert.Equal(IndexOf(t, parent.Id) + 1, IndexOf(t, child.Id));
    });

    [Fact]
    public void An_important_subtask_stays_under_a_plain_parent_with_no_group_break() => StaThread.Run(() =>
    {
        using var t = Build();
        var parent = t.Tasks.AddNote("Ship the release", t.Gepha).Value;
        var child = t.Tasks.AddNote("Write the notes", t.Gepha, Quadrant.II, parent.Id).Value;
        t.Bar.UpdateLayout();

        Assert.Equal(IndexOf(t, parent.Id) + 1, IndexOf(t, child.Id));
        Assert.False(((TaskLine)t.Lines[IndexOf(t, child.Id)].Tag).StartsGroup);
    });

    [Fact]
    public void A_parents_count_repaints_when_a_subtask_off_the_bar_is_added() => StaThread.Run(() =>
    {
        using var t = Build();
        var parent = t.Tasks.AddNote("Ship the release", t.Gepha).Value;
        t.Bar.UpdateLayout();
        var child = t.Tasks.AddNote("Claude's piece", t.Gepha, parent: parent.Id).Value;
        t.Tasks.Assign(child.Id, Assignee.Claude);
        t.Bar.UpdateLayout();

        Assert.Equal("Ship the release ·\u00A01\u00A0subtask", TextOf(t.Lines.Single(l => ((TaskLine)l.Tag).Id == parent.Id)));
    });

    [Fact]
    public void Add_subtask_on_a_subtask_adds_a_sibling() => StaThread.Run(() =>
    {
        using var t = Build();
        var parent = t.Tasks.AddNote("Parent", t.Gepha).Value;
        var child = t.Tasks.AddNote("Child", t.Gepha, parent: parent.Id).Value;

        t.Bar.BeginSubtaskAdd(child.Id);
        t.Box.Text = "Sibling";
        Press(t.Bar, t.Box, Key.Enter);

        Assert.Equal(Maybe.From(parent.Id), t.Tasks.Board.Tasks.Single(x => x.Title == "Sibling").ParentId);
    });

    // THE BOX IS THE BAR'S DARK FIELD UNDER EITHER THEME AND IN EVERY STATE (task "White looks bad", 2026-11-24).
    // Petre's screenshot showed the new-task box white, his light-grey text on it barely readable, with a clear button
    // inside: the box ran WPF's Fluent TextBox template, which paints its own fill per state from the theme and ignores
    // the Background the bar sets. Rendered off-screen under both themes, before typing and then typed into with the
    // keyboard in it, the box's empty middle must be the bar's field colour (TaskFieldFill, #15161A), and the template
    // must hold no button of its own.
    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void The_add_box_stays_dark_under_either_theme_and_focused(string theme) => StaThread.Run(() =>
    {
        using var t = Build();
#pragma warning disable WPF0001 // ThemeMode is experimental; set here to try the box under both of the system's themes
        t.Bar.ThemeMode = theme == "Light" ? ThemeMode.Light : ThemeMode.Dark;
#pragma warning restore WPF0001
        t.Bar.UpdateLayout();

        // One look before typing, one with text in it and the keyboard in it (Fluent's focused state, and its clear
        // button, which shows only once there is text).
        var looks = new[] { false, true }.Select(typed =>
        {
            if (typed)
            {
                ClickBox(t);
                t.Box.Text = "Call";
                t.Box.Focus();
            }
            t.Bar.UpdateLayout();
            var box = t.Box;
            var width = (int)Math.Ceiling(box.ActualWidth);
            var height = (int)Math.Ceiling(box.ActualHeight);
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(box);
            // The middle of the box, well right of "Call" and left of where Fluent puts its clear button.
            var pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect(width / 2, height / 2, 1, 1), pixel, 4, 0);
            var buttons = Descendants(box).OfType<System.Windows.Controls.Primitives.ButtonBase>().Count();
            return (typed, focused: box.IsKeyboardFocused || box.IsFocused, r: pixel[2], g: pixel[1], b: pixel[0], buttons);
        }).ToList();

        var report = string.Join("; ", looks.Select(l => $"typed={l.typed} focused={l.focused} rgb=({l.r},{l.g},{l.b}) buttons={l.buttons}"));
        Assert.All(looks, l =>
        {
            Assert.True(Math.Abs(l.r - 0x15) <= 6 && Math.Abs(l.g - 0x16) <= 6 && Math.Abs(l.b - 0x1A) <= 6, report);
            Assert.True(l.buttons == 0, report);
        });
    });

    // Every element under a node in the visual tree, the template's parts included (the logical tree has none of them).
    static IEnumerable<DependencyObject> Descendants(DependencyObject node) =>
        Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(node))
            .Select(i => VisualTreeHelper.GetChild(node, i))
            .SelectMany(child => new[] { child }.Concat(Descendants(child)));

    // --- pictures in the add box (task 5cc9f1d4, Petre at the bar's "Add to Deskbar..." box: "can i paste an image here?") ---

    static DataObject BitmapData()
    {
        var pixels = Enumerable.Repeat((byte)0x80, 40 * 30 * 4).ToArray();
        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(40, 30, 96, 96, PixelFormats.Bgra32, null, pixels, 40 * 4);
        bitmap.Freeze();
        return new DataObject(DataFormats.Bitmap, bitmap);
    }

    static string ScratchRoot() => Directory.CreateTempSubdirectory("deskbar-bar-images-").FullName;

    static IEnumerable<Button> RemovePictureButtons(ListBar t) =>
        Inside(t.List).OfType<Button>().Where(b => Equals(b.Tag, FloatingBar.TaskPictureRemoveTag));

    // A click in the box, Ctrl+V a screenshot, a title, Enter: one task, its title the words and its description the
    // picture, written under the new task's id. The box is empty again, pictures included.
    [Fact]
    public void A_picture_pasted_into_the_bars_add_box_becomes_the_description() => StaThread.Run(() =>
    {
        var root = ScratchRoot();
        try
        {
            using var t = Build(stateDir: root);
            ClickBox(t);

            Assert.True(t.Bar.PasteIntoTaskAdd(BitmapData()));
            Assert.Single(RemovePictureButtons(t));
            t.Box.Text = "Bar looks wrong";
            Press(t.Bar, t.Box, Key.Enter);

            var task = Assert.Single(t.Tasks.Board.Tasks);
            Assert.Equal("Bar looks wrong", task.Title);
            var picture = Assert.Single(TaskImages.Split(task.Description).Images);
            Assert.StartsWith($"task-images/{task.Id}/", picture);
            Assert.True(File.Exists(TaskImages.Resolve(picture, root).Value));
            Assert.Empty(t.Bar.TaskAddImages);
            Assert.Empty(RemovePictureButtons(t));
        }
        finally { Directory.Delete(root, true); }
    });

    // "also let me add just the image without a title too": Enter with only a picture adds a task called "Picture".
    [Fact]
    public void A_picture_alone_in_the_bars_add_box_adds_a_task_called_picture() => StaThread.Run(() =>
    {
        var root = ScratchRoot();
        try
        {
            using var t = Build(stateDir: root);
            ClickBox(t);
            t.Bar.PasteIntoTaskAdd(BitmapData());
            Press(t.Bar, t.Box, Key.Enter);

            var task = Assert.Single(t.Tasks.Board.Tasks);
            Assert.Equal("Picture", task.Title);
            Assert.Single(TaskImages.Split(task.Description).Images);
        }
        finally { Directory.Delete(root, true); }
    });

    // Text alone on the clipboard is the box's own paste; the x takes a picture out; Esc drops the rest and writes nothing.
    [Fact]
    public void The_bars_add_box_drops_pictures_on_x_and_esc() => StaThread.Run(() =>
    {
        var root = ScratchRoot();
        try
        {
            using var t = Build(stateDir: root);
            ClickBox(t);

            Assert.False(t.Bar.PasteIntoTaskAdd(new DataObject(DataFormats.UnicodeText, "just words")));
            t.Bar.PasteIntoTaskAdd(BitmapData());
            t.Bar.PasteIntoTaskAdd(BitmapData());
            Click(RemovePictureButtons(t).First());
            Assert.Single(t.Bar.TaskAddImages);

            Press(t.Bar, t.Box, Key.Escape);

            Assert.Empty(t.Bar.TaskAddImages);
            Assert.Empty(t.Tasks.Board.Tasks);
            Assert.Empty(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, true); }
    });

    // A drop on an idle box takes the keyboard as a click would, so Enter right after it adds the task.
    [Fact]
    public void A_picture_dropped_on_the_idle_box_starts_typing() => StaThread.Run(() =>
    {
        var root = ScratchRoot();
        try
        {
            using var t = Build(stateDir: root);
            Assert.False(t.Bar.TaskListAdding);

            t.Bar.PasteIntoTaskAdd(BitmapData());

            Assert.True(t.Bar.TaskListAdding);
        }
        finally { Directory.Delete(root, true); }
    });

    // --- THE WAITING ON YOU BLOCK FOLDS AFTER THREE LINES (task 39f4cc8a, FloatingBar.FoldWaiting) ---------------------

    static TaskLine Pinned(string title, Deskbar.Core.Tasks.TaskStatus status = Deskbar.Core.Tasks.TaskStatus.WaitingOnYou) =>
        new(Guid.NewGuid(), title, "", status, true, null, null, Maybe<nint>.None);

    static TaskLine Plain(string title, bool important = false) =>
        new(Guid.NewGuid(), title, "", Deskbar.Core.Tasks.TaskStatus.Backlog, false, null, null, Maybe<nint>.None) { Important = important };

    // Three lines of the block stay, the rest (questions and Done tasks alike) go into the group line, which sits where
    // the block ends, so the important task follows it straight away.
    [Fact]
    public void The_waiting_block_folds_after_three_lines()
    {
        var lines = new[]
        {
            Pinned("q1"), Pinned("q2"), Pinned("q3"), Pinned("q4"), Pinned("d1", Deskbar.Core.Tasks.TaskStatus.Done),
            Plain("important", important: true), Plain("plain"),
        };

        var fold = FloatingBar.FoldWaiting(lines, FloatingBar.WaitingShown);

        Assert.Equal(["q1", "q2", "q3", "important", "plain"], fold.Lines.Select(l => l.Title));
        Assert.Equal(["q4", "d1"], fold.Folded.Select(l => l.Title));
        Assert.Equal(3, fold.At);
        Assert.Equal(1, fold.Questions);
        Assert.Equal("+2 more waiting on you", FloatingBar.WaitingGroupText(fold));
    }

    // A group line standing for one task would hide its title and save no room, so four waiting lines all show.
    [Fact]
    public void One_line_past_three_is_drawn_not_folded()
    {
        var lines = new[] { Pinned("q1"), Pinned("q2"), Pinned("q3"), Pinned("q4"), Plain("plain") };

        var fold = FloatingBar.FoldWaiting(lines, FloatingBar.WaitingShown);

        Assert.False(fold.Any);
        Assert.Equal(-1, fold.At);
        Assert.Equal(lines, fold.Lines);
    }

    // The selected line (a terminal brought to the front selects its task) and a line in its undo window never fold: the
    // first must show its selection, the second must not move for its six seconds. A closing line among the first three
    // still takes one of the three places, so nothing slides up under the pointer.
    [Fact]
    public void The_selected_line_and_a_closing_line_never_fold()
    {
        var closingEarly = Pinned("q2") with { Closing = true };
        var picked = Pinned("q5");
        var closingLate = Pinned("q7") with { Closing = true };
        var lines = new[] { Pinned("q1"), closingEarly, Pinned("q3"), Pinned("q4"), picked, Pinned("q6"), closingLate, Plain("plain") };

        var fold = FloatingBar.FoldWaiting(lines, FloatingBar.WaitingShown, picked.Id);

        Assert.Equal(["q1", "q2", "q3", "q5", "q7", "plain"], fold.Lines.Select(l => l.Title));
        Assert.Equal(["q4", "q6"], fold.Folded.Select(l => l.Title));
        Assert.Equal(5, fold.At);
    }

    // On the bar: three questions, the group line in the asking orange, then the important task in sight; "All tasks"
    // does not count the folded questions again while the group line shows them; a click on the group line opens the panel.
    [Fact]
    public void The_group_line_keeps_the_important_task_in_sight_and_opens_the_panel() => StaThread.Run(() =>
    {
        var opened = 0;
        using var t = Build(openWindow: () => opened++);
        t.Bar.SizeToContent = SizeToContent.Manual;
        t.Bar.Height = 900;
        var important = t.Tasks.AddNote("important", t.Gepha).Value.Id;
        t.Tasks.SetFlag(important, TaskFlag.Important, true);
        Enumerable.Range(1, 6).ToList().ForEach(i =>
        {
            var made = t.Tasks.Create($"Task {i}", t.Gepha, Session($"s{i}")).Value;
            t.Tasks.Ask(made.Id, $"Question {i}?");
        });
        Settle(t);

        Assert.Equal(3, t.Lines.Count(l => ((TaskLine)l.Tag).Status == Deskbar.Core.Tasks.TaskStatus.WaitingOnYou));
        Assert.Equal("important", TitleOf(t.Lines[3]));
        var drawn = Inside(t.List).OfType<Button>().Where(b => b.Tag is TaskLine || Equals(b.Tag, FloatingBar.WaitingGroupTag)).ToList();
        var group = ButtonNamed(t.List, "More waiting tasks");
        Assert.Equal(3, drawn.IndexOf(group));
        var words = Inside(group).OfType<TextBlock>().Single();
        Assert.Equal("+3 more waiting on you", words.Text);
        Assert.Equal(Orange, ((SolidColorBrush)words.Foreground).Color);
        Assert.Equal("All tasks", t.Bar.TaskListMoreText);

        Click(group);

        Assert.Equal(1, opened);
    });

    // With no tasks panel behind the bar the group line expands the list in place, as "+N more" does, and every
    // waiting line shows.
    [Fact]
    public void Without_a_panel_the_group_line_expands_the_list() => StaThread.Run(() =>
    {
        using var t = Build();
        Enumerable.Range(1, 5).ToList().ForEach(i =>
        {
            var made = t.Tasks.Create($"Task {i}", t.Gepha, Session($"s{i}")).Value;
            t.Tasks.Ask(made.Id, $"Question {i}?");
        });
        Assert.Equal(1, t.Drawn.Count);

        Click(ButtonNamed(t.List, "More waiting tasks"));

        Assert.Equal(5, t.Drawn.Count);
        Assert.False(HasButtonNamed(t.List, "More waiting tasks"));
    });

    // A folded task selected from elsewhere gets its line back, in its place in the block, lit as selected.
    [Fact]
    public void A_folded_task_selected_from_elsewhere_gets_its_line() => StaThread.Run(() =>
    {
        using var t = Build(openWindow: () => { });
        t.Bar.SizeToContent = SizeToContent.Manual;
        t.Bar.Height = 900;
        var made = Enumerable.Range(1, 6).Select(i =>
        {
            var task = t.Tasks.Create($"Task {i}", t.Gepha, Session($"s{i}")).Value;
            t.Tasks.Ask(task.Id, $"Question {i}?");
            return task.Id;
        }).ToList();
        Settle(t);
        var folded = made[0]; // the first asked sits lowest in the block
        Assert.DoesNotContain(t.Drawn, l => ((TaskLine)l.Tag).Id == folded);

        t.Bar.Selection.Select(folded);
        Settle(t);

        Assert.Contains(t.Drawn, l => ((TaskLine)l.Tag).Id == folded);
        Assert.True(t.Bar.TaskLineSelected(folded));
        Assert.Equal("+2 more waiting on you", Inside(ButtonNamed(t.List, "More waiting tasks")).OfType<TextBlock>().Single().Text);

        // Picking another task folds it again at once, not at some later paint.
        t.Bar.Selection.Select(made[5]);
        Settle(t);

        Assert.DoesNotContain(t.Drawn, l => ((TaskLine)l.Tag).Id == folded);
        Assert.Equal("+3 more waiting on you", Inside(ButtonNamed(t.List, "More waiting tasks")).OfType<TextBlock>().Single().Text);
    });

    // A selected subtask drawn under a waiting parent keeps its parent's line too, so it is never drawn under some other
    // task's line, as if it were that one's subtask.
    [Fact]
    public void A_selected_subtask_keeps_its_parent_out_of_the_fold()
    {
        var parent = Pinned("q5");
        var child = Plain("child") with { ParentId = parent.Id, ShownIn = "waiting" };
        var lines = new[] { Pinned("q1"), Pinned("q2"), Pinned("q3"), Pinned("q4"), parent, child, Pinned("q6"), Pinned("q7"), Plain("plain") };

        var fold = FloatingBar.FoldWaiting(lines, FloatingBar.WaitingShown, child.Id);

        Assert.Equal(["q1", "q2", "q3", "q5", "child", "plain"], fold.Lines.Select(l => l.Title));
        Assert.Equal(["q4", "q6", "q7"], fold.Folded.Select(l => l.Title));
        Assert.True(fold.KeptSelected);
    }

    // A plain subtask drawn under a waiting parent folds with the block but is not counted as waiting on Petre.
    [Fact]
    public void A_folded_plain_subtask_is_not_counted_as_waiting()
    {
        var parent = Pinned("q4");
        var child = Plain("child") with { ParentId = parent.Id, ShownIn = "waiting" };
        var lines = new[] { Pinned("q1"), Pinned("q2"), Pinned("q3"), parent, child, Plain("plain") };

        var fold = FloatingBar.FoldWaiting(lines, FloatingBar.WaitingShown);

        Assert.Equal("+2 more, 1 waiting on you", FloatingBar.WaitingGroupText(fold));
    }

    // A closing line put back past the block's end (the block shrank in its undo window) does not drag the group line
    // in among the quadrant lines: the group line goes after the leading run of waiting lines.
    [Fact]
    public void The_group_line_follows_the_leading_waiting_lines()
    {
        var lines = new[]
        {
            Pinned("q1"), Pinned("q2"), Pinned("q3"), Pinned("q4"), Pinned("q5"), Plain("i1", important: true),
            Pinned("q8") with { Closing = true }, Plain("i2", important: true),
        };

        var fold = FloatingBar.FoldWaiting(lines, FloatingBar.WaitingShown);

        Assert.Equal(["q4", "q5"], fold.Folded.Select(l => l.Title));
        Assert.Equal(3, fold.At);
    }
}
