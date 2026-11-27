using System.Text.Json;
using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// DESKBAR WATCHES THE PULL REQUESTS, NOT EVERY SESSION (Deskbar task c8e6967d, 2026-11-27; design:
// docs/superpowers/specs/2026-11-27-repo-watcher-design.md, plan: docs/superpowers/plans/2026-11-27-repo-watch.md).
// Petre: "let one agent per repo take on the role of the watcher. it should monitor pr merges and deploys and report to
// the manager", then, offered a model for it, "do what you like". Deskbar itself is the watcher: no model, no tokens.
//
// Before this, every session with an open pull request ran its own Monitor loop against GitHub and, after the merge,
// waited on the release in another loop. Five features in one repository were five identical loops, and a session that
// forgot to re-arm its loop silently stopped watching. Now Deskbar asks GitHub once a minute per repository that has a
// task in flight, applies the rules in this file, and nudges the task's session through CommentWaker's delivery queue.
//
// This file holds the PURE parts only: the types, the gh answer parsers (this section), and the rules (Poll, Follow).
// State lives in TaskService, the polling schedule and the gh calls in RepoWatchPoll, so every rule here can be tested
// with plain values and no clock, scheduler or gh.
//
// THE GH ANSWERS. One GraphQL query per repository asks, for each in-flight pull request, its state, mergeable flag,
// base branch, that base branch's head commit and its merge commit. The merge commit's workflow runs come from
// `gh run list --commit <sha> --json name,status,conclusion,url`. Both parsers fail loudly (Result.Failure) on anything
// unexpected, as RunningChanges.ParseMerge does: a look that cannot be read is a failed look, never a guess.
public static class RepoWatch
{
    // ------------------------------------------------------------------------------------------------------------------
    // THE POLL RULES (Poll, InFlight, Watched)
    // ------------------------------------------------------------------------------------------------------------------

    // "owner/repo" lower case: the key of a repository everywhere in RepoWatchState (GitHub's names ignore case).
    static string RepositoryOf(PullRequestLink link) => $"{link.Owner}/{link.Repo}".ToLowerInvariant();

    // The key of a base branch's last seen head: the repository and the branch ("owner/repo:main"). A stacked pull
    // request names another pull request's branch as its base, and that branch's head is tracked the same way.
    static string HeadKey(PullRequestLink link, string baseRef) => $"{RepositoryOf(link)}:{baseRef}";

    // The pull requests of a task that keep it in flight (the spec's "In flight"): the task has a Claude session, is open
    // and not Done, is not a recurring task between runs, and each link is not known (Seen) to be closed unmerged. Done
    // ends flight because Done comes LAST in the briefing's order (merged, released, deployed, done); a snoozed task
    // still counts, its pull request falls behind main all the same.
    static IEnumerable<PullRequestLink> FlightLinks(TaskItem task, RepoWatchState state) =>
        task is { IsOpen: true, IsClaudes: true, IsBetweenRuns: false } && task.Status != TaskStatus.Done
            ? RunningChanges.Watched(task).Where(l => !(state.Seen.TryGetValue(l.Key, out var seen) && seen == PullRequestState.Closed))
            : [];

    /// <summary>
    /// The tasks in flight (see FlightLinks), each with its pull request, grouped by "owner/repo" lower case: the
    /// repositories to ask about, and for each the pull requests to put in one query.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<(TaskItem Task, PullRequestLink Link)>> InFlight(TaskBoard board, RepoWatchState state) =>
        board.Tasks
            .SelectMany(task => FlightLinks(task, state).Select(link => (Task: task, Link: link)))
            .GroupBy(x => RepositoryOf(x.Link))
            .ToDictionary(g => g.Key, g => (IReadOnlyList<(TaskItem Task, PullRequestLink Link)>)g.ToArray());

