using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

/// <summary>
/// A switch of a task's session to other settings, begun by the user's pick (TaskItem.Switching). It goes through three
/// phases, each stored so a Deskbar that closes in the middle can go on with it:
/// <list type="bullet">
/// <item>REQUESTED: <see cref="Issued"/> none. Waiting for <see cref="FromSession"/> to pause.</item>
/// <item>ISSUED: the resume that makes the copy went out at <see cref="Issued"/>; <see cref="CopyShortId"/> once Claude
/// printed it. Waiting for the copy to be listed.</item>
/// <item>JOINED: <see cref="CopyId"/> set. The copy is the task's session; the original still runs, until the copy has
/// answered once and the original pauses (then it is stopped), or the copy failed (then the task goes back to it).</item>
/// </list>
/// <see cref="To"/> is what the copy runs on, <see cref="At"/> when the user picked.
/// </summary>
public sealed record PendingSwitch(string FromSession, SessionSettings To, DateTimeOffset At)
{
    public Maybe<DateTimeOffset> Issued { get; init; }
    public Maybe<string> CopyShortId { get; init; }
    public Maybe<string> CopyId { get; init; }
}

/// <summary>What to do about a task's pick at the moment it is made (SettingsSwitch.Plan).</summary>
public abstract record SwitchStep
{
    /// <summary>Nothing: no pick, a closed task, or the session already runs on the pick.</summary>
    public sealed record Nothing : SwitchStep;

    /// <summary>The session is an idle background one: copy it with the new settings now.</summary>
    public sealed record CopyNow(TaskSession Session, string ShortId, SessionSettings To) : SwitchStep;

    /// <summary>Not now (<see cref="Why"/> is the line the picker shows); the switch waits, stored, for a pause.</summary>
    public sealed record Wait(string Why) : SwitchStep;

    /// <summary>A session in a window of its own (VS Code, a terminal), which Deskbar cannot restart: changed only there.</summary>
    public sealed record ReadOnly(string Why) : SwitchStep;

    /// <summary>
    /// The task's live session is not listed (its process went in the moments before the list noticed) but can be resumed:
    /// it is resumed with the new settings now, and nothing needs stopping.
    /// </summary>
    public sealed record AtNextWake(TaskSession Session, SessionSettings To) : SwitchStep;

    /// <summary>No session of this task runs (none, an ended one, one now on another task): the next start uses the pick.</summary>
    public sealed record NextStart(SessionSettings To) : SwitchStep;
}

/// <summary>The next thing to do about a switch under way (SettingsSwitch.Next).</summary>
public abstract record SwitchAction
{
    /// <summary>No switch under way.</summary>
    public sealed record Nothing : SwitchAction;

    /// <summary>Not yet (<see cref="Why"/> is the picker's line); asked again on the next good read of the list.</summary>
    public sealed record Wait(string Why) : SwitchAction;

    /// <summary>Run the resume that makes the copy (ClaudeStart.SwitchArguments) of <see cref="From"/>, now.</summary>
    public sealed record Issue(TaskSession From, SessionSettings To) : SwitchAction;

    /// <summary>The copy is listed, under the short id Claude printed for our resume: it joins the task.</summary>
    public sealed record Join(ClaudeAgent Copy) : SwitchAction;

    /// <summary>
    /// The resume kept the session's own id (a session Claude Code held no saved options for, measured 2026-11-26), as
    /// Claude printed it: the same session, now on the new settings (TaskBoard.SessionRelaunched).
    /// </summary>
    public sealed record Relaunched(ClaudeAgent Session) : SwitchAction;

    /// <summary>
    /// The copy has answered and the original is at a pause (or runs no more): the switch is done.
    /// <see cref="StopOriginal"/> is the original's short id when it is still listed and must be stopped; none when not.
    /// </summary>
    public sealed record Confirm(Maybe<string> StopOriginal) : SwitchAction;

    /// <summary>
    /// The copy failed before it answered, or the task was closed before it answered: the task keeps (or goes back to)
    /// the original, which never stopped (TaskBoard.SwitchReverted), and the copy is stopped by the short id Claude
    /// printed for it. <see cref="Why"/> is said in the thread when not empty.
    /// </summary>
    public sealed record Revert(string StopCopy, string Why) : SwitchAction;

