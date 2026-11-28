using System.Text.Json;
using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// A CHANGE IS DONE WHEN IT RUNS, NOT WHEN IT MERGES (task 8470d176, 2026-11-25). Petre: "change in software should
// report as done with the checkbox after it has been deployed and something else, like a different done or almost done
// sign when it hasn't yet", and "for deskbar it would mean checking the running version vs what the change asked for".
// Mockup https://claude.ai/artifact/2NujT6C7cJyYNQUVupjhfp, his pick A: "ring fills on arrival".
//
// A session marks its task Done once its pull request merges, and the bar drew the same green disc for a change that
// runs and one merged a minute ago that nothing has deployed yet. So a Done task with a change has two looks
// (TaskStatusMarks in the app): a hollow green ring while what it merged is not running, and the full disc once it is.
//
// WHICH TASKS (Of): a Done task with at least one pull request RECORDED on it (TaskItem.PullRequests: the `gh pr create`
// hook or `task pr`), in any repository. Links only mentioned in a task's text are left out, because a thread can
// mention someone else's pull request. A pull request closed without a merge is left out too: it never shipped and
// never will. Everything else keeps today's full tick, DeliveryStage.None: a Done note, research, a task with no
// recorded pull request. An open task that is not Done shows the ring and the disc too, but only those two (task
// 1afbd44f, the comment on Of).
//
// TWO SOURCES OF "RUNNING", one per kind of repository.
//
// 1. THIS BUILD'S REPOSITORY (RunningBuild.Builds): Deskbar checks itself. The running build knows its own commit: the
//    SDK appends it to the InformationalVersion, "2.6.23+<hex>" (RunningBuild.Of). GitHub knows each pull request's
//    merge commit (with "Rebase and merge" it is the pull request's last commit as it landed on main). The pull request
//    runs here when the running commit CONTAINS the merge commit, which GitHub's compare answers:
//    `compare/<merge>...<running>` says "ahead" or "identical" when it does, "behind" or "diverged" when it does not
//    (measured 2026-11-25 on #570 against main and against v2.5.0's commit, a second each). A local build whose commit
//    GitHub has never seen answers 404, which is a failed look: unknown. The running build is the truth here: a deploy
//    reported for such a pull request (TaskItem.Deployed) changes nothing, since it would claim this copy runs code it
//    does not.
//
// 2. EVERY OTHER REPOSITORY (phase 2; Petre: "it's not about deskbar only, but all other software tasks", and "for
//    other projects - checking logs for app version"). Deskbar cannot see where another app is deployed, so the session
//    that did the work checks the deployed app's logs, health page or deploy output for the version carrying the merge
//    and REPORTS it (`task deployed`), or Petre marks it by hand ("Mark deployed"); either lands in TaskItem.Deployed. A
//    reported pull request counts as running, even when GitHub was never asked about it (the report is the claim, and
//    it was measured where the app runs). One not reported needs GitHub's state, `gh pr view` alone (no compare, there
//    is no running commit to compare with): open says "not merged yet", merged says "not reported deployed yet".
//
// UNKNOWN: CHECKING, OR CANNOT TELL. A pull request no answer and no report covers is one of two things.
//
// - NOT ASKED YET (task 8db4c78e). The watch asks once a minute, so for up to a minute after a task turns Done (and for
//   the seconds after a start while it works through the list) nothing is known. That used to draw the full disc, and
//   Petre caught a task he had just merged showing "deployed" ("why did you mark the task with the deployed chip?"):
//   the full disc is the claim "it runs", and it was made before anything was measured. Now such a task is
//   DeliveryStage.Checking: the hollow ring, which claims only "not confirmed running", and it fills (or stays a ring)
//   when the answer lands.
// - CANNOT TELL (gh missing, signed out, offline, a local commit GitHub has never seen): the watch's last look at it
//   FAILED (`unreachable`), or the watch's last look failed for every pull request it asked (`blind`: a machine that
//   cannot look at all, so a task that turns Done there is not ringed for the minute until its own look fails too). Its
//   task keeps DeliveryStage.None, the full disc, as before this existed, rather than a ring that would never fill on a
//   machine that cannot look. Deskbar draws the ring only while an answer is on its way. The one exception is the first
//   seconds after a start on such a machine: nothing has failed yet, so the rings show until the first look fails, which
//   is at once when gh is missing (its look fails before asking anything).
public static class RunningChanges
{
    // AN OPEN TASK GETS THE MERGED AND DEPLOYED CIRCLES TOO (task 1afbd44f, 2026-11-27, Petre: "but we have two circles -
    // one for merged and the other for deployed"). Since sessions mark a task done only after its deploy (the briefing's
    // merged, released, deployed, done order), a task whose pull request merged sits Waiting on a redeploy, or In progress
    // while its session releases, and the ring that says "merged, not running yet" never showed on it. So a task that is
    // not Done but still open shows the ring while its change is Merged and the full disc once it is Running. Only those
    // two: Checking and Open say nothing about a merge, and None stays None, so a task still being worked on, or one
    // whose pull request is not merged yet, keeps no mark at all. A Closed task is off every surface, so None.
    /// <summary>
    /// What a task's tick says (see the header): None (the full disc, as before this existed) for anything that is not a
    /// Done task with a recorded pull request whose state is all known; otherwise Open while one of them is still open,
    /// Merged while one is merged but not running (not in the running build, or not reported deployed), and Running once
    /// every one runs. A task that is open but not Done shows only Merged and Running (the comment above), None otherwise.
    /// <paramref name="facts"/> is keyed by <see cref="PullRequestLink.Key"/>; a missing key is unknown: Checking while
    /// its look is still to come, None once it is in <paramref name="unreachable"/> (its last look failed).
    /// </summary>
    /// <paramref name="blind"/>: the watch's last look failed for every pull request it asked (gh missing, signed out,
    /// offline), so one not asked yet cannot be answered either and keeps the full disc too, rather than a ring for the
    /// minute until its own look fails.
    public static TaskDelivery Of(TaskItem task, RunningBuild build, IReadOnlyDictionary<string, PullRequestArrival> facts,
        IReadOnlySet<string>? unreachable = null, bool blind = false) =>
        task.Status switch
        {
            TaskStatus.Done => StageOf(task, build, facts, unreachable, blind),
            TaskStatus.Closed => TaskDelivery.None,
            // BACK FROM DONE: a task reopened, woken by a comment, or on a later run of its schedule still carries the
            // pull requests of the work it finished (TaskItem.PullRequests is never cleared), and a mark from those would
            // sit on the new work, a green disc saying "deployed" all through a run that has shipped nothing yet. The
            // links carry no time to tell old from new, so a task whose status log shows it Done or Closed before gets no
            // mark until it is Done again.
            _ when task.StatusLog.Any(c => c.To is TaskStatus.Done or TaskStatus.Closed) => TaskDelivery.None,
            _ => StageOf(task, build, facts, unreachable, blind) is { Stage: DeliveryStage.Merged or DeliveryStage.Unchecked or DeliveryStage.Running } merged
                ? merged
                : TaskDelivery.None,
        };