    /// <summary>
    /// Whether Deskbar is watching this task's pull request right now: it is in flight in a repository whose last
    /// successful poll was within five minutes. The prompt line tells the session so (and to stop its own loop), and
    /// when gh is missing or signed out the last success ages past five minutes, this turns false and the session
    /// watches for itself again: the fallback of the spec's "The sessions".
    /// </summary>
    // REVIEW FINDING (Deskbar task c8e6967d): a link counts only when something is actually watching it, which is a link
    // last Seen Open (Poll nudges it) or one being followed after its merge (Follow nudges it). A link Seen Merged and not
    // followed (first seen already merged, or its follow ended) has nothing watching it, yet used to count, so the prompt
    // line told the session "Deskbar watches" and to drop its own loop while nobody watched.
    public static bool Watched(RepoWatchState state, TaskItem task, DateTimeOffset now) =>
        FlightLinks(task, state)
            .Where(link => state.Seen.TryGetValue(link.Key, out var seen) && seen == PullRequestState.Open
                || state.Following.Any(m => m.TaskId == task.Id && m.Link.Key == link.Key))
            .Any(link => state.LastSuccess.TryGetValue(RepositoryOf(link), out var at) && now - at <= TimeSpan.FromMinutes(5));

    /// <summary>
    /// One poll of one repository: what gh said (<paramref name="facts"/>, one per pull request asked about) against what
    /// was seen before (<paramref name="state"/>), giving the new memory and the nudges to deliver. See the rules inline.
    /// Everything is judged against the OLD memory and the memory is updated once at the end, so one poll cannot see its
    /// own changes. A task gets at most one nudge per poll, its texts joined with a newline.
    /// </summary>
    public static RepoWatchStep Poll(RepoWatchState state, TaskBoard board, string repository, IReadOnlyList<PullFacts> facts, DateTimeOffset now)
    {
        var flying = InFlight(board, state).GetValueOrDefault(repository.ToLowerInvariant(), []);
        var tasksOf = (PullRequestLink link) => flying.Where(x => x.Link.Key == link.Key).Select(x => x.Task).ToArray();

        // Why a nudge for each task of a pull request, and only those rules that fire.
        IEnumerable<(TaskItem Task, string Text)> Nudges(PullFacts f) =>
            tasksOf(f.Link).SelectMany(task => Texts(f, task).Select(text => (task, text)));

        IEnumerable<string> Texts(PullFacts f, TaskItem task)
        {
            var label = f.Link.Label;
            // BASE MOVED: a head other than the one seen last (BaseMoved). A head first seen is only recorded (nothing was
            // seen to move, so the first poll after a start nudges nobody). A snoozed task is spared: its stopped session
            // would otherwise be resumed every time main moves.
            if (BaseMoved(f) && !task.IsSnoozed)
                yield return $"The base {f.BaseRef} of {label} moved to {f.BaseHead[..Math.Min(7, f.BaseHead.Length)]}: rebase and push it now, by the repository's own rules.";
            // CONFLICTING: once per base head, snoozed tasks too (a conflict is the one thing worth resuming for).
            // UNKNOWN is neither a conflict nor a clear: GitHub is still computing, so nothing fires here.
            if (f.State == PullRequestState.Open && f.Mergeable == Mergeable.Conflicting
                && state.ConflictNudged.GetValueOrDefault(f.Link.Key) != f.BaseHead)
                yield return $"{label} conflicts with {f.BaseRef}: rebase, resolve and push it now.";
            // MERGED, only if seen Open before: one first seen already merged (a link recorded late, Deskbar off while
            // it merged) is left to its session under the old rule, since nothing says it is not already past this.
            if (IsNewMerge(f))
                yield return $"{label} merged. Deskbar follows the runs on its merge commit and nudges you with the result; you need not wait on them in a loop.";
        }

        // REVIEW FINDING (Deskbar task c8e6967d): the move is judged only for a link whose PREVIOUS look saw it Open. Heads
        // is per base branch and used to be fed by open pull requests only and never pruned, so a link seen for the first
        // time (or back after a merged or closed spell) was compared against a head another pull request, or its own
        // older self, had left behind, and got a "base moved" nudge for a move it never saw. Such a link only records.
        bool BaseMoved(PullFacts f) =>
            f.State == PullRequestState.Open && f.BaseHead.Length > 0
            && state.Seen.TryGetValue(f.Link.Key, out var seen) && seen == PullRequestState.Open
            && state.Heads.TryGetValue(HeadKey(f.Link, f.BaseRef), out var old) && old != f.BaseHead;

        bool IsNewMerge(PullFacts f) =>
            f.State == PullRequestState.Merged && f.MergeCommit.HasValue
            && state.Seen.TryGetValue(f.Link.Key, out var seen) && seen == PullRequestState.Open;

        var nudges = facts
            .SelectMany(Nudges)
            .GroupBy(x => x.Task.Id)
            .Select(g => new WatchNudge(g.Key, string.Join("\n", g.Select(x => x.Text))))
            .ToArray();

        var open = facts.Where(f => f.State == PullRequestState.Open).ToArray();
        var newMerges = facts.Where(IsNewMerge)
            .SelectMany(f => tasksOf(f.Link).Select(task => new FollowedMerge(task.Id, f.Link, f.MergeCommit.Value, now)))
            .Where(m => !state.Following.Any(o => o.TaskId == m.TaskId && o.Link.Key == m.Link.Key))
            .ToArray();
        // A clear (Mergeable) forgets the conflict nudge so a later conflict at the same head is told again; Unknown
        // leaves the memory exactly as it was.
        var cleared = open.Where(f => f.Mergeable == Mergeable.Mergeable).Select(f => f.Link.Key).ToHashSet();
        // A base-move nudge asks the session to rebase, which answers a conflict at that head too, so the conflict is
        // recorded as told at that head as well (review finding): otherwise GitHub reporting the conflict a poll later, at
        // the same head, would nudge the session a second time for the one rebase it was just told to do.
        var moveTold = open.Where(f => BaseMoved(f) && tasksOf(f.Link).Any(t => !t.IsSnoozed))
            .Select(f => KeyValuePair.Create(f.Link.Key, f.BaseHead));
        var nextState = state with
        {
            Seen = Updated(state.Seen, facts.Select(f => KeyValuePair.Create(f.Link.Key, f.State))),
            // Every fact that carries a base head updates it, merged and closed ones too (review finding): a head fed by
            // open pull requests alone went stale while none was open and nudged the next one that appeared.
            Heads = Updated(state.Heads, facts.Where(f => f.BaseHead.Length > 0)
                .Select(f => KeyValuePair.Create(HeadKey(f.Link, f.BaseRef), f.BaseHead))),
            ConflictNudged = Updated(state.ConflictNudged.Where(kv => !cleared.Contains(kv.Key)).ToDictionary(),
                open.Where(f => f.Mergeable == Mergeable.Conflicting).Select(f => KeyValuePair.Create(f.Link.Key, f.BaseHead))
                    .Concat(moveTold)),
            Following = [.. state.Following, .. newMerges],
            LastSuccess = Updated(state.LastSuccess, [KeyValuePair.Create(repository.ToLowerInvariant(), now)]),
        };
        return new RepoWatchStep(nextState, nudges);
    }

