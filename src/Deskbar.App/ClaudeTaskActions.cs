using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using CSharpFunctionalExtensions;
using Deskbar.Core;
using Deskbar.Core.Control;
using Deskbar.Core.Domain;
using Deskbar.Core.QuickFind;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// WHAT A CLICK ON A TASK DOES WITH ITS CLAUDE SESSIONS (Task 9). Design:
// docs/superpowers/specs/2026-11-23-tasks-open-claude-sessions-design.md, sections "Where the clicks are", "Choosing the
// folder", "The window on a session" and "Ended sessions and transcript retention". Measurements:
// docs/superpowers/notes/2026-11-23-claude-background-probes.md.
//
// Every surface goes through here, so they cannot disagree: the bar's double-click and the tasks window's (both through
// App.GoToTask), the line menus' "Start Claude on this", the thread's session list and its Open buttons, and the control
// pipe's `task go|start|open-session` (Commands). ClaudeSessions below it polls `claude agents --json`, starts and
// resumes sessions; this class decides what a click does with what that list says, and puts the window on a session.
//
// GOING TO A TASK (Go). The task's workspace first, as before this change (a free task stays where Petre is). Then the
// session TaskViews.SessionToGoTo names, judged by the latest list ClaudeSessions read:
//   * listed and background: its terminal window. Only ONE window can be attached to a session: a second `claude attach`
//     takes the session over and the first window closes by itself (measured), so a window already open on it, found
//     by its title, is brought forward; only when there is none is a new one opened (`wt -w new ... claude attach`),
//     through WorkspaceManager.StartIn, which expects the new WindowsTerminal window on the task's workspace (or the
//     one in front for a free task) the way `deskbar.cmd start` places a program it starts;
//   * listed and interactive (VS Code, a terminal Petre sits in): its editor window brought forward, when the window
//     with its folder can be found (EditorFolder.Best, the rule the bar's icon uses); nothing more can be attached;
//   * not listed, transcript gone ("expired"): nothing more, the thread shows it expired;
//   * not listed, transcript there: resumed now (ClaudeSessions.ResumeNow, under the keeper's guards), and attached once
//     the list shows it. A failure has said why in the task's thread by then;
//   * no session (a note): the workspace switch alone. A double-click never starts a session: a note is often an errand.
// With no list at all ("cannot tell": the feature off, or `claude` unreadable) nothing is resumed or attached; only the
// editor window is brought forward, as the bar did before.
//
// The window's title is ClaudeStart.AttachTitle (`<title> · Claude`); the "Deskbar Claude" profile keeps it
// (suppressApplicationTitle), so the same title finds the window again. A window launched a moment ago and not yet
// shown counts as open for AttachPending, so a second click during wt's start does not launch a second attach.
//
// STARTING CLAUDE ON A TASK (Offer for the menus and the thread's button, Start for the pipe). A task with a running
// session asks first ("rarely meant"). The folder is ClaudeStart.Folders: exactly one, it starts there; several or
// none, a pick list at the pointer ends with "Choose a folder…". `claude --bg` runs off the UI thread; once the session
// is found, its window opens as above. A failure goes into the task's thread in Claude's words, and an untrusted
// folder offers a plain terminal running `claude` there, so Petre answers the trust prompt himself: Deskbar never
// accepts trust for him.
//
// A START WITHOUT CLAUDE CODE (task 3641561d). When the start failed because no claude.exe can be found (the seam
// ClaudeInstalled, asked once the failure is in), Petre is asked whether to install it, with the folder he chose named.
// "Yes" opens a terminal there running Claude Code's official installer and then claude itself (ClaudeStart.InstallWindow,
// InstallConsole), where he signs in and trusts the folder; after that, "Start Claude on this" works. Nothing is retried on
// its own: a session cannot start before he has signed in, and a retry would only fail again with a second message. "No"
// leaves the reason, with the way to install by hand, in the task's thread. The question is asked whichever way the start
// came (a menu, the thread's button, the pipe's `task start`), as the trust question is.
//
// STOP CLAUDE AND CLOSE (task 37cd7081, StopAndClose). A task Claude is still working on (open, not Done, a live
// session) refuses a plain close (TaskBoard.Close): closing ends no session, and a session left running on a closed task
// has nowhere to report. The ticks, the menus' Close and `task close --stop-claude` offer this instead: the task is
// closed first, past the refusal, so the keeper never resumes it, and then each of its sessions the latest list shows
// running in the background gets `claude stop`, which keeps the conversation (measured 2026-11-23: double-click the task
// to resume it). The thread says what happened to each. A VS Code or terminal session cannot be stopped from outside
// (`claude stop` knows only background ones): its window stays open, and its next prompt tells it the task was closed.
// With no list (the feature off, or `claude` unreadable) nothing is stopped and the thread says so.
//
// THREADS. Everything here runs on the UI thread (`scheduler` in the app); the answers of `claude` arrive back on it.
public sealed class ClaudeTaskActions : IDisposable
{
    /// <summary>The asking before a second session on a task (the spec: "a second session on one task is rarely meant").</summary>
    public const string AnotherSessionQuestion = "This task already has a Claude session running. Start another one?";

