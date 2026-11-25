using System.IO;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using CSharpFunctionalExtensions;
using Deskbar.Core;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// WHAT ONE CLICK ON THE MANAGER BUTTON DOES (and the pipe's `task manager`, its twin). Design:
// docs/superpowers/specs/2026-11-25-manager-session-design.md, sections "The button" and "Where it runs and what it is
// told"; the claude flags were measured in docs/superpowers/notes/2026-11-25-manager-session-probes.md.
//
// The manager is a Fable background Claude Code session, one per local calendar day, running in its own folder
// (<state dir>\manager) and coordinating the user's task board. Every rule about it lives in Core (ManagerSession) and is
// tested there; this class joins those rules to the pieces that act:
//
//   1. The list is read FRESH (ClaudeSessions.ListNow), never taken from the last poll: a manager started a moment ago is
//      not in a poll up to fifteen seconds old, and ManagerSession.Next would then resume a session that is running,
//      which starts a forked COPY (probe notes, "Resume"). A list that cannot be read is "cannot tell": nothing starts.
//   2. ManagerSession.Next decides, from the stored ManagerRecord (AppState.Manager), today and that list:
//        * Attach: today's manager (or yesterday's, while it is busy: a new day never stops a turn in flight) is listed;
//        * Resume: today's manager is not listed and its transcript is there: a BARE `claude --bg --resume <full id>`,
//          which keeps every option it was started with (measured), then attach;
//        * StartNew: nothing stored, yesterday's, or its transcript gone: `claude stop` on the old one first when it is
//          still listed (idle), then the start (ManagerSession.StartArguments), its id stored with today, then attach.
//   3. Before every start AND every resume, the folder is created and the briefing (ManagerSession.Briefing) written to
//      manager-briefing.md in it. A resume keeps the saved --append-system-prompt-file, which names this same path, so a
//      Deskbar rebuilt with a new briefing reaches the manager at its next resume, not only at the next day's start.
//   4. The window is ClaudeTaskActions.AttachTo, the one a task's session gets, titled
//      ClaudeStart.AttachTitle(ManagerSession.Name) and opened on the workspace in front (the manager has none of its
//      own). An open one is brought forward.
//
// A PENDING CLICK. From the click until the window is attached (or the open failed) a second click does nothing: the
// first start is still finding its session, and a second would start another manager.
//
// FAILURES. Open answers at once; what `claude` says later is told to the user in a message (ClaudeTaskActions.Tell,
// the seam every Claude click uses), since the manager has no task thread to say it in. An untrusted manager folder
// (`claude --bg` fails with "Workspace not trusted", the case on every machine's first start) goes through the same
// offer a task's start makes: a terminal running `claude` in the folder, where the user accepts trust himself. Nothing is
// stored and nothing retried; his next click starts the manager.
//
// THREADS. Open runs on the UI thread (`scheduler` in the app); ClaudeSessions runs `claude` off it and every answer is
// brought back onto `scheduler`, the only thread that touches the state or `pending`. So nothing here locks.
//
// Nothing here runs on a timer (Global Constraints): the manager is started, resumed or stopped only by a click.
public sealed class ManagerActions : IDisposable
{
    /// <summary>The manager's window title, by which an open window is found again (ClaudeTaskActions.AttachTo).</summary>
    public static string Title => ClaudeStart.AttachTitle(ManagerSession.Name);

    readonly WorkspaceManager manager;
    readonly ClaudeSessions sessions;
    readonly ClaudeTaskActions actions;
    readonly IScheduler scheduler;
    readonly Func<DateOnly> today;
    readonly Action<string>? trace;
    readonly SerialDisposable opening = new();

    // The manager's folder (ManagerSession.FolderIn the state dir), and the shim's full path with forward slashes: the
    // allow rule and the briefing must name exactly what the manager runs, and Git Bash wants forward slashes (probes).
    readonly string folder;
    readonly string shim;

    // From a click until its window is attached or its failure told (see the header). Touched only on `scheduler`.
    bool pending;

    /// <param name="stateDir">Deskbar's state folder (AppPaths.NewStateDir), so DESKBAR_APPDATA moves the manager too.</param>
    /// <param name="shimPath">The `deskbar.cmd` ControlCommandLine.WriteShim writes in the state folder.</param>
    /// <param name="scheduler">Where answers land: the UI thread in the app.</param>
    /// <param name="today">The local calendar day; the system's unless a test says otherwise.</param>
    public ManagerActions(WorkspaceManager manager, ClaudeSessions sessions, ClaudeTaskActions actions, string stateDir, string shimPath,
        IScheduler scheduler, Func<DateOnly>? today = null, Action<string>? trace = null)
    {
        this.manager = manager;
        this.sessions = sessions;
        this.actions = actions;
        this.scheduler = scheduler;
        this.today = today ?? (() => DateOnly.FromDateTime(DateTime.Now));
        this.trace = trace;
        folder = ManagerSession.FolderIn(stateDir);
        shim = shimPath.Replace('\\', '/');
    }