    // A copy of a dictionary with the updates written over it (the last of a key wins).
    static IReadOnlyDictionary<string, T> Updated<T>(IReadOnlyDictionary<string, T> old, IEnumerable<KeyValuePair<string, T>> updates) =>
        old.Concat(updates).GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.Last().Value);

    // ------------------------------------------------------------------------------------------------------------------
    // FOLLOWING A MERGE (Follow)
    // ------------------------------------------------------------------------------------------------------------------

    // Conclusions that mean the merge commit's run FAILED. "cancelled" is deliberately absent: release.yml cancels an
    // older build when a newer merge lands ("a newer merge cancels the older build"), so a cancelled run is the normal
    // end of a burst of merges, never a reason to wake anybody. "skipped", "neutral" and "stale" are not failures either.
    static readonly IReadOnlySet<string> RedConclusions =
        new HashSet<string> { "failure", "timed_out", "startup_failure", "action_required" };

    // How long a merge commit may show no workflow runs at all before Deskbar decides none will start (a workflow can
    // take a minute or two to appear), and how long Deskbar follows before it gives the merge back to the session.
    static readonly TimeSpan NoRunsAfter = TimeSpan.FromMinutes(10);
    static readonly TimeSpan GiveUpAfter = TimeSpan.FromHours(3);

    /// <summary>
    /// One look at the workflow runs on a followed merge commit. Ends the follow (removes it from
    /// <see cref="RepoWatchState.Following"/>) with a nudge when a run failed, when every run finished, when no run
    /// started within ten minutes, or after three hours; otherwise nothing changes.
    /// </summary>
    public static FollowStep Follow(RepoWatchState state, FollowedMerge merge, IReadOnlyList<RunFacts> runs, DateTimeOffset now)
    {
        var label = merge.Link.Label;
        FollowStep Ends(string text, params RedRun[] red) => EndsFollow(state, merge, text, red);

        // A failed run ends the follow at once, even while others still run: the session must look now, and the manager
        // hears of it too (RedRun). Only the first red run is reported, so one broken merge is one message.
        if (runs.FirstOrDefault(r => r.Finished && RedConclusions.Contains(r.Conclusion)) is { } failed)
            return Ends($"{failed.Name} failed on the merge commit of {label}: {failed.Url}. Look at it; the manager is told too.",
                new RedRun(merge.TaskId, $"{merge.Link.Owner}/{merge.Link.Repo}", failed.Name, failed.Url, now));
        // Every run finished and none failed: the session carries on in the briefing's order.
        if (runs.Count > 0 && runs.All(r => r.Finished))
            return Ends($"The runs on the merge commit of {label} finished: "
                + string.Join(", ", runs.Select(r => $"{r.Name} ({Describe(r.Conclusion)})"))
                + ". Carry on in the briefing's order: released, deployed, done.");
        // No runs after ten minutes: a repository without workflows on its main branch is normal, so this is information.
        if (runs.Count == 0 && now - merge.Since >= NoRunsAfter)
            return Ends($"{label} merged and no workflow runs started on its merge commit; carry on as the briefing says.");
        // Runs that never finish must not be followed forever.
        return GiveUp(state, merge, now);
    }

    // The end of a follow: the merge leaves Following and the session gets one nudge (and the red runs, if any).
    static FollowStep EndsFollow(RepoWatchState state, FollowedMerge merge, string text, params RedRun[] red) =>
        new(Unfollowed(state, merge), [new WatchNudge(merge.TaskId, text)], red);

    static RepoWatchState Unfollowed(RepoWatchState state, FollowedMerge merge) =>
        state with { Following = state.Following.Where(m => m != merge).ToArray() };

    /// <summary>
    /// The three-hour limit of a follow: after it the follow ends with the nudge that tells the session to check the runs
    /// itself; before it nothing changes. Review finding (Deskbar task c8e6967d): a runs look that kept FAILING (gh signed
    /// out, the commit gone) never reached <see cref="Follow"/>, so its limit never came and the merge was looked at, and
    /// failed, every minute for ever. The poll calls this on a failed look, so the limit holds whatever gh says.
    /// </summary>
    public static FollowStep GiveUp(RepoWatchState state, FollowedMerge merge, DateTimeOffset now) =>
        now - merge.Since >= GiveUpAfter
            ? EndsFollow(state, merge, $"Deskbar stopped following the runs on the merge commit of {merge.Link.Label} after 3 hours; check them yourself.")
            : new FollowStep(state, [], []);

    /// <summary>
    /// Ends a follow silently, no nudge: its task is no longer open or is Done, so there is nobody to tell (review finding,
    /// Deskbar task c8e6967d: a follow nudge used to reach a Done or Closed task and resume its session for nothing).
    /// </summary>
    public static FollowStep Drop(RepoWatchState state, FollowedMerge merge) => new(Unfollowed(state, merge), [], []);

    static string Describe(string conclusion) => conclusion == "cancelled" ? "cancelled, a newer run took over" : conclusion;

    // ------------------------------------------------------------------------------------------------------------------
    // THE GH ANSWERS (Query, ParseRepository, ParseRuns)
    // ------------------------------------------------------------------------------------------------------------------

    // One alias per pull request ("p12") so a single request answers them all. The alias is the pull request's number,
    // which is unique within a repository, and ParseRepository finds each link's answer by it.
    static string Alias(PullRequestLink link) => $"p{link.Number}";

    /// <summary>
    /// The GraphQL text asking about every pull request in <paramref name="links"/>, all of one repository (the first
    /// link's): <c>p12: pullRequest(number: 12) { state mergeable baseRefName baseRef { target { oid } } mergeCommit { oid } }</c>
    /// inside <c>repository(owner: "...", name: "...")</c>. Owner and repo are GitHub names (letters, digits, '-', '_',
    /// '.'), which AskedPullRequests' link pattern already guarantees, so nothing here needs escaping.
    /// </summary>
    public static string Query(IReadOnlyList<PullRequestLink> links) =>
        $"{{ repository(owner: \"{links[0].Owner}\", name: \"{links[0].Repo}\") {{ "
        + string.Join(" ", links.Select(l =>
            $"{Alias(l)}: pullRequest(number: {l.Number}) {{ state mergeable baseRefName baseRef {{ target {{ oid }} }} mergeCommit {{ oid }} }}"))
        + " } }";

    /// <summary>
    /// The facts of every link in <paramref name="links"/> from the GraphQL answer <paramref name="json"/>. A link whose
    /// alias is missing (or null: GitHub answers null for a pull request it cannot show) fails the whole look, so a
    /// pull request is never dropped silently and then treated as unchanged.
    /// </summary>
    public static Result<IReadOnlyList<PullFacts>> ParseRepository(IReadOnlyList<PullRequestLink> links, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("repository", out var repository)
                || repository.ValueKind != JsonValueKind.Object)
                return Result.Failure<IReadOnlyList<PullFacts>>($"gh gave no repository: {json}");
            return links
                .Select(link => repository.TryGetProperty(Alias(link), out var pull) && pull.ValueKind == JsonValueKind.Object
                    ? ParsePull(link, pull)
                    : Result.Failure<PullFacts>($"gh's answer has nothing for {link.Label}: {json}"))
                .Combine()
                .Map(facts => (IReadOnlyList<PullFacts>)facts.ToArray());
        }
        catch (JsonException e)
        {
            return Result.Failure<IReadOnlyList<PullFacts>>($"gh's answer is not JSON: {e.Message}");
        }
    }

    // A string property, or "" when absent or not a string (GraphQL nulls what it cannot show).
    static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    // baseRef.target.oid, the head commit of the base branch; "" when the branch is gone (a deleted stacked base).
    static string BaseHead(JsonElement pull) =>
        pull.TryGetProperty("baseRef", out var baseRef) && baseRef.ValueKind == JsonValueKind.Object
        && baseRef.TryGetProperty("target", out var target) && target.ValueKind == JsonValueKind.Object
            ? Text(target, "oid")
            : "";

    static Result<PullFacts> ParsePull(PullRequestLink link, JsonElement pull) =>
        Text(pull, "state").ToUpperInvariant() switch
        {
            "OPEN" => Maybe.From(PullRequestState.Open),
            "MERGED" => Maybe.From(PullRequestState.Merged),
            "CLOSED" => Maybe.From(PullRequestState.Closed),
            _ => Maybe<PullRequestState>.None,
        } is { HasValue: true } state
            ? new PullFacts(link, state.Value,
                // Anything but the two known answers is Unknown: GitHub computes the flag lazily and says UNKNOWN meanwhile.
                Text(pull, "mergeable").ToUpperInvariant() switch
                {
                    "MERGEABLE" => Mergeable.Mergeable,
                    "CONFLICTING" => Mergeable.Conflicting,
                    _ => Mergeable.Unknown,
                },
                Text(pull, "baseRefName"), BaseHead(pull),
                pull.TryGetProperty("mergeCommit", out var commit) && commit.ValueKind == JsonValueKind.Object
                && Text(commit, "oid") is { Length: > 0 } sha
                    ? Maybe.From(sha)
                    : Maybe<string>.None)
            : Result.Failure<PullFacts>($"gh gave an unknown state for {link.Label}: {Text(pull, "state")}");

    /// <summary>
    /// What `gh run list --json name,status,conclusion,url` printed, read: each run's name, link, whether it finished
    /// (status "completed") and its conclusion lower case ("" while it runs, since GitHub gives null).
    /// </summary>
    public static Result<IReadOnlyList<RunFacts>> ParseRuns(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Array
                ? Result.Success<IReadOnlyList<RunFacts>>(document.RootElement.EnumerateArray()
                    .Select(run => new RunFacts(Text(run, "name"), Text(run, "url"),
                        Text(run, "status").Equals("completed", StringComparison.OrdinalIgnoreCase),
                        Text(run, "conclusion").ToLowerInvariant()))
                    .ToArray())
                : Result.Failure<IReadOnlyList<RunFacts>>($"gh's run list is not an array: {json}");
        }
        catch (JsonException e)
        {
            return Result.Failure<IReadOnlyList<RunFacts>>($"gh's answer is not JSON: {e.Message}");
        }
    }
}