    /// <summary>The pick list's last item, which opens Windows' folder picker.</summary>
    public const string ChooseFolderItem = "Choose a folder…";

    /// <summary>What an untrusted folder's failure adds to Claude's own message, as a Yes/No question.</summary>
    public const string TrustQuestion = "Open a terminal there so you can trust it?";

    /// <summary>The question after a start found no Claude Code (see the header), below the folder it would start in.</summary>
    public const string InstallQuestion =
        "Install it now? A terminal opens there, runs Claude Code's official installer and then starts Claude so you can "
        + "sign in and trust the folder. After that, choose Start Claude on this again.";

    /// <summary>Why nothing starts while the feature is off.</summary>
    public const string FeatureOff =
        "Claude sessions are off. Turn on \"Claude Code sessions keep their tasks in the task list\" in Manage first.";

    // Every window of Windows Terminal belongs to this process, whichever `wt.exe` asked for it, so a new window is
    // expected by this name, not by wt's.
    const string TerminalProcess = "WindowsTerminal";

    // Without Windows Terminal (task abfe1db8) the window is a classic console started on claude.exe, and such a window
    // reports its client, claude.exe, as its owner (measured, see ClaudeStart.AttachConsole).
    const string ConsoleProcess = "claude";

    // How long a window launched on a session counts as on its way: wt takes a second or two to show it (measured),
    // and an attach issued meanwhile would take the session from the window that is still opening.
    static readonly TimeSpan AttachPending = TimeSpan.FromSeconds(10);

    readonly TaskService tasks;
    readonly WorkspaceManager manager;
    readonly ClaudeTaskSeams seams;
    readonly IScheduler scheduler;
    readonly Action<string>? trace;
    readonly Subject<Unit> changed = new();
    readonly IDisposable liveSubscription;

    // The latest list ClaudeSessions could read, none before the first.
    Maybe<IReadOnlyCollection<ClaudeAgent>> latest = Maybe<IReadOnlyCollection<ClaudeAgent>>.None;

    // Sessions whose window was launched, and when (see AttachPending).
    ImmutableDictionary<string, DateTimeOffset> attaching = ImmutableDictionary<string, DateTimeOffset>.Empty;

    /// <param name="live">ClaudeSessions.Live: every list `claude agents --json` gave that could be read.</param>
    /// <param name="scheduler">Where answers land: the UI thread in the app.</param>
    public ClaudeTaskActions(TaskService tasks, WorkspaceManager manager, IObservable<IReadOnlyCollection<ClaudeAgent>> live,
        ClaudeTaskSeams seams, IScheduler scheduler, Action<string>? trace = null)
    {
        this.tasks = tasks;
        this.manager = manager;
        this.seams = seams;
        this.scheduler = scheduler;
        this.trace = trace;
        liveSubscription = live.Subscribe(list =>
        {
            latest = Maybe.From(list);
            changed.OnNext(Unit.Default);
        });
    }