    /// <summary>
    /// A copy that never joined (its task was closed first) is stopped by the short id Claude printed for it, and the
    /// switch is given up; the original was never touched.
    /// </summary>
    public sealed record Discard(string StopCopy) : SwitchAction;

    /// <summary>
    /// The switch is given up (TaskBoard.SwitchAbandoned); <see cref="Why"/> is said in the thread when not empty, and
    /// <see cref="KeepWanted"/> keeps the pick for later starts when the switch was not its fault. Nothing is stopped.
    /// </summary>
    public sealed record Abandon(string Why, bool KeepWanted) : SwitchAction;
}

// CHANGING THE MODEL, EFFORT OR PERMISSION MODE OF A TASK'S SESSION: WHEN, AND IN WHAT ORDER. Pure; the app runs the
// steps (ClaudeSessions.Switch.cs). Measured 2026-11-26 with Claude Code 2.1.332, background sessions:
//   * There is no scriptable way to change the model or effort of a running session. `claude --bg --resume <id>` with
//     flags starts a COPY under a new id when Claude Code holds saved options for that id (a background session, running
//     or stopped): a new short id, printed on the usual "backgrounded · <id>" line, a new session id, a new transcript
//     holding the whole old conversation, the new settings, and a SessionStart hook with source "fork". The original
//     stays in the list, its transcript untouched byte for byte.
//   * A session Claude Code holds no saved options for (an ended `claude -p` one was measured; a VS Code one is assumed
//     to be the same) is resumed under its OWN id with the flags applied, no copy (source "resume").
//   * A model Claude Code cannot run does not fail the resume: it exits 0, and the first turn fails, the list then showing
//     the session "idle" with state "failed".
//   * Stopping a busy session loses the step in flight; its transcript marks the tool call as interrupted.
//
// THE ORDER: COPY, JOIN, AND STOP THE ORIGINAL ONLY ONCE THE COPY HAS ANSWERED AND THE ORIGINAL IS AT A PAUSE. The copy is
// made while the original still runs, the task moves to the copy (so the user's next message goes to it), and the
// original is stopped only after the copy's first reply has come back, and only at a read of the list that shows the
// original at a pause exactly as a copy needs it (IsPause: not busy, not waiting on a question, its turn done). The
// original stays reachable meanwhile (a window can be open on it, a job of its own can wake it), so a busy original
// waits, with no time limit, for its next pause. At no moment does a conversation sit stopped waiting for Deskbar: the
// owner's rule is that a Claude session must never stop or hang because Deskbar closed, restarted or crashed. If the
// copy cannot run its model, the task goes back to the original, which never stopped. Once the copy has answered the
// switch is final: Cancel is gone, and going back is a new pick. Every phase is stored (PendingSwitch), so a restarted
// Deskbar carries on where the last one was (Next).
//
// WHAT STARTS ONE. Only the user's pick (ClaudeSessions.Pick, through TaskBoard.Want and SwitchStarted). Nothing here
// makes a switch by itself: Next acts only on a switch the pick began (TaskItem.Switching), so a session that joins a
// task with a pick on it, or a wake that comes back on other settings (Drifted), never starts a copy of its own accord.
//
// WHICH SESSION, ONE RULE (SessionOf, review H2): the task's session that is LIVE on this task, not replaced, and that the
// board says works on this task now (TaskBoard.TaskOf), the latest joined. An ended entry is history, and a session that
// has since joined another task is that task's: neither is ever copied or stopped for this task's pick, which then waits
// for the next start. Every place that asks "the task's session" for a switch asks this.
//
// WHICH SESSIONS IT TOUCHES AT ALL (review M3): a copy is claimed only by the short id Claude printed for our own resume,
// never by its folder; with no printed id there is no claim, and the switch is given up at once. A switch copies only the
// task's own live session (SessionOf) when the list shows it as a BACKGROUND session, one with no window of the user's
// own (review N2: not whether Deskbar holds a launch record for it, which every session from before the picker lacks), so
// the original it may later stop is that same background session. ClaudeSessions.StopForSwitch checks this in one place,
// MayStop, before any `claude stop`.
//
// A TASK CLOSED OR DELETED DURING A SWITCH (review L3): before the copy has answered, the copy is stopped and the original
// stays the task's session; after, the copy stays and the original is stopped at its next pause as above. A replaced
// session is never left running for good.
public static class SettingsSwitch
{
    /// <summary>How long after the copy's resume went out it may take to be listed before the switch is given up.</summary>
    public static readonly TimeSpan RecoverWithin = TimeSpan.FromMinutes(1);

