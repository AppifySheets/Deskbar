using System.Collections.Immutable;
using System.IO;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// CHANGING A TASK SESSION'S MODEL, EFFORT OR PERMISSION MODE: THE APP'S HALF (the picker under the Inbox page's comment
// box). Every rule is in Core and tested there: SettingsSwitch says what to do (Plan at the pick, Next on every good read
// of the list), TaskBoard stores each step, ClaudeStart writes the command line. This file runs the steps, on the same
// threads as the rest of ClaudeSessions: decisions and board changes on `scheduler`, `claude` on `background`.
//
// HOW ONE STARTS. Only Pick, the user's pick: it stores the pick (TaskBoard.Want), and when the task's session can take it
// (an idle or busy background session, or one that is not running but can be resumed) it begins the switch
// (TaskBoard.SwitchStarted) and takes its first step at once. Nothing else begins one: the poll (AdvanceSwitches, a
// single line in Heard) only carries on with switches already begun, so no session is ever copied, stopped or resumed
// because of a pick nobody just made, a session that happened to join, or a timer.
//
// THE STEPS (SettingsSwitch's header has why, and the measurements):
//   1. ISSUE: `claude --bg --resume <id> --model .. [--effort ..] [--permission-mode ..] --settings ..` in the session's
//      folder, stored as issued BEFORE it runs. Claude prints the copy's short id on the usual "backgrounded" line; it
//      is stored, and the list is read until it shows the copy (Found, about ten seconds).
//   2. JOIN: the copy joins the task in the original's place (TaskBoard.SessionSwitched), or the copy's own SessionStart
//      hook got there first (TaskService.JoinAfterSwitch). A session that came back under its OWN id (no saved options to
//      keep, so no copy) is the same session on the new settings (SessionRelaunched).
//   3. CONFIRM: once the copy has answered (its transcript has a reply written after it joined), the original is stopped
//      (`claude stop`), and the switch is done. Until then the original runs on, idle: the owner's rule is that no
//      conversation sits stopped waiting for Deskbar, and if the copy cannot run its model it is the way back.
//   REVERT: the list shows the copy "failed" (a model this account cannot use fails its first turn, measured): the task
//      goes back to the original, which never stopped, and the copy is stopped.
//   ABANDON: Claude refused the resume (its words go into the thread), no copy appeared within a minute, or the session
//      moved on before the switch could be made. The original was never touched.
// A Deskbar that closes at any point leaves a stored phase, and the first good read after it starts again carries on.
//
// A WINDOW of Deskbar's attached to the original is closed by its stop (measured 2026-11-25). When one was open, a window
// is opened on the task's session, the copy, a moment after the stop (ReopenWindow, which the app hands to
// ClaudeTaskActions; opening windows is not this class's job).
public sealed partial class ClaudeSessions
{
    // Tasks whose switch has a `claude` call out right now: the poll leaves them alone until it answers.
    ImmutableHashSet<Guid> switchingNow = ImmutableHashSet<Guid>.Empty;

    // When a stop of a switch's original was last tried, by session id, so one that fails is tried again after
    // StopRetry rather than on every poll.
    ImmutableDictionary<string, DateTimeOffset> switchStopTried = ImmutableDictionary<string, DateTimeOffset>.Empty;

    /// <summary>
    /// The user picked the model, effort and permission mode for the task's session: stored as the task's pick, and when
    /// its session can take it, the switch begins and its first step is taken now. Answers what the pick meets
    /// (SettingsSwitch.Plan), for the picker to say: copied now, waiting for a pause, read-only (VS Code, a terminal),
    /// or kept for the next start. A list that cannot be read switches nothing (the pick is still stored). Called on the
    /// UI thread; the returned task completes there.
    /// </summary>
    public Task<Result<SwitchStep>> Pick(Guid taskId, SessionSettings settings)
    {
        var wanted = tasks.Want(taskId, settings);
        if (wanted.IsFailure) return Task.FromResult(Result.Failure<SwitchStep>(wanted.Error));
        return OffThread(() => cli.Run(AgentsArguments, null, ReadTimeout))
            .Select(read => read.Bind(ClaudeAgents.Parse).Match(
                list => Result.Success(Picked(taskId, list)),
                error => Result.Failure<SwitchStep>($"Claude Code's list of sessions could not be read, so the session was not changed. Your pick is kept for the next session started on this task.\n\n{error}")))
            .Catch((Exception e) => Observable.Return(Result.Failure<SwitchStep>(e.Message)))
            .TakeUntil(stopping)
            .DefaultIfEmpty(Result.Failure<SwitchStep>("Deskbar closed before the pick was applied."))
            .ToTask();
    }