    /// <summary>Pulses when the list changes, so a thread showing the sessions' states redraws.</summary>
    public IObservable<Unit> Changed => changed.AsObservable();

    /// <summary>The pipe's three verbs (RemoteControl.Tasks.cs), answered by the methods the clicks use.</summary>
    public TaskSessionCommands Commands => new(Go, Folders, Start, OpenSession) { StopAndClose = StopAndClose };

    // The latest list while the feature is on; none while it is off, whatever was read before it was turned off.
    Maybe<IReadOnlyCollection<ClaudeAgent>> Latest =>
        manager.State.ClaudeTasksInEffect ? latest : Maybe<IReadOnlyCollection<ClaudeAgent>>.None;

    // --- going ------------------------------------------------------------------------------------------------------

    /// <summary>A double-click on a line, `Go` in the tasks window and `task go` (see the header).</summary>
    public Result Go(TaskItem task) =>
        SwitchTo(task).Tap(() => TaskViews.SessionToGoTo(task).Tap(session => Open(task, session)));

    /// <summary>The thread's Open on one session and `task open-session`: what going to that session does.</summary>
    public Result OpenSession(TaskItem task, string sessionId) =>
        task.Sessions.TryFirst(s => s.Id == sessionId)
            .ToResult($"The task has no session {sessionId}")
            .Bind(session => SwitchTo(task).Tap(() => Open(task, session)));

    Result SwitchTo(TaskItem task) => task.WorkspaceId.HasValue ? manager.Switch(task.WorkspaceId.Value) : Result.Success();

    void Open(TaskItem task, TaskSession session)
    {
        if (Latest is not { HasValue: true } listed)
        {
            ToEditor(session.Folder, session.Id);
            return;
        }
        if (listed.Value.TryFirst(a => a.SessionId == session.Id) is { HasValue: true } agent)
        {
            Shown(task, agent.Value);
            return;
        }
        if (!seams.TranscriptExists(session))
        {
            trace?.Invoke($"claude actions: {session.Id} has expired, nothing to open");
            return;
        }
        Resume(task, session);
    }

    // An interactive session's window, as the bar found it before: the VS Code window showing its folder.
    void ToEditor(string folder, string sessionId) =>
        seams.EditorWindow(folder).Tap(hwnd => seams.BringForward(new WindowHandle(hwnd))
            .TapError(e => trace?.Invoke($"claude actions: editor window of {sessionId} not brought forward: {e}")));

    // Resumed off the UI thread; shown once ClaudeSessions finds it listed. A failure was said in the thread there.
    void Resume(TaskItem task, TaskSession session) =>
        Observable.FromAsync(() => seams.Resume(task.Id, session))
            .ObserveOn(scheduler)
            .Subscribe(
                woken => woken
                    .Tap(agent => Current(task.Id).Tap(t => Shown(t, agent)))
                    .TapError(e => trace?.Invoke($"claude actions: {session.Id} not resumed: {e}")),
                error => trace?.Invoke($"claude actions: resume of {session.Id} threw: {error.Message}"));

    // A session the list shows, found at once or after a resume: ONE rule for both (final review, item 4), so a resumed
    // session someone sits in is never handed a terminal. Background: the window attached to it. Interactive: its
    // editor window.
    //
    // A task the keeper had given up on is running again now (final review, item 3), so the give-up goes: back to In
    // progress with no question, and the session's old resumes forgotten (seams.Forget, ClaudeSessions.Forget), or the
    // loop guard would still count them and give up again on the next poll. A double-click on such a task is Petre acting
    // on it, which is what the give-up was waiting for.
    void Shown(TaskItem task, ClaudeAgent agent)
    {
        var current = Current(task.Id).GetValueOrDefault(task);
        if (SessionKeeper.GivenUp(current))
            tasks.SetStatus(current.Id, Deskbar.Core.Tasks.TaskStatus.InProgress)
                .Tap(() => seams.Forget(agent.SessionId))
                .TapError(e => trace?.Invoke($"claude actions: give-up on {current.Id} not cleared: {e}"));
        if (agent.Background) Attach(current, agent);
        else ToEditor(current.Sessions.TryFirst(s => s.Id == agent.SessionId).Map(s => s.Folder).GetValueOrDefault(agent.Folder), agent.SessionId);
    }

