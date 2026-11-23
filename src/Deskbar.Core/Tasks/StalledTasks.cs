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
// 15 seconds: every row, VS Code tabs included, says busy or idle. The caller passes the ids of the sessions that are
// WORKING (busy, and listed long enough for busy to mean it: see BusyGrace). A session missing from the list is simply
// not working, so a task whose session has gone counts as quiet too.
//
// THE RULE, per open task that is In progress and has sessions (Petre's own notes are never touched):
//   * working (any of its sessions is in the set): LastBusy becomes now.
//   * quiet since = the later of LastBusy and the task's UpdatedAt, so a status line or a comment counts as progress.
//     A task with no LastBusy (the first poll after Deskbar started) is measured from UpdatedAt alone, so a restart does
//     not hold back a task that has been quiet for an hour. A task seen busy is measured from then, so a session that
//     worked for an hour without touching its task is not handed back the moment it goes idle.
//   * Claude's, not working, quiet for After or longer: handed back (TaskBoard.HandBack).
//   * handed back, still Petre's, and working again: taken back (TaskBoard.TakeBack).
// Everything else is left alone, and a step that changes nothing returns the SAME board, so the caller saves nothing.
//
// THIS IS A PURE FUNCTION: the clock (LastBusy) comes in and goes out, "now" comes in. TaskService keeps the clock in
// memory between polls; nothing about it is written to disk, because UpdatedAt already stands in for it after a restart.
public static class StalledTasks
{
    /// <summary>How long a Claude task may sit with no session working on it before it is handed back to Petre.</summary>
    public static readonly TimeSpan After = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long a session must have been listed before its busy counts: a session just resumed reports busy for up to
    /// 30 seconds while idle (measured, ClaudeAgents), and that must not take a task back.
    /// </summary>
    public static readonly TimeSpan BusyGrace = TimeSpan.FromSeconds(30);

    public static StalledStep Step(TaskBoard board, IReadOnlySet<string> working,
        ImmutableDictionary<Guid, DateTimeOffset> lastBusy, DateTimeOffset now)
    {
        var watched = board.Tasks.Where(t => t is { IsOpen: true, Status: TaskStatus.InProgress, IsClaudes: true }).ToArray();
        var busy = watched.Where(t => t.Sessions.Any(s => working.Contains(s.Id))).Select(t => t.Id).ToHashSet();
        // The clock after this poll: now for a task being worked on, what it had for the rest, and nothing for a task no
        // longer watched (closed, done, waiting on Petre or gone).
        var clock = watched
            .Select(t => busy.Contains(t.Id) ? KeyValuePair.Create(t.Id, now)
                : lastBusy.TryGetValue(t.Id, out var at) ? KeyValuePair.Create(t.Id, at)
                : (KeyValuePair<Guid, DateTimeOffset>?)null)
            .OfType<KeyValuePair<Guid, DateTimeOffset>>()
            .ToImmutableDictionary();

        DateTimeOffset QuietSince(TaskItem t) =>
            clock.TryGetValue(t.Id, out var at) && at > t.UpdatedAt ? at : t.UpdatedAt;

        var handBack = watched
            .Where(t => t.Assignee.IsClaude && !busy.Contains(t.Id) && now - QuietSince(t) >= After)
            .Select(t => t.Id).ToArray();
        var takeBack = watched
            .Where(t => t.HandedBack.HasValue && t.Assignee.IsMe && busy.Contains(t.Id))
            .Select(t => t.Id).ToArray();

        var next = takeBack.Aggregate(handBack.Aggregate(board, (b, id) => b.HandBack(id, now)), (b, id) => b.TakeBack(id, now));
        return new StalledStep(next, clock, handBack, takeBack);
    }
}

/// <summary>
/// What one poll did: the board (the same reference when nothing changed), the clock to pass to the next poll, and the
/// tasks handed back to Petre and taken back by Claude, for the trace.
/// </summary>
public sealed record StalledStep(TaskBoard Board, ImmutableDictionary<Guid, DateTimeOffset> LastBusy,
    IReadOnlyList<Guid> HandedBack, IReadOnlyList<Guid> TakenBack);