    SwitchStep Picked(Guid taskId, IReadOnlyCollection<ClaudeAgent> list)
    {
        if (tasks.Board.Tasks.FirstOrDefault(t => t.Id == taskId) is not { } task) return new SwitchStep.Nothing();
        var step = SettingsSwitch.Plan(task, list, Resumable, RecentlyWoken);
        // The switch begins only where there is a session to switch and the pick differs from what it runs on; an
        // issued switch keeps going, and the pick made meanwhile follows it (FollowLaterPick).
        var begins = step switch
        {
            SwitchStep.CopyNow copy => Maybe.From(new PendingSwitch(copy.Session.Id, copy.To, scheduler.Now)),
            SwitchStep.AtNextWake wake => Maybe.From(new PendingSwitch(wake.Session.Id, wake.To, scheduler.Now)),
            SwitchStep.Wait wait when wait.Why != SettingsSwitch.UnderWay =>
                SettingsSwitch.Current(task).Map(s => new PendingSwitch(s.Id, SettingsSwitch.Target(task.Wanted.Value, s), scheduler.Now)),
            _ => Maybe<PendingSwitch>.None,
        };
        // Nothing to switch (the session already runs on the pick): a request still waiting for a pause is dropped.
        if (step is SwitchStep.Nothing && task.Switching is { HasValue: true } waiting && waiting.Value.Issued.HasNoValue)
            tasks.SwitchAbandoned(taskId, keepWanted: true);
        begins.Tap(pending => tasks.SwitchStarted(taskId, pending)
            .Tap(() => Act(taskId, list))
            .TapError(e => trace?.Invoke($"claude sessions: switch on {taskId} not begun: {e}")));
        return step;
    }

    // The poll's part (Heard): every switch already begun takes its next step on this list.
    void AdvanceSwitches(IReadOnlyCollection<ClaudeAgent> listed) =>
        tasks.Board.Tasks
            .Where(t => t.Switching.HasValue && !switchingNow.Contains(t.Id))
            .Select(t => t.Id)
            .ToList()
            .ForEach(id => Act(id, listed));

