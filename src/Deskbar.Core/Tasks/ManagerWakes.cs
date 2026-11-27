using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

/// <summary>What ManagerWakes.Plan decided for one tick of the sessions poll.</summary>
public abstract record ManagerWake
{
    /// <summary>No wake. <paramref name="Record"/> is the record to keep: the same one, or one with Since filled in.</summary>
    public sealed record Nothing(ManagerRecord Record) : ManagerWake;

    /// <summary>Wake the manager with <paramref name="Message"/>, then store <paramref name="Record"/>.</summary>
    public sealed record Wake(string Message, ManagerRecord Record) : ManagerWake;
}

// DESKBAR WAKES THE MANAGER ON EVENTS (step 2 of the manager session). Design:
// docs/superpowers/specs/2026-11-25-manager-event-wakes-design.md; plan docs/superpowers/plans/2026-11-25-manager-event-wakes.md.
//
// Step 1's manager slept until the user wrote to it. Step 2 wakes it, batched and filtered, because every wake is a Fable
// turn and the user asked for the manager to be light (24 tasks went to "waiting on you" in half a day; one wake each
// would be about 25 turns a day). So:
//
//   * THE ONE TRIGGER: a Claude task handed back to the user since the last wake (StalledTasks: its session went quiet
//     for 15 minutes without `task ask` or `task done`). The manager nudges that session, which no task can do for itself.
//   * THE SECOND TRIGGER, a red run (Deskbar task c8e6967d, spec docs/superpowers/specs/2026-11-27-repo-watcher-design.md):
//     Deskbar's repository watch follows each merge commit's workflow runs and, when one FAILS, nudges the task's session and
//     records a RedRun. Petre: the watcher should "report to the manager". A red run since the last wake wakes the manager
//     (same MinGap, same busy rule) with a "Failed runs on merge commits" section that carries its own instruction: the
//     briefing is at its line cap, so the wake says what to do. A cancelled run is never red (RepoWatch), so a burst of
//     merges cancelling older release builds wakes nobody.
//   * QUESTIONS ARE NOT CARRIED (task 6a87df67). Step 2 also woke on a question two hours old and listed new questions
//     alongside; the manager then repeated them to the user, who said "manager doesn't have to tell me each merge and
//     needs you now" and "tasks do it themselves already on the bar". Every waiting task shows its question on the bar
//     and in the tasks panel already, so the wake no longer mentions them.
//   * THE DOT (ManagerRecord.Unread) lights only when a nudge already failed: a task handed back AGAIN today, which the
//     manager names to the user. A first hand-back is the manager's own work (one comment), with nothing for the user.
//   * At most one wake per MinGap, counted from ManagerRecord.Since (the manager's start, then each wake). Since is
//     stored in state.json, so a Deskbar restart reports nothing twice.
//   * Only today's manager (the user opened it today: that is the opt-in), and never while it is listed busy: the
//     batch waits, Since does not move, and the next idle tick takes everything that piled up.
//   * A task reported as handed back once today is marked "do not nudge again" the second time, so a session that
//     stops again after a nudge goes to the user instead of round another loop (ManagerRecord.HandedBack).
//
// A PURE FUNCTION: the record, the board, the listed sessions, the day and the time come in; nothing is read or written.
// ManagerWaker (in the app) runs it on every sessions poll and delivers the message.
public static class ManagerWakes
{
    /// <summary>The least time between two wakes, and between the manager's start and its first wake.</summary>
    public static readonly TimeSpan MinGap = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How every wake's text starts. The briefing names it, and ManagerQuestion.AfterPrompt reads it to tell a wake from
    /// the user's reply (both arrive at the manager as a prompt).
    /// </summary>
    public const string Prefix = "Deskbar wake.";

    public static ManagerWake Plan(ManagerRecord record, TaskBoard board, IReadOnlyCollection<ClaudeAgent> listed,
        DateOnly today, DateTimeOffset now, IReadOnlyList<RedRun>? red = null)
    {
        // Yesterday's manager is not woken: a new day's manager starts only when the user opens it.
        if (record.Day != today) return new ManagerWake.Nothing(record);
        // A record saved by step 1 has no Since: events are counted from the first tick that sees it, never from the past.
        if (record.Since is not { } since) return new ManagerWake.Nothing(record with { Since = now });
        if (now - since < MinGap) return new ManagerWake.Nothing(record);
        if (listed.Any(agent => agent.SessionId == record.SessionId && agent.Busy)) return new ManagerWake.Nothing(record);

        // Snoozed tasks are ones the user put aside on purpose: they wake nothing and are not listed.
        var open = board.Tasks.Where(t => t.IsOpen && !t.IsSnoozed).ToList();
        var handedBack = open
            .Where(t => t.HandedBack.HasValue && t.HandedBack.Value > since && t.Assignee.IsMe)
            .ToList();

        // Red runs newer than the last wake, from TaskService.RedRuns (a day's worth, in memory).
        var failed = (red ?? []).Where(r => r.At > since).ToList();

        if (handedBack.Count == 0 && failed.Count == 0) return new ManagerWake.Nothing(record);

        var before = record.HandedBack ?? [];
        return new ManagerWake.Wake(
            Message(handedBack, before, failed, board),
            record with
            {
                Since = now,
                // Only a repeat hand-back is news for the user (THE DOT above). A dot already lit stays lit.
                Unread = record.Unread || handedBack.Any(t => before.Contains(t.Id)),
                HandedBack = before.Concat(handedBack.Select(t => t.Id)).Distinct().ToList(),
            });
    }

    // The wake's text: what happened, one line per task, then one line pointing at the briefing's wake rules
    // (ManagerSession.Briefing), which say what to do with it. Kept short: the manager pays for every character.
    static string Message(IReadOnlyList<TaskItem> handedBack, IReadOnlyList<Guid> nudgedBefore, IReadOnlyList<RedRun> failed, TaskBoard board) =>
        string.Join("\n", new[]
            {
                new[] { $"{Prefix} Since your last look:" },
                Section("Handed back to the user (the session stopped without task ask or task done):",
                    handedBack.Select(t => nudgedBefore.Contains(t.Id)
                        ? $"{Name(t)}: handed back AGAIN today. Do not nudge; list it for the user."
                        : $"{Name(t)}: status line \"{Cut(t.StatusLine, 100)}\"")),
                Section("Failed runs on merge commits:",
                    failed.Select(f => $"{f.Repository}: {f.Name} {f.Url}, task {TaskName(board, f.TaskId)}")),
                failed.Count > 0
                    ? new[] { "Tell the user in one line only if several tasks are blocked by it; the task's session was nudged." }
                    : [],
                new[] { "Handle it as your briefing says for a Deskbar wake, then stop." },
            }.SelectMany(lines => lines));

    static IEnumerable<string> Section(string heading, IEnumerable<string> lines) =>
        lines.ToList() is { Count: > 0 } list ? [heading, .. list.Select(line => "- " + line)] : [];

    // A red run's task by name; the id alone when the task is gone from the board.
    static string TaskName(TaskBoard board, Guid id) =>
        board.Tasks.TryFirst(t => t.Id == id).Match(Name, () => id.ToString()[..8]);

    static string Name(TaskItem task) => $"{task.Id.ToString()[..8]} \"{Cut(task.Title, 60)}\"";

    // One line, at most `limit` characters, so a long question or a pasted paragraph cannot blow up the wake.
    static string Cut(string text, int limit) =>
        text.ReplaceLineEndings(" ").Trim() is var line && line.Length > limit ? line[..(limit - 1)].TrimEnd() + "…" : line;
}
