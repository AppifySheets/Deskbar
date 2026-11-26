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
/// answered once (then it is stopped) or failed (then the task goes back to it).</item>
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

    /// <summary>A session Deskbar cannot stop from outside (VS Code, a terminal): it can only be changed in its window.</summary>
    public sealed record ReadOnly(string Why) : SwitchStep;

    /// <summary>The session does not run but can be resumed: it is resumed with the new settings now (no stop needed).</summary>
    public sealed record AtNextWake(TaskSession Session, SessionSettings To) : SwitchStep;

    /// <summary>No session can take it (none, or none that can be resumed): the next start on the task uses it.</summary>
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

    /// <summary>The copy is listed: it joins the task (TaskBoard.SessionSwitched).</summary>
    public sealed record Join(ClaudeAgent Copy) : SwitchAction;

    /// <summary>
    /// The resume kept the session's own id (a session Claude Code held no saved options for, measured 2026-11-26): it is
    /// the same session, now on the new settings (TaskBoard.SessionRelaunched).
    /// </summary>
    public sealed record Relaunched(ClaudeAgent Session) : SwitchAction;

    /// <summary>
    /// The copy has answered once: the switch is done. <see cref="StopOriginal"/> is the original's short id when it is
    /// still listed as a background session and must be stopped; none when nothing of it runs.
    /// </summary>
    public sealed record Confirm(Maybe<string> StopOriginal) : SwitchAction;

    /// <summary>
    /// The copy failed (Claude Code lists it "failed": a model it cannot run, measured 2026-11-26): the task goes back to
    /// the original, which never stopped (TaskBoard.SwitchReverted), and the copy is stopped when it has a short id.
    /// </summary>
    public sealed record Revert(Maybe<string> StopCopy, string Why) : SwitchAction;

    /// <summary>
    /// The switch is given up (TaskBoard.SwitchAbandoned); <see cref="Why"/> is said in the thread when not empty, and
    /// <see cref="KeepWanted"/> keeps the pick for later starts when the switch was not its fault.
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
// THE ORDER: COPY, JOIN, AND STOP THE ORIGINAL ONLY ONCE THE COPY HAS ANSWERED. The copy is made while the original still
// runs, the task moves to the copy (so the user's next message goes to it), and the original is stopped only after the
// copy's first reply has come back. At no moment does a conversation sit stopped waiting for Deskbar: the owner's rule is
// that a Claude session must never stop or hang because Deskbar closed, restarted or crashed. If the copy cannot run its
// model, the task goes back to the original, which never stopped. Every phase is stored (PendingSwitch), so a restarted
// Deskbar carries on where the last one was (Next).
//
// WHAT STARTS ONE. Only the user's pick (ClaudeSessions.Pick, through TaskBoard.Want and SwitchStarted). Nothing here
// makes a switch by itself: Next acts only on a switch the pick began (TaskItem.Switching), so a session that joins a
// task with a pick on it, or a wake that comes back on other settings (Drifted), never starts a copy of its own accord.
//
// WHICH SESSION: the one a comment goes to (CommentWake: never one a /clear or a switch replaced; live first, then the
// latest joined). The task's other sessions are not touched.
//
// WHEN: only at a pause. A background session that is idle, its turn over (state "done"), not woken within
// SessionKeeper.Grace (a fresh resume reads busy for up to 30 seconds while idle, measured). Busy, waiting on a permission
// prompt (status "waiting"), or busy only on a background job of its own (ClaudeAgent.TurnOver: busy with state done or
// blocked; the job belongs to the original's process) is a Wait. A session that never pauses never switches: nothing
// forces it. An interactive session (no short id: VS Code, a terminal) cannot be stopped from outside (SessionKeeper's
// header), so it is read-only; the pick still applies to the next session Deskbar starts on the task.
public static class SettingsSwitch
{
    /// <summary>How long after the copy's resume went out it may take to be listed before the switch is given up.</summary>
    public static readonly TimeSpan RecoverWithin = TimeSpan.FromMinutes(1);

    // The list's startedAt is whole milliseconds and Claude's own clock reading; the moment the resume was issued is this
    // machine's, to the tick. A session that started within this much before it still counts as started after it. The
    // original never does: it was listed long before, and a session woken within SessionKeeper.Grace is not switched.
    static readonly TimeSpan StartSlack = TimeSpan.FromSeconds(1);