    void Act(Guid taskId, IReadOnlyCollection<ClaudeAgent> listed)
    {
        if (switchingNow.Contains(taskId) || tasks.Board.Tasks.FirstOrDefault(t => t.Id == taskId) is not { Switching.HasValue: true } task) return;
        var pending = task.Switching.Value;
        switch (SettingsSwitch.Next(task, listed, scheduler.Now, Resumable, RecentlyWoken, Known, CopyReplied))
        {
            case SwitchAction.Issue issue:
                Issue(taskId, issue);
                break;
            case SwitchAction.Join join:
                tasks.SessionSwitched(taskId, pending.FromSession,
                        tasks.NewSession(join.Copy.SessionId, FolderOf(task, pending)) with { LaunchedWith = pending.To })
                    // NOT put in the keeper's grace (`answered`) as a wake is: the user picked a moment ago and is likely
                    // to send a message now, and within the grace a comment's wake is refused and the comment waits in an
                    // inbox the copy, which has had no turn yet, has no waiter on. The copy is listed, so the keeper
                    // leaves it alone anyway.
                    .Tap(() => Say(taskId, SettingsSwitch.JoinedNote(pending.To)))
                    .TapError(e => trace?.Invoke($"claude sessions: copy {join.Copy.SessionId} not joined to {taskId}: {e}"));
                break;
            case SwitchAction.Relaunched relaunched:
                tasks.SessionRelaunched(taskId, relaunched.Session.SessionId, pending.To)
                    .Tap(() => Say(taskId, SettingsSwitch.RelaunchedNote(pending.To)))
                    .TapError(e => trace?.Invoke($"claude sessions: relaunch of {relaunched.Session.SessionId} not stored: {e}"));
                break;
            case SwitchAction.Confirm confirm:
                Confirm(taskId, pending, confirm.StopOriginal);
                break;
            case SwitchAction.Revert revert:
                tasks.SwitchReverted(taskId)
                    .Tap(() => Say(taskId, revert.Why))
                    .TapError(e => trace?.Invoke($"claude sessions: switch on {taskId} not reverted: {e}"));
                pending.CopyId.Tap(retireWaiter);
                revert.StopCopy.Tap(shortId => StopForSwitch(taskId, shortId, _ => { }));
                break;
            case SwitchAction.Abandon abandon:
                tasks.SwitchAbandoned(taskId, abandon.KeepWanted);
                if (abandon.Why.Length > 0) Say(taskId, abandon.Why);
                break;
        }
    }

    // The folder the copy runs in: the original's, where the resume ran.
    static string FolderOf(TaskItem task, PendingSwitch pending) =>
        task.Sessions.FirstOrDefault(s => s.Id == pending.FromSession)?.Folder ?? "";

    void Issue(Guid taskId, SwitchAction.Issue issue)
    {
        // The model is always named (ClaudeStart.SwitchArguments). A pick that named none keeps the model the session was
        // started on (SettingsSwitch.Target); for a session Deskbar did not start, the model its last reply came from,
        // else the setting a start would use.
        var to = issue.To.Model.HasValue ? issue.To : issue.To with { Model = LastModelOf(issue.From).GetValueOrDefault(manager.State.ClaudeTaskModelInEffect) };
        if (tasks.SwitchIssued(taskId, to) is { IsFailure: true } notIssued)
        {
            trace?.Invoke($"claude sessions: switch on {taskId} not issued: {notIssued.Error}");
            return;
        }
        switchingNow = switchingNow.Add(taskId);
        var from = issue.From;
        OffThread(() => cli.Run(ClaudeStart.SwitchArguments(from.Id, to), from.Folder, LaunchTimeout))
            .Catch((Exception e) => Observable.Return(Result.Failure<string>(e.Message)))
            .SelectMany(ran =>
            {
                if (ran.IsFailure) return Observable.Return((Ran: ran, Found: Maybe<Result<ClaudeAgent>>.None));
                var shortId = ClaudeAgents.ShortIdFromStart(ran.Value);
                // No short id printed: the poll still finds the copy by its folder and start time (SettingsSwitch.Next).
                if (shortId.HasNoValue) return Observable.Return((Ran: ran, Found: Maybe<Result<ClaudeAgent>>.None));
                tasks.SwitchCopyNamed(taskId, shortId.Value).TapError(e => trace?.Invoke($"claude sessions: copy of {from.Id} not named: {e}"));
                return Found(shortId.Value, FindAttempts, $"Claude did not list {shortId.Value} yet.")
                    .Select(found => (Ran: ran, Found: Maybe.From(found)));
            })
            .TakeUntil(stopping)
            .Subscribe(answer =>
            {
                switchingNow = switchingNow.Remove(taskId);
                if (answer.Ran.IsFailure)
                {
                    // Claude refused the resume: nothing was started, and the original goes on as it was.
                    tasks.SwitchAbandoned(taskId);
                    Say(taskId, $"Could not change the session to {SessionChoices.Describe(to)}. It goes on as it was. Claude Code said:\n\n{answer.Ran.Error}");
                    return;
                }
                // Found in time: the next step (the join) is taken now, on the list as it is plus what was found;
                // otherwise the poll takes it when the copy shows up.
                answer.Found.Bind(found => found.IsSuccess ? Maybe.From(found.Value) : Maybe<ClaudeAgent>.None)
                    .Tap(agent => Act(taskId, lastLive.Where(a => a.SessionId != agent.SessionId).Append(agent).ToList()));
            },
            error =>
            {
                switchingNow = switchingNow.Remove(taskId);
                trace?.Invoke($"claude sessions: switch on {taskId} threw: {error.Message}");
            });
    }

