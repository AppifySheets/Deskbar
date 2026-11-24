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
//   * no session with a transcript left: nothing to wake, said in the thread.
//
// ONE comment goes to ONE session: the one a double-click goes to (TaskViews.SessionToGoTo: live first, then the newest
// joined), among those whose transcript still exists (an ended session without one cannot be resumed) and that /clear did
// not replace (its conversation is over; the session id lives on in the new one).
public static class CommentWake
{
    public static WakePlan Plan(TaskItem task, IReadOnlyList<ClaudeAgent> listed, Func<TaskSession, bool> hasTranscript, Func<string, bool> waiterLapsed) =>
        task.Sessions.Where(s => !s.Replaced && hasTranscript(s))
                .OrderByDescending(s => s.IsLive).ThenByDescending(s => s.Joined).TryFirst() is not { HasValue: true } chosen
            ? new WakePlan.Nothing("No session of this task can be resumed, so the comment was not sent to Claude.")
            : listed.TryFirst(agent => agent.SessionId == chosen.Value.Id) is not { HasValue: true } running
                ? new WakePlan.Resume(chosen.Value)
                : waiterLapsed(chosen.Value.Id) && running.Value is { Background: true, ShortId: { HasValue: true } shortId }
                    ? new WakePlan.StopThenResume(chosen.Value, shortId.Value)
                    : new WakePlan.Inbox(chosen.Value.Id);
}
