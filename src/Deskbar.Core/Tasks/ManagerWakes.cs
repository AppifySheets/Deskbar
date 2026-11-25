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
//   * TRIGGERS, which wake it: a Claude task handed back to the user since the last wake (StalledTasks: its session went
//     quiet for 15 minutes without `task ask` or `task done`), and a question that crossed QuestionAge unanswered since
//     the last wake. Both need a judgement call: nudge the session, or put the question in front of the user again.
//   * RIDERS, which never wake it alone: a task that started waiting on the user since the last wake. The tasks panel
//     already shows those; the manager only lists them with the rest when something else woke it.
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

    /// <summary>How long a question waits on the user before it wakes the manager.</summary>
    public static readonly TimeSpan QuestionAge = TimeSpan.FromHours(2);

    public static ManagerWake Plan(ManagerRecord record, TaskBoard board, IReadOnlyCollection<ClaudeAgent> listed,
        DateOnly today, DateTimeOffset now)
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
        var waiting = open.Where(t => t.Status == TaskStatus.WaitingOnYou && t.Question.HasValue).ToList();
        var aged = waiting
            .Where(t => AskedAt(t) + QuestionAge > since && AskedAt(t) + QuestionAge <= now)
            .ToList();
        var fresh = waiting.Where(t => AskedAt(t) > since && !aged.Contains(t)).ToList();

        if (handedBack.Count == 0 && aged.Count == 0) return new ManagerWake.Nothing(record);

        var before = record.HandedBack ?? [];
        return new ManagerWake.Wake(
            Message(handedBack, aged, fresh, before),
            record with
            {
                Since = now,
                Unread = true,
                HandedBack = before.Concat(handedBack.Select(t => t.Id)).Distinct().ToList(),
            });
    }

    // When a waiting task's question was asked; a task loaded from before WaitingSince existed falls back to Raised.
    static DateTimeOffset AskedAt(TaskItem task) => task.WaitingSince.GetValueOrDefault(task.Raised);

    // The wake's text: what happened, one line per task, then one line pointing at the briefing's wake rules
    // (ManagerSession.Briefing), which say what to do with it. Kept short: the manager pays for every character.
    static string Message(IReadOnlyList<TaskItem> handedBack, IReadOnlyList<TaskItem> aged, IReadOnlyList<TaskItem> fresh,
        IReadOnlyList<Guid> nudgedBefore) =>
        string.Join("\n", new[]
            {
                new[] { "Deskbar wake. Since your last look:" },
                Section("Handed back to the user (the session stopped without task ask or task done):",
                    handedBack.Select(t => nudgedBefore.Contains(t.Id)
                        ? $"{Name(t)}: handed back AGAIN today. Do not nudge; list it for the user."
                        : $"{Name(t)}: status line \"{Cut(t.StatusLine, 100)}\"")),
                Section($"Unanswered for {QuestionAge.TotalHours:0} hours or more:",
                    aged.Select(t => $"{Name(t)}: asked \"{Cut(t.Question.GetValueOrDefault(""), 140)}\"")),
                Section("New questions for the user:",
                    fresh.Select(t => $"{Name(t)}: asked \"{Cut(t.Question.GetValueOrDefault(""), 140)}\"")),
                new[] { "Handle it as your briefing says for a Deskbar wake, then stop." },
            }.SelectMany(lines => lines));

    static IEnumerable<string> Section(string heading, IEnumerable<string> lines) =>
        lines.ToList() is { Count: > 0 } list ? [heading, .. list.Select(line => "- " + line)] : [];

    static string Name(TaskItem task) => $"{task.Id.ToString()[..8]} \"{Cut(task.Title, 60)}\"";

    // One line, at most `limit` characters, so a long question or a pasted paragraph cannot blow up the wake.
    static string Cut(string text, int limit) =>
        text.ReplaceLineEndings(" ").Trim() is var line && line.Length > limit ? line[..(limit - 1)].TrimEnd() + "…" : line;
}
