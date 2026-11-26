using System.Text.Json;
using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// A CHANGE IS DONE WHEN IT RUNS, NOT WHEN IT MERGES (task 8470d176, 2026-11-25). Petre: "change in software should
// report as done with the checkbox after it has been deployed and something else, like a different done or almost done
// sign when it hasn't yet", and "for deskbar it would mean checking the running version vs what the change asked for".
// Mockup https://claude.ai/artifact/2NujT6C7cJyYNQUVupjhfp, his pick A: "ring fills on arrival".
//
// A session marks its task Done once its pull request merges, and the bar drew the same green disc for a change in the
// Deskbar he is running and one merged a minute ago that the next release has not even built yet. So a Done task that
// changed Deskbar now has two looks (TaskStatusMarks in the app): a hollow green ring while what it merged is not in the
// running build, and the full disc once it is.
//
// WHICH TASKS (Of): a Done task with at least one pull request RECORDED on it (TaskItem.PullRequests: the `gh pr create`
// hook or `task pr`) in the repository this Deskbar is built from (RunningBuild.Builds). Links only mentioned in a
// task's text are left out, because a thread can mention someone else's pull request. A pull request closed without a
// merge is left out too: it never shipped and never will. Everything else keeps today's full tick, DeliveryStage.None:
// a Done note, research, and work in other repositories (phase 2 of the task, a session reporting a deploy it checked in
// the app's logs, is a later pull request).
//
// HOW "RUNNING" IS DECIDED. The running build knows its own commit: the SDK appends it to the InformationalVersion,
// "2.6.23+<40 hex>" (RunningBuild.Of). GitHub knows each pull request's merge commit (with "Rebase and merge" it is the
// pull request's last commit as it landed on main). The pull request runs here when the running commit CONTAINS the
// merge commit, which GitHub's compare answers: `compare/<merge>...<running>` says "ahead" or "identical" when it does,
// "behind" or "diverged" when it does not (measured 2026-11-25 on #570 against main and against v2.5.0's commit, a
// second each). A local build whose commit GitHub has never seen answers 404, which is a failed look: unknown.
//
// UNKNOWN DRAWS AS TODAY. A pull request GitHub could not tell about (gh missing, signed out, offline, a local commit)
// leaves its task at DeliveryStage.None, the full disc, rather than a ring that might never fill on a machine that
// cannot look. The ring is a claim ("not running yet"), and Deskbar only makes claims it measured.
public static class RunningChanges
{
    /// <summary>
    /// What a task's tick says (see the header): None (the full disc, as before this existed) for anything that is not a
    /// Done task with a recorded pull request of this build's repository whose facts are all known; otherwise Open while
    /// one of them is still open, Merged while one is merged but not in the running build, and Running once every one is
    /// in it.
    /// <paramref name="facts"/> is keyed by <see cref="PullRequestLink.Key"/>; a missing key is unknown.
    /// </summary>
    public static TaskDelivery Of(TaskItem task, RunningBuild build, IReadOnlyDictionary<string, PullRequestArrival> facts)
    {
        if (task.Status != TaskStatus.Done) return TaskDelivery.None;
        var links = Watched(task, build);
        if (links.Count == 0 || links.Any(l => !facts.ContainsKey(l.Key))) return TaskDelivery.None;
        // Closed without a merge: never shipped, never will, so it neither holds the ring nor earns the disc.
        var shipped = links.Where(l => facts[l.Key].State != PullRequestState.Closed).ToArray();
        if (shipped.Length == 0) return TaskDelivery.None;
        var waiting = shipped.Where(l => !facts[l.Key].Running).ToArray();
        if (waiting.Length == 0) return new TaskDelivery(DeliveryStage.Running, $"Running in this Deskbar ({build.Version}).");
        // Open while any pull request is still open (the chip must not say "Merged" of one that is not), Merged otherwise.
        var stage = waiting.Any(l => facts[l.Key].State == PullRequestState.Open) ? DeliveryStage.Open : DeliveryStage.Merged;
        return new TaskDelivery(stage, string.Join(" ", waiting.Select(l => Waiting(l, facts[l.Key], build))));
    }

    // The hover line for one pull request that does not run here yet.
    static string Waiting(PullRequestLink link, PullRequestArrival fact, RunningBuild build) =>
        fact.State == PullRequestState.Open
            ? $"{link.Label} is not merged yet."
            : $"Merged in {link.Label}, not in this Deskbar ({build.Version}) yet; it ships with the next release.";

    /// <summary>A task's recorded pull requests in the running build's repository (see the header), whatever its status.</summary>
    public static IReadOnlyList<PullRequestLink> Watched(TaskItem task, RunningBuild build) =>
        task.PullRequests.Where(build.Builds).DistinctBy(l => l.Key).ToArray();

    /// <summary>
    /// The pull requests the watch should ask GitHub about now: those of Done tasks (Watched), each once, that have no
    /// settled answer yet. Only an open one is ever asked again, since it can still merge. A merged or closed one is
    /// settled for good: whether the running build holds a merge cannot change until Deskbar restarts, and a restart
    /// starts the watch afresh with nothing known.
    /// </summary>
    public static IReadOnlyList<PullRequestLink> ToLook(TaskBoard board, RunningBuild build, IReadOnlyDictionary<string, PullRequestArrival> facts) =>
        board.Tasks
            .Where(t => t.Status == TaskStatus.Done)
            .SelectMany(t => Watched(t, build))
            .DistinctBy(l => l.Key)
            .Where(l => !facts.TryGetValue(l.Key, out var f) || !f.Settled)
            .ToArray();