    // The list's startedAt is whole milliseconds and Claude's own clock reading; the moment the resume was issued is this
    // machine's, to the tick. A session that started within this much before it still counts as started after it.
    static readonly TimeSpan StartSlack = TimeSpan.FromSeconds(1);

    public const string Busy = "Applies when Claude finishes this step.";
    public const string WaitingOnPermission = "Applies once Claude's permission question is answered.";
    public const string JustWoken = "Applies in a moment: the session has only just been woken.";
    public const string UnderWay = "Switching…";
    public const string Confirming = "Applies from Claude's next turn.";
    public const string OriginalStopsAtPause = "Switched. The earlier session stops at its next pause.";
    public const string NotDeskbars = "This session runs in its own window (VS Code or a terminal), which Deskbar cannot restart. Change it there: /model, /effort, Shift+Tab. Your pick applies to the next session Deskbar starts on this task.";
    public const string SessionMoved = "The session changed before the switch could be made (it ended, moved to another task, or was replaced). Your pick applies to the next session Deskbar starts on this task.";
    public const string CannotResume = "The session can no longer be resumed (its transcript or folder is gone). Your pick applies to the next session Deskbar starts on this task.";
    public const string NoCopy = "Claude Code did not list the copy it was asked to make within a minute, so the switch was given up. The session goes on as it was.";
    public const string NoShortId = "Claude Code did not say which session it started for the switch, so the switch was given up. The session goes on as it was.";
    public const string CopyFailed = "Claude Code could not run the session on these settings (the copy it made failed, for example on a model this account cannot use). The task is back on its earlier session, which never stopped. A message sent meanwhile went to the failed copy: send it again.";

    /// <summary>The thread's line when the copy joins its task.</summary>
    public static string JoinedNote(SessionSettings to) =>
        $"Now on {SessionChoices.Describe(to)}. Claude Code made a copy of the session for this, with the same conversation; the earlier one stops once the copy has answered.";

    /// <summary>The thread's line when the session was resumed on the new settings under its own id (no copy).</summary>
    public static string RelaunchedNote(SessionSettings to) => $"Now on {SessionChoices.Describe(to)}.";

    /// <param name="listed">The last good `claude agents --json` read. Never an unreadable one passed as empty.</param>
    /// <param name="resumable">Whether an unlisted session can be woken (ClaudeSessions.Resumable).</param>
    /// <param name="recentlyWoken">Whether Deskbar resumed this session id within SessionKeeper.Grace.</param>
    public static SwitchStep Plan(TaskBoard board, TaskItem task, IReadOnlyCollection<ClaudeAgent> listed,
        Func<TaskSession, bool> resumable, Func<string, bool> recentlyWoken)
    {
        if (!task.IsOpen || task.Wanted.HasNoValue) return new SwitchStep.Nothing();
        if (task.Switching is { HasValue: true } under && under.Value.Issued.HasValue) return new SwitchStep.Wait(UnderWay);
        if (SessionOf(board, task) is not { HasValue: true } current) return new SwitchStep.NextStart(task.Wanted.Value);
        return Decide(current.Value, Target(task.Wanted.Value, current.Value), listed, resumable, recentlyWoken);
    }

