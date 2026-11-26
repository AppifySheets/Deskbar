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
        public List<ConsoleLaunch> Consoles { get; } = [];
        // Whether wt.exe is installed; no on Windows Server 2022 (task abfe1db8).
        public bool HasTerminal { get; set; } = true;
        public List<WindowHandle> BroughtForward { get; } = [];
        public List<(Guid Task, string Session)> Resumes { get; } = [];
        public List<(string Title, string Folder)> Starts { get; } = [];

        // The model each start was given (task e52e33cb): none unless the caller named one.
        public List<Maybe<string>> Models { get; } = [];
        public List<string> Asked { get; } = [];
        public List<string> Told { get; } = [];
        public List<string> Forgotten { get; } = [];
        // `claude stop <short id>` calls, for "Stop Claude and close" (task 37cd7081), and what each answers.
        public List<string> Stopped { get; } = [];
        public Func<string, Result> StopAnswer { get; set; } = _ => Result.Success();
        public Func<ProgramLaunch, Result<int?>> LaunchAnswer { get; set; } = _ => Result.Success<int?>(4321);

        public Func<TaskSession, Result<ClaudeAgent>> ResumeAnswer { get; set; } = s => Agent(s.Id, background: true);
        public Func<string, Result<string>> StartAnswer { get; set; } = _ => Result.Success(SessionId);
        public bool Confirms { get; set; }
        public bool TrustsTerminal { get; set; }
        // Whether claude.exe can be found (ClaudeLocator), and the answer to the install question when it cannot.
        public bool Installed { get; set; } = true;
        public bool Installs { get; set; }
        public Func<TaskSession, bool> Transcript { get; set; } = _ => true;
        public Maybe<nint> Editor { get; set; } = Maybe<nint>.None;
        public IReadOnlyList<string> EditorNames { get; set; } = [];

        public ClaudeTaskActions Actions { get; }

        public Rig() =>
            Actions = new ClaudeTaskActions(Tasks, Harness.Manager, Live, new ClaudeTaskSeams(
                Start: (task, folder, model) =>
                {
                    Starts.Add((task.Title, folder));
                    Models.Add(model);
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
                    return LaunchAnswer(launch);
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
                OfferInstall = message =>
                {
                    Asked.Add(message);
                    return Installs;
                },
                ClaudeInstalled = () => Installed,
                Tell = Told.Add,
                Forget = Forgotten.Add,
                Stop = shortId =>
                {
                    Stopped.Add(shortId);
                    return System.Threading.Tasks.Task.FromResult(StopAnswer(shortId));
                },
                HasWindowsTerminal = () => HasTerminal,
                OpenConsole = console =>
                {
                    Consoles.Add(console);
                    return Result.Success<int?>(5432);
                },
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

    // Task abfe1db8: Windows Server 2022 has no Windows Terminal, and "Could not start wt.exe" left every click without a
    // window. Without wt.exe the window is a console of claude.exe's own, titled as the terminal's would be, expected on
    // the task's workspace by claude's process name (a classic console window reports its client as its owner).
    [Fact]
    public void Without_windows_terminal_the_attach_window_is_a_console_on_its_workspace()
    {
        using var rig = new Rig { HasTerminal = false };
        var task = rig.ClaudeTask();
        rig.Listed(Agent(SessionId, background: true));

        Assert.True(rig.Actions.Go(task).IsSuccess);

        Assert.Empty(rig.Launches);
        var console = Assert.Single(rig.Consoles);
        Assert.Equal(["attach", "884c127f"], console.Arguments);
        Assert.Equal(Folder, console.WorkingDirectory);
        Assert.Equal("Fix the bar · Claude", console.Title.GetValueOrDefault());
        Assert.True(rig.Harness.Manager.AwaitsAny("claude"));
        Assert.False(rig.Harness.Manager.AwaitsAny("WindowsTerminal"));
    }

    // The console window is found again by its title like the terminal's, so a second click brings it forward instead of
    // a second attach taking the session from it; and the task counts as having a window while it is open.
    [Fact]
    public void Without_windows_terminal_going_again_brings_the_console_forward()
    {
        using var rig = new Rig { HasTerminal = false };
        var task = rig.ClaudeTask();
        rig.Listed(Agent(SessionId, background: true));
        rig.Actions.Go(task);

        var window = new WindowInfo(new WindowHandle(0x7171), 71, "claude", @"C:\Users\me\.local\bin\claude.exe", "Fix the bar · Claude", null);
        rig.Harness.Desktops.Placements[window.Handle] = rig.SparrowDesktop;
        rig.Harness.Monitor.Pump.OnNext(new WindowEvent(WindowEventKind.Appeared, window));
        rig.Actions.Go(task);

        Assert.Single(rig.Consoles);
        Assert.Equal([window.Handle], rig.BroughtForward);
        Assert.True(ClaudeTaskActions.HasWindow(rig.Harness.Manager, task));
    }

    // Task 4846fde1 (Petre: "active claude terminal window should highlight the current task in deskbar"): the window
    // Deskbar opened on a task's session names that task, found by the same title Attach finds it by, whether Windows
    // Terminal's or the console window where there is none. Any other window names no task.
    [Fact]
    public void A_session_window_names_its_task()
    {
        using var rig = new Rig();
        var task = rig.ClaudeTask();
        rig.ClaudeTask("Another task", "11111111-2222-3333-4444-555555555555");
        var window = rig.TerminalWindow("Fix the bar · Claude");

        Assert.Equal(task.Id, rig.Actions.TaskOfWindow(window.Handle).GetValueOrDefault());

        var plain = new WindowInfo(new WindowHandle(0x7272), 72, "WindowsTerminal", @"C:\Program Files\WindowsApps\WindowsTerminal.exe", "Fix the bar", null);
        rig.Harness.Desktops.Placements[plain.Handle] = rig.SparrowDesktop;
        rig.Harness.Monitor.Pump.OnNext(new WindowEvent(WindowEventKind.Appeared, plain));
        Assert.True(rig.Actions.TaskOfWindow(plain.Handle).HasNoValue);
        Assert.True(rig.Actions.TaskOfWindow(new WindowHandle(0x9999)).HasNoValue);

        using var console = new Rig { HasTerminal = false };
        var onConsole = console.ClaudeTask();
        var claudeWindow = new WindowInfo(new WindowHandle(0x7171), 71, "claude", @"C:\Users\me\.local\bin\claude.exe", "Fix the bar · Claude", null);
        console.Harness.Desktops.Placements[claudeWindow.Handle] = console.SparrowDesktop;
        console.Harness.Monitor.Pump.OnNext(new WindowEvent(WindowEventKind.Appeared, claudeWindow));
        Assert.Equal(onConsole.Id, console.Actions.TaskOfWindow(claudeWindow.Handle).GetValueOrDefault());
    }

    // A closed task's window names nothing (a closed task is never selected, TaskSelection.KeepOnly). Two open tasks
    // whose titles give the same window title: the one with a session, and with both or neither, no answer rather than
    // a guess that would highlight the wrong line.
    [Fact]
    public void A_session_window_names_no_task_when_the_title_is_ambiguous_or_closed()
    {
        using var rig = new Rig();
        var first = rig.ClaudeTask();
        var window = rig.TerminalWindow("Fix the bar · Claude");

        var note = rig.Tasks.AddNote("Fix the bar", rig.Sparrow).Value;
        Assert.Equal(first.Id, rig.Actions.TaskOfWindow(window.Handle).GetValueOrDefault());

        var second = rig.ClaudeTask(session: "11111111-2222-3333-4444-555555555555");
        Assert.True(rig.Actions.TaskOfWindow(window.Handle).HasNoValue);

        Assert.True(rig.Tasks.Close(first.Id, stoppingClaude: true).IsSuccess);
        Assert.Equal(second.Id, rig.Actions.TaskOfWindow(window.Handle).GetValueOrDefault());

        Assert.True(rig.Tasks.Close(second.Id, stoppingClaude: true).IsSuccess);
        Assert.Equal(note.Id, rig.Actions.TaskOfWindow(window.Handle).GetValueOrDefault());
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

    // Task 6a57f59b: CanOpen answers what Go would do, so a line offers Open Claude only where the click opens something.
    // A listed session opens, an ended one with its transcript resumes, and with no list at all the editor is tried. An
    // ended session that cannot be resumed (the App's seam also says no once its worktree is gone) and a task with no
    // session at all have nothing to open.
    [Fact]
    public void Can_open_only_a_session_the_click_can_reach()
    {
        using var rig = new Rig();
        var task = rig.ClaudeTask();
        Assert.True(rig.Actions.CanOpen(task));
        rig.Listed(Agent(SessionId, background: true));
        Assert.True(rig.Actions.CanOpen(task));
        rig.Listed();
        Assert.True(rig.Actions.CanOpen(task));

        rig.Transcript = _ => false;
        Assert.False(rig.Actions.CanOpen(task));
        // Listed, it opens whatever the disk says: the list is what Go goes by first.
        rig.Listed(Agent(SessionId, background: true));
        Assert.True(rig.Actions.CanOpen(task));

        var note = rig.Tasks.AddNote("Dishwasher tablets", rig.Sparrow).Value;
        Assert.False(rig.Actions.CanOpen(note));
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

    // Task 6a57f59b (Petre: "and it's waiting on me"): a task the keeper gave up on (its session could not be resumed)
    // waits on Petre with the give-up's question. A fresh session started on it answers that, so the task is back In
    // progress with no question. A question of the session's own is left alone by a start.
    [Fact]
    public void A_start_clears_the_keepers_give_up_and_nothing_else()
    {
        using var rig = new Rig();
        var task = rig.ClaudeTask();
        rig.Listed();
        Assert.True(rig.Tasks.Ask(task.Id, SessionKeeper.ResumeFailed).IsSuccess);

        Assert.True(rig.Actions.Start(rig.TaskOf(task.Id), Folder).IsSuccess);
        rig.Settle();
        Assert.Equal(Deskbar.Core.Tasks.TaskStatus.InProgress, rig.TaskOf(task.Id).Status);
        Assert.False(rig.TaskOf(task.Id).Question.HasValue);

        var asked = rig.ClaudeTask("Merge it", "aaaaaaaa-0000-0000-0000-000000000001");
        Assert.True(rig.Tasks.Ask(asked.Id, "Merge PR 512").IsSuccess);
        Assert.True(rig.Actions.Start(rig.TaskOf(asked.Id), Folder).IsSuccess);
        rig.Settle();
        Assert.Equal(Deskbar.Core.Tasks.TaskStatus.WaitingOnYou, rig.TaskOf(asked.Id).Status);
        Assert.Equal("Merge PR 512", rig.TaskOf(asked.Id).Question.GetValueOrDefault());
    }

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

    // Task abfe1db8, the case in the report: on a terminal server without Windows Terminal, "Yes" to the trust question
    // opened nothing ("Could not start wt.exe"). It opens a console running plain claude in the folder instead.
    [Fact]
    public void Without_windows_terminal_the_trust_window_is_a_console()
    {
        using var rig = new Rig { TrustsTerminal = true, HasTerminal = false };
        var task = rig.ClaudeTask();
        rig.Listed();
        rig.StartAnswer = folder => Result.Failure<string>($"Workspace not trusted. Run `claude` in {folder} once and accept the trust prompt, then retry.");

        rig.Actions.Start(task, Folder);
        rig.Settle();

        Assert.Empty(rig.Launches);
        var console = Assert.Single(rig.Consoles);
        Assert.Equal("claude", console.Program);
        Assert.Empty(console.Arguments);
        Assert.Equal(Folder, console.WorkingDirectory);
        Assert.False(console.Title.HasValue);
        Assert.Empty(rig.Told);
    }

    // Task 3641561d: "Start Claude on this" on a machine without Claude Code. Instead of only the not-found message, Petre
    // is asked whether to install it, and "Yes" opens a terminal in the chosen folder running the official installer and
    // then claude, expected on the task's workspace like every other window Deskbar opens for a task. The question is the
    // whole answer, so nothing else is told and nothing is said in the thread.
    [Fact]
    public void Without_claude_a_start_offers_to_install_it_in_a_terminal()
    {
        using var rig = new Rig { Installed = false, Installs = true };
        var task = rig.ClaudeTask();
        rig.Listed();
        rig.StartAnswer = _ => Result.Failure<string>("Claude Code is not installed where Deskbar can find it.");

        Assert.True(rig.Actions.Start(task, Folder).IsSuccess);
        rig.Settle();

        var asked = Assert.Single(rig.Asked);
        Assert.Contains(Folder, asked);
        Assert.EndsWith(ClaudeTaskActions.InstallQuestion, asked, StringComparison.Ordinal);
        Assert.Equal(ClaudeStart.InstallWindow(Folder), Assert.Single(rig.Launches), LaunchComparer.Instance);
        Assert.True(rig.Harness.Manager.AwaitsAny("WindowsTerminal"));
        Assert.Empty(rig.Told);
        Assert.Empty(rig.TaskOf(task.Id).Comments);
    }

    // Without Windows Terminal the installer runs in a console of PowerShell's own, expected by that process's name.
    [Fact]
    public void Without_claude_or_windows_terminal_the_install_runs_in_a_console()
    {
        using var rig = new Rig { Installed = false, Installs = true, HasTerminal = false };
        var task = rig.ClaudeTask();
        rig.Listed();
        rig.StartAnswer = _ => Result.Failure<string>("Claude Code is not installed where Deskbar can find it.");

        rig.Actions.Start(task, Folder);
        rig.Settle();

        Assert.Empty(rig.Launches);
        Assert.Equal("powershell.exe", Assert.Single(rig.Consoles).Program);
        Assert.True(rig.Harness.Manager.AwaitsAny("powershell"));
    }

    // "No" installs nothing; the reason, with the way to install by hand, stays in the task's thread.
    [Fact]
    public void Without_claude_declining_the_install_leaves_the_reason_in_the_thread()
    {
        using var rig = new Rig { Installed = false, Installs = false };
        var task = rig.ClaudeTask();
        rig.Listed();
        rig.StartAnswer = _ => Result.Failure<string>("Claude Code is not installed where Deskbar can find it.");

        rig.Actions.Start(task, Folder);
        rig.Settle();

        Assert.Single(rig.Asked);
        Assert.Empty(rig.Launches);
        Assert.Empty(rig.Consoles);
        Assert.Contains(rig.TaskOf(task.Id).Comments, c => c.Markdown.Contains("not installed"));
        Assert.Empty(rig.Told);
    }

    // A failure while claude IS installed is not an install question, whatever it says.
    [Fact]
    public void With_claude_installed_a_failure_is_told_not_offered_an_install()
    {
        using var rig = new Rig { Installs = true };
        var task = rig.ClaudeTask();
        rig.Listed();
        rig.StartAnswer = _ => Result.Failure<string>("claude exited with code 1.");

        rig.Actions.Start(task, Folder);
        rig.Settle();

        Assert.Empty(rig.Asked);
        Assert.Empty(rig.Launches);
        Assert.Single(rig.Told);
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

    // Final review, item 4: a resumed session goes through the same rule as one found listed. One the list shows as
    // interactive (someone sits in it) is not attached to: its editor window comes forward instead.
    [Fact]
    public void A_resumed_session_that_is_interactive_goes_to_its_editor_not_a_terminal()
    {
        using var rig = new Rig { Editor = Maybe.From((nint)0x77), ResumeAnswer = s => Agent(s.Id, background: false) };
        var task = rig.ClaudeTask();
        rig.Listed();

        Assert.True(rig.Actions.Go(task).IsSuccess);
        rig.Settle();

        Assert.Single(rig.Resumes);
        Assert.Empty(rig.Launches);
        Assert.Equal([new WindowHandle(0x77)], rig.BroughtForward);
    }

    // Final review, item 3: a task the keeper gave up on waits for Petre. A double-click that brings its session back
    // (resumed, or found running) is him acting on it: the task goes back to In progress with no question, and the
    // session's old resumes are forgotten so the loop guard does not give up again at once.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Bringing_back_a_given_up_task_clears_the_give_up(bool alreadyListed)
    {
        using var rig = new Rig();
        var task = rig.ClaudeTask();
        Assert.True(rig.Tasks.Ask(task.Id, SessionKeeper.LoopGaveUp).IsSuccess);
        if (alreadyListed) rig.Listed(Agent(SessionId, background: true));
        else rig.Listed();

        Assert.True(rig.Actions.Go(rig.TaskOf(task.Id)).IsSuccess);
        rig.Settle();

        var now = rig.TaskOf(task.Id);
        Assert.Equal(Deskbar.Core.Tasks.TaskStatus.InProgress, now.Status);
        Assert.True(now.Question.HasNoValue);
        Assert.Equal([SessionId], rig.Forgotten);
        Assert.Single(rig.Launches);
        Assert.Equal(alreadyListed ? 0 : 1, rig.Resumes.Count);
    }

    // A question a SESSION asked is not a give-up: bringing its window forward answers nothing.
    [Fact]
    public void Going_to_a_task_waiting_on_a_sessions_question_leaves_it_waiting()
    {
        using var rig = new Rig();
        var task = rig.ClaudeTask();
        Assert.True(rig.Tasks.Ask(task.Id, "OK to merge?").IsSuccess);
        rig.Listed(Agent(SessionId, background: true));

        Assert.True(rig.Actions.Go(rig.TaskOf(task.Id)).IsSuccess);

        Assert.Equal(Deskbar.Core.Tasks.TaskStatus.WaitingOnYou, rig.TaskOf(task.Id).Status);
        Assert.Empty(rig.Forgotten);
    }

    // Final review, item 12: `claude` missing makes the start fail after Start answered "under way". The reason goes to
    // the thread and to Petre at once; so does a terminal that cannot open (`wt` missing).
    [Fact]
    public void A_start_that_fails_or_a_window_that_cannot_open_is_told()
    {
        using var rig = new Rig();
        var task = rig.ClaudeTask();
        rig.Listed();
        rig.StartAnswer = _ => Result.Failure<string>("Could not run claude: The system cannot find the file specified.");

        Assert.True(rig.Actions.Start(task, Folder).IsSuccess);
        rig.Settle();

        Assert.Contains(rig.Told, t => t.Contains("Could not run claude"));
        Assert.Contains(rig.TaskOf(task.Id).Comments, c => c.Markdown.Contains("Could not run claude"));
        Assert.Empty(rig.Asked);

        rig.Told.Clear();
        rig.StartAnswer = _ => Result.Success("6785e6b5-0d8f-4a51-9a3e-1c2b3d4e5f60");
        rig.LaunchAnswer = _ => Result.Failure<int?>("Could not start wt: The system cannot find the file specified.");
        Assert.True(rig.Actions.Start(task, Folder).IsSuccess);
        rig.Settle();

        Assert.Contains(rig.Told, t => t.Contains("Could not start wt"));
        Assert.Contains(rig.TaskOf(task.Id).Comments, c => c.Markdown.Contains("Could not start wt"));
    }

    // Task 37cd7081: "Stop Claude and close" closes the task past the live-session refusal, then stops each of its running
    // background sessions by short id and says so in the thread. A VS Code or terminal session is not stopped (`claude
    // stop` cannot), and the thread says its window stays open.
    [Fact]
    public void Stop_and_close_closes_then_stops_the_background_sessions() => StaThread.Run(() =>
    {
        using var rig = new Rig();
        var task = rig.ClaudeTask();
        var other = "22222222-3333-4444-5555-666666666666";
        Assert.True(rig.Tasks.Join(task.Id, new TaskSession(other, Folder, DateTimeOffset.UnixEpoch, Maybe<DateTimeOffset>.None)).IsSuccess);
        rig.Listed(Agent(SessionId, background: true, busy: true), Agent(other, background: false));
        Assert.Equal(TaskViews.ClaudeStillWorkingRefusal, rig.Tasks.Close(task.Id).Error);

        Assert.True(rig.Actions.StopAndClose(rig.TaskOf(task.Id)).IsSuccess);
        rig.Settle();

        Assert.Equal(TaskStatus.Closed, rig.TaskOf(task.Id).Status);
        Assert.Equal([SessionId[..8]], rig.Stopped);
        Assert.Contains(rig.TaskOf(task.Id).Comments, c => c.Markdown.StartsWith("Claude stopped by Deskbar"));
        Assert.Contains(rig.TaskOf(task.Id).Comments, c => c.Markdown.StartsWith("Claude's window is still open"));
    });

    // A stop that fails leaves the task closed and says why in the thread.
    [Fact]
    public void A_failed_stop_is_said_in_the_thread() => StaThread.Run(() =>
    {
        using var rig = new Rig { StopAnswer = _ => Result.Failure("no such session") };
        var task = rig.ClaudeTask();
        rig.Listed(Agent(SessionId, background: true));

        Assert.True(rig.Actions.StopAndClose(rig.TaskOf(task.Id)).IsSuccess);
        rig.Settle();

        Assert.Equal(TaskStatus.Closed, rig.TaskOf(task.Id).Status);
        Assert.Contains(rig.TaskOf(task.Id).Comments, c => c.Markdown == "Claude could not be stopped: no such session");
    });

    // Item 12: a closed task is refused up front, from the menu and from `task start` alike, before any question.
    [Fact]
    public void Starting_on_a_closed_task_is_refused_up_front() => StaThread.Run(() =>
    {
        using var rig = new Rig { Confirms = true };
        var task = rig.ClaudeTask();
        rig.Listed(Agent(SessionId, background: true));
        Assert.True(rig.Tasks.Close(task.Id, stoppingClaude: true).IsSuccess);

        Assert.Null(rig.Actions.Offer(task.Id));
        var started = rig.Actions.Start(task, Folder);

        Assert.Equal([RemoteControl.ClosedTaskRefusal], rig.Told);
        Assert.Equal(RemoteControl.ClosedTaskRefusal, started.Error);
        Assert.Empty(rig.Asked);
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
