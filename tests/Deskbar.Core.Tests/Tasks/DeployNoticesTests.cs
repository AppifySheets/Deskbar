using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
using Xunit;
// System.Threading.Tasks (an implicit using) has a TaskStatus too; the alias says which one this file means.
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests.Tasks;

// TASK 43765c61: a deploy one task reports reaches the sessions of the other tasks whose merged pull request in the same
// repository may be in it (DeployNotices), straight from Deskbar, with no manager turn.
public sealed class DeployNoticesTests
{
    static readonly DateTimeOffset t0 = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);
    static readonly Guid reporter = Guid.Parse("44444444-0000-0000-0000-000000000004");
    static readonly Guid id1 = Guid.Parse("11111111-0000-0000-0000-000000000001");
    static readonly PullRequestLink portal1 = new("AppifySheets", "portal", 301);
    static readonly PullRequestLink portal2 = new("AppifySheets", "portal", 302);
    static readonly PullRequestLink api1 = new("AppifySheets", "api", 7);
    const string Evidence = "2026.11.27.23 in the portal log";

    static TaskSession Session(string id) => new(id, @"E:\repos\portal", t0.AddDays(-1), Maybe<DateTimeOffset>.None);

    static TaskItem Task(Guid id, string title, TaskStatus status, params PullRequestLink[] links) =>
        new TaskItem(id, title, Maybe<Guid>.None, status, "", Maybe<string>.None, t0.AddDays(-1), t0.AddDays(-1), t0.AddDays(-1),
            [Session("s-" + id)], []) with { PullRequests = links };

    static TaskItem Reporter(params PullRequestLink[] links) => Task(reporter, "Activity chips", TaskStatus.InProgress, links.Length > 0 ? links : [portal1]);

    // A task waiting on a redeploy: its pull request merged, no deploy reported.
    static TaskItem Waiting(PullRequestLink link, TaskStatus status = TaskStatus.WaitingOnYou) => Task(id1, "Portal accounts", status, link);

    static IReadOnlyDictionary<string, PullRequestArrival> Merged(params PullRequestLink[] links) =>
        links.ToDictionary(l => l.Key, _ => new PullRequestArrival(PullRequestState.Merged, false));

    // No pull request here belongs to the build Deskbar runs, unless a test says otherwise.
    static bool NotSelf(PullRequestLink link) => false;

    // The board before the report and after it, where the reporter's pull requests were stamped by TaskBoard.MarkDeployed.
    static IReadOnlyList<DeployNotice> Report(TaskItem reportingTask, TaskItem other, IReadOnlyDictionary<string, PullRequestArrival> states,
        string note = Evidence)
    {
        var before = new TaskBoard([reportingTask, other]);
        var after = before.MarkDeployed(reporter, note, t0).Value;
        return DeployNotices.For(before, after, reporter, states, NotSelf);
    }

    [Fact]
    public void A_deploy_tells_a_task_merged_in_the_same_repository()
    {
        var notice = Assert.Single(Report(Reporter(), Waiting(portal2), Merged(portal1, portal2)));
        Assert.Equal((id1, reporter, "Activity chips", Evidence), (notice.TaskId, notice.ReporterId, notice.ReporterTitle, notice.Evidence));
        Assert.Equal(["appifysheets/portal"], notice.Repositories);
    }

    [Fact]
    public void A_deploy_nobody_waits_on_tells_nobody()
    {
        Assert.Empty(Report(Reporter(), Waiting(api1), Merged(portal1, api1)));
    }

    // Only a merged pull request can be in a deploy: an open one, or one GitHub has not answered for, is not waiting.
    [Fact]
    public void A_pull_request_not_known_merged_is_not_waiting()
    {
        var open = new Dictionary<string, PullRequestArrival> { [portal2.Key] = new(PullRequestState.Open, false) };
        Assert.Empty(Report(Reporter(), Waiting(portal2), open));
        Assert.Empty(Report(Reporter(), Waiting(portal2), Merged(portal1)));
    }

    [Fact]
    public void A_task_already_reported_deployed_is_not_waiting()
    {
        var reported = Waiting(portal2) with { Deployed = [new DeployedPullRequest(portal2.Key, t0.AddHours(-1), "earlier")] };
        Assert.Empty(Report(Reporter(), reported, Merged(portal1, portal2)));
    }

    // The message is what wakes a session; a Done task already settled its deploy; a closed or snoozed one is put aside; a
    // recurring task between runs would start a whole new run.
    [Fact]
    public void A_task_without_a_session_done_closed_snoozed_or_between_runs_is_not_waiting()
    {
        var states = Merged(portal1, portal2);
        var repeat = new TaskRepeat(TaskRepeats.Parse("every 1h 09:00-19:00").Value, "sonnet", @"E:\repos\portal");
        Assert.Empty(Report(Reporter(), Waiting(portal2) with { Sessions = [] }, states));
        Assert.Empty(Report(Reporter(), Waiting(portal2, TaskStatus.Done), states));
        Assert.Empty(Report(Reporter(), Waiting(portal2, TaskStatus.Closed), states));
        Assert.Empty(Report(Reporter(), Waiting(portal2) with { SnoozedUntil = DateTimeOffset.MaxValue }, states));
        Assert.Empty(Report(Reporter(), Waiting(portal2, TaskStatus.Backlog) with { Repeat = repeat }, states));
    }

    // The briefing tells a session whose change nothing runs to report "nothing to deploy": that is no deploy.
    [Fact]
    public void A_nothing_to_deploy_report_tells_nobody() =>
        Assert.Empty(Report(Reporter(), Waiting(portal2), Merged(portal1, portal2), "Nothing to deploy: docs"));

    // A second report stamps nothing new (TaskBoard.MarkDeployed returns the same board), so nobody is told twice.
    [Fact]
    public void A_report_that_stamps_nothing_new_tells_nobody()
    {
        var board = new TaskBoard([Reporter(), Waiting(portal2)]).MarkDeployed(reporter, Evidence, t0).Value;
        var again = board.MarkDeployed(reporter, "again", t0.AddMinutes(5)).Value;
        Assert.Empty(DeployNotices.For(board, again, reporter, Merged(portal1, portal2), NotSelf));
    }

    // One report stamps every pull request of the task: a waiting task is told once, with the repositories it waits on.
    [Fact]
    public void A_report_covering_two_repositories_names_the_ones_the_task_waits_on()
    {
        var both = Task(id1, "Portal accounts", TaskStatus.InProgress, portal2, new PullRequestLink("AppifySheets", "api", 8));
        var notice = Assert.Single(Report(Reporter(portal1, api1), both, Merged(portal1, portal2, api1, new PullRequestLink("AppifySheets", "api", 8))));
        Assert.Equal(["appifysheets/api", "appifysheets/portal"], notice.Repositories);
    }

    // An open pull request stamped beside a merged one cannot have shipped, so its repository's tasks are not told.
    [Fact]
    public void A_stamped_pull_request_that_is_not_merged_makes_no_deploy()
    {
        var api2 = new PullRequestLink("AppifySheets", "api", 8);
        var states = new Dictionary<string, PullRequestArrival>
        {
            [portal1.Key] = new(PullRequestState.Merged, false),
            [api1.Key] = new(PullRequestState.Open, false),
            [api2.Key] = new(PullRequestState.Merged, false),
        };
        Assert.Empty(Report(Reporter(portal1, api1), Task(id1, "Api keys", TaskStatus.InProgress, api2), states));
    }

    // Deskbar's own pull requests are decided by the running build, never by a report: they neither make a deploy nor wait
    // on one, or one report would wake every open Deskbar task and each of their reports the rest.
    [Fact]
    public void The_running_builds_repository_neither_deploys_nor_waits()
    {
        var mine = new PullRequestLink("AppifySheets", "Deskbar", 654);
        var theirs = new PullRequestLink("AppifySheets", "Deskbar", 655);
        bool deskbar(PullRequestLink link) => link.Repo == "Deskbar";
        var before = new TaskBoard([Reporter(mine), Task(id1, "Bar blink", TaskStatus.InProgress, theirs)]);
        var after = before.MarkDeployed(reporter, "2.6.62 runs", t0).Value;
        Assert.Empty(DeployNotices.For(before, after, reporter, Merged(mine, theirs), deskbar));
        Assert.Empty(DeployNotices.WaitingOn(after.Tasks.Single(t => t.Id == id1), ["appifysheets/deskbar"], Merged(theirs), deskbar));
    }

    // One deploy confirmed by several tasks in turn tells each waiting task once an hour per repository.
    [Fact]
    public void A_task_told_within_the_hour_is_not_told_again()
    {
        var notice = new DeployNotice(id1, ["appifysheets/api", "appifysheets/portal"], reporter, "Activity chips", Evidence);
        var told = new Dictionary<(Guid, string), DateTimeOffset> { [(id1, "appifysheets/portal")] = t0 };
        Assert.Equal(["appifysheets/api"], Assert.Single(DeployNotices.NotToldRecently([notice], told, t0.AddMinutes(59))).Repositories);
        Assert.Equal(2, Assert.Single(DeployNotices.NotToldRecently([notice], told, t0.AddMinutes(60))).Repositories.Count);
        var onlyPortal = notice with { Repositories = ["appifysheets/portal"] };
        Assert.Empty(DeployNotices.NotToldRecently([onlyPortal], told, t0.AddMinutes(10)));
    }

    // Asked again at delivery: a task that reported the deploy meanwhile no longer waits.
    [Fact]
    public void A_task_that_reported_meanwhile_no_longer_waits()
    {
        var reported = Waiting(portal2) with { Deployed = [new DeployedPullRequest(portal2.Key, t0, "seen")] };
        Assert.Equal(["appifysheets/portal"], DeployNotices.WaitingOn(Waiting(portal2), ["appifysheets/portal"], Merged(portal2), NotSelf));
        Assert.Empty(DeployNotices.WaitingOn(reported, ["appifysheets/portal"], Merged(portal2), NotSelf));
    }

    [Fact]
    public void The_thread_line_names_the_repository_and_the_reporter() =>
        Assert.Equal("appifysheets/portal was reported deployed by \"Activity chips\"; asked Claude to check whether this task's change is in it.",
            DeployNotices.ThreadLine(new DeployNotice(id1, ["appifysheets/portal"], reporter, "Activity chips", Evidence)));

    // The session's message: what was deployed, by whom, with what evidence, and the commands for each outcome. It is
    // Deskbar's prompt, never the user replying (it must not set the task's question aside).
    [Fact]
    public void The_sessions_message_carries_the_deploy_and_is_deskbars_own()
    {
        var message = SessionBriefing.DeployNotice(Waiting(portal2), new DeployNotice(id1, ["appifysheets/portal"], reporter, "Activity chips", Evidence));
        Assert.Contains("Task 44444444 \"Activity chips\" reported appifysheets/portal deployed: \"2026.11.27.23 in the portal log\"", message);
        Assert.Contains("task deployed - --session", message);
        Assert.Contains("task answered --session", message);
        Assert.True(SessionBriefing.DeliveredByDeskbar(message));
    }
}