    // The stage of a task's recorded pull requests, whatever its status (Of decides which stages each status shows).
    static TaskDelivery StageOf(TaskItem task, RunningBuild build, IReadOnlyDictionary<string, PullRequestArrival> facts,
        IReadOnlySet<string>? unreachable, bool blind)
    {
        var links = Watched(task);
        var reported = task.Deployed.Select(d => d.Key).ToHashSet();
        // An other-repository pull request that was reported deployed runs, unless GitHub says it is still OPEN: a
        // session's report stamps every recorded pull request (TaskBoard.MarkDeployed), and a second one, open or in an
        // app the session never checked, must not fill the disc with it. Unknown (gh missing, not asked yet) trusts the
        // report: the session looked at the running app, which is more than Deskbar can.
        bool Reported(PullRequestLink link) =>
            !build.Builds(link) && reported.Contains(link.Key)
            && !(facts.TryGetValue(link.Key, out var fact) && fact.State == PullRequestState.Open);
        if (links.Count == 0) return TaskDelivery.None;
        // Unknown (see the header): a failed look cannot tell, so the full disc as before; one still to come is Checking,
        // the ring, never the disc that says it runs.
        var unknown = links.Where(l => !Reported(l) && !facts.ContainsKey(l.Key)).ToArray();
        if (unknown.Any(l => blind || unreachable?.Contains(l.Key) == true)) return TaskDelivery.None;
        if (unknown.Length > 0) return new TaskDelivery(DeliveryStage.Checking, CheckingNote);
        // Closed without a merge: never shipped, never will, so it neither holds the ring nor earns the disc.
        var shipped = links.Where(l => !(facts.TryGetValue(l.Key, out var fact) && fact.State == PullRequestState.Closed)).ToArray();
        if (shipped.Length == 0) return TaskDelivery.None;
        // Running: in the running build for this build's repository, reported deployed for any other.
        var waiting = shipped.Where(l => build.Builds(l) ? !facts[l.Key].Running : !Reported(l)).ToArray();
        if (waiting.Length == 0) return new TaskDelivery(DeliveryStage.Running, RunningNote(task, shipped, build));
        // Open while any pull request is still open (the chip must not say "Merged" of one that is not), Merged otherwise.
        var stage = waiting.Any(l => facts[l.Key].State == PullRequestState.Open) ? DeliveryStage.Open : DeliveryStage.Merged;
        // Merged, and its session said it cannot tell whether that was deployed (task 485ba96b, TaskItem.DeployUnknown):
        // Unchecked, its hover line the session's reason. Only from Merged: one still open is not deployed by anyone.
        if (stage == DeliveryStage.Merged && task.DeployUnknown.HasValue)
            return new TaskDelivery(DeliveryStage.Unchecked, UncheckedNote(task.DeployUnknown.Value));
        return new TaskDelivery(stage, string.Join(" ", waiting.Select(l => Waiting(l, facts[l.Key], build))));
    }