    /// <summary>
    /// What `gh pr view &lt;url&gt; --json state,closedAt,mergeCommit` printed, read: the state and the merge commit (none
    /// unless merged). Measured on 2026-11-25:
    /// <c>{"closedAt":"2026-11-25T17:53:11Z","mergeCommit":{"oid":"64599cab..."},"state":"MERGED"}</c>; an open one has
    /// <c>"mergeCommit":null</c>. A merged pull request without a merge commit is a failure, never a guess.
    /// </summary>
    public static Result<(PullRequestState State, Maybe<string> MergeCommit)> ParseMerge(string json) =>
        AskedPullRequests.Parse(json).Bind(facts =>
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var merge = document.RootElement.TryGetProperty("mergeCommit", out var commit)
                    && commit.ValueKind == JsonValueKind.Object
                    && commit.TryGetProperty("oid", out var oid)
                    && oid.ValueKind == JsonValueKind.String
                    && oid.GetString() is { Length: > 0 } sha
                        ? Maybe.From(sha)
                        : Maybe<string>.None;
                return facts.State == PullRequestState.Merged && merge.HasNoValue
                    ? Result.Failure<(PullRequestState, Maybe<string>)>($"gh gave a merged pull request without its merge commit: {json}")
                    : Result.Success((facts.State, merge));
            }
            catch (JsonException e)
            {
                return Result.Failure<(PullRequestState, Maybe<string>)>($"gh's answer is not JSON: {e.Message}");
            }
        });

    /// <summary>
    /// Whether GitHub's compare status (`compare/&lt;merge&gt;...&lt;running&gt;`, its "status" field) means the running
    /// commit contains the merge commit: "ahead" (running is past it) or "identical" (running is it). "behind" and
    /// "diverged" mean it does not; anything else is a failure.
    /// </summary>
    public static Result<bool> Contains(string compareStatus) =>
        compareStatus.Trim().ToLowerInvariant() switch
        {
            "ahead" or "identical" => true,
            "behind" or "diverged" => false,
            var other => Result.Failure<bool>($"GitHub's compare gave an unknown status: {other}"),
        };
}

/// <summary>
/// The build this Deskbar runs: the GitHub repository it is built from, the version it shows ("2.6.23") and the commit
/// it was built from (40 hex digits).
/// </summary>
public sealed record RunningBuild(string Owner, string Repo, string Version, string Commit)
{
    /// <summary>Whether the pull request is in this build's repository (owner and name without case, as GitHub's are).</summary>
    public bool Builds(PullRequestLink link) =>
        link.Owner.Equals(Owner, StringComparison.OrdinalIgnoreCase) && link.Repo.Equals(Repo, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The running build from the app's InformationalVersion, "2.6.23+&lt;commit&gt;" (Nerdbank.GitVersioning appends the
    /// full commit). None when there is no '+' or what follows is not a 40-digit hex commit: such a build cannot be
    /// compared, and every task keeps the full tick.
    /// </summary>
    public static Maybe<RunningBuild> Of(string owner, string repo, string informationalVersion)
    {
        var plus = informationalVersion.IndexOf('+');
        if (plus <= 0) return Maybe<RunningBuild>.None;
        // Anything after the commit (a '.' and more metadata) is not part of it.
        var commit = new string(informationalVersion[(plus + 1)..].TakeWhile(Uri.IsHexDigit).ToArray());
        return commit.Length == 40
            ? new RunningBuild(owner, repo, informationalVersion[..plus], commit.ToLowerInvariant())
            : Maybe<RunningBuild>.None;
    }
}

/// <summary>
/// What GitHub says about one pull request against the running build: its state, and whether the running commit holds
/// its merge (false unless merged). <see cref="Settled"/> answers are never asked again in this run.
/// </summary>
public sealed record PullRequestArrival(PullRequestState State, bool Running)
{
    /// <summary>
    /// Merged or closed: nothing about either can change before Deskbar restarts, since the running commit is fixed for
    /// the life of the process. Only an open pull request can still change (it can merge).
    /// </summary>
    public bool Settled => State != PullRequestState.Open;
}

/// <summary>Where a Done task's change stands against the running build (RunningChanges.Of).</summary>
public enum DeliveryStage
{
    /// <summary>Not a change Deskbar can check, or not known yet: the full tick, as before.</summary>
    None,

    /// <summary>A recorded pull request is still open: the hollow ring, and the chip says so.</summary>
    Open,

    /// <summary>Merged but not in the running build: the hollow ring.</summary>
    Merged,

    /// <summary>Every pull request it recorded is in the running build: the full tick.</summary>
    Running,
}

/// <summary>A task's delivery stage and the line its hover text adds (empty for None).</summary>
public sealed record TaskDelivery(DeliveryStage Stage, string Note)
{
    public static readonly TaskDelivery None = new(DeliveryStage.None, "");

    /// <summary>Whether the change is not running yet (Open or Merged): the ring instead of the disc.</summary>
    public bool Shipping => Stage is DeliveryStage.Open or DeliveryStage.Merged;
}