    // The window on a background session: the open one brought forward, else a new one (see the header).
    void Attach(TaskItem task, ClaudeAgent agent)
    {
        var title = ClaudeStart.AttachTitle(task.Title);
        if (manager.KnownWindows.FirstOrDefault(w => Titled(manager, w, title)) is { } open)
        {
            seams.BringForward(open.Handle).TapError(e => trace?.Invoke($"claude actions: window on {agent.SessionId} not brought forward: {e}"));
            return;
        }
        if (attaching.TryGetValue(agent.SessionId, out var launched) && scheduler.Now - launched < AttachPending) return;

        // A background session's short id is the first eight characters of its id (measured, also after a resume).
        var shortId = agent.ShortId.GetValueOrDefault(agent.SessionId[..Math.Min(8, agent.SessionId.Length)]);
        // A launch that fails (wt missing from the PATH, say) is said in the thread AND to Petre at once (final review,
        // item 12): he clicked and no window came, and the thread is not where he is looking.
        var claude = seams.Claude();
        Launch(task, ClaudeStart.AttachWindow(shortId, agent.Folder, title, claude), ClaudeStart.AttachConsole(shortId, agent.Folder, title, claude))
            .Tap(() => attaching = attaching.SetItem(agent.SessionId, scheduler.Now))
            .TapError(e => SayAndTell(task.Id, $"Deskbar could not open a window on session {shortId}:\n\n{e}"));
    }

    /// <summary>
    /// Whether a window Deskbar opened on the task's session is open: the Windows Terminal window titled for the task, as
    /// Attach finds it. ClaudeSessions leaves a finished task's session running while one is (job 5 there), so a stop
    /// never closes the window Petre is reading.
    /// </summary>
    public static bool HasWindow(WorkspaceManager manager, TaskItem task)
    {
        var title = ClaudeStart.AttachTitle(task.Title);
        return manager.KnownWindows.Any(w => Titled(manager, w, title));
    }

    /// <summary>
    /// The task a window is on, when it is a window Deskbar opened on a task's session: the open task whose
    /// <see cref="ClaudeStart.AttachTitle"/> the window carries, by the same rule Attach finds it by. The App selects
    /// that task whenever the window comes to the front (task 4846fde1, Petre: "active claude terminal window should
    /// highlight the current task in deskbar"), so the line of the session he is typing into is the one lit on the bar
    /// and in the tasks panel.
    /// </summary>
    /// <remarks>
    /// Only open tasks count, since a closed one is never selected (TaskSelection.KeepOnly). The title is cut to
    /// ClaudeStart.MaxTitle, so two tasks can give the same window title; then the one with a session is meant (the
    /// window is on a session), and with both or neither having one there is no answer, never a guess that lights the
    /// wrong line. A VS Code window is never an answer: one folder holds the sessions of many tasks.
    /// </remarks>
    public Maybe<Guid> TaskOfWindow(WindowHandle window) =>
        manager.KnownWindows.TryFirst(w => w.Handle == window)
            .Bind(info => OnlyOne(tasks.Board.Tasks.Where(t => t.IsOpen && Titled(manager, info, ClaudeStart.AttachTitle(t.Title))).ToList()))
            .Map(task => task.Id);

    // The one task a window's title names: the only one, else the only one of them with a session, else none.
    static Maybe<TaskItem> OnlyOne(IReadOnlyList<TaskItem> named) =>
        named is [var only] ? only
            : named.Where(t => t.Sessions.Any()).ToList() is [var withSession] ? withSession
            : Maybe<TaskItem>.None;

