using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// DECIDING WHICH CLAUDE SESSIONS TO WAKE. Design: docs/superpowers/specs/2026-11-23-tasks-open-claude-sessions-design.md,
// "Keeping open tasks running"; measurements: docs/superpowers/notes/2026-11-23-claude-background-probes.md.
//
// THE RULE. An open task (Backlog, In progress or Waiting on you) whose latest session `claude agents --json` does not
// list is resumed. Done and Closed tasks never are. Petre's words: "if a session has ended and done its work, i will
// mark the task as done". So a finished session on a task that is still open is not a finished task: until he says
// Done, the task is meant to be running, and a session that has gone missing is woken.
//
// WHY ONLY SESSIONS THE LIST DOES NOT SHOW. Claude Code's own daemon restarts a session that crashed mid-turn by itself
// and sends it a continue (Task 1 note), so a session that is listed is either working, or idle and attachable, and
// either way not ours to touch. Resuming a listed session would start a COPY with a new id (measured). What the daemon
// cannot cover is the daemon being gone: a reboot, a sign-out, a killed daemon or a closed VS Code session. Those leave
// sessions missing from the list, and after the grace below that is the only case left for the keeper.
//
// THIS IS A PURE FUNCTION. It takes the board, the list, a ledger of resumes already made, and two questions it cannot
// answer without touching files (does the transcript still exist, was the last turn cut off), and returns steps.
// Nothing here starts a process or reads a clock ("now" comes in); Task 8 executes the steps, one at a time,
// <see cref="Spacing"/> apart, and records each resume in the ledger.
//
// THE STEPS, per task, for its latest session (the one with the greatest Joined):
//   * listed: nothing, ever.
//   * resumed within <see cref="Grace"/>: nothing. The list may not show the woken session yet, or shows it busy while
//     it is idle (measured, up to 30 s), and a second resume would start a copy.
//   * no folder: a give-up, since `claude --bg --resume` runs in a folder and there is none to run it in.
//   * transcript gone: a give-up; Claude Code keeps sessions 30 days.
//   * already resumed <see cref="MaxResumesInWindow"/> times within <see cref="LoopWindow"/>: a give-up. A session that
//     ends again straight after waking is crashing, and waking it forever helps nobody.
//   * otherwise a resume, with a continue message when the last turn was cut off.
// A give-up is said ONCE: the board shows it as the task waiting on Petre with the reason as its question, and a task
// already in that state with that exact question gets no further step, so the next poll does not repeat it.
//
// A session id on two tasks is resumed once, for the task that comes first in the ordering. Listed sessions that no
// task knows (open or closed, any entry) are adopted: one step each.
//
// THE ORDER. Resume and give-up steps first, Waiting on you tasks before In progress before Backlog, the most recently
// updated first within a status, so the session a person is waiting on to answer comes back first. Adopts follow, in
// the list's order.
public static class SessionKeeper
{
    /// <summary>Within this long of a resume the session is left alone, whatever the list says.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

    /// <summary>The window in which resumes are counted for the loop guard.</summary>
    public static readonly TimeSpan LoopWindow = TimeSpan.FromMinutes(10);

    /// <summary>Resumes within <see cref="LoopWindow"/> after which the next is a give-up.</summary>
    public const int MaxResumesInWindow = 3;

    /// <summary>The pause Task 8 leaves between two resumes, so a first poll after a reboot does not wake all at once.</summary>
    public static readonly TimeSpan Spacing = TimeSpan.FromSeconds(5);

    public const string LoopGaveUp =
        "Deskbar stopped resuming this session: it ended again straight after 3 resumes in 10 minutes.";

    public const string TranscriptGone =
        "Claude Code no longer has this session's transcript (it keeps sessions for 30 days), so it cannot be resumed.";

    public const string NoFolder =
        "Deskbar cannot resume this session: Claude Code never said which folder it ran in.";

