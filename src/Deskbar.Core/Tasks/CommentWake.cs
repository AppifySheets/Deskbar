using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

/// <summary>How one of Petre's comments reaches the task's Claude session (CommentWake.Plan).</summary>
public abstract record WakePlan
{
    /// <summary>The session runs: the comment goes into its inbox file, which its waiting Stop hook hands it.</summary>
    public sealed record Inbox(string SessionId) : WakePlan;

    /// <summary>The session does not run: it is resumed in the background with the comment as its message.</summary>
    public sealed record Resume(TaskSession Session) : WakePlan;

    /// <summary>A background session whose waiter has lapsed: stopped by its short id, then resumed with the comment.</summary>
    public sealed record StopThenResume(TaskSession Session, string ShortId) : WakePlan;

    /// <summary>No session can take it; the comment stays in the thread, and Why is said there.</summary>
    public sealed record Nothing(string Why) : WakePlan;

    /// <summary>
    /// A recurring task between runs with no session listed (task 0b78269e, spec
    /// docs/superpowers/specs/2026-11-26-recurring-tasks-design.md, "A run ends"): the comment starts a new run, a fresh
    /// session that reads the thread, as "Wake now" does, instead of reaching an old run's session. The comment is
    /// already in the thread, so it needs carrying nowhere.
    /// </summary>
    public sealed record FreshRun : WakePlan;
}

// A COMMENT WAKES ITS SESSION. Design: docs/superpowers/specs/2026-11-24-task-images-design.md, "PR 2: a comment wakes its
// session"; plan docs/superpowers/plans/2026-11-24-comment-wakes-session.md, whose first section lists what was measured.
//
// Petre asked (2026-11-24) that a comment he posts reach the task's session and WAKE it if it is idle, not wait for its
// next turn. Claude Code's documented hook option `asyncRewake` does that: Deskbar's Stop hook keeps a small waiter running
// after each turn, and when the waiter exits 2 its text becomes a new message and the session takes a turn (measured: in
// under 2 seconds; a waiter that fires mid-turn lands at the next step). So the road depends on the session:
//
//   * listed by `claude agents --json` (running), waiter alive: the comment goes into its inbox; the waiter wakes it.
//   * running, waiter lapsed (idle past the hook's month-long timeout): a background session is stopped and resumed with
//     the comment, because resuming one that runs starts a copy (measured 2026-11-23); a terminal or VS Code session
//     cannot be restarted from outside, so its comment waits in the inbox for its next turn.
//   * not running (closed, rebooted, stopped because its task was Done): resumed with the comment.
//   * listed WITHOUT A PROCESS (ClaudeAgent.HasProcess, task daeaa26c): it is not running, whatever the list says, and
//     its inbox may be read by an orphaned waiter that hands the comment to nobody. A background one is stopped (which
//     takes it off the list; harmless on a session with no process) and resumed with the comment; any other is resumed.
//   * no session with a transcript left: nothing to wake, said in the thread.
//
// ONE comment goes to ONE session: the one a double-click goes to (TaskViews.SessionToGoTo: live first, then the newest
// joined), among those whose transcript still exists (an ended session without one cannot be resumed) and that /clear did
// not replace (its conversation is over; the session id lives on in the new one).
//
// A RECURRING TASK BETWEEN RUNS is asked first (task 0b78269e, spec docs/superpowers/specs/2026-11-26-recurring-tasks-design.md,
// "A run ends"). Its last run is over and its session stopped, so a comment starts a run straight away (FreshRun), as
// "Wake now" does: a fresh session that reads the thread, comment included, rather than the old run's session resumed
// with it. That is the spec's ruling: every run is a fresh session, never one long-lived session woken again (the
// approach it considered and dropped, whose context grows run after run), and a run learns what was said since the last
// one from the thread (ClaudeStart's scheduled-run line tells it to read the thread first). Only while NO session of the
// task is listed: the last run's session can still be there
// (not yet idle for the keeper's StopAfter, or with Petre's window on it), and then it reads the comment by the usual
// road, since a fresh run beside it would put two sessions on one task. A run in progress or waiting on Petre is not
// between runs and takes the usual road too.
public static class CommentWake
{
    public static WakePlan Plan(TaskItem task, IReadOnlyList<ClaudeAgent> listed, Func<TaskSession, bool> hasTranscript, Func<string, bool> waiterLapsed) =>
        task.IsBetweenRuns && !task.Sessions.Any(session => listed.Any(agent => agent.SessionId == session.Id))
            ? new WakePlan.FreshRun()
            : PlanFor(task.Sessions, listed, hasTranscript, waiterLapsed);

    /// <summary>
    /// The same roads for sessions that belong to no task: the manager's one session, which Deskbar's event wakes reach
    /// this way (ManagerActions.Tick; docs/superpowers/specs/2026-11-25-manager-event-wakes-design.md).
    /// </summary>
    public static WakePlan PlanFor(IReadOnlyList<TaskSession> sessions, IReadOnlyList<ClaudeAgent> listed, Func<TaskSession, bool> hasTranscript,
        Func<string, bool> waiterLapsed) =>
        sessions.Where(s => !s.Replaced && hasTranscript(s))
                .OrderByDescending(s => s.IsLive).ThenByDescending(s => s.Joined).TryFirst() is not { HasValue: true } chosen
            ? new WakePlan.Nothing("No session of this task can be resumed, so the comment was not sent to Claude.")
            : listed.TryFirst(agent => agent.SessionId == chosen.Value.Id) is not { HasValue: true } running
                ? new WakePlan.Resume(chosen.Value)
                : !running.Value.HasProcess
                    ? running.Value is { Background: true, ShortId: { HasValue: true } noProcessId }
                        ? new WakePlan.StopThenResume(chosen.Value, noProcessId.Value)
                        : new WakePlan.Resume(chosen.Value)
                // Never a busy one (final review, I3): stopping it would kill the turn it is in; the waiter its turn's Stop starts
                // finds the inbox at once.
                : waiterLapsed(chosen.Value.Id) && running.Value is { Background: true, Busy: false, ShortId: { HasValue: true } shortId }
                    ? new WakePlan.StopThenResume(chosen.Value, shortId.Value)
                    : new WakePlan.Inbox(chosen.Value.Id);
}