    // A Windows Terminal window with this title, or the console window Deskbar opens where there is no Windows Terminal
    // (claude.exe's own), as it reads now or read before a rename rule shortened it.
    static bool Titled(WorkspaceManager manager, WindowInfo window, string title) =>
        (window.ProcessName.Equals(TerminalProcess, StringComparison.OrdinalIgnoreCase)
            || window.ProcessName.Equals(ConsoleProcess, StringComparison.OrdinalIgnoreCase))
        && (window.Title == title || manager.OriginalTitle(window.Handle).Map(t => t == title).GetValueOrDefault(false));

    // A terminal window started for the task: Windows Terminal's when wt.exe is installed, else a console window on
    // claude.exe (task abfe1db8: Windows Server 2022 has no Windows Terminal, and every window here failed with "Could not
    // start wt.exe"). Asked at each launch, like claude.exe's own lookup, so installing Windows Terminal while Deskbar
    // runs takes effect at the next click.
    Result Launch(TaskItem task, ProgramLaunch terminal, ConsoleLaunch console) =>
        seams.HasWindowsTerminal()
            ? Launch(task, new LaunchTarget(terminal, TerminalProcess), seams.Launch)
            // The console's ProgramLaunch is only what StartIn names the expectation by (claude.exe's file name); the
            // start itself is the console's, with its title and environment.
            : Launch(task, new LaunchTarget(new ProgramLaunch(console.Program, console.Arguments, console.WorkingDirectory), ConsoleProcessOf(console)),
                _ => seams.OpenConsole(console));

    // The process a console window shows as: its own program's (a classic console window reports its client as its owner),
    // so claude for the attach and trust consoles, whether claude.exe came by its full path or by name, and powershell for
    // the install console.
    static string ConsoleProcessOf(ConsoleLaunch console) => System.IO.Path.GetFileNameWithoutExtension(console.Program);

    // Expected on its workspace (the one in front for a free task) through StartIn, so it lands there like any program
    // `deskbar.cmd start` starts. With no workspace at all (an unnamed desktop in front) it is started where Petre is.
    Result Launch(TaskItem task, LaunchTarget target, Func<ProgramLaunch, Result<int?>> starter) =>
        task.WorkspaceId.Or(manager.CurrentWorkspaceId) is { HasValue: true } workspace
            ? manager.StartIn(workspace.Value, target, starter).Bind(_ => Result.Success())
            : starter(target.Launch).Bind(_ => Result.Success());

    // --- starting ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The folders a new session on the task could start in, the likeliest first (ClaudeStart.Folders): its latest
    /// session's repo, or the repos of its workspace's tasks and VS Code windows.
    /// </summary>
    public IReadOnlyList<string> Folders(TaskItem task) =>
        ClaudeStart.Folders(task, tasks.Board, ClaudeStart.EditorRepos(seams.EditorFolderNames(task.WorkspaceId), tasks.Board));

    /// <summary>
    /// "Stop Claude and close" (see the header): closes the task past the live-session refusal, then stops each of its
    /// running background sessions. The close is the answer; each stop lands in the task's thread later.
    /// </summary>
    public Result StopAndClose(TaskItem task)
    {
        // Read before the close: the live entries are what was running when Petre chose to stop it.
        var live = task.Sessions.Where(s => s.IsLive).Select(s => s.Id).ToHashSet();
        var listed = Latest.Map(list => list.Where(agent => live.Contains(agent.SessionId)).ToList());
        return tasks.Close(task.Id, stoppingClaude: true).Tap(() => listed.Match(
            agents =>
            {
                agents.Where(agent => agent.Background).ToList().ForEach(agent => StopAfterClose(task.Id, agent));
                if (agents.Any(agent => !agent.Background))
                    Note(task.Id, "Claude's window is still open: a VS Code or terminal session cannot be stopped from Deskbar. Its next message tells it the task is closed.");
            },
            () => Note(task.Id, "Claude's sessions were not stopped: Deskbar cannot read Claude Code's list of sessions right now.")));
    }