    public const string Busy = "Applies when Claude finishes this step.";
    public const string WaitingOnPermission = "Applies once Claude's permission question is answered.";
    public const string JustWoken = "Applies in a moment: the session has only just been woken.";
    public const string UnderWay = "Switching…";
    public const string Confirming = "Applies from Claude's next turn.";
    public const string NotDeskbars = "This session runs in its own window (VS Code or a terminal), which Deskbar cannot restart. Change it there: /model, /effort, Shift+Tab. Your pick applies to the next session Deskbar starts on this task.";
    public const string SessionMoved = "The session changed before the switch could be made (a new one joined the task, or it was replaced). Your pick applies to the next session Deskbar starts on this task.";
    public const string CannotResume = "The session can no longer be resumed (its transcript or folder is gone). Your pick applies to the next session Deskbar starts on this task.";
    public const string NoCopy = "Claude Code did not list the copy it was asked to make within a minute, so the switch was given up. The session goes on as it was.";
    public const string CopyFailed = "Claude Code could not run the session on these settings (the copy it made failed, for example on a model this account cannot use). The task is back on its earlier session, which never stopped. A message sent meanwhile went to the failed copy: send it again.";

    /// <summary>The thread's line when the copy joins its task.</summary>
    public static string JoinedNote(SessionSettings to) =>
        $"Now on {SessionChoices.Describe(to)}. Claude Code made a copy of the session for this, with the same conversation; the earlier one stops once the copy has answered.";

    /// <summary>The thread's line when the session was resumed on the new settings under its own id (no copy).</summary>
    public static string RelaunchedNote(SessionSettings to) => $"Now on {SessionChoices.Describe(to)}.";

    /// <param name="listed">The last good `claude agents --json` read. Never an unreadable one passed as empty.</param>
    /// <param name="resumable">Whether an unlisted session can be woken (ClaudeSessions.Resumable).</param>
    /// <param name="recentlyWoken">Whether Deskbar resumed this session id within SessionKeeper.Grace.</param>
    public static SwitchStep Plan(TaskItem task, IReadOnlyCollection<ClaudeAgent> listed,
        Func<TaskSession, bool> resumable, Func<string, bool> recentlyWoken)
    {
        if (!task.IsOpen || task.Wanted.HasNoValue) return new SwitchStep.Nothing();
        if (task.Switching is { HasValue: true } under && under.Value.Issued.HasValue) return new SwitchStep.Wait(UnderWay);
        if (Current(task) is not { HasValue: true } current) return new SwitchStep.NextStart(task.Wanted.Value);
        return Decide(current.Value, Target(task.Wanted.Value, current.Value), listed, resumable, recentlyWoken);
    }

    static SwitchStep Decide(TaskSession session, SessionSettings to, IReadOnlyCollection<ClaudeAgent> listed,
        Func<TaskSession, bool> resumable, Func<string, bool> recentlyWoken)
    {
        if (to == session.LaunchedWith) return new SwitchStep.Nothing();

        if (listed.TryFirst(agent => agent.SessionId == session.Id) is not { HasValue: true } listing)
            return resumable(session) ? new SwitchStep.AtNextWake(session, to) : new SwitchStep.NextStart(to);

        var agent = listing.Value;
        if (!agent.Background || agent.ShortId.HasNoValue) return new SwitchStep.ReadOnly(NotDeskbars);
        if (recentlyWoken(session.Id)) return new SwitchStep.Wait(JustWoken);
        // Listed with no process (ClaudeAgent.HasProcess): nothing runs, so there is no step to lose.
        if (!agent.HasProcess) return new SwitchStep.CopyNow(session, agent.ShortId.Value, to);
        if (agent.Status.Map(s => s == "waiting").GetValueOrDefault(false)) return new SwitchStep.Wait(WaitingOnPermission);
        return agent.Busy || !agent.State.Map(s => s == "done").GetValueOrDefault(false)
            ? new SwitchStep.Wait(Busy)
            : new SwitchStep.CopyNow(session, agent.ShortId.Value, to);
    }

    /// <summary>
    /// The session a pick applies to: the one a comment goes to (CommentWake), so never one a /clear or a switch
    /// replaced; live first, then the latest joined. None for a task with no such session.
    /// </summary>
    public static Maybe<TaskSession> Current(TaskItem task) =>
        task.Sessions.Where(s => !s.Replaced).OrderByDescending(s => s.IsLive).ThenByDescending(s => s.Joined).TryFirst();

    /// <summary>
    /// What the copy runs on: the pick, with the model the session was started on when the pick names none. A copy always
    /// names a model (ClaudeStart.SwitchArguments), and "I changed only the effort" must not move it to another model. A
    /// session Deskbar did not start has no model on record; the app fills that in when it issues the copy.
    /// </summary>
    public static SessionSettings Target(SessionSettings wanted, TaskSession session) =>
        wanted.Model.HasValue ? wanted : wanted with { Model = session.LaunchedWith.Model };

