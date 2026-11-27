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
//   * THE FIRST TRIGGER: a Claude task handed back to the user since the last wake (StalledTasks: its session went quiet
//     for 15 minutes without `task ask` or `task done`). The manager nudges that session, which no task can do for itself.
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
//   * THE SECOND TRIGGER: A DEPLOY REPORTED (task 43765c61; the user: "when a session learns that its PR has been
//     deployed, it should notify the manager and the manager should notify all sessions waiting for the deployment").
//     The session's `task deployed` (or the user's "Mark deployed") already lands in TaskItem.Deployed with its time and
//     evidence, so that report IS the notice to the manager: the session runs nothing new. A deploy ships everything
//     merged before it, so other tasks whose pull request in the SAME repository is merged (GitHub's word, from the
//     running-change watch) but not reported deployed may be in it too. Those are the waiting sessions, and Deskbar
//     lists them in the wake rather than leave the manager to search the board (it pays for every line it reads). Only
//     tasks with a Claude session are listed, since the comment is what wakes the session, and a deploy with no such
//     task wakes nothing. The manager comments each one; the session checks its own app's evidence and reports
//     `task deployed` itself, because a deploy of one service does not prove another service of the repository was
//     redeployed. No dot: this is sessions' business, not the user's (task 6a87df67, "manager doesn't have to tell
//     me each merge"). It shares the gap and the busy rule with the hand-backs: a batch waits, nothing is lost.
//     Who waits and what counts as a deploy is narrowed in Deploys and Waits (no Done task, no "nothing to deploy").
//     Two gaps are accepted: a hand-back wake that fires while GitHub's answers are still arriving (just after a
//     Deskbar start) moves Since past a deploy whose waiters were not known yet, and a pull request merged after the
//     deploy can still be listed. Both cost at most a missed or a wasted nudge, and the session checks either way.
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

    /// <param name="pullRequests">
    /// What GitHub said about the recorded pull requests, keyed by PullRequestLink.Key (TaskService.PullRequestStates):
    /// a waiting task's pull request counts only once it is known to be merged.
    /// </param>
    public static ManagerWake Plan(ManagerRecord record, TaskBoard board, IReadOnlyCollection<ClaudeAgent> listed,
        DateOnly today, DateTimeOffset now, IReadOnlyDictionary<string, PullRequestArrival> pullRequests)
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

        var deploys = Deploys(board, open, since, pullRequests);

        if (handedBack.Count == 0 && deploys.Count == 0) return new ManagerWake.Nothing(record);

        var before = record.HandedBack ?? [];
        return new ManagerWake.Wake(
            Message(handedBack, before, deploys),
            record with
            {
                Since = now,
                // Only a repeat hand-back is news for the user (THE DOT above). A dot already lit stays lit.
                Unread = record.Unread || handedBack.Any(t => before.Contains(t.Id)),
                HandedBack = before.Concat(handedBack.Select(t => t.Id)).Distinct().ToList(),
            });
    }

    /// <summary>
    /// One repository with a deploy reported since the last wake (THE SECOND TRIGGER above): the latest report among them
    /// (the task that made it and its evidence), and the other open tasks with a session whose merged pull request in
    /// that repository has no deploy report yet.
    /// </summary>
    /// <remarks>
    /// <see cref="Covered"/> is every repository the same report stamped. TaskBoard.MarkDeployed stamps all of a task's
    /// pull requests with one time and one note, so a task with pull requests in two repositories (an app and its stack
    /// file, say) reports both at once, though the evidence may speak for one. The wake says so, and the waiting session
    /// checks its own app's evidence either way.
    /// </remarks>
    sealed record RepositoryDeploy(string Repository, TaskItem Reporter, string Evidence, IReadOnlyList<string> Covered,
        IReadOnlyList<TaskItem> Waiting);

    // The repositories with a deploy reported after `since` that some other task waits on. Every task on the board can
    // report (a session marks done right after its report, and the user may close it at once), but only open, unsnoozed
    // ones wait. Repositories are compared by PullRequestLink.Key's "owner/repo" part, already lower case.
    //
    // A "nothing to deploy" report is not a deploy: the session briefing (SessionBriefing.Commands) tells a session whose
    // change nothing runs (docs, a skill, a library) to report exactly that, and it must not send the repository's
    // waiting sessions off to look for one.
    static IReadOnlyList<RepositoryDeploy> Deploys(TaskBoard board, IReadOnlyList<TaskItem> open, DateTimeOffset since,
        IReadOnlyDictionary<string, PullRequestArrival> pullRequests) =>
        board.Tasks
            .SelectMany(task => task.Deployed
                .Where(d => d.At > since && !d.Note.TrimStart().StartsWith(NothingToDeploy, StringComparison.OrdinalIgnoreCase))
                .Select(d => (Task: task, Report: d)))
            .GroupBy(r => RepositoryOf(r.Report.Key))
            .Select(reports => (Repository: reports.Key, Latest: reports.MaxBy(r => r.Report.At)))
            .Select(r => new RepositoryDeploy(
                r.Repository,
                r.Latest.Task,
                r.Latest.Report.Note,
                r.Latest.Task.Deployed.Where(d => d.At == r.Latest.Report.At).Select(d => RepositoryOf(d.Key)).Distinct().Order(StringComparer.Ordinal).ToList(),
                open.Where(t => t.Id != r.Latest.Task.Id && Waits(t) && WaitsOn(t, r.Repository, pullRequests)).ToList()))
            .Where(d => d.Waiting.Count > 0)
            .OrderBy(d => d.Repository, StringComparer.Ordinal)
            .ToList();

    /// <summary>How a session reports a change nothing runs (the session briefing's words); such a report is no deploy.</summary>
    public const string NothingToDeploy = "nothing to deploy";

    // Who can be waiting on a deploy: a task with a Claude session (the manager's comment is what wakes it) that is still
    // under way. A Done task is left out: the briefing's order (merged, released, deployed, done) means its session
    // already settled the deploy, and an older Done task without a report would otherwise get a resumed turn on every
    // deploy in its repository, for good. So is a recurring task between runs, where a comment starts a whole new run
    // (CommentWake.Plan's FreshRun).
    static bool Waits(TaskItem task) => task.Sessions.Count > 0 && task.Status != TaskStatus.Done && !task.IsBetweenRuns;

    // A pull request of `repository` that GitHub says is merged and the task has not reported deployed.
    static bool WaitsOn(TaskItem task, string repository, IReadOnlyDictionary<string, PullRequestArrival> pullRequests) =>
        task.PullRequests.Any(p => RepositoryOf(p.Key) == repository
            && task.Deployed.All(d => d.Key != p.Key)
            && pullRequests.TryGetValue(p.Key, out var arrival) && arrival.State == PullRequestState.Merged);

    // "owner/repo" from a PullRequestLink.Key ("owner/repo#12").
    static string RepositoryOf(string key) => key.LastIndexOf('#') is var hash and > 0 ? key[..hash] : key;

    // The wake's text: what happened, one line per task, then one line pointing at the briefing's wake rules
    // (ManagerSession.Briefing), which say what to do with it. Kept short: the manager pays for every character. The
    // deploy section carries its own instruction, so the briefing that every manager turn reads does not grow for it.
    static string Message(IReadOnlyList<TaskItem> handedBack, IReadOnlyList<Guid> nudgedBefore, IReadOnlyList<RepositoryDeploy> deploys) =>
        string.Join("\n", new[]
            {
                new[] { $"{Prefix} Since your last look:" },
                Section("Handed back to the user (the session stopped without task ask or task done):",
                    handedBack.Select(t => nudgedBefore.Contains(t.Id)
                        ? $"{Name(t)}: handed back AGAIN today. Do not nudge; list it for the user."
                        : $"{Name(t)}: status line \"{Cut(t.StatusLine, 100)}\"")),
                Section("Deployed (a deploy ships what merged before it, so the tasks under it may be in it too):",
                    deploys.SelectMany(d => new[]
                        {
                            $"{d.Repository}, reported by {Name(d.Reporter)}: \"{Cut(d.Evidence, 120)}\"."
                                + (d.Covered.Count > 1 ? $" One report for {string.Join(", ", d.Covered)}, so it may be another's deploy." : "")
                                + " Waiting on a deploy of it:",
                        }.Concat(d.Waiting.Select(t => $"  {Name(t)}")))),
                deploys.Count > 0
                    ? new[]
                    {
                        "Comment each waiting task once: what was deployed, with that evidence, and that it should check whether",
                        "  its own change is in it and, if so, report task deployed; otherwise carry on. Tell the user nothing about it.",
                    }
                    : [],
                new[] { "Handle it as your briefing says for a Deskbar wake, then stop." },
            }.SelectMany(lines => lines));

    static IEnumerable<string> Section(string heading, IEnumerable<string> lines) =>
        lines.ToList() is { Count: > 0 } list ? [heading, .. list.Select(line => "- " + line)] : [];

    static string Name(TaskItem task) => $"{task.Id.ToString()[..8]} \"{Cut(task.Title, 60)}\"";

    // One line, at most `limit` characters, so a long question or a pasted paragraph cannot blow up the wake.
    static string Cut(string text, int limit) =>
        text.ReplaceLineEndings(" ").Trim() is var line && line.Length > limit ? line[..(limit - 1)].TrimEnd() + "…" : line;
}
