using System.Collections.Immutable;

namespace Deskbar.Core.Tasks;

// WHICH CLAUDE SESSIONS ARE WORKING RIGHT NOW, AS THE TASKS PANEL SHOWS IT (task 10b49554, 2026-11-26, Petre: "separate
// claude sessions that are actively working from the ones that are idle in the tasks"; option B of the mockup).
//
// The panel's "Claude working" block used to hold every task with a LIVE session, so a session that ended its turn twenty
// minutes ago sat beside one writing code right now, told apart only by the halo on its session square. The block now
// splits: the tasks whose session is WORKING first, then the IDLE ones under a divider, and the halo follows the same
// rule. This record decides, from each `claude agents --json` list, which sessions count as working.
//
// THE RULE, per listed session:
//   * RAW WORKING: listed busy, its turn not over (ClaudeAgent.TurnOver: a background session that ended its turn and
//     only waits on its own job, a PR watch or a sleep, is listed busy with state "done" or "blocked", and nothing is
//     happening), and listed for at least Grace. The grace is StalledTasks.BusyGrace: a session just resumed reports busy
//     for up to 30 seconds while idle (measured, ClaudeAgents). The sessions in the FIRST list after Deskbar starts are
//     trusted at once (FirstSeen is MinValue for them): a fresh Deskbar would otherwise show every session idle for half
//     a minute, and a session resumed within 30 seconds of a Deskbar start is rare enough to accept. A PROMPT waives the
//     grace too: a session whose UserPromptSubmit hook fired since shortly before it was first listed (TaskService.
//     TurnsStarted, every prompt, Deskbar's own included) has a turn running, so a session Petre just started with
//     "Start Claude on this", or one he typed into, shows working at once instead of idle for 30 to 45 seconds.
//   * HELD: a session shown working stays working until HoldPolls lists IN A ROW find it not raw-working, so a line does
//     not jump between the panel's two parts on one quiet poll (a list is read every 15 seconds, ClaudeSessions.Poll, so
//     the hold is about 30 seconds).
//   * A session missing from the list is not working, at once: it has ended.
//   * A GAP in the lists (none for StaleAfter: the feature was off, or `claude` could not be read) starts the record over,
//     as at a Deskbar start: what it held before says nothing about the sessions now.
//
// PURE: the list and "now" come in, the next record goes out. ClaudeTaskActions keeps the latest one.
public sealed record ClaudeActivity(
    ImmutableDictionary<string, DateTimeOffset> FirstSeen,
    ImmutableDictionary<string, int> QuietLists,
    ImmutableHashSet<string> Working,
    bool Started)
{
    /// <summary>Before the first list: nothing is working.</summary>
    public static readonly ClaudeActivity None = new(
        ImmutableDictionary<string, DateTimeOffset>.Empty, ImmutableDictionary<string, int>.Empty, ImmutableHashSet<string>.Empty, false);

    /// <summary>When the last list was read; MinValue before the first.</summary>
    public DateTimeOffset LastList { get; private init; } = DateTimeOffset.MinValue;

    /// <summary>A gap in the lists this long starts the record over (see the header): four polls missed.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(1);

    /// <summary>How long a session must have been listed before its busy counts (see the header).</summary>
    public static readonly TimeSpan Grace = StalledTasks.BusyGrace;

    /// <summary>How many lists in a row must find a working session quiet before it shows idle (see the header).</summary>
    public const int HoldPolls = 2;

    /// <summary>The record after <paramref name="list"/>, read at <paramref name="now"/>.</summary>
    /// <param name="turnsStarted">When each session last had a prompt (TaskService.TurnsStarted); none, no waiver.</param>
    public ClaudeActivity Next(IReadOnlyCollection<ClaudeAgent> list, DateTimeOffset now,
        IReadOnlyDictionary<string, DateTimeOffset>? turnsStarted = null)
    {
        // After a gap the record starts over, as at a Deskbar start (see the header).
        if (Started && now - LastList >= StaleAfter) return None.Next(list, now, turnsStarted);
        // One row per session id: a duplicate row (two processes on one id) counts as working if either is.
        var rows = list.GroupBy(agent => agent.SessionId).ToArray();
        // Each listed session keeps the moment it was first listed; the first list's are trusted at once (see the header).
        var firstSeen = rows.ToImmutableDictionary(g => g.Key,
            g => FirstSeen.TryGetValue(g.Key, out var seen) ? seen : Started ? now : DateTimeOffset.MinValue);
        // Past the grace, or prompted since shortly before it was first listed (the hook can fire before the list shows it).
        bool Trusted(string id) =>
            now - firstSeen[id] >= Grace
            || (turnsStarted is not null && turnsStarted.TryGetValue(id, out var prompt) && prompt >= firstSeen[id] - Grace);
        bool RawWorking(IGrouping<string, ClaudeAgent> g) =>
            g.Any(agent => agent.Busy && !agent.TurnOver) && Trusted(g.Key);
        // Quiet lists in a row for every listed session that was showing working and is not raw-working now.
        var states = rows.Select(g => (Id: g.Key, Raw: RawWorking(g),
                Quiet: Working.Contains(g.Key) ? QuietLists.GetValueOrDefault(g.Key) + 1 : HoldPolls))
            .Select(s => (s.Id, Working: s.Raw || s.Quiet < HoldPolls, Quiet: s.Raw ? 0 : s.Quiet))
            .ToArray();
        return new ClaudeActivity(
            firstSeen,
            states.Where(s => s.Working && s.Quiet > 0).ToImmutableDictionary(s => s.Id, s => s.Quiet),
            states.Where(s => s.Working).Select(s => s.Id).ToImmutableHashSet(),
            true) { LastList = now };
    }

    /// <summary>How many of the task's LIVE sessions are working (the tasks panel's halos and its working part).</summary>
    // Live by the hooks' word AND working by the list, as TaskViews.WorkingCount has it: a session the hooks have ended can
    // linger in the list for a poll, and a halo on an ended session's square would contradict the square's dim colour.
    public int WorkingCount(TaskItem task) => task.Sessions.Count(s => s.IsLive && Working.Contains(s.Id));
}