    // `claude stop <short id>` off the UI thread (ClaudeSessions.StopNow), its answer back on it, said in the thread.
    void StopAfterClose(Guid taskId, ClaudeAgent agent)
    {
        var shortId = agent.ShortId.GetValueOrDefault(agent.SessionId[..Math.Min(8, agent.SessionId.Length)]);
        var at = scheduler.Now;
        trace?.Invoke($"claude sessions: stopping {agent.SessionId}, its task {taskId} was closed with Stop Claude and close");
        Observable.FromAsync(() => seams.Stop(shortId))
            .ObserveOn(scheduler)
            .Subscribe(
                stopped => Note(taskId, stopped.IsSuccess
                    ? $"Claude stopped by Deskbar at {at.ToLocalTime():HH:mm}, when the task was closed. Double-click the task to resume it."
                    : $"Claude could not be stopped: {stopped.Error}"),
                error => Note(taskId, $"Claude could not be stopped: {error.Message}"));
    }

    void Note(Guid taskId, string markdown) =>
        tasks.Note(taskId, markdown).TapError(e => trace?.Invoke($"claude sessions: no comment on {taskId}: {e}"));

    /// <summary>Whether a session of the task is running (listed; the hooks' word while there is no list).</summary>
    public bool HasRunningSession(TaskItem task) =>
        Latest.Match(list => task.Sessions.Any(s => list.Any(a => a.SessionId == s.Id)), () => task.HasLiveSession);

    /// <summary>
    /// "Start Claude on this" from a line's menu or the thread's button (see the header): asks first on a task with a
    /// running session, then starts in the one folder, or opens the pick list at the pointer and answers it (for the bar,
    /// which holds itself still while it is open, and the tests). A refusal is told (a message box in the app).
    /// </summary>
    public ContextMenu? Offer(Guid id)
    {
        if (tasks.Board.Tasks.FirstOrDefault(t => t.Id == id) is not { } task) return null;
        if (!manager.State.ClaudeTasksInEffect)
        {
            seams.Tell(FeatureOff);
            return null;
        }
        // Final review, item 12: refused up front, before any question or pick list (see Start).
        if (!task.IsOpen)
        {
            seams.Tell(RemoteControl.ClosedTaskRefusal);
            return null;
        }
        if (HasRunningSession(task) && !seams.Confirm(AnotherSessionQuestion)) return null;

        var folders = Folders(task);
        if (folders is [var only])
        {
            Told(Start(task, only));
            return null;
        }
        // At the pointer, on the screen of the window under it (OwnMenus.AtPointer says why a bare menu is not enough).
        var menu = OwnMenus.AtPointer();
        folders.ToList().ForEach(folder => menu.Items.Add(Item(folder, () => Told(Start(task, folder)))));
        if (folders.Count > 0) menu.Items.Add(new Separator());
        menu.Items.Add(Item(ChooseFolderItem, () => seams.PickFolder().Tap(folder => Told(Start(task, folder)))));
        menu.IsOpen = true;
        return menu;
    }

    void Told(Result result) => result.TapError(seams.Tell);

    // A TextBlock header, so an underscore in a folder is not read as an access key.
    static MenuItem Item(string text, Action click)
    {
        var item = new MenuItem { Header = new TextBlock { Text = text } };
        item.Click += (_, _) => click();
        return item;
    }

    /// <summary>
    /// Starts a background session on the task in the folder (ClaudeSessions.Start) and opens its window once it is
    /// found. Answers at once; what `claude` says comes later, into the task's thread when it fails.
    /// </summary>
    public Result Start(TaskItem task, string folder, Maybe<string> model = default)
    {
        if (!manager.State.ClaudeTasksInEffect) return Result.Failure(FeatureOff);
        // A closed task takes no session (TaskBoard refuses the join), so `claude --bg` would run for nothing (final
        // review, item 12). Judged on the task as the board has it now, not as the caller last saw it.
        if (!Current(task.Id).GetValueOrDefault(task).IsOpen) return Result.Failure(RemoteControl.ClosedTaskRefusal);
        Observable.FromAsync(() => seams.Start(task, folder, model))
            .ObserveOn(scheduler)
            .Subscribe(
                started => started.Match(
                    sessionId => Current(task.Id).Tap(t =>
                        Attach(t, new ClaudeAgent(sessionId, Maybe<string>.None, folder, Background: true, Busy: false, Name: ""))),
                    error => StartFailed(task.Id, folder, error)),
                error => StartFailed(task.Id, folder, error.Message));
        return Result.Success();
    }

