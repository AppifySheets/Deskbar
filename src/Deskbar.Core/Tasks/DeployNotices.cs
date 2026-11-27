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
// evidence may speak for one; the message names the repositories and the waiting session checks either way. Of those
// stamps only the MERGED pull requests make their repository deployed (an open one cannot have shipped), and never one
// of Deskbar's own repository, which the running build decides (selfChecked). One deploy confirmed by several tasks in
// turn tells each waiting task once an hour per repository (Quiet, NotToldRecently), not once per confirmation.
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
    /// <param name="selfChecked">
    /// Whether a pull request belongs to the repository of the build Deskbar runs (RunningBuild.Builds): Deskbar decides
    /// those from the running build and never from a report (RunningChanges' header), so they neither make a deploy nor
    /// wait on one. Without this, a report on a task holding a Deskbar pull request would wake every open Deskbar task with
    /// a merged one, each of whose reports would wake the rest again.
    /// </param>
    public static IReadOnlyList<DeployNotice> For(TaskBoard before, TaskBoard after, Guid reporterId,
        IReadOnlyDictionary<string, PullRequestArrival> pullRequests, Func<PullRequestLink, bool> selfChecked)
    {
        if (after.Tasks.FirstOrDefault(t => t.Id == reporterId) is not { } reporter) return [];
        var earlier = before.Tasks.FirstOrDefault(t => t.Id == reporterId)?.Deployed ?? [];
        var fresh = reporter.Deployed.Where(d => !earlier.Contains(d)).ToList();
        if (fresh.Count == 0 || fresh[0].Note.TrimStart().StartsWith(NothingToDeploy, StringComparison.OrdinalIgnoreCase)) return [];

        // A report stamps every unreported pull request of the task, open ones too (TaskBoard.MarkDeployed). Only a MERGED
        // one can have gone out, so only its repository counts as deployed: an open infra pull request beside the app's
        // must not tell the infra repository's tasks that anything shipped there.
        var repositories = fresh
            .Select(d => reporter.PullRequests.FirstOrDefault(p => p.Key == d.Key))
            .Where(link => link is not null && !selfChecked(link) && IsMerged(link, pullRequests))
            .Select(link => RepositoryOf(link!.Key))
            .Distinct().Order(StringComparer.Ordinal).ToList();
        return after.Tasks
            .Where(t => t.Id != reporterId)
            .Select(t => new DeployNotice(t.Id, WaitingOn(t, repositories, pullRequests, selfChecked), reporter.Id, reporter.Title, fresh[0].Note))
            .Where(notice => notice.Repositories.Count > 0)
            .ToList();
    }

    /// <summary>
    /// Which of <paramref name="repositories"/> the task still waits on a deploy of (see WHO WAITS above): none for a task
    /// that cannot be waiting at all. Asked again at delivery, so a notice that waited in the queue is dropped once the
    /// task reported the deploy, finished, or was put aside meanwhile.
    /// </summary>
    public static IReadOnlyList<string> WaitingOn(TaskItem task, IReadOnlyList<string> repositories,
        IReadOnlyDictionary<string, PullRequestArrival> pullRequests, Func<PullRequestLink, bool> selfChecked) =>
        Waits(task)
            ? repositories.Where(r => task.PullRequests.Any(p => RepositoryOf(p.Key) == r && !selfChecked(p)
                && task.Deployed.All(d => d.Key != p.Key) && IsMerged(p, pullRequests))).ToList()
            : [];

    /// <summary>How long a task told about a repository's deploy is not told again about the same repository.</summary>
    // One deploy is often reported by several tasks in turn: each waiting session that finds its change running reports it
    // too. Without the window a task whose own change was not in it (another service, or a session that cannot be reached)
    // would be resumed once per confirming report. An hour covers the confirmations of one deploy; a deploy an hour later
    // is news again.
    public static readonly TimeSpan Quiet = TimeSpan.FromHours(1);

    /// <summary>
    /// The notices left once each task's repositories told within <see cref="Quiet"/> of <paramref name="now"/> are taken
    /// out (<paramref name="told"/>: when each task was last told about each repository).
    /// </summary>
    public static IReadOnlyList<DeployNotice> NotToldRecently(IReadOnlyList<DeployNotice> notices,
        IReadOnlyDictionary<(Guid Task, string Repository), DateTimeOffset> told, DateTimeOffset now) =>
        notices
            .Select(n => n with
            {
                Repositories = n.Repositories.Where(r => !told.TryGetValue((n.TaskId, r), out var at) || now - at >= Quiet).ToList(),
            })
            .Where(n => n.Repositories.Count > 0)
            .ToList();

    // Who can be waiting on a deploy at all (see WHO WAITS above).
    static bool Waits(TaskItem task) =>
        task.Sessions.Count > 0 && task.Status is not (TaskStatus.Done or TaskStatus.Closed) && !task.IsSnoozed && !task.IsBetweenRuns;

    static bool IsMerged(PullRequestLink link, IReadOnlyDictionary<string, PullRequestArrival> pullRequests) =>
        pullRequests.TryGetValue(link.Key, out var arrival) && arrival.State == PullRequestState.Merged;

    /// <summary>"owner/repo" from a PullRequestLink.Key ("owner/repo#12"), lower case like the key.</summary>
    public static string RepositoryOf(string key) => key.LastIndexOf('#') is var hash and > 0 ? key[..hash] : key;

    /// <summary>The Deskbar line in the waiting task's thread, so the user sees why its session woke.</summary>
    public static string ThreadLine(DeployNotice notice) =>
        $"{string.Join(", ", notice.Repositories)} was reported deployed by \"{notice.ReporterTitle}\"; asked Claude to check whether this task's change is in it.";
}
