using System.Collections.Immutable;

namespace Deskbar.Core.Tasks;

// STALLED CLAUDE TASKS COME BACK TO PETRE. Spec: docs/superpowers/specs/2026-11-23-stalled-claude-tasks-design.md.
//
// WHY. Ruling 57 took Claude's tasks off the bar: the bar lists only tasks assigned to Petre, and a session's task is
// Claude's while it works. It came back only when the session ran `task ask` or `task done`, and sessions often stop
// without either. On 2026-11-23, 13 of the 14 open Claude tasks had a session sitting idle at the prompt, most with a
// status line that already said the next move was his ("PR #442 green, ready to merge"), and none was on the bar.
// Petre: "i want the tasks that haven't had any progress from claude and are still open to reappear for me to act upon".
//
// THE SIGNAL is Claude Code's own list of sessions (`claude agents --json`), which ClaudeSessions already reads every
// 15 seconds: every row, VS Code tabs included, says busy or idle. The caller passes two sets: every session listed
// BUSY, which keeps a task with Claude and restarts its clock, and the busy sessions listed long enough for busy to mean
// it (WORKING, see BusyGrace), which alone take a task back. Two sets because the grace guards only the take-back: right
// after a Deskbar restart every session is inside it, and a busy one must still not be handed back (final review,
// Important 1). A session missing from the list is simply not busy, so a task whose session has gone counts as quiet.
//
// THE RULE, per open task that is In progress and has sessions (Petre's own notes are never touched):
//   * working (any of its sessions is in the set): LastBusy becomes now.
//   * quiet since = the later of LastBusy and the task's UpdatedAt, so a status line or a comment counts as progress.
//     A task with no LastBusy (the first poll after Deskbar started) is measured from UpdatedAt alone, so a restart does
//     not hold back a task that has been quiet for an hour. A task seen busy is measured from then, so a session that
//     worked for an hour without touching its task is not handed back the moment it goes idle.
//   * Claude's, not working, quiet for After or longer, and not asked yet: its session is ASKED first (Nudge, below).
//   * asked, and nothing on the task has changed since (no status line, comment, ask or done; LastWord), no session
//     working on it, and Answer has passed since the ask: handed back (TaskBoard.HandBack).
//   * handed back, still Petre's, and working again: taken back (TaskBoard.TakeBack).
// Everything else is left alone, and a step that changes nothing returns the SAME board, so the caller saves nothing.
//
// ASK THE SESSION BEFORE HANDING BACK (task 8710ac09). Petre, 2026-11-26: "i think what it should do is notify the session
// and if the session is still not responding, then ask the task to wait on me ... otherwise, it's just a session that's
// waiting for something else, not me necessarily". A quiet task used to come to him after 15 minutes on the assumption
// that its session was waiting on HIM, when it was as often waiting on CI, a background job or another session. So a
// quiet task's session is asked first (TaskService.StalledNudges, delivered on a comment's roads by the app's
// CommentWaker, SessionBriefing.StalledNudge): ask the user if the next move is theirs, say what it waits on in the
// status line if it is something else, `task done` once its merged change is deployed (SessionBriefing's order). Any change to the task after the ask is
// the answer, and the 15-minute clock starts again from it. Only a session that says nothing within Answer is taken to be
// waiting on Petre. Waking up and stopping without a word is not an answer: busy after the ask holds the hand-back off
// while it lasts, and does not cancel it. A session that cannot be reached at all is handed back at once
// (TaskService.NudgeUndelivered).
//
// THIS IS A PURE FUNCTION: the clocks (LastBusy, Nudged, Heard) come in, "now" comes in, the next clocks go out.
// TaskService keeps them in memory between polls; nothing about them is written to disk, because UpdatedAt already stands
// in for the quiet clock after a restart, and a restart that forgets an ask only means the session is asked again.
public static class StalledTasks
{
    /// <summary>How long a Claude task may sit with no session working on it before it is handed back to Petre.</summary>
    public static readonly TimeSpan After = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long a session must have been listed before its busy counts: a session just resumed reports busy for up to
    /// 30 seconds while idle (measured, ClaudeAgents), and that must not take a task back.
    /// </summary>
    public static readonly TimeSpan BusyGrace = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a session has to answer Deskbar's ask (see the header) before its quiet task is handed back to Petre. A
    /// woken session is busy within seconds and answers in one short turn; five minutes leaves room for a resume.
    /// </summary>
    public static readonly TimeSpan Answer = TimeSpan.FromMinutes(5);