    public static IReadOnlyList<KeeperStep> Plan(
        TaskBoard board,
        IReadOnlyCollection<ClaudeAgent> live,
        ResumeLedger ledger,
        Func<TaskSession, bool> transcriptExists,
        Func<TaskSession, bool> cutOff,
        DateTimeOffset now)
    {
        var listed = live.Select(agent => agent.SessionId).ToHashSet();

        var resumes = board.Tasks
            .Where(task => task.IsClaudes && task.Status is TaskStatus.Backlog or TaskStatus.InProgress or TaskStatus.WaitingOnYou)
            .OrderBy(task => StatusRank(task.Status))
            .ThenByDescending(task => task.UpdatedAt)
            .Select(task => (Task: task, Session: task.Sessions.MaxBy(session => session.Joined)!))
            // A session on two tasks belongs to the first in the ordering; the later task is not looked at.
            .DistinctBy(pair => pair.Session.Id)
            .Where(pair => !listed.Contains(pair.Session.Id))
            .Select(pair => StepFor(pair.Task, pair.Session, ledger, transcriptExists, cutOff, now))
            .Where(step => step.HasValue)
            .Select(step => step.Value);

        // Every session id any task holds, closed tasks included: a closed task's session is not a stranger.
        var known = board.Tasks.SelectMany(task => task.Sessions).Select(session => session.Id).ToHashSet();

        var adopts = live
            .DistinctBy(agent => agent.SessionId)
            .Where(agent => !known.Contains(agent.SessionId))
            .Select(agent => (KeeperStep)new KeeperStep.Adopt(agent));

        return resumes.Concat(adopts).ToList();
    }

    static int StatusRank(TaskStatus status) => status switch
    {
        TaskStatus.WaitingOnYou => 0,
        TaskStatus.InProgress => 1,
        _ => 2
    };

    static Maybe<KeeperStep> StepFor(
        TaskItem task, TaskSession session, ResumeLedger ledger,
        Func<TaskSession, bool> transcriptExists, Func<TaskSession, bool> cutOff, DateTimeOffset now) =>
        ledger.Within(session.Id, now, Grace) > 0 ? Maybe<KeeperStep>.None
        : string.IsNullOrEmpty(session.Folder) ? GiveUp(task, NoFolder)
        : !transcriptExists(session) ? GiveUp(task, TranscriptGone)
        : ledger.Within(session.Id, now, LoopWindow) >= MaxResumesInWindow ? GiveUp(task, LoopGaveUp)
        : new KeeperStep.Resume(task.Id, session, cutOff(session));

    // Said once: a task already waiting on Petre with this very question has had it.
    static Maybe<KeeperStep> GiveUp(TaskItem task, string reason) =>
        task.Status == TaskStatus.WaitingOnYou && task.Question.Equals(Maybe.From(reason))
            ? Maybe<KeeperStep>.None
            : new KeeperStep.GiveUp(task.Id, reason);
}

/// <summary>What the keeper wants done. Task 8 executes these; the keeper itself does nothing.</summary>
public abstract record KeeperStep
{
    /// <summary>Wake the task's session; <paramref name="SendContinue"/> when its last turn was cut off.</summary>
    public sealed record Resume(Guid TaskId, TaskSession Session, bool SendContinue) : KeeperStep;

    /// <summary>Stop trying for this task and tell Petre why, as the task's question.</summary>
    public sealed record GiveUp(Guid TaskId, string Reason) : KeeperStep;

    /// <summary>A live session no task knows: make it a task.</summary>
    public sealed record Adopt(ClaudeAgent Agent) : KeeperStep;
}

/// <summary>
/// When each session was last resumed, immutable. Only what the keeper's rules need is kept: nothing older than
/// <see cref="SessionKeeper.LoopWindow"/> before the newest record can change a decision, so <see cref="Record"/>
/// prunes it.
/// </summary>
public sealed record ResumeLedger(IReadOnlyDictionary<string, IReadOnlyList<DateTimeOffset>> Resumes)
{
    public static readonly ResumeLedger Empty = new(new Dictionary<string, IReadOnlyList<DateTimeOffset>>());

    public ResumeLedger Record(string sessionId, DateTimeOffset at)
    {
        var kept = Resumes.GetValueOrDefault(sessionId, Array.Empty<DateTimeOffset>())
            .Where(past => past > at - SessionKeeper.LoopWindow)
            .Append(at)
            .ToList();
        var next = Resumes.ToDictionary(pair => pair.Key, pair => pair.Value);
        next[sessionId] = kept;
        return new ResumeLedger(next);
    }

    /// <summary>How many resumes of the session happened in the span ending at <paramref name="now"/>.</summary>
    public int Within(string sessionId, DateTimeOffset now, TimeSpan span) =>
        Resumes.GetValueOrDefault(sessionId, Array.Empty<DateTimeOffset>())
            .Count(at => at > now - span && at <= now);
}