    static SwitchStep Decide(TaskSession session, SessionSettings to, IReadOnlyCollection<ClaudeAgent> listed,
        Func<TaskSession, bool> resumable, Func<string, bool> recentlyWoken)
    {
        if (to == session.LaunchedWith) return new SwitchStep.Nothing();

        if (listed.TryFirst(agent => agent.SessionId == session.Id) is not { HasValue: true } listing)
            return resumable(session) ? new SwitchStep.AtNextWake(session, to) : new SwitchStep.NextStart(to);

        var agent = listing.Value;
        // Only a background session is ever copied, so the original a switch later stops is never one in a window of the
        // user's own.
        if (!IsBackground(agent)) return new SwitchStep.ReadOnly(NotDeskbars);
        if (recentlyWoken(session.Id)) return new SwitchStep.Wait(JustWoken);
        // Listed with no process (ClaudeAgent.HasProcess): nothing runs, so there is no step to lose.
        if (!agent.HasProcess) return new SwitchStep.CopyNow(session, agent.ShortId.Value, to);
        if (agent.Status.Map(s => s == "waiting").GetValueOrDefault(false)) return new SwitchStep.Wait(WaitingOnPermission);
        return IsPause(agent) ? new SwitchStep.CopyNow(session, agent.ShortId.Value, to) : new SwitchStep.Wait(Busy);
    }

    /// <summary>
    /// Whether a listed session is at a pause, the one moment a switch acts on it (a copy made of it, or its stop): not
    /// busy, not waiting on a question (status "waiting"), and its turn done (state "done"). A row with no process runs
    /// nothing, so it is at a pause too.
    /// </summary>
    public static bool IsPause(ClaudeAgent agent) =>
        !agent.HasProcess
        || (!agent.Busy && !agent.Status.Map(s => s == "waiting").GetValueOrDefault(false) && agent.State.Map(s => s == "done").GetValueOrDefault(false));

    /// <summary>
    /// Whether a listed session is a background one, with no window of the user's own (VS Code, a terminal): a background
    /// row with a short id. The one test of what a switch may copy, and of an original it may stop (MayStop).
    /// </summary>
    public static bool IsBackground(ClaudeAgent agent) => agent.Background && agent.ShortId.HasValue;

    /// <summary>
    /// THE TASK'S SESSION, for every question a switch or the picker asks (see the header, review H2): live on this task,
    /// not replaced, and working on this task now by the board's own rule (TaskBoard.TaskOf: a session that joined
    /// another task since is that task's); the latest joined. None when no session of the task runs.
    /// </summary>
    public static Maybe<TaskSession> SessionOf(TaskBoard board, TaskItem task) =>
        task.Sessions
            // TaskOf knows open tasks only, so a closed task has no session here: nothing of a closed task is switched.
            .Where(s => s.IsLive && !s.Replaced && board.TaskOf(s.Id).Map(t => t.Id == task.Id).GetValueOrDefault(false))
            .OrderByDescending(s => s.Joined)
            .TryFirst();

    /// <summary>
    /// What the copy runs on: the pick, with the model the session was started on when the pick names none. A copy always
    /// names a model (ClaudeStart.SwitchArguments), and "I changed only the effort" must not move it to another model.
    /// </summary>
    public static SessionSettings Target(SessionSettings wanted, TaskSession session) =>
        wanted.Model.HasValue ? wanted : wanted with { Model = session.LaunchedWith.Model };