    // No Claude Code at all comes first (see the header): the install question instead of the failure. Otherwise Claude's
    // words in the thread; an untrusted folder (measured: "Workspace not trusted. Run `claude` in <folder> once
    // and accept the trust prompt, then retry.") also offers the terminal in which Petre can trust it. Any other failure
    // (`claude` missing from the PATH, say) is also told to him at once (final review, item 12): Start answered
    // "under way" a moment ago, and a failure that only reaches the thread looks like nothing happened.
    void StartFailed(Guid taskId, string folder, string error)
    {
        if (!seams.ClaudeInstalled())
        {
            OfferInstall(taskId, folder, error);
            return;
        }
        Say(taskId, $"Deskbar could not start a Claude session in {folder}:\n\n{error}");
        if (!error.Contains("not trusted", StringComparison.OrdinalIgnoreCase))
        {
            seams.Tell($"Deskbar could not start a Claude session in {folder}:\n\n{error}");
            return;
        }
        if (!seams.OfferTrustTerminal($"{error}\n\n{TrustQuestion}")) return;
        var claude = seams.Claude();
        Current(taskId).Tap(task => Launch(task, ClaudeStart.TrustWindow(folder, claude), ClaudeStart.TrustConsole(folder, claude))
            .TapError(e => SayAndTell(taskId, $"Deskbar could not open a terminal in {folder}:\n\n{e}")));
    }

    // Claude Code is not installed: asked first, and on "Yes" the install window opens in the folder on the task's
    // workspace, like the trust window. "No" keeps the reason in the thread, since he chose not to act on it now.
    void OfferInstall(Guid taskId, string folder, string error)
    {
        if (!seams.OfferInstall($"Claude Code is not installed on this computer, so Deskbar could not start a session in {folder}.\n\n{InstallQuestion}"))
        {
            Say(taskId, $"Deskbar could not start a Claude session in {folder}:\n\n{error}");
            return;
        }
        Current(taskId).Tap(task => Launch(task, ClaudeStart.InstallWindow(folder), ClaudeStart.InstallConsole(folder))
            .TapError(e => SayAndTell(taskId, $"Deskbar could not open a terminal to install Claude Code in {folder}:\n\n{e}")));
    }

    // --- the thread -------------------------------------------------------------------------------------------------

    /// <summary>The task's sessions as its thread lists them (TaskViews.SessionList).</summary>
    public IReadOnlyList<SessionView> SessionsOf(TaskItem task) => TaskViews.SessionList(task, Latest, seams.TranscriptExists);

    /// <summary>
    /// How many of the task's live sessions the latest list shows busy (TaskViews.WorkingCount), for the tasks panel's
    /// session halos (tasks panel plan, Task 8). 0 before the first list and while the feature is off (Latest).
    /// </summary>
    public int WorkingCount(TaskItem task) => TaskViews.WorkingCount(task, Latest);

    // --- helpers ----------------------------------------------------------------------------------------------------

    Maybe<TaskItem> Current(Guid id) => tasks.Board.Tasks.TryFirst(t => t.Id == id);

    void Say(Guid taskId, string markdown) =>
        tasks.Note(taskId, markdown)
            .TapError(e => trace?.Invoke($"claude actions: no comment on {taskId}: {e}"));

    void SayAndTell(Guid taskId, string markdown)
    {
        Say(taskId, markdown);
        seams.Tell(markdown);
    }