    /// <summary>
    /// The next step of the switch under way on <paramref name="task"/>, on a good read of the list: the only way a
    /// switch moves on after the pick began it. See <see cref="PendingSwitch"/> for the phases.
    /// </summary>
    /// <param name="known">Whether a session id is on any task already (the copy is not, until it is joined).</param>
    /// <param name="copyReplied">Whether the joined copy has answered since it joined (ClaudeTranscript.RepliedSince).</param>
    public static SwitchAction Next(TaskItem task, IReadOnlyCollection<ClaudeAgent> listed, DateTimeOffset now,
        Func<TaskSession, bool> resumable, Func<string, bool> recentlyWoken, Func<string, bool> known, Func<TaskSession, bool> copyReplied)
    {
        if (task.Switching is not { HasValue: true } switching) return new SwitchAction.Nothing();
        var pending = switching.Value;
        if (!task.IsOpen) return new SwitchAction.Abandon("", KeepWanted: true);
        if (task.Sessions.TryFirst(s => s.Id == pending.FromSession) is not { HasValue: true } fromEntry)
            return new SwitchAction.Abandon(SessionMoved, KeepWanted: true);
        var from = fromEntry.Value;

        // JOINED: the copy is the task's session; the original waits for the copy's first reply.
        if (pending.CopyId is { HasValue: true } copyId)
        {
            var copyListing = listed.TryFirst(a => a.SessionId == copyId.Value);
            var original = listed.TryFirst(a => a.SessionId == from.Id && a.Background && a.ShortId.HasValue).Map(a => a.ShortId.Value);
            if (copyListing.Map(a => a.State.Map(s => s == "failed").GetValueOrDefault(false)).GetValueOrDefault(false))
                return new SwitchAction.Revert(copyListing.Bind(a => a.ShortId), CopyFailed);
            if (task.Sessions.TryFirst(s => s.Id == copyId.Value) is { HasValue: true } copy && copyReplied(copy.Value))
                return new SwitchAction.Confirm(original);
            // Neither runs any more (both stopped, a finished task's sessions are): nothing is left to stop or to fall
            // back to, so the copy simply stays the task's session.
            if (copyListing.HasNoValue && listed.All(a => a.SessionId != from.Id)) return new SwitchAction.Confirm(Maybe<string>.None);
            return new SwitchAction.Wait(Confirming);
        }

        // ISSUED: the copy's resume went out; it joins once listed.
        if (pending.Issued is { HasValue: true } issued)
        {
            var copies = listed.Where(agent => IsCopyOf(task, pending, agent) && !known(agent.SessionId)).ToList();
            if (copies.Count == 1) return new SwitchAction.Join(copies[0]);
            // The resume kept the session's own id: it is listed again, started after the resume went out.
            if (listed.TryFirst(a => a.SessionId == from.Id && a.StartedAt.Map(at => at >= issued.Value - StartSlack).GetValueOrDefault(false)
                    && pending.CopyShortId.Map(id => from.Id.StartsWith(id, StringComparison.OrdinalIgnoreCase)).GetValueOrDefault(true))
                is { HasValue: true } same)
                return new SwitchAction.Relaunched(same.Value);
            return now - issued.Value > RecoverWithin ? new SwitchAction.Abandon(NoCopy, KeepWanted: false) : new SwitchAction.Wait(UnderWay);
        }

        // REQUESTED: waiting for a pause of the session it was picked for.
        if (Current(task).Map(s => s.Id != from.Id).GetValueOrDefault(true)) return new SwitchAction.Abandon(SessionMoved, KeepWanted: true);
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

    /// <summary>
    /// Whether a listed session may be the copy of a switch issued on <paramref name="task"/>, so the keeper must not
    /// adopt it as a task of its own (SessionKeeper.Plan), and the copy's own SessionStart hook may join it
    /// (TaskBoard.SwitchingTaskFor).
    /// </summary>
    public static bool MayClaim(TaskItem task, ClaudeAgent agent) =>
        task.Switching is { HasValue: true } pending && pending.Value.CopyId.HasNoValue && IsCopyOf(task, pending.Value, agent);

    // By short id when the switch has one; else by folder and start time. The folder is compared as text, ignoring case,
    // slashes and trailing separators: the copy runs in the folder the resume ran in, which is the original's own.
    static bool IsCopyOf(TaskItem task, PendingSwitch pending, ClaudeAgent agent) =>
        agent.SessionId != pending.FromSession
        && pending.Issued.HasValue
        && pending.CopyShortId.Match(
            shortId => agent.ShortId.Map(id => id.Equals(shortId, StringComparison.OrdinalIgnoreCase)).GetValueOrDefault(false)
                || agent.SessionId.StartsWith(shortId, StringComparison.OrdinalIgnoreCase),
            () => task.Sessions.TryFirst(s => s.Id == pending.FromSession)
                .Map(from => SameFolder(from.Folder, agent.Folder) && agent.StartedAt.Map(at => at >= pending.Issued.Value - StartSlack).GetValueOrDefault(false))
                .GetValueOrDefault(false));

    /// <summary>Two folders as the same text, ignoring case, slashes and trailing separators.</summary>
    public static bool SameFolder(string a, string b) =>
        a.Length > 0 && string.Equals(Normal(a), Normal(b), StringComparison.OrdinalIgnoreCase);

    static string Normal(string folder) => folder.Replace('/', '\\').TrimEnd('\\');

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
