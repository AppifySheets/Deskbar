namespace Deskbar.Core.Tasks;

/// <summary>
/// One task told about a deploy another task reported (DeployNotices.For): the repositories of that report it waits on,
/// the task that reported it and the report's evidence.
/// </summary>
public sealed record DeployNotice(Guid TaskId, IReadOnlyList<string> Repositories, Guid ReporterId, string ReporterTitle, string Evidence);

// A DEPLOY REPORTED REACHES THE SESSIONS WAITING ON IT (task 43765c61; the user: "when a session learns that its PR has
// been deployed, it should notify the manager and the manager should notify all sessions waiting for the deployment").
//
// The first build (PR 654, v2.6.62) went through the manager: the report woke it, and it commented each waiting task.
// That cost a Fable turn per wake, waited up to the manager's 30 minute gap, and did nothing on a day the user had not
// opened the manager, while Deskbar had already worked out the list itself. So Deskbar now tells the waiting sessions
// directly, at the moment the report lands (TaskService.MarkDeployed), and the manager is not involved (the user: "okay,
// go for it").
//
// The report itself is the notice: a session's `task deployed` (or the user's "Mark deployed") lands in TaskItem.Deployed
// with its time and evidence, so the reporting session runs nothing new.
//
// WHO WAITS. A deploy ships everything merged before it, so another task whose pull request in the SAME repository is
// merged (GitHub's word, from the running-change watch: TaskService's arrivals) but not reported deployed may be in it
// too. Only a task with a Claude session is told (the message is what wakes the session), and not one that is Done (its
// session settled its deploy already: merged, released, deployed, done; an older Done task without a report would
// otherwise be resumed on every deploy of its repository, for good), Closed, snoozed (put aside on purpose), or a
// recurring task between runs (a message there starts a whole new run). The session then checks its own app's evidence
// and reports `task deployed` itself: a deploy of one service does not prove another service of the same repository
// was redeployed.
//
// WHAT IS NOT A DEPLOY. A "nothing to deploy" report (the session briefing tells a session whose change nothing runs,
// docs or a library, to report exactly that) sends nobody looking. One report stamps every unreported pull request of
// the task at once (TaskBoard.MarkDeployed), so a task with pull requests in two repositories reports both, though its
// evidence may speak for one; the message names the repositories and the waiting session checks either way.
//
// Accepted gaps: GitHub's answers arrive in the minute or two after a Deskbar start, and a report made before a waiting
// task's pull request is known merged tells nobody. A pull request merged after the deploy is still told, and its
// session's check settles it.
//
// A PURE FUNCTION: the boards before and after the report, the reporting task and GitHub's answers come in.
public static class DeployNotices
{
    /// <summary>How a session reports a change nothing runs (the session briefing's words); such a report is no deploy.</summary>
    public const string NothingToDeploy = "nothing to deploy";

    /// <summary>
    /// The tasks to tell about the deploy <paramref name="reporterId"/> just reported: the pull requests stamped in
    /// <paramref name="after"/> and not in <paramref name="before"/>. None when nothing new was stamped, when the report
    /// says nothing was deployed, or when no other task waits on those repositories.
    /// </summary>
    /// <param name="pullRequests">What GitHub said about the recorded pull requests, keyed by PullRequestLink.Key.</param>
    public static IReadOnlyList<DeployNotice> For(TaskBoard before, TaskBoard after, Guid reporterId,
        IReadOnlyDictionary<string, PullRequestArrival> pullRequests)
    {
        if (after.Tasks.FirstOrDefault(t => t.Id == reporterId) is not { } reporter) return [];
        var earlier = before.Tasks.FirstOrDefault(t => t.Id == reporterId)?.Deployed ?? [];
        var fresh = reporter.Deployed.Where(d => !earlier.Contains(d)).ToList();
        if (fresh.Count == 0 || fresh[0].Note.TrimStart().StartsWith(NothingToDeploy, StringComparison.OrdinalIgnoreCase)) return [];

        var repositories = fresh.Select(d => RepositoryOf(d.Key)).Distinct().Order(StringComparer.Ordinal).ToList();
        return after.Tasks
            .Where(t => t.Id != reporterId && Waits(t))
            .Select(t => (Task: t, Repositories: repositories.Where(r => WaitsOn(t, r, pullRequests)).ToList()))
            .Where(w => w.Repositories.Count > 0)
            .Select(w => new DeployNotice(w.Task.Id, w.Repositories, reporter.Id, reporter.Title, fresh[0].Note))
            .ToList();
    }

    // Who can be waiting on a deploy at all (see WHO WAITS above).
    static bool Waits(TaskItem task) =>
        task.Sessions.Count > 0 && task.Status is not (TaskStatus.Done or TaskStatus.Closed) && !task.IsSnoozed && !task.IsBetweenRuns;

    // A pull request of `repository` that GitHub says is merged and the task has not reported deployed.
    static bool WaitsOn(TaskItem task, string repository, IReadOnlyDictionary<string, PullRequestArrival> pullRequests) =>
        task.PullRequests.Any(p => RepositoryOf(p.Key) == repository
            && task.Deployed.All(d => d.Key != p.Key)
            && pullRequests.TryGetValue(p.Key, out var arrival) && arrival.State == PullRequestState.Merged);

    /// <summary>"owner/repo" from a PullRequestLink.Key ("owner/repo#12"), lower case like the key.</summary>
    public static string RepositoryOf(string key) => key.LastIndexOf('#') is var hash and > 0 ? key[..hash] : key;

    /// <summary>The Deskbar line in the waiting task's thread, so the user sees why its session woke.</summary>
    public static string ThreadLine(DeployNotice notice) =>
        $"{string.Join(", ", notice.Repositories)} was reported deployed by \"{notice.ReporterTitle}\"; asked Claude to check whether this task's change is in it.";
}