    /// <summary>
    /// The next step of the switch under way on <paramref name="task"/>, on a good read of the list: the only way a
    /// switch moves on after the pick began it. See <see cref="PendingSwitch"/> for the phases and the header for the
    /// rules. <paramref name="task"/> may be one the board no longer holds (deleted during the switch): it is then handed
    /// in as last seen, closed, so its copy or original is still seen to.
    /// </summary>
    /// <param name="copyReplied">Whether the joined copy has answered since it joined (ClaudeTranscript.RepliedSince).</param>
    public static SwitchAction Next(TaskBoard board, TaskItem task, IReadOnlyCollection<ClaudeAgent> listed, DateTimeOffset now,
        Func<TaskSession, bool> resumable, Func<string, bool> recentlyWoken, Func<TaskSession, bool> copyReplied)
    {
        if (task.Switching is not { HasValue: true } switching) return new SwitchAction.Nothing();
        var pending = switching.Value;
        if (task.Sessions.TryFirst(s => s.Id == pending.FromSession) is not { HasValue: true } fromEntry)
            return new SwitchAction.Abandon(SessionMoved, KeepWanted: true);
        var from = fromEntry.Value;

        // JOINED: the copy is the task's session; the original waits for the copy's first reply, then for its own pause.
        if (pending.CopyId is { HasValue: true } copyId)
        {
            var copyShortId = pending.CopyShortId.GetValueOrDefault(copyId.Value[..Math.Min(8, copyId.Value.Length)]);
            var replied = task.Sessions.TryFirst(s => s.Id == copyId.Value).Map(copyReplied).GetValueOrDefault(false);
            if (replied) return Finish(board, task, from, listed);
            if (listed.TryFirst(a => a.SessionId == copyId.Value).Map(a => a.State.Map(s => s == "failed").GetValueOrDefault(false)).GetValueOrDefault(false))
                return new SwitchAction.Revert(copyShortId, CopyFailed);
            // Closed (or deleted) before the copy answered: the original stays the task's session, the copy goes.
            if (!task.IsOpen) return new SwitchAction.Revert(copyShortId, "");
            // Neither runs any more (both stopped, a finished task's sessions are): nothing is left to stop or to fall
            // back to, so the copy simply stays the task's session.
            if (listed.All(a => a.SessionId != copyId.Value) && listed.All(a => a.SessionId != from.Id)) return new SwitchAction.Confirm(Maybe<string>.None);
            return new SwitchAction.Wait(Confirming);
        }

        // ISSUED: the copy's resume went out; it is claimed only by the short id Claude printed for it.
        if (pending.Issued is { HasValue: true } issued)
        {
            if (pending.CopyShortId is not { HasValue: true } shortId) return new SwitchAction.Abandon(NoShortId, KeepWanted: false);
            var known = board.Tasks.SelectMany(t => t.Sessions).Select(s => s.Id).ToHashSet();
            var copy = listed.TryFirst(a => IsCopyOf(task, pending, a) && !known.Contains(a.SessionId));
            if (copy.HasValue) return task.IsOpen ? new SwitchAction.Join(copy.Value) : new SwitchAction.Discard(shortId.Value);
            // The resume kept the session's own id: Claude printed the original's own short id, and it is listed again,
            // started after the resume went out.
            if (from.Id.StartsWith(shortId.Value, StringComparison.OrdinalIgnoreCase)
                && listed.TryFirst(a => a.SessionId == from.Id && a.StartedAt.Map(at => at >= issued.Value - StartSlack).GetValueOrDefault(false))
                    is { HasValue: true } same)
                return task.IsOpen ? new SwitchAction.Relaunched(same.Value) : new SwitchAction.Abandon("", KeepWanted: true);
            return now - issued.Value > RecoverWithin ? new SwitchAction.Abandon(NoCopy, KeepWanted: false) : new SwitchAction.Wait(UnderWay);
        }

        // REQUESTED: waiting for a pause of the session it was picked for, while it is still this task's session.
        if (!task.IsOpen) return new SwitchAction.Abandon("", KeepWanted: true);
        if (SessionOf(board, task).Map(s => s.Id != from.Id).GetValueOrDefault(true)) return new SwitchAction.Abandon(SessionMoved, KeepWanted: true);
        return Decide(from, pending.To, listed, resumable, recentlyWoken) switch
        {
            SwitchStep.CopyNow copy => new SwitchAction.Issue(from, copy.To),
            SwitchStep.AtNextWake wake => new SwitchAction.Issue(from, wake.To),
            SwitchStep.Wait wait => new SwitchAction.Wait(wait.Why),
            SwitchStep.ReadOnly readOnly => new SwitchAction.Abandon(readOnly.Why, KeepWanted: true),
            SwitchStep.NextStart => new SwitchAction.Abandon(CannotResume, KeepWanted: true),
            _ => new SwitchAction.Abandon("", KeepWanted: true),
        };
    }