    /// <param name="busy">Every session the list shows busy now. Keeps a task with Claude and restarts its clock.</param>
    /// <param name="working">The busy sessions listed for at least <see cref="BusyGrace"/>. Only these take a task back.</param>
    /// <param name="nudged">When each quiet task's session was asked (the previous step's <see cref="StalledStep.Nudged"/>).</param>
    /// <param name="heard">
    /// When a session last set each task's status line, even to the line it already had (TaskService.SetStatusLine): the
    /// same line again changes nothing on the board, yet it is a session answering the ask with "still waiting on CI".
    /// </param>
    public static StalledStep Step(TaskBoard board, IReadOnlySet<string> busy, IReadOnlySet<string> working,
        ImmutableDictionary<Guid, DateTimeOffset> lastBusy, DateTimeOffset now,
        ImmutableDictionary<Guid, DateTimeOffset>? nudged = null, IReadOnlyDictionary<Guid, DateTimeOffset>? heard = null)
    {
        var asked = nudged ?? ImmutableDictionary<Guid, DateTimeOffset>.Empty;
        var said = heard ?? ImmutableDictionary<Guid, DateTimeOffset>.Empty;
        var watched = board.Tasks.Where(t => t is { IsOpen: true, Status: TaskStatus.InProgress, IsClaudes: true }).ToArray();
        // Only LIVE entries count: a session that moved to another task (Join, Create) leaves an ended entry with the same
        // id on the old one, and its work there is not work on the old task (final review, Important 2).
        var worked = watched.Where(t => t.Sessions.Any(s => s.IsLive && busy.Contains(s.Id))).Select(t => t.Id).ToHashSet();
        var settled = watched.Where(t => t.Sessions.Any(s => s.IsLive && working.Contains(s.Id))).Select(t => t.Id).ToHashSet();
        // The clock after this poll: now for a task being worked on, what it had for the rest, and nothing for a task no
        // longer watched (closed, done, waiting on Petre or gone).
        var clock = watched
            .Select(t => worked.Contains(t.Id) ? KeyValuePair.Create(t.Id, now)
                : lastBusy.TryGetValue(t.Id, out var at) ? KeyValuePair.Create(t.Id, at)
                : (KeyValuePair<Guid, DateTimeOffset>?)null)
            .OfType<KeyValuePair<Guid, DateTimeOffset>>()
            .ToImmutableDictionary();

        // The last time anything was heard about the task: a change on the board, or its status line set again unchanged.
        DateTimeOffset LastWord(TaskItem t) =>
            said.TryGetValue(t.Id, out var at) && at > t.UpdatedAt ? at : t.UpdatedAt;

        DateTimeOffset QuietSince(TaskItem t) =>
            clock.TryGetValue(t.Id, out var at) && at > LastWord(t) ? at : LastWord(t);

        var claudes = watched.Where(t => t.Assignee.IsClaude).ToArray();
        // An ask still waiting for its answer: the task is still Claude's, and nothing was heard about it since the ask.
        // Busy alone does not answer it (see the header), so the session's own turn after the ask keeps it standing.
        var standing = claudes
            .Where(t => asked.TryGetValue(t.Id, out var at) && LastWord(t) <= at)
            .ToImmutableDictionary(t => t.Id, t => asked[t.Id]);

        var handBack = claudes
            .Where(t => standing.TryGetValue(t.Id, out var at) && !worked.Contains(t.Id) && now - at >= Answer)
            .Select(t => t.Id).ToArray();
        var nudge = claudes
            .Where(t => !standing.ContainsKey(t.Id) && !worked.Contains(t.Id) && now - QuietSince(t) >= After)
            .Select(t => t.Id).ToArray();
        var takeBack = watched
            .Where(t => t.HandedBack.HasValue && t.Assignee.IsMe && settled.Contains(t.Id))
            .Select(t => t.Id).ToArray();
        // The asks after this poll: those still waiting, less the tasks handed back now, plus the asks made now.
        var nextNudged = standing.RemoveRange(handBack).SetItems(nudge.Select(id => KeyValuePair.Create(id, now)));

        var next = takeBack.Aggregate(handBack.Aggregate(board, (b, id) => b.HandBack(id, now)), (b, id) => b.TakeBack(id, now));
        return new StalledStep(next, clock, handBack, takeBack, nudge, nextNudged);
    }
}

/// <summary>
/// What one poll did: the board (the same reference when nothing changed), the clock to pass to the next poll, the
/// tasks handed back to Petre and taken back by Claude, for the trace, the tasks whose sessions are to be asked now, and
/// every ask still waiting for its answer, to pass to the next poll.
/// </summary>
public sealed record StalledStep(TaskBoard Board, ImmutableDictionary<Guid, DateTimeOffset> LastBusy,
    IReadOnlyList<Guid> HandedBack, IReadOnlyList<Guid> TakenBack, IReadOnlyList<Guid> Nudge,
    ImmutableDictionary<Guid, DateTimeOffset> Nudged);
