using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
using Xunit;
// System.Threading.Tasks (an implicit using) has a TaskStatus too; the alias says which one this file means.
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests.Tasks;

// DESKBAR WAKES THE MANAGER (docs/superpowers/specs/2026-11-25-manager-event-wakes-design.md): batched, at most once per
// 30 minutes, only for a hand-back (task 6a87df67 dropped the questions), never while the manager is busy, only today's
// manager.
public sealed class ManagerWakesTests
{
    static readonly DateOnly today = new(2026, 11, 25);
    static readonly DateTimeOffset t0 = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);
    const string Manager = "aaaaaaaa-1111-2222-3333-444444444444";
    static readonly Guid id1 = Guid.Parse("11111111-0000-0000-0000-000000000001");
    static readonly Guid id2 = Guid.Parse("22222222-0000-0000-0000-000000000002");

    static ManagerRecord Record(DateTimeOffset? since = null, IReadOnlyList<Guid>? handedBack = null) =>
        new(Manager, today, since ?? t0, HandedBack: handedBack);

    static DateTimeOffset At(int minutes) => t0.AddMinutes(minutes);

    static TaskItem Task(Guid id, string title, TaskStatus status = TaskStatus.InProgress) =>
        new(id, title, Maybe<Guid>.None, status, "", Maybe<string>.None, t0.AddDays(-1), t0.AddDays(-1), t0.AddDays(-1), [], []);

    // A Claude task handed back to the user at `at`, with the status line its session left.
    static TaskItem HandedBack(Guid id, DateTimeOffset at, string statusLine = "PR 512 green, ready to merge") =>
        Task(id, "Tasks redesign") with { HandedBack = at, StatusLine = statusLine };

    static TaskItem Waiting(Guid id, DateTimeOffset since, string question = "Merge PR 512?") =>
        Task(id, "Bar blink", TaskStatus.WaitingOnYou) with { Question = question, WaitingSince = since };

    static TaskBoard Board(params TaskItem[] tasks) => new(tasks);

    static ClaudeAgent Agent(bool busy) => new(Manager, Manager[..8], @"C:\state\manager", true, busy, ManagerSession.Name);

    static ManagerWake Plan(ManagerRecord record, TaskBoard board, DateTimeOffset now, IReadOnlyCollection<ClaudeAgent>? listed = null,
        IReadOnlyDictionary<string, PullRequestArrival>? pullRequests = null) =>
        ManagerWakes.Plan(record, board, listed ?? [], today, now, pullRequests ?? new Dictionary<string, PullRequestArrival>());

    [Fact]
    public void Yesterdays_manager_is_never_woken()
    {
        var record = Record() with { Day = today.AddDays(-1) };
        Assert.Equal(new ManagerWake.Nothing(record), Plan(record, Board(HandedBack(id1, At(10))), At(60)));
    }

    [Fact]
    public void A_record_without_since_takes_now_and_does_not_wake()
    {
        var record = new ManagerRecord(Manager, today);
        var plan = Plan(record, Board(HandedBack(id1, At(-60))), At(60));
        Assert.Equal(new ManagerWake.Nothing(record with { Since = At(60) }), plan);
    }

    // ManagerQuestion.AfterPrompt tells a wake from the user's reply by this prefix, so every wake must start with it.
    [Fact]
    public void A_wakes_message_starts_with_the_prefix()
    {
        var plan = Assert.IsType<ManagerWake.Wake>(Plan(Record(), Board(HandedBack(id1, At(10))), At(30)));
        Assert.StartsWith(ManagerWakes.Prefix, plan.Message);
    }

    [Fact]
    public void A_hand_back_after_since_wakes_with_its_status_line()
    {
        var plan = Assert.IsType<ManagerWake.Wake>(Plan(Record(), Board(HandedBack(id1, At(10))), At(30)));
        Assert.Contains("11111111 \"Tasks redesign\"", plan.Message);
        Assert.Contains("PR 512 green, ready to merge", plan.Message);
    }

    [Fact]
    public void A_hand_back_before_since_does_not_wake() =>
        Assert.IsType<ManagerWake.Nothing>(Plan(Record(), Board(HandedBack(id1, At(-5))), At(60)));

    [Fact]
    public void A_task_taken_back_by_a_busy_session_is_not_reported() =>
        // TakeBack clears HandedBack, so a session that went back to work is no event.
        Assert.IsType<ManagerWake.Nothing>(Plan(Record(), Board(Task(id1, "Tasks redesign")), At(60)));

    [Fact]
    public void Within_thirty_minutes_of_since_it_waits()
    {
        var record = Record();
        Assert.Equal(new ManagerWake.Nothing(record), Plan(record, Board(HandedBack(id1, At(10))), At(29)));
    }

    [Fact]
    public void A_busy_manager_is_not_woken_and_keeps_since()
    {
        var record = Record();
        Assert.Equal(new ManagerWake.Nothing(record), Plan(record, Board(HandedBack(id1, At(10))), At(90), [Agent(busy: true)]));
    }

    [Fact]
    public void An_idle_listed_manager_is_woken() =>
        Assert.IsType<ManagerWake.Wake>(Plan(Record(), Board(HandedBack(id1, At(10))), At(90), [Agent(busy: false)]));

    // Task 6a87df67: "tasks do it themselves already on the bar". A waiting task's question is on the bar and in the tasks
    // panel, so a question never wakes the manager, however old, and is not carried along with a hand-back either.
    [Fact]
    public void A_question_never_wakes_however_old() =>
        // Asked at 09:10, two hours old at 11:10: before 6a87df67 this woke the manager.
        Assert.IsType<ManagerWake.Nothing>(Plan(Record(), Board(Waiting(id1, At(10))), At(600)));

    [Fact]
    public void A_question_is_not_carried_along_with_a_hand_back()
    {
        // Two hours old by minute 60 (the old trigger) and asked after since (the old "new questions" rider).
        var aged = Waiting(id2, At(-90), "Merge PR 512?");
        var fresh = Waiting(Guid.Parse("33333333-0000-0000-0000-000000000003"), At(20), "Ship it?");
        var wake = Assert.IsType<ManagerWake.Wake>(Plan(Record(), Board(HandedBack(id1, At(10)), aged, fresh), At(60)));
        Assert.DoesNotContain("22222222", wake.Message);
        Assert.DoesNotContain("33333333", wake.Message);
        Assert.DoesNotContain("Merge PR 512?", wake.Message);
    }

    [Fact]
    public void An_answered_or_closed_task_is_not_reported()
    {
        var answered = Waiting(id2, At(-60)) with { Status = TaskStatus.InProgress };
        var closed = HandedBack(id1, At(10)) with { Status = TaskStatus.Closed };
        Assert.IsType<ManagerWake.Nothing>(Plan(Record(), Board(answered, closed), At(60)));
    }

    [Fact]
    public void A_snoozed_task_is_not_reported() =>
        Assert.IsType<ManagerWake.Nothing>(Plan(Record(), Board(HandedBack(id1, At(10)) with { SnoozedUntil = At(600) }), At(60)));

    [Fact]
    public void A_first_hand_back_moves_since_and_remembers_it_without_the_dot()
    {
        // The nudge is the manager's own work, with nothing for the user, so the Manager button gets no dot.
        var wake = Assert.IsType<ManagerWake.Wake>(Plan(Record(), Board(HandedBack(id1, At(10))), At(45)));
        Assert.Equal(Record(At(45), [id1]), wake.Record);
    }

    [Fact]
    public void A_repeat_hand_back_lights_the_dot_and_a_lit_dot_stays()
    {
        var again = Assert.IsType<ManagerWake.Wake>(Plan(Record(handedBack: [id1]), Board(HandedBack(id1, At(10))), At(45)));
        Assert.True(again.Record.Unread);
        // A later first hand-back does not put out a dot the user has not seen yet.
        var lit = Record(handedBack: [id1]) with { Unread = true };
        var next = Assert.IsType<ManagerWake.Wake>(Plan(lit, Board(HandedBack(id2, At(10))), At(45)));
        Assert.True(next.Record.Unread);
    }

    // TASK 43765c61: a deploy reported wakes the manager to tell the sessions waiting on the same repository's deploy.

    static readonly Guid reporter = Guid.Parse("44444444-0000-0000-0000-000000000004");
    static readonly PullRequestLink portal1 = new("AppifySheets", "portal", 301);
    static readonly PullRequestLink portal2 = new("AppifySheets", "portal", 302);
    static readonly PullRequestLink api1 = new("AppifySheets", "api", 7);

    static TaskSession Session(string id) => new(id, @"E:\repos\portal", t0.AddDays(-1), Maybe<DateTimeOffset>.None);

    // A task whose session reported `link` deployed at `at`, with that evidence.
    static TaskItem Reporter(DateTimeOffset at, PullRequestLink? link = null) =>
        Task(reporter, "Activity chips", TaskStatus.Done) with
        {
            Sessions = [Session("s-reporter")],
            PullRequests = [link ?? portal1],
            Deployed = [new DeployedPullRequest((link ?? portal1).Key, at, "2026.11.27.23 in the portal log")],
        };

    // A task with a session and a pull request, not reported deployed.
    static TaskItem WaitingOnDeploy(Guid id, PullRequestLink link, string title = "Portal accounts") =>
        Task(id, title, TaskStatus.WaitingOnYou) with { Sessions = [Session("s-" + id)], PullRequests = [link], Question = "Redeploy the portal" };

    static IReadOnlyDictionary<string, PullRequestArrival> Merged(params PullRequestLink[] links) =>
        links.ToDictionary(l => l.Key, _ => new PullRequestArrival(PullRequestState.Merged, false));

    [Fact]
    public void A_deploy_wakes_with_the_tasks_merged_in_the_same_repository()
    {
        var wake = Assert.IsType<ManagerWake.Wake>(
            Plan(Record(), Board(Reporter(At(10)), WaitingOnDeploy(id1, portal2)), At(45), pullRequests: Merged(portal1, portal2)));
        Assert.Contains("appifysheets/portal, reported by 44444444 \"Activity chips\"", wake.Message);
        Assert.Contains("2026.11.27.23 in the portal log", wake.Message);
        Assert.Contains("11111111 \"Portal accounts\"", wake.Message);
        Assert.Contains("report task deployed", wake.Message);
        Assert.StartsWith(ManagerWakes.Prefix, wake.Message);
    }

    [Fact]
    public void A_deploy_moves_since_without_the_dot_or_a_hand_back()
    {
        var wake = Assert.IsType<ManagerWake.Wake>(
            Plan(Record(), Board(Reporter(At(10)), WaitingOnDeploy(id1, portal2)), At(45), pullRequests: Merged(portal1, portal2)));
        Assert.Equal(Record(At(45), []), wake.Record);
    }

    [Fact]
    public void A_deploy_nobody_waits_on_wakes_nothing() =>
        Assert.IsType<ManagerWake.Nothing>(Plan(Record(), Board(Reporter(At(10))), At(45), pullRequests: Merged(portal1)));

    [Fact]
    public void A_deploy_before_since_wakes_nothing() =>
        Assert.IsType<ManagerWake.Nothing>(
            Plan(Record(), Board(Reporter(At(-5)), WaitingOnDeploy(id1, portal2)), At(45), pullRequests: Merged(portal1, portal2)));

    [Fact]
    public void A_deploy_waits_for_the_gap() =>
        Assert.IsType<ManagerWake.Nothing>(
            Plan(Record(), Board(Reporter(At(10)), WaitingOnDeploy(id1, portal2)), At(20), pullRequests: Merged(portal1, portal2)));

    // Only a merged pull request can be in a deploy: an open one, or one GitHub has not answered for yet, is not waiting.
    [Fact]
    public void A_task_whose_pull_request_is_not_known_merged_is_not_waiting()
    {
        var open = new Dictionary<string, PullRequestArrival>
        {
            [portal1.Key] = new(PullRequestState.Merged, false),
            [portal2.Key] = new(PullRequestState.Open, false),
        };
        Assert.IsType<ManagerWake.Nothing>(Plan(Record(), Board(Reporter(At(10)), WaitingOnDeploy(id1, portal2)), At(45), pullRequests: open));
        Assert.IsType<ManagerWake.Nothing>(
            Plan(Record(), Board(Reporter(At(10)), WaitingOnDeploy(id1, portal2)), At(45), pullRequests: Merged(portal1)));
    }

    [Fact]
    public void Another_repositorys_task_is_not_waiting() =>
        Assert.IsType<ManagerWake.Nothing>(
            Plan(Record(), Board(Reporter(At(10)), WaitingOnDeploy(id1, api1)), At(45), pullRequests: Merged(portal1, api1)));

    [Fact]
    public void A_task_already_reported_deployed_is_not_waiting()
    {
        var reported = WaitingOnDeploy(id1, portal2) with { Deployed = [new DeployedPullRequest(portal2.Key, At(-60), "earlier")] };
        Assert.IsType<ManagerWake.Nothing>(Plan(Record(), Board(Reporter(At(10)), reported), At(45), pullRequests: Merged(portal1, portal2)));
    }

    // The comment is what wakes a session: a task with no session, a closed one or a snoozed one is not listed.
    [Fact]
    public void A_task_without_a_session_closed_or_snoozed_is_not_waiting()
    {
        var states = Merged(portal1, portal2);
        Assert.IsType<ManagerWake.Nothing>(
            Plan(Record(), Board(Reporter(At(10)), WaitingOnDeploy(id1, portal2) with { Sessions = [] }), At(45), pullRequests: states));
        Assert.IsType<ManagerWake.Nothing>(
            Plan(Record(), Board(Reporter(At(10)), WaitingOnDeploy(id1, portal2) with { Status = TaskStatus.Closed }), At(45), pullRequests: states));
        Assert.IsType<ManagerWake.Nothing>(
            Plan(Record(), Board(Reporter(At(10)), WaitingOnDeploy(id1, portal2) with { SnoozedUntil = At(600) }), At(45), pullRequests: states));
    }

    // A Done task's session already settled its deploy (merged, released, deployed, done), and a comment would resume an
    // old session on every deploy of the repository. A recurring task between runs would start a whole new run.
    [Fact]
    public void A_done_task_or_one_between_runs_is_not_waiting()
    {
        var states = Merged(portal1, portal2);
        var repeat = new TaskRepeat(TaskRepeats.Parse("every 1h 09:00-19:00").Value, "sonnet", @"E:\repos\portal");
        Assert.IsType<ManagerWake.Nothing>(
            Plan(Record(), Board(Reporter(At(10)), WaitingOnDeploy(id1, portal2) with { Status = TaskStatus.Done }), At(45), pullRequests: states));
        Assert.IsType<ManagerWake.Nothing>(Plan(Record(),
            Board(Reporter(At(10)), WaitingOnDeploy(id1, portal2) with { Status = TaskStatus.Backlog, Repeat = repeat }), At(45), pullRequests: states));
    }

    // The briefing tells a session whose change nothing runs to report "nothing to deploy": that is no deploy.
    [Fact]
    public void A_nothing_to_deploy_report_wakes_nothing()
    {
        var docs = Reporter(At(10)) with { Deployed = [new DeployedPullRequest(portal1.Key, At(10), "Nothing to deploy: docs")] };
        Assert.IsType<ManagerWake.Nothing>(Plan(Record(), Board(docs, WaitingOnDeploy(id1, portal2)), At(45), pullRequests: Merged(portal1, portal2)));
    }

    // One report stamps every pull request of the task: the wake says which repositories it covered.
    [Fact]
    public void A_report_covering_two_repositories_says_so()
    {
        var both = Reporter(At(10)) with
        {
            PullRequests = [portal1, api1],
            Deployed = [new DeployedPullRequest(portal1.Key, At(10), "portal log"), new DeployedPullRequest(api1.Key, At(10), "portal log")],
        };
        var wake = Assert.IsType<ManagerWake.Wake>(
            Plan(Record(), Board(both, WaitingOnDeploy(id1, portal2)), At(45), pullRequests: Merged(portal1, portal2, api1)));
        Assert.Contains("One report for appifysheets/api, appifysheets/portal, so it may be another's deploy.", wake.Message);
    }

    // A session marks done right after its report and the user may close the task at once: the report still counts.
    [Fact]
    public void A_closed_reporters_deploy_still_wakes() =>
        Assert.IsType<ManagerWake.Wake>(
            Plan(Record(), Board(Reporter(At(10)) with { Status = TaskStatus.Closed }, WaitingOnDeploy(id1, portal2)), At(45),
                pullRequests: Merged(portal1, portal2)));

    [Fact]
    public void A_deploy_rides_along_with_a_hand_back()
    {
        var wake = Assert.IsType<ManagerWake.Wake>(Plan(Record(),
            Board(HandedBack(id2, At(15)), Reporter(At(10)), WaitingOnDeploy(id1, portal2)), At(45), pullRequests: Merged(portal1, portal2)));
        Assert.Contains("22222222 \"Tasks redesign\"", wake.Message);
        Assert.Contains("11111111 \"Portal accounts\"", wake.Message);
        Assert.Equal([id2], wake.Record.HandedBack);
    }

    [Fact]
    public void A_hand_back_alone_has_no_deploy_lines()
    {
        var wake = Assert.IsType<ManagerWake.Wake>(Plan(Record(), Board(HandedBack(id1, At(10))), At(45)));
        Assert.DoesNotContain("Deployed", wake.Message);
        Assert.DoesNotContain("report task deployed", wake.Message);
    }

    [Fact]
    public void A_second_hand_back_today_says_do_not_nudge()
    {
        var wake = Assert.IsType<ManagerWake.Wake>(Plan(Record(handedBack: [id1]), Board(HandedBack(id1, At(10))), At(45)));
        Assert.Contains("handed back AGAIN today. Do not nudge", wake.Message);
        Assert.DoesNotContain("PR 512 green", wake.Message);
        Assert.Equal([id1], wake.Record.HandedBack);
    }
}
