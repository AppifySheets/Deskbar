using System.Reactive.Subjects;
using System.Windows.Controls;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core.Control;
using Deskbar.Core.Domain;
using Deskbar.Core.Tasks;
using Microsoft.Reactive.Testing;

namespace Deskbar.Windows.Tests;

// What a click on a task does with its Claude sessions (ClaudeTaskActions, Task 9; design
// docs/superpowers/specs/2026-11-23-tasks-open-claude-sessions-design.md, "Where the clicks are", "Choosing the folder",
// "The window on a session"). Over the shared two-workspace harness (GEPHA in front, Sparrow beside it), a real
// TaskService, and seams that RECORD instead of acting: no test starts `claude`, `wt` or a terminal, and a window "on a
// session" is a made-up WindowsTerminal window handed to the stub monitor.
public class ClaudeTaskActionsTests
{
    sealed class MemoryStore : ITaskStore
    {
        public Result<TaskBoard> Load() => TaskBoard.Empty;

        public Result Save(TaskBoard board) => Result.Success();
    }

    const string SessionId = "884c127f-9633-40eb-bed7-593800bca40f";
    const string Folder = @"C:\repos\Deskbar";

    sealed class Rig : IDisposable
    {
        public Harness Harness { get; } = Harness.Build();
        public TaskService Tasks { get; } = new(new MemoryStore(), () => new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.Zero));
        public TestScheduler Clock { get; } = new();
        public ReplaySubject<IReadOnlyCollection<ClaudeAgent>> Live { get; } = new(1);

        public List<ProgramLaunch> Launches { get; } = [];
        public List<WindowHandle> BroughtForward { get; } = [];
        public List<(Guid Task, string Session)> Resumes { get; } = [];
        public List<(string Title, string Folder)> Starts { get; } = [];
        public List<string> Asked { get; } = [];
        public List<string> Told { get; } = [];

        public Func<TaskSession, Result<ClaudeAgent>> ResumeAnswer { get; set; } = s => Agent(s.Id, background: true);
        public Func<string, Result<string>> StartAnswer { get; set; } = _ => Result.Success(SessionId);
        public bool Confirms { get; set; }
        public bool TrustsTerminal { get; set; }
        public Func<TaskSession, bool> Transcript { get; set; } = _ => true;
        public Maybe<nint> Editor { get; set; } = Maybe<nint>.None;
        public IReadOnlyList<string> EditorNames { get; set; } = [];

        public ClaudeTaskActions Actions { get; }

        public Rig() =>
            Actions = new ClaudeTaskActions(Tasks, Harness.Manager, Live, new ClaudeTaskSeams(
                Start: (task, folder) =>
                {
                    Starts.Add((task.Title, folder));
                    return System.Threading.Tasks.Task.FromResult(StartAnswer(folder));
                },
                Resume: (task, session) =>
                {
                    Resumes.Add((task, session.Id));
                    return System.Threading.Tasks.Task.FromResult(ResumeAnswer(session));
                },
                Launch: launch =>
                {
                    Launches.Add(launch);
                    return Result.Success<int?>(4321);
                },
                EditorWindow: _ => Editor,
                BringForward: window =>
                {
                    BroughtForward.Add(window);
                    return Result.Success();
                },
                TranscriptExists: session => Transcript(session),
                EditorFolderNames: _ => EditorNames)
            {
                Confirm = question =>
                {
                    Asked.Add(question);
                    return Confirms;
                },
                OfferTrustTerminal = message =>
                {
                    Asked.Add(message);
                    return TrustsTerminal;
                },
                Tell = Told.Add,
            }, Clock);

        public Guid Gepha => Harness.Manager.State.Workspaces.Single(w => w.Name == "GEPHA").Id;
        public Guid Sparrow => Harness.Manager.State.Workspaces.Single(w => w.Name == "Sparrow").Id;
        public Guid SparrowDesktop => Harness.Manager.State.Workspaces.Single(w => w.Name == "Sparrow").DesktopId!.Value;

        public TaskItem TaskOf(Guid id) => Tasks.Board.Tasks.Single(t => t.Id == id);

        // A Claude task on Sparrow with one session in Folder.
        public TaskItem ClaudeTask(string title = "Fix the bar", string session = SessionId) =>
            Tasks.Create(title, Sparrow, new TaskSession(session, Folder, DateTimeOffset.UnixEpoch, Maybe<DateTimeOffset>.None)).Value;

        public void Listed(params ClaudeAgent[] agents) => Live.OnNext(agents);

        // The window wt opens on a session, as the window monitor reports it.
        public WindowInfo TerminalWindow(string title)
        {
            var window = new WindowInfo(new WindowHandle(0x7070), 70, "WindowsTerminal", @"C:\Program Files\WindowsApps\WindowsTerminal.exe", title, null);
            Harness.Desktops.Placements[window.Handle] = SparrowDesktop;
            Harness.Monitor.Pump.OnNext(new WindowEvent(WindowEventKind.Appeared, window));
            return window;
        }