    // The copy has answered: the switch is final. The original is stopped at a read that shows it at a pause (H1); one
    // that no longer runs, or that has since joined another task (never ours to stop then), is left alone and the switch
    // is done.
    static SwitchAction Finish(TaskBoard board, TaskItem task, TaskSession from, IReadOnlyCollection<ClaudeAgent> listed)
    {
        if (listed.TryFirst(a => a.SessionId == from.Id) is not { HasValue: true } original) return new SwitchAction.Confirm(Maybe<string>.None);
        if (board.TaskOf(from.Id).Map(t => t.Id != task.Id).GetValueOrDefault(false)) return new SwitchAction.Confirm(Maybe<string>.None);
        if (!original.Value.Background || original.Value.ShortId.HasNoValue) return new SwitchAction.Confirm(Maybe<string>.None);
        return IsPause(original.Value) ? new SwitchAction.Confirm(original.Value.ShortId) : new SwitchAction.Wait(OriginalStopsAtPause);
    }

    /// <summary>
    /// Whether a listed session may be the copy of a switch issued on <paramref name="task"/>, so the keeper must not
    /// adopt it as a task of its own (SessionKeeper.Plan): only the session Claude printed for our resume.
    /// </summary>
    public static bool MayClaim(TaskItem task, ClaudeAgent agent) =>
        task.Switching is { HasValue: true } pending && pending.Value.CopyId.HasNoValue && IsCopyOf(task, pending.Value, agent);

    // By the short id Claude printed for our resume, and by nothing else (review M3): a session started in the same folder
    // meanwhile (a VS Code panel, a terminal) is never taken for the copy.
    static bool IsCopyOf(TaskItem task, PendingSwitch pending, ClaudeAgent agent) =>
        agent.SessionId != pending.FromSession
        && pending.Issued.HasValue
        && pending.CopyShortId.Map(shortId =>
                agent.ShortId.Map(id => id.Equals(shortId, StringComparison.OrdinalIgnoreCase)).GetValueOrDefault(false)
                || agent.SessionId.StartsWith(shortId, StringComparison.OrdinalIgnoreCase))
            .GetValueOrDefault(false);

    /// <summary>
    /// Whether a switch may run `claude stop` on <paramref name="shortId"/> (the one place this is decided): the copy's
    /// short id as Claude printed it for our own resume, or the short id of the switch's original session, the task's own,
    /// while <paramref name="listed"/> shows it as a background session (IsBackground, review N2). Nothing else, so a
    /// switch never stops a session in a window of the user's own, nor any session it did not copy from or make.
    /// </summary>
    public static bool MayStop(TaskItem task, PendingSwitch pending, string shortId, IReadOnlyCollection<ClaudeAgent> listed) =>
        shortId.Length >= 8
        && (pending.CopyShortId.Map(id => id.Equals(shortId, StringComparison.OrdinalIgnoreCase)).GetValueOrDefault(false)
            || (task.Sessions.Any(s => s.Id == pending.FromSession && s.Id.StartsWith(shortId, StringComparison.OrdinalIgnoreCase))
                && listed.Any(a => a.SessionId == pending.FromSession && IsBackground(a)
                    && a.ShortId.Value.Equals(shortId, StringComparison.OrdinalIgnoreCase))));

    /// <summary>
    /// Whether a woken session came back on other settings than Deskbar started it with: its effort or permission mode,
    /// as its hooks report them (<paramref name="observed"/>), differs from a value <see cref="TaskSession.LaunchedWith"/>
    /// holds. Only values both sides know count; the model is left out (the hooks report it rarely). For telling, not
    /// for acting: a bare wake keeps the saved options (measured), and if that ever fails (after a reboot, say) this is
    /// how it is noticed. Nothing switches on it (see WHAT STARTS ONE above). Later in a session a difference is the
    /// user's own /effort or Shift+Tab, which wins.
    /// </summary>
    public static bool Drifted(TaskSession session, SessionSettings observed) =>
        Differs(session.LaunchedWith.Effort, observed.Effort) || Differs(session.LaunchedWith.Mode, observed.Mode);

    static bool Differs(Maybe<string> launched, Maybe<string> observed) =>
        launched.HasValue && observed.HasValue && !launched.Value.Equals(observed.Value, StringComparison.OrdinalIgnoreCase)
        // `manual` is Claude Code's other name for `default` (SessionChoices.Mode).
        && !(launched.Value == "default" && observed.Value.Equals("manual", StringComparison.OrdinalIgnoreCase));
}