    void Confirm(Guid taskId, PendingSwitch pending, Maybe<string> stopOriginal)
    {
        if (stopOriginal.HasNoValue)
        {
            Confirmed(taskId);
            return;
        }
        var now = scheduler.Now;
        if (switchStopTried.TryGetValue(pending.FromSession, out var tried) && now - tried < StopRetry) return;
        switchStopTried = switchStopTried.SetItem(pending.FromSession, now);
        // A window of Deskbar's attached to the original closes with its stop (measured 2026-11-25); asked before the stop.
        var hadWindow = tasks.Board.Tasks.TryFirst(t => t.Id == taskId).Map(windowOpen).GetValueOrDefault(false);
        // Its comment waiter goes before its process does, as before every stop and resume (task daeaa26c).
        retireWaiter(pending.FromSession);
        StopForSwitch(taskId, stopOriginal.Value, stopped =>
        {
            if (!stopped) return;
            switchStopTried = switchStopTried.Remove(pending.FromSession);
            Confirmed(taskId);
            if (hadWindow) ReopenLater(taskId);
        });
    }

    /// <summary>
    /// Opens a window on the task's session again (the app: ClaudeTaskActions, the way Open Claude does) after a switch
    /// stopped the original that a window of Deskbar's was attached to. None: no window is reopened.
    /// </summary>
    public Action<Guid>? ReopenWindow { get; set; }

    // The stopped window may still be on its way out for a moment, and the opener brings forward a window with the task's
    // title when it finds one; so the reopen waits for the old one to go.
    static readonly TimeSpan ReopenAfter = TimeSpan.FromSeconds(2);

    void ReopenLater(Guid taskId) =>
        scheduler.Schedule(ReopenAfter, () =>
        {
            if (tasks.Board.Tasks.Any(t => t.Id == taskId && t.IsOpen)) ReopenWindow?.Invoke(taskId);
        });

    /// <summary>
    /// The picker's Cancel. A pick still waiting for a pause is dropped (TaskBoard.Unwant); a joined switch goes back to the
    /// original, which still runs, and the copy is stopped. While the copy is being made there is nothing to cancel yet.
    /// Called on the UI thread.
    /// </summary>
    public Result CancelPick(Guid taskId)
    {
        if (tasks.Board.Tasks.FirstOrDefault(t => t.Id == taskId) is not { } task) return Result.Failure("The task is gone.");
        if (task.Switching is not { HasValue: true } switching) return tasks.Unwant(taskId);
        var pending = switching.Value;
        if (pending.Issued.HasNoValue) return tasks.Unwant(taskId);
        if (pending.CopyId is not { HasValue: true } copyId) return Result.Failure("The copy is being made right now. Cancel once it shows.");
        // The list's short id, else the one Claude printed, else the id's first eight characters (a background session's
        // short id is those, measured): the copy may have joined after the last poll, so the list may not show it yet.
        var listedShortId = lastLive.TryFirst(a => a.SessionId == copyId.Value).Bind(a => a.ShortId);
        var copyShortId = listedShortId.HasValue ? listedShortId.Value
            : pending.CopyShortId.GetValueOrDefault(copyId.Value[..Math.Min(8, copyId.Value.Length)]);
        return tasks.SwitchReverted(taskId)
            .Tap(() =>
            {
                Say(taskId, "Cancelled. The task is back on its earlier session, which never stopped.");
                retireWaiter(copyId.Value);
                StopForSwitch(taskId, copyShortId, _ => { });
            });
    }