/// <summary>GitHub's mergeable flag: Unknown while it is still computing (neither a conflict nor a clear).</summary>
public enum Mergeable { Unknown, Mergeable, Conflicting }

/// <summary>
/// What one poll learned about one pull request: its state, whether it merges cleanly, its base branch and that branch's
/// head commit, and (once merged) its merge commit.
/// </summary>
public sealed record PullFacts(PullRequestLink Link, PullRequestState State, Mergeable Mergeable, string BaseRef, string BaseHead,
    Maybe<string> MergeCommit);

/// <summary>One workflow run on a merge commit; <see cref="Conclusion"/> is lower case and "" while it runs.</summary>
public sealed record RunFacts(string Name, string Url, bool Finished, string Conclusion);

/// <summary>A message for the session of a task; Deskbar's watch sent it, so it leaves no line in the task's thread.</summary>
public sealed record WatchNudge(Guid TaskId, string Text);

/// <summary>
/// A merge Deskbar saw happen (the pull request was Open, then Merged) and now follows: until every workflow run on
/// <see cref="MergeCommit"/> finished, or three hours after <see cref="Since"/>.
/// </summary>
public sealed record FollowedMerge(Guid TaskId, PullRequestLink Link, string MergeCommit, DateTimeOffset Since);

/// <summary>
/// The watch's memory, in memory only (a restart starts from <see cref="Empty"/>: base heads are recorded again with
/// no nudge, and a merge in the middle of being followed is left to its session, see the spec).
/// <see cref="Seen"/>: the last state seen per pull request (link key). <see cref="Heads"/>: the last head seen per base
/// branch ("owner/repo:branch", lower case repository). <see cref="ConflictNudged"/>: the base head a conflict was
/// nudged at, per link key. <see cref="LastSuccess"/>: the last successful poll per "owner/repo" lower case.
/// </summary>
public sealed record RepoWatchState(
    IReadOnlyDictionary<string, PullRequestState> Seen,
    IReadOnlyDictionary<string, string> Heads,
    IReadOnlyDictionary<string, string> ConflictNudged,
    IReadOnlyList<FollowedMerge> Following,
    IReadOnlyDictionary<string, DateTimeOffset> LastSuccess)
{
    public static readonly RepoWatchState Empty = new(
        new Dictionary<string, PullRequestState>(), new Dictionary<string, string>(), new Dictionary<string, string>(),
        [], new Dictionary<string, DateTimeOffset>());
}

/// <summary>The memory after a poll and the nudges it produced.</summary>
public sealed record RepoWatchStep(RepoWatchState State, IReadOnlyList<WatchNudge> Nudges);

/// <summary>A workflow run that failed on a followed merge commit; the manager is told (ManagerWakes) after the session is.</summary>
public sealed record RedRun(Guid TaskId, string Repository, string Name, string Url, DateTimeOffset At);

/// <summary>The memory after looking at one followed merge, the nudges it produced and any failed run.</summary>
public sealed record FollowStep(RepoWatchState State, IReadOnlyList<WatchNudge> Nudges, IReadOnlyList<RedRun> Red);