    public void Dispose()
    {
        liveSubscription.Dispose();
        changed.OnCompleted();
    }
}

/// <summary>
/// What ClaudeTaskActions needs from outside, so its tests start no `claude`, no terminal and no real window:
/// starting and resuming a session (ClaudeSessions), starting a program (ProgramStarter), the editor window of a folder
/// and bringing a window forward (the bar's rules), whether a transcript still exists, and the folder names of the
/// VS Code windows on a workspace. The four at the end are the questions a click may ask Petre, which the app answers
/// with message boxes and Windows' folder picker.
/// </summary>
public sealed record ClaudeTaskSeams(
    Func<TaskItem, string, Maybe<string>, Task<Result<string>>> Start,
    Func<Guid, TaskSession, Task<Result<ClaudeAgent>>> Resume,
    Func<ProgramLaunch, Result<int?>> Launch,
    Func<string, Maybe<nint>> EditorWindow,
    Func<WindowHandle, Result> BringForward,
    Func<TaskSession, bool> TranscriptExists,
    Func<Maybe<Guid>, IEnumerable<string>> EditorFolderNames)
{
    /// <summary>A Yes/No question (the second session's); no means no.</summary>
    public Func<string, bool> Confirm { get; init; } = _ => false;

    /// <summary>Windows' folder picker; none when cancelled.</summary>
    public Func<Maybe<string>> PickFolder { get; init; } = () => Maybe<string>.None;

    /// <summary>The untrusted folder's message with its Yes/No question; yes opens the terminal.</summary>
    public Func<string, bool> OfferTrustTerminal { get; init; } = _ => false;

    /// <summary>
    /// The question after a start found no Claude Code (ClaudeTaskActions.InstallQuestion under the reason); yes opens the
    /// install window. No by default.
    /// </summary>
    public Func<string, bool> OfferInstall { get; init; } = _ => false;

    /// <summary>
    /// Whether claude.exe can be found (ClaudeLocator.Find in the app, asked fresh each time); yes by default, so a failed
    /// start is reported as before unless the app says Claude Code is missing.
    /// </summary>
    public Func<bool> ClaudeInstalled { get; init; } = () => true;

    /// <summary>
    /// `claude stop <short id>` (ClaudeSessions.StopNow), for "Stop Claude and close" (task 37cd7081). Refused by default,
    /// so a test that does not hand one in stops nothing.
    /// </summary>
    public Func<string, Task<Result>> Stop { get; init; } = _ => Task.FromResult(Result.Failure("No way to stop a session"));

    /// <summary>A refusal or a failure said to Petre at once (the feature is off, `claude` or `wt` missing).</summary>
    public Action<string> Tell { get; init; } = _ => { };

    /// <summary>
    /// Forget a session's past resumes (ClaudeSessions.Forget), when a click brings back a task the keeper had given up
    /// on; nothing by default.
    /// </summary>
    public Action<string> Forget { get; init; } = _ => { };

    /// <summary>
    /// The `claude` a terminal window runs (attach, trust): the app gives claude.exe's full path when ClaudeLocator finds
    /// it, so the window works where the PATH does not lead to claude (task dbe25323); the bare name by default.
    /// </summary>
    public Func<string> Claude { get; init; } = () => "claude";

    /// <summary>
    /// Whether Windows Terminal is installed (ClaudeStart.HasWindowsTerminal): the windows on sessions open in it when it
    /// is, and in a console window of their own (OpenConsole) when it is not (task abfe1db8, Windows Server 2022). Yes by
    /// default.
    /// </summary>
    public Func<bool> HasWindowsTerminal { get; init; } = () => true;

    /// <summary>
    /// Starts a console window on claude.exe (DetachedLauncher.StartConsole) for a machine without Windows Terminal; the
    /// answer is the new process's id. Refused by default.
    /// </summary>
    public Func<ConsoleLaunch, Result<int?>> OpenConsole { get; init; } = _ => Result.Failure<int?>("No console launcher");
}