    /// <summary>What the task's picker row shows (SessionPicker.RowOf) on the latest list. Called on the UI thread.</summary>
    public SessionPickRow? PickRow(Guid taskId) =>
        tasks.Board.Tasks.FirstOrDefault(t => t.Id == taskId) is { } task ? SessionPicker.RowOf(task, lastLive, CachedLastModel) : null;

    // The page asks on every redraw, so the model of a session's last reply is read from its transcript once per change of
    // the file (its length and write time), as LastTurnError is.
    readonly Dictionary<string, (long Length, DateTime Written, Maybe<string> Model)> lastModels = [];

    Maybe<string> CachedLastModel(TaskSession session)
    {
        try
        {
            if (TranscriptOf(session).GetValueOrDefault() is not { } path) return Maybe<string>.None;
            var file = new FileInfo(path);
            var (length, written) = (file.Length, file.LastWriteTimeUtc);
            if (lastModels.TryGetValue(path, out var known) && known.Length == length && known.Written == written) return known.Model;
            var model = ClaudeTranscript.LastModel(Tail(path));
            lastModels[path] = (length, written, model);
            return model;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Maybe<string>.None;
        }
    }

    void Confirmed(Guid taskId)
    {
        tasks.SwitchConfirmed(taskId).TapError(e => trace?.Invoke($"claude sessions: switch on {taskId} not confirmed: {e}"));
        FollowLaterPick(taskId);
    }

    // A pick made while the switch was under way (TaskItem.Wanted moved on): the user's own later pick begins the next
    // switch, on the copy, which the following reads carry on with like any other.
    void FollowLaterPick(Guid taskId)
    {
        if (tasks.Board.Tasks.FirstOrDefault(t => t.Id == taskId) is not { Wanted.HasValue: true } task) return;
        if (SettingsSwitch.Current(task) is not { HasValue: true } current) return;
        var to = SettingsSwitch.Target(task.Wanted.Value, current.Value);
        if (to == current.Value.LaunchedWith) return;
        tasks.SwitchStarted(taskId, new PendingSwitch(current.Value.Id, to, scheduler.Now))
            .TapError(e => trace?.Invoke($"claude sessions: later pick on {taskId} not begun: {e}"));
    }

    // `claude stop <short id>` for a switch, off the UI thread; `done` hears whether it worked, on `scheduler`.
    void StopForSwitch(Guid taskId, string shortId, Action<bool> done)
    {
        switchingNow = switchingNow.Add(taskId);
        OffThread(() => cli.Run(["stop", shortId], null, LaunchTimeout))
            .Catch((Exception e) => Observable.Return(Result.Failure<string>(e.Message)))
            .TakeUntil(stopping)
            .Subscribe(stopped =>
            {
                switchingNow = switchingNow.Remove(taskId);
                if (stopped.IsFailure) trace?.Invoke($"claude sessions: stop of {shortId} for a switch failed: {stopped.Error}");
                done(stopped.IsSuccess);
            });
    }

    bool RecentlyWoken(string sessionId) => Queued(sessionId) || AnsweredWithinGrace(sessionId);

    bool Known(string sessionId) => tasks.Board.Tasks.Any(t => t.Sessions.Any(s => s.Id == sessionId));

    // Whether a joined copy has answered since it joined (ClaudeTranscript.RepliedSince), from its transcript's tail.
    // Unreadable or not there yet is "not yet".
    bool CopyReplied(TaskSession copy) =>
        TailOf(copy).Map(lines => ClaudeTranscript.RepliedSince(lines, copy.Joined)).GetValueOrDefault(false);

    Maybe<string> LastModelOf(TaskSession session) => TailOf(session).Bind(ClaudeTranscript.LastModel);

    Maybe<IReadOnlyList<string>> TailOf(TaskSession session)
    {
        try
        {
            return TranscriptOf(session).Map(Tail);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Maybe<IReadOnlyList<string>>.None;
        }
    }
}