    /// <summary>
    /// The words of a task whose session cannot tell whether its change was deployed (DeliveryStage.Unchecked, task
    /// 485ba96b): "Can't check the deploy: no access to the Portainer logs on contabo1", the session's reason after the colon.
    /// </summary>
    public static string UncheckedNote(DeployUnknownReport report) => $"{UncheckedWords}: {report.Reason.TrimEnd('.')}";

    /// <summary>The state in a few words, for a mark's name and the page's header (DeliveryStage.Unchecked).</summary>
    public const string UncheckedWords = "Can't check the deploy";

    /// <summary>The hover line of a task whose pull requests are still being asked about (DeliveryStage.Checking).</summary>
    public const string CheckingNote = "Checking with GitHub whether its change runs here yet.";

    // The hover line once every pull request runs. A Deskbar-only task keeps phase 1's sentence. With other repositories
    // involved it leads with the evidence of the latest report among them ("Deployed: 2026.11.25.39-3749d21 in the portal
    // logs"), then the Deskbar sentence when the task changed Deskbar too.
    static string RunningNote(TaskItem task, IReadOnlyList<PullRequestLink> shipped, RunningBuild build)
    {
        var here = $"Running in this Deskbar ({build.Version}).";
        var elsewhere = shipped.Where(l => !build.Builds(l)).Select(l => l.Key).ToHashSet();
        if (elsewhere.Count == 0) return here;
        var latest = task.Deployed.Where(d => elsewhere.Contains(d.Key)).MaxBy(d => d.At);
        var deployed = $"Deployed: {latest?.Note}";
        return shipped.Any(build.Builds) ? $"{deployed} {here}" : deployed;
    }

    // The hover line for one pull request that does not run yet.
    static string Waiting(PullRequestLink link, PullRequestArrival fact, RunningBuild build) =>
        fact.State == PullRequestState.Open ? $"{link.Label} is not merged yet."
        : build.Builds(link) ? $"Merged in {link.Label}, not in this Deskbar ({build.Version}) yet; it ships with the next release."
        : $"Merged in {link.Label}, not reported deployed yet.";

    /// <summary>A task's recorded pull requests, each once, in any repository (see the header), whatever its status.</summary>
    public static IReadOnlyList<PullRequestLink> Watched(TaskItem task) =>
        task.PullRequests.DistinctBy(l => l.Key).ToArray();

