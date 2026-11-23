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
// THREADS. Everything here runs on the UI thread (`scheduler` in the app); the answers of `claude` arrive back on it.
public sealed class ClaudeTaskActions : IDisposable
{
    /// <summary>The asking before a second session on a task (the spec: "a second session on one task is rarely meant").</summary>
    public const string AnotherSessionQuestion = "This task already has a Claude session running. Start another one?";

    /// <summary>The pick list's last item, which opens Windows' folder picker.</summary>
    public const string ChooseFolderItem = "Choose a folder…";

    /// <summary>What an untrusted folder's failure adds to Claude's own message, as a Yes/No question.</summary>
    public const string TrustQuestion = "Open a terminal there so you can trust it?";

    /// <summary>Why nothing starts while the feature is off.</summary>
    public const string FeatureOff =
        "Claude sessions are off. Turn on \"Claude Code sessions keep their tasks in the task list\" in Manage first.";

    // Every window of Windows Terminal belongs to this process, whichever `wt.exe` asked for it, so a new window is
    // expected by this name, not by wt's.
    const string TerminalProcess = "WindowsTerminal";

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
    public TaskSessionCommands Commands => new(Go, Folders, Start, OpenSession);

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
            ToEditor(session);
            return;
        }
        if (listed.Value.TryFirst(a => a.SessionId == session.Id) is { HasValue: true } agent)
        {
            if (agent.Value.Background) Attach(task, agent.Value);
            else ToEditor(session);
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
    void ToEditor(TaskSession session) =>
        seams.EditorWindow(session.Folder).Tap(hwnd => seams.BringForward(new WindowHandle(hwnd))
            .TapError(e => trace?.Invoke($"claude actions: editor window of {session.Id} not brought forward: {e}")));

    // Resumed off the UI thread; attached once ClaudeSessions finds it listed. A failure was said in the thread there.
    void Resume(TaskItem task, TaskSession session) =>
        Observable.FromAsync(() => seams.Resume(task.Id, session))
            .ObserveOn(scheduler)
            .Subscribe(
                woken => woken
                    .Tap(agent => Current(task.Id).Tap(t => Attach(t, agent)))
                    .TapError(e => trace?.Invoke($"claude actions: {session.Id} not resumed: {e}")),
                error => trace?.Invoke($"claude actions: resume of {session.Id} threw: {error.Message}"));

    // The window on a background session: the open one brought forward, else a new one (see the header).
    void Attach(TaskItem task, ClaudeAgent agent)
    {
        var title = ClaudeStart.AttachTitle(task.Title);
        if (manager.KnownWindows.FirstOrDefault(w => Titled(w, title)) is { } open)
        {
            seams.BringForward(open.Handle).TapError(e => trace?.Invoke($"claude actions: window on {agent.SessionId} not brought forward: {e}"));
            return;
        }
        if (attaching.TryGetValue(agent.SessionId, out var launched) && scheduler.Now - launched < AttachPending) return;

        // A background session's short id is the first eight characters of its id (measured, also after a resume).
        var shortId = agent.ShortId.GetValueOrDefault(agent.SessionId[..Math.Min(8, agent.SessionId.Length)]);
        Launch(task, ClaudeStart.AttachWindow(shortId, agent.Folder, title))
            .Tap(() => attaching = attaching.SetItem(agent.SessionId, scheduler.Now))
            .TapError(e => Say(task.Id, $"Deskbar could not open a window on session {shortId}:\n\n{e}"));
    }

    // A Windows Terminal window with this title, as it reads now or read before a rename rule shortened it.
    bool Titled(WindowInfo window, string title) =>
        window.ProcessName.Equals(TerminalProcess, StringComparison.OrdinalIgnoreCase)
        && (window.Title == title || manager.OriginalTitle(window.Handle).Map(t => t == title).GetValueOrDefault(false));

    // A terminal window started for the task: expected on its workspace (the one in front for a free task) through
    // StartIn, so it lands there like any program `deskbar.cmd start` starts. With no workspace at all (an unnamed
    // desktop in front) it is started where Petre is.
    Result Launch(TaskItem task, ProgramLaunch launch) =>
        task.WorkspaceId.Or(manager.CurrentWorkspaceId) is { HasValue: true } workspace
            ? manager.StartIn(workspace.Value, new LaunchTarget(launch, TerminalProcess), seams.Launch).Bind(_ => Result.Success())
            : seams.Launch(launch).Bind(_ => Result.Success());

    // --- starting ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The folders a new session on the task could start in, the likeliest first (ClaudeStart.Folders): its latest
    /// session's repo, or the repos of its workspace's tasks and VS Code windows.
    /// </summary>
    public IReadOnlyList<string> Folders(TaskItem task) =>
        ClaudeStart.Folders(task, tasks.Board, ClaudeStart.EditorRepos(seams.EditorFolderNames(task.WorkspaceId), tasks.Board));

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
        if (HasRunningSession(task) && !seams.Confirm(AnotherSessionQuestion)) return null;

        var folders = Folders(task);
        if (folders is [var only])
        {
            Told(Start(task, only));
            return null;
        }
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
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
    public Result Start(TaskItem task, string folder)
    {
        if (!manager.State.ClaudeTasksInEffect) return Result.Failure(FeatureOff);
        Observable.FromAsync(() => seams.Start(task, folder))
            .ObserveOn(scheduler)
            .Subscribe(
                started => started.Match(
                    sessionId => Current(task.Id).Tap(t =>
                        Attach(t, new ClaudeAgent(sessionId, Maybe<string>.None, folder, Background: true, Busy: false, Name: ""))),
                    error => StartFailed(task.Id, folder, error)),
                error => StartFailed(task.Id, folder, error.Message));
        return Result.Success();
    }

    // Claude's words in the thread; an untrusted folder (measured: "Workspace not trusted. Run `claude` in <folder> once
    // and accept the trust prompt, then retry.") also offers the terminal in which Petre can trust it.
    void StartFailed(Guid taskId, string folder, string error)
    {
        Say(taskId, $"Deskbar could not start a Claude session in {folder}:\n\n{error}");
        if (!error.Contains("not trusted", StringComparison.OrdinalIgnoreCase)) return;
        if (!seams.OfferTrustTerminal($"{error}\n\n{TrustQuestion}")) return;
        Current(taskId).Tap(task => Launch(task, ClaudeStart.TrustWindow(folder))
            .TapError(e => Say(taskId, $"Deskbar could not open a terminal in {folder}:\n\n{e}")));
    }

    // --- the thread -------------------------------------------------------------------------------------------------

    /// <summary>The task's sessions as its thread lists them (TaskViews.SessionList).</summary>
    public IReadOnlyList<SessionView> SessionsOf(TaskItem task) => TaskViews.SessionList(task, Latest, seams.TranscriptExists);

    // --- helpers ----------------------------------------------------------------------------------------------------

    Maybe<TaskItem> Current(Guid id) => tasks.Board.Tasks.TryFirst(t => t.Id == id);

    void Say(Guid taskId, string markdown) =>
        tasks.Comment(taskId, Maybe<string>.None, markdown)
            .TapError(e => trace?.Invoke($"claude actions: no comment on {taskId}: {e}"));

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
    Func<TaskItem, string, Task<Result<string>>> Start,
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

    /// <summary>A refusal said to Petre at once (the feature is off, say).</summary>
    public Action<string> Tell { get; init; } = _ => { };
}