        // The answers of `claude` arrive on the scheduler (the UI thread in the app).
        public void Settle() => Clock.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);

        public void Dispose() => Actions.Dispose();
    }

    static ClaudeAgent Agent(string id, bool background, bool busy = false) =>
        new(id, background ? Maybe.From(id[..8]) : Maybe<string>.None, Folder, background, busy, "");

    // Spec, "Where the clicks are": a running background session is shown by a terminal attached to it, on the task's
    // workspace. The workspace switch comes first, and the window is expected there through StartIn by Windows
    // Terminal's process name, since wt hands the window to WindowsTerminal.
    [Fact]
    public void Going_to_a_task_with_a_listed_background_session_opens_an_attach_window_on_its_workspace()
    {
        using var rig = new Rig();
        var task = rig.ClaudeTask();
        rig.Listed(Agent(SessionId, background: true));

        Assert.True(rig.Actions.Go(task).IsSuccess);

        Assert.Equal(rig.SparrowDesktop, rig.Harness.Desktops.CurrentId);
        var launch = Assert.Single(rig.Launches);
        Assert.Equal(ClaudeStart.AttachWindow("884c127f", Folder, "Fix the bar · Claude"), launch, LaunchComparer.Instance);
        Assert.True(rig.Harness.Manager.AwaitsAny("WindowsTerminal"));
        Assert.Empty(rig.Resumes);
    }

    // Only one window can be attached to a session: a second `claude attach` takes the session over and the first
    // window closes by itself (Task 1 note). So going again brings the open window forward, found by its title; and a
    // click while wt is still opening the first window launches nothing.
    [Fact]
    public void Going_again_brings_the_open_window_forward_instead()
    {
        using var rig = new Rig();
        var task = rig.ClaudeTask();
        rig.Listed(Agent(SessionId, background: true));
        rig.Actions.Go(task);

        // Before the window shows: still on its way, nothing more.
        rig.Actions.Go(task);
        Assert.Single(rig.Launches);
        Assert.Empty(rig.BroughtForward);

        var window = rig.TerminalWindow("Fix the bar · Claude");
        rig.Actions.Go(task);

        Assert.Single(rig.Launches);
        Assert.Equal([window.Handle], rig.BroughtForward);
    }

    // Spec: an ended session is resumed in its folder, then a window opens on it. ClaudeSessions.ResumeNow does the
    // resume under the keeper's guards; the window opens with the short id the list gives once it shows the session.
    // A session whose transcript is gone is expired: nothing is resumed and nothing opens.
    [Fact]
    public void Going_to_a_task_with_an_ended_session_resumes_then_attaches()
    {
        using var rig = new Rig();
        var task = rig.ClaudeTask();
        rig.Listed();

        Assert.True(rig.Actions.Go(task).IsSuccess);
        rig.Settle();

        Assert.Equal([(task.Id, SessionId)], rig.Resumes);
        Assert.Equal(ClaudeStart.AttachWindow("884c127f", Folder, "Fix the bar · Claude"), Assert.Single(rig.Launches), LaunchComparer.Instance);

        using var expired = new Rig { Transcript = _ => false };
        var old = expired.ClaudeTask("Old one");
        expired.Listed();
        Assert.True(expired.Actions.Go(old).IsSuccess);
        expired.Settle();
        Assert.Empty(expired.Resumes);
        Assert.Empty(expired.Launches);
    }

    // A session someone is sitting in (VS Code, a terminal) cannot be attached to: its editor window comes forward, as
    // the bar's icon finds it. With no list at all ("cannot tell") nothing is resumed or attached, the editor is all.
    [Fact]
    public void Going_to_a_task_whose_session_is_interactive_goes_to_its_editor_window_as_before()
    {
        using var rig = new Rig { Editor = Maybe.From((nint)0x77) };
        var task = rig.ClaudeTask();
        rig.Listed(Agent(SessionId, background: false));

        Assert.True(rig.Actions.Go(task).IsSuccess);

        Assert.Equal(rig.SparrowDesktop, rig.Harness.Desktops.CurrentId);
        Assert.Equal([new WindowHandle(0x77)], rig.BroughtForward);
        Assert.Empty(rig.Launches);

        using var unknown = new Rig { Editor = Maybe.From((nint)0x78) };
        var other = unknown.ClaudeTask();
        Assert.True(unknown.Actions.Go(other).IsSuccess);
        unknown.Settle();
        Assert.Equal([new WindowHandle(0x78)], unknown.BroughtForward);
        Assert.Empty(unknown.Resumes);
        Assert.Empty(unknown.Launches);
    }

    // Spec: "A double-click never starts a session: a note is often an errand."
    [Fact]
    public void Going_to_a_note_without_sessions_only_switches_workspace()
    {
        using var rig = new Rig();
        var note = rig.Tasks.AddNote("Dishwasher tablets", rig.Sparrow).Value;
        rig.Listed();

        Assert.True(rig.Actions.Go(note).IsSuccess);
        rig.Settle();

        Assert.Equal(rig.SparrowDesktop, rig.Harness.Desktops.CurrentId);
        Assert.Empty(rig.Launches);
        Assert.Empty(rig.Resumes);
        Assert.Empty(rig.Starts);
        Assert.Empty(rig.BroughtForward);
    }

    // Spec: "On a task with a running session it asks first, since a second session on one task is rarely meant." No
    // starts nothing; yes starts in the task's one folder (its latest session's repo) and opens the window on the new
    // session once Claude has started it.
    [Fact]
    public void Starting_on_a_task_with_a_live_session_asks_first() => StaThread.Run(() =>
    {
        using var rig = new Rig();
        var task = rig.ClaudeTask();
        rig.Listed(Agent(SessionId, background: true));

        Assert.Null(rig.Actions.Offer(task.Id));
        Assert.Equal([ClaudeTaskActions.AnotherSessionQuestion], rig.Asked);
        Assert.Empty(rig.Starts);

        rig.Confirms = true;
        rig.StartAnswer = _ => Result.Success("6785e6b5-0d8f-4a51-9a3e-1c2b3d4e5f60");
        Assert.Null(rig.Actions.Offer(task.Id));
        rig.Settle();

        Assert.Equal([("Fix the bar", Folder)], rig.Starts);
        Assert.Equal(ClaudeStart.AttachWindow("6785e6b5", Folder, "Fix the bar · Claude"), Assert.Single(rig.Launches), LaunchComparer.Instance);
    });

    // Spec, "Choosing the folder": a task with no sessions offers the repos its workspace knows (its tasks' sessions'
    // repos and its VS Code windows' folders, resolved through them), and several make a pick list at the pointer ending
    // with "Choose a folder…". A pick starts there.
    [Fact]
    public void Several_folders_offer_a_pick_list_ending_in_Choose_a_folder() => StaThread.Run(() =>
    {
        using var rig = new Rig();
        rig.Tasks.Create("A", rig.Sparrow, new TaskSession("s-a", @"C:\repos\Deskbar\.claude\worktrees\a", DateTimeOffset.UnixEpoch, Maybe<DateTimeOffset>.None));
        rig.Tasks.Create("B", rig.Gepha, new TaskSession("s-b", @"E:\work\iam.ge", DateTimeOffset.UnixEpoch, Maybe<DateTimeOffset>.None));
        rig.EditorNames = ["iam.ge"];
        var note = rig.Tasks.AddNote("Write the docs", rig.Sparrow).Value;
        rig.Listed();

        var menu = rig.Actions.Offer(note.Id);

        Assert.NotNull(menu);
        Assert.Equal([@"C:\repos\Deskbar", @"E:\work\iam.ge", "-", ClaudeTaskActions.ChooseFolderItem],
            menu.Items.Cast<object>().Select(i => i is MenuItem { Header: TextBlock text } ? text.Text : "-"));
        Assert.Empty(rig.Asked);

        menu.Items.OfType<MenuItem>().First().RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent));
        menu.IsOpen = false;
        Assert.Equal([("Write the docs", @"C:\repos\Deskbar")], rig.Starts);
    });

    // Review Focus 4: an untrusted folder. Claude's own words go into the thread, and Petre is offered a plain terminal
    // there (`wt -w new -d <folder> claude`) to accept the trust prompt himself; no second start, no retry.
    [Fact]
    public void An_untrusted_folder_comments_and_offers_a_terminal_to_trust_it()
    {
        using var rig = new Rig { TrustsTerminal = true };
        var task = rig.ClaudeTask();
        rig.Listed();
        rig.StartAnswer = folder => Result.Failure<string>($"Workspace not trusted. Run `claude` in {folder} once and accept the trust prompt, then retry.");

        Assert.True(rig.Actions.Start(task, Folder).IsSuccess);
        rig.Settle();

        Assert.Contains(rig.TaskOf(task.Id).Comments, c => c.Markdown.Contains("Workspace not trusted"));
        Assert.Contains(rig.Asked, a => a.EndsWith(ClaudeTaskActions.TrustQuestion, StringComparison.Ordinal));
        Assert.Equal(ClaudeStart.TrustWindow(Folder), Assert.Single(rig.Launches), LaunchComparer.Instance);
        Assert.Single(rig.Starts);
    }

    // With the feature off, nothing starts and Petre is told why.
    [Fact]
    public void Nothing_starts_while_the_feature_is_off() => StaThread.Run(() =>
    {
        using var rig = new Rig();
        var task = rig.ClaudeTask();
        Assert.True(rig.Harness.Manager.SetClaudeTasks(false).IsSuccess);

        Assert.Null(rig.Actions.Offer(task.Id));
        Assert.True(rig.Actions.Start(task, Folder).IsFailure);

        Assert.Equal([ClaudeTaskActions.FeatureOff], rig.Told);
        Assert.Empty(rig.Starts);
    });

    // ProgramLaunch holds a list, so two launches compare by their parts.
    sealed class LaunchComparer : IEqualityComparer<ProgramLaunch>
    {
        public static readonly LaunchComparer Instance = new();

        public bool Equals(ProgramLaunch? a, ProgramLaunch? b) =>
            a is not null && b is not null && a.Program == b.Program && a.Arguments.SequenceEqual(b.Arguments) && a.WorkingDirectory == b.WorkingDirectory;

        public int GetHashCode(ProgramLaunch launch) => launch.Program.GetHashCode();
    }
}