    /// <summary>
    /// The pull requests the watch should ask GitHub about now: those of open tasks, Done or not (Watched; an open task
    /// shows the merged ring too, task 1afbd44f), each once, that have no
    /// settled answer yet, a reported one included: a report does not fill a pull request GitHub says is still open
    /// (Of), so its state is still wanted, and asked once it costs one gh call. Only an open one is ever asked again,
    /// since it can still merge. A
    /// merged or closed one is settled for good: whether the running build holds a merge cannot change until Deskbar
    /// restarts, and a restart starts the watch afresh with nothing known; for another repository, what is left to know
    /// after the merge is the report, which arrives on the board, not from GitHub.
    /// </summary>
    public static IReadOnlyList<PullRequestLink> ToLook(TaskBoard board, RunningBuild build, IReadOnlyDictionary<string, PullRequestArrival> facts) =>
        board.Tasks
            .Where(t => t.Status != TaskStatus.Closed)
            .SelectMany(Watched)
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
/// it was built from (hex, as short as the version carries it; GitHub's compare takes a short one).
/// </summary>
public sealed record RunningBuild(string Owner, string Repo, string Version, string Commit)
{
    /// <summary>Whether the pull request is in this build's repository (owner and name without case, as GitHub's are).</summary>
    public bool Builds(PullRequestLink link) =>
        link.Owner.Equals(Owner, StringComparison.OrdinalIgnoreCase) && link.Repo.Equals(Repo, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The running build from the app's InformationalVersion, "2.6.23+&lt;commit&gt;". Nerdbank.GitVersioning appends the
    /// commit SHORTENED to ten hex digits (measured 2026-11-26: a worktree build says "2.6.95+a1bb17c550", the installed
    /// 2.6.28 says "2.6.28+5051c48433"), and GitHub's compare takes a short commit as it takes a full one (measured with
    /// eight digits), so anything from seven to forty hex digits is the commit. None when there is no '+' or what follows
    /// is not such a commit: that build cannot be compared, and every task keeps the full tick.
    /// </summary>
    public static Maybe<RunningBuild> Of(string owner, string repo, string informationalVersion)
    {
        var plus = informationalVersion.IndexOf('+');
        if (plus <= 0) return Maybe<RunningBuild>.None;
        // Anything after the commit (a '.' and more metadata) is not part of it.
        var commit = new string(informationalVersion[(plus + 1)..].TakeWhile(Uri.IsHexDigit).ToArray());
        return commit.Length is >= 7 and <= 40
            ? new RunningBuild(owner, repo, informationalVersion[..plus], commit.ToLowerInvariant())
            : Maybe<RunningBuild>.None;
    }
}

/// <summary>
/// What GitHub says about one pull request against the running build: its state, and whether the running commit holds
/// its merge (false unless merged, and always false for another repository's, which has no running commit to compare
/// with: RunningChanges' header). <see cref="Settled"/> answers are never asked again in this run.
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
    /// <summary>Not a change Deskbar can check, or one it cannot tell about (its look failed): the full tick, as before.</summary>
    None,

    /// <summary>
    /// Its pull requests have not been asked about yet (task 8db4c78e): the hollow ring until the answer lands, so a task
    /// that just turned Done never shows the full disc, which says its change runs, before anything was measured.
    /// </summary>
    Checking,

    /// <summary>A recorded pull request is still open: the hollow ring, and the chip says so.</summary>
    Open,

    /// <summary>Merged but not running: not in the running build, or (another repository) not reported deployed. The hollow ring.</summary>
    Merged,

    /// <summary>Every pull request it recorded runs (in the running build, or reported deployed): the full tick.</summary>
    Running,

    /// <summary>
    /// Merged, and its session cannot tell whether it was deployed (task 485ba96b, TaskItem.DeployUnknown): a mark of its
    /// own, and the note is the session's reason. Counts as Shipping, so whatever has no look of its own for it yet keeps
    /// the merged ring. Added last, so no stored or traced number of the others changes.
    /// </summary>
    Unchecked,
}

/// <summary>A task's delivery stage and the line its hover text adds (empty for None).</summary>
public sealed record TaskDelivery(DeliveryStage Stage, string Note)
{
    public static readonly TaskDelivery None = new(DeliveryStage.None, "");

    /// <summary>Whether the change is not known to run (Checking, Open, Merged or Unchecked): the ring instead of the disc.</summary>
    public bool Shipping => Stage is DeliveryStage.Checking or DeliveryStage.Open or DeliveryStage.Merged or DeliveryStage.Unchecked;

    /// <summary>Whether its session cannot tell whether the change was deployed (DeliveryStage.Unchecked, task 485ba96b).</summary>
    public bool Unchecked => Stage == DeliveryStage.Unchecked;
}
