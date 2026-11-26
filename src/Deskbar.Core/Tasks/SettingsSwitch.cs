using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

/// <summary>
/// A switch of a task's session to other settings, under way (TaskItem.Switching): the session it copies
/// (<see cref="FromSession"/>, its full id), what the copy runs on (<see cref="To"/>), when it began
/// (<see cref="At"/>), and the copy's short id once Claude named it (<see cref="CopyShortId"/>).
/// </summary>
public sealed record PendingSwitch(string FromSession, SessionSettings To, DateTimeOffset At)
{
    public Maybe<string> CopyShortId { get; init; }
}

/// <summary>What to do about a task's pick (SettingsSwitch.Plan).</summary>
public abstract record SwitchStep
{
    /// <summary>Nothing: no pick, a closed task, or the session already runs on the pick.</summary>
    public sealed record Nothing : SwitchStep;

    /// <summary>The session is an idle background one: copy it with the new settings now, then stop it.</summary>
    public sealed record CopyNow(TaskSession Session, string ShortId, SessionSettings To) : SwitchStep;

    /// <summary>Not now (<see cref="Why"/> is the line the picker shows); the next good read of the list asks again.</summary>
    public sealed record Wait(string Why) : SwitchStep;

    /// <summary>A session Deskbar cannot stop from outside (VS Code, a terminal): it can only be changed in its window.</summary>
    public sealed record ReadOnly(string Why) : SwitchStep;

    /// <summary>The session does not run: the next wake of it (Open, a comment, the keeper) makes the copy instead.</summary>
    public sealed record AtNextWake(TaskSession Session, SessionSettings To) : SwitchStep;

    /// <summary>No session can take it (none, or none that can be resumed): the next start on the task uses it.</summary>
    public sealed record NextStart(SessionSettings To) : SwitchStep;
}

/// <summary>What a restarted Deskbar does about a switch it left under way (SettingsSwitch.Recover).</summary>
public abstract record SwitchRecovery
{
    /// <summary>No switch under way.</summary>
    public sealed record Nothing : SwitchRecovery;

    /// <summary>The copy is listed: join it to the task and stop the original (TaskBoard.SessionSwitched).</summary>
    public sealed record Finish(ClaudeAgent Copy) : SwitchRecovery;

    /// <summary>Not found yet; ask again on the next read.</summary>
    public sealed record Wait : SwitchRecovery;

    /// <summary>No copy appeared in time: give the switch up (TaskBoard.SwitchAbandoned). The original was never stopped.</summary>
    public sealed record Abandon : SwitchRecovery;
}

// CHANGING THE MODEL, EFFORT OR PERMISSION MODE OF A TASK'S SESSION: WHEN, AND TO WHICH SESSION. Pure; the app runs the
// steps. Measured 2026-11-26 with Claude Code 2.1.332, background sessions:
//   * There is no scriptable way to change the model or effort of a running session. `claude --bg --resume <id>` with
//     flags starts a COPY under a new id, whether the session is running or stopped: a new short id, a new session id, a
//     new transcript holding the whole old conversation, and the new settings. The original stays in the list.
//   * `claude stop <short id>` takes about a second, `claude --bg --resume` returns in about one more.
//   * Stopping a busy session loses the step in flight; its transcript marks the tool call as interrupted.
//
// THE ORDER IS COPY FIRST, STOP SECOND. Because a resume with flags makes a copy even of a RUNNING session, the app
// starts the copy while the original still runs, joins the copy to the task, and only then stops the original. At no
// moment does a conversation sit stopped waiting for Deskbar, which is the owner's rule: a Claude session must never
// stop or hang because Deskbar closed, restarted or crashed. If the copy fails to start, the original simply goes on.
// If Deskbar dies between the two, both run (the original idle, nothing lost), and TaskItem.Switching, stored before
// the copy is started, lets the next Deskbar finish the job (Recover) instead of adopting the copy as a stray task.
//
// WHICH SESSION: the one a comment goes to (CommentWake: never one a /clear or a switch replaced; live first, then the
// latest joined). The task's other sessions are not touched.
//
// WHEN (Plan): only at a pause. A background session that is idle, its turn over (state "done"), its process running
// or gone, and not woken within SessionKeeper.Grace (a fresh resume reads busy for up to 30 seconds while idle, measured).
// Busy, waiting on a permission prompt (status "waiting"), or busy only on a background job of its own (ClaudeAgent.
// TurnOver: busy with state done or blocked; the job belongs to the original's process and a stop would kill it) is a
// Wait, asked again on every good read of the list. A session that never pauses never switches: nothing forces it.
// An interactive session (no short id: VS Code, a terminal) cannot be stopped from outside (SessionKeeper's header), so
// it is read-only; the pick still applies to the next session Deskbar starts on the task.
public static class SettingsSwitch
{
    /// <summary>How long after a switch began a restarted Deskbar looks for its copy before giving it up (Recover).</summary>
    public static readonly TimeSpan RecoverWithin = TimeSpan.FromMinutes(1);