    /// <summary>
    /// The Manager button and `task manager`: attaches to today's manager, resumes it or starts it (see the header).
    /// Answers at once; refused while Claude sessions are off, as "Start Claude on this" is (the terminal profile the
    /// window opens with and the hooks exist only while they are on). A click while one is pending does nothing.
    /// </summary>
    public Result Open()
    {
        if (!manager.State.ClaudeTasksInEffect) return Result.Failure(ClaudeTaskActions.FeatureOff);
        if (pending) return Result.Success();
        pending = true;
        // The day is read once, at the click, so a click just before midnight stores the day it was decided on.
        var day = today();
        opening.Disposable = OnScheduler(sessions.ListNow)
            .SelectMany(listed => listed.Match(
                list => Take(ManagerSession.Next(Maybe.From(manager.State.Manager), day, list, TranscriptExists), day),
                error => Observable.Return(Result.Failure<ClaudeAgent>(
                    $"Claude Code's list of sessions could not be read, so the manager was not opened:\n\n{error}"))))
            .Subscribe(
                opened =>
                {
                    pending = false;
                    opened.Match(agent => actions.AttachTo(Title, Maybe<Guid>.None, agent, actions.Tell), Failed);
                },
                error =>
                {
                    pending = false;
                    Failed(error.Message);
                });
        return Result.Success();
    }

    // The step Next chose, ending in the session to attach to, or why there is none.
    IObservable<Result<ClaudeAgent>> Take(ManagerStep step, DateOnly day) => step switch
    {
        ManagerStep.Attach attach => Observable.Return(Result.Success(attach.Agent)),
        ManagerStep.Resume resume => Briefed(_ => OnScheduler(() =>
            sessions.RunAndFind(ManagerSession.ResumeArguments(resume.SessionId), folder, resume.SessionId))),
        ManagerStep.StartNew start => StopFirst(start.StopFirst).SelectMany(_ => Started(day)),
        _ => Observable.Return(Result.Failure<ClaudeAgent>($"Unknown manager step {step}")),
    };

    // A new manager: the briefing written, `claude --bg` with the measured flags, and once it is listed, its full id stored
    // with the day of the click, so the next click that day attaches or resumes it rather than starting another.
    IObservable<Result<ClaudeAgent>> Started(DateOnly day) =>
        Briefed(briefing => OnScheduler(() => sessions.RunAndFind(ManagerSession.StartArguments(briefing, shim), folder))
            .Select(found => found.Tap(agent => manager.SetManagerSession(new ManagerRecord(agent.SessionId, day)))));

    // Yesterday's idle manager is stopped before today's starts, so two managers never run side by side. `claude stop`
    // returns once the process is gone (measured). A stop that fails (the session ended in the second since the list was
    // read, say) does not hold today's manager back; it is traced.
    IObservable<Unit> StopFirst(Maybe<ClaudeAgent> stale) =>
        stale.HasValue
            ? OnScheduler(() => sessions.StopNow(ShortIdOf(stale.Value)))
                .Do(stopped => stopped.TapError(e => trace?.Invoke($"manager: yesterday's {stale.Value.SessionId} not stopped: {e}")))
                .Select(_ => Unit.Default)
            : Observable.Return(Unit.Default);

    // The briefing written before a start or a resume (see the header), then `next` with its path; a briefing that cannot
    // be written starts nothing, since the manager would run without its rules.
    IObservable<Result<ClaudeAgent>> Briefed(Func<string, IObservable<Result<ClaudeAgent>>> next) =>
        WriteBriefing().Match(next, error => Observable.Return(Result.Failure<ClaudeAgent>(error)));

    Result<string> WriteBriefing()
    {
        try
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, ManagerSession.BriefingFile);
            File.WriteAllText(path, ManagerSession.Briefing(shim));
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Result.Failure<string>($"The manager's briefing could not be written in {folder}: {e.Message}");
        }
    }

    // An untrusted folder gets the trust offer (ClaudeTaskActions.OfferTrust); anything else is told as it is.
    void Failed(string error)
    {
        trace?.Invoke($"manager: not opened: {error}");
        if (error.Contains("not trusted", StringComparison.OrdinalIgnoreCase)) actions.OfferTrust(folder, error);
        else actions.Tell($"Deskbar could not open the manager session:\n\n{error}");
    }

    // The manager's transcript, looked for as the keeper looks for a task's (worktree project folders included).
    bool TranscriptExists(string sessionId) =>
        sessions.TranscriptExists(new TaskSession(sessionId, folder, DateTimeOffset.MinValue, Maybe<DateTimeOffset>.None));

    // A background session's short id is the first eight characters of its id when the list does not give it (measured).
    static string ShortIdOf(ClaudeAgent agent) =>
        agent.ShortId.GetValueOrDefault(agent.SessionId[..Math.Min(8, agent.SessionId.Length)]);

    // A ClaudeSessions call, started when subscribed and answered on `scheduler`. The scheduler is given to ToObservable,
    // so the answer is handed on as the task completes rather than from a thread-pool continuation.
    IObservable<T> OnScheduler<T>(Func<Task<T>> call) => Observable.Defer(() => call().ToObservable(scheduler));

    public void Dispose() => opening.Dispose();
}