    public const string Busy = "Applies when Claude finishes this step.";
    public const string WaitingOnPermission = "Applies once Claude's permission question is answered.";
    public const string JustWoken = "Applies in a moment: the session has only just been woken.";
    public const string UnderWay = "Switching…";
    public const string NotDeskbars = "This session runs in its own window (VS Code or a terminal), which Deskbar cannot restart. Change it there: /model, /effort, Shift+Tab. Your pick applies to the next session Deskbar starts on this task.";

    /// <param name="listed">The last good `claude agents --json` read. Never an unreadable one passed as empty.</param>
    /// <param name="resumable">Whether an unlisted session can be woken (ClaudeSessions.Resumable).</param>
    /// <param name="recentlyWoken">Whether Deskbar resumed this session id within SessionKeeper.Grace.</param>
    public static SwitchStep Plan(TaskItem task, IReadOnlyCollection<ClaudeAgent> listed,
        Func<TaskSession, bool> resumable, Func<string, bool> recentlyWoken)
    {
        if (!task.IsOpen || task.Wanted.HasNoValue) return new SwitchStep.Nothing();
        if (task.Switching.HasValue) return new SwitchStep.Wait(UnderWay);
        if (Current(task) is not { HasValue: true } current) return new SwitchStep.NextStart(task.Wanted.Value);

        var session = current.Value;
        var to = Target(task.Wanted.Value, session);
        if (to == session.LaunchedWith) return new SwitchStep.Nothing();

        if (listed.TryFirst(agent => agent.SessionId == session.Id) is not { HasValue: true } listing)
            return resumable(session) ? new SwitchStep.AtNextWake(session, to) : new SwitchStep.NextStart(to);

        var agent = listing.Value;
        if (!agent.Background || agent.ShortId.HasNoValue) return new SwitchStep.ReadOnly(NotDeskbars);
        if (recentlyWoken(session.Id)) return new SwitchStep.Wait(JustWoken);
        // Listed with no process (ClaudeAgent.HasProcess): nothing runs, so there is no step to lose; the copy starts
        // from its transcript, and the stop that follows only takes the dead row off the list.
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

    // What the copy runs on: the pick, with the model the session was started on when the pick names none. A copy always
    // names a model (ClaudeStart.SwitchArguments), and "I changed only the effort" must not move it to another model. A
    // session Deskbar did not start has no model on record; the app fills that from what it observed or the setting.
    static SessionSettings Target(SessionSettings wanted, TaskSession session) =>
        wanted.Model.HasValue ? wanted : wanted with { Model = session.LaunchedWith.Model };

    /// <summary>
    /// What to do about a switch a Deskbar that closed left under way (TaskItem.Switching), on the first good reads of the
    /// list after it starts again. The copy is the listed session the switch named by its short id; when the short id
    /// was never stored (Deskbar closed before Claude answered), it is the ONE listed session in the original's folder
    /// that started after the switch began and that no task knows. Two such sessions, or none past
    /// <see cref="RecoverWithin"/>, and the switch is given up: the original was never stopped, so it still runs.
    /// </summary>
    /// <param name="known">Whether a session id is on any task already (the copy is not, until it is joined).</param>
    public static SwitchRecovery Recover(TaskItem task, IReadOnlyCollection<ClaudeAgent> listed, DateTimeOffset now, Func<string, bool> known)
    {
        if (task.Switching is not { HasValue: true } pending) return new SwitchRecovery.Nothing();
        var copies = listed.Where(agent => IsCopyOf(task, pending.Value, agent) && !known(agent.SessionId)).ToList();
        return copies.Count == 1 ? new SwitchRecovery.Finish(copies[0])
            : now - pending.Value.At > RecoverWithin ? new SwitchRecovery.Abandon()
            : new SwitchRecovery.Wait();
    }

    /// <summary>
    /// Whether a listed session may be the copy of a switch under way on <paramref name="task"/>, so the keeper must not
    /// adopt it as a task of its own (SessionKeeper.Plan) while Recover can still claim it.
    /// </summary>
    public static bool MayClaim(TaskItem task, ClaudeAgent agent) =>
        task.Switching is { HasValue: true } pending && IsCopyOf(task, pending.Value, agent);

    // By short id when the switch has one; else by folder and start time. The folder is compared as text, ignoring case,
    // slashes and trailing separators: the copy runs in the folder the resume ran in, which is the original's own.
    static bool IsCopyOf(TaskItem task, PendingSwitch pending, ClaudeAgent agent) =>
        agent.SessionId != pending.FromSession
        && pending.CopyShortId.Match(
            shortId => agent.ShortId.Map(id => id.Equals(shortId, StringComparison.OrdinalIgnoreCase)).GetValueOrDefault(false)
                || agent.SessionId.StartsWith(shortId, StringComparison.OrdinalIgnoreCase),
            () => task.Sessions.TryFirst(s => s.Id == pending.FromSession)
                .Map(from => SameFolder(from.Folder, agent.Folder) && agent.StartedAt.Map(at => at >= pending.At).GetValueOrDefault(false))
                .GetValueOrDefault(false));

    static bool SameFolder(string a, string b) =>
        a.Length > 0 && string.Equals(Normal(a), Normal(b), StringComparison.OrdinalIgnoreCase);

    static string Normal(string folder) => folder.Replace('/', '\\').TrimEnd('\\');

    /// <summary>
    /// Whether a woken session came back on other settings than Deskbar started it with: its effort or permission mode,
    /// as its hooks report them (<paramref name="observed"/>), differs from a value <see cref="TaskSession.LaunchedWith"/>
    /// holds. Only values both sides know count; the model is left out (the hooks report it rarely). Meant for the FIRST
    /// hook after Deskbar woke the session: a bare wake keeps the saved options (measured), and if that ever fails
    /// (after a reboot, say) this is how it is noticed. Later in a session a difference is the user's own /effort or
    /// Shift+Tab, which wins and is not undone.
    /// </summary>
    public static bool Drifted(TaskSession session, SessionSettings observed) =>
        Differs(session.LaunchedWith.Effort, observed.Effort) || Differs(session.LaunchedWith.Mode, observed.Mode);

    static bool Differs(Maybe<string> launched, Maybe<string> observed) =>
        launched.HasValue && observed.HasValue && !launched.Value.Equals(observed.Value, StringComparison.OrdinalIgnoreCase)
        // `manual` is Claude Code's other name for `default` (SessionChoices.Mode).
        && !(launched.Value == "default" && observed.Value.Equals("manual", StringComparison.OrdinalIgnoreCase));
}
