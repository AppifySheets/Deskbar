using System.Collections.Immutable;
using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
using Xunit;
// System.Threading.Tasks (an implicit using) has a TaskStatus too; the alias says which one this file means.
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests.Tasks;

// TASK 6a4104c0: a task waiting on the user for a deploy had its session sit idle for an hour after the user deployed,
// because the session set no check of its own. Deskbar now wakes the idle session on a backing-off schedule (DeployWaits).
public sealed class DeployWaitsTests
{
    static readonly DateTimeOffset asked = new(2026, 11, 27, 15, 44, 0, TimeSpan.Zero);
    // Deskbar started long before the ask, unless a test says otherwise.
    static readonly DateTimeOffset started = asked.AddHours(-1);
    static readonly Guid id1 = Guid.Parse("11111111-0000-0000-0000-000000000001");
    static readonly PullRequestLink portal = new("AppifySheets", "portal", 579);
    const string Redeploy = "Mark stacks PR #309 ready and merge it, then Pull and redeploy the portal stack in Portainer.";

    static TaskSession Live(string id) => new(id, @"E:\repos\portal", asked.AddDays(-1), Maybe<DateTimeOffset>.None);
    static TaskSession Ended(string id) => new(id, @"E:\repos\portal", asked.AddDays(-1), asked.AddMinutes(-5));

    // A task waiting on the user with the question given, its pull request recorded and its session live.
    static TaskItem Waiting(string question = Redeploy, TaskSession? session = null) =>
        new TaskItem(id1, "Cycle tracker", Maybe<Guid>.None, TaskStatus.WaitingOnYou, "", Maybe<string>.From(question),
            asked.AddDays(-1), asked, asked, [session ?? Live("s1")], [])
        with { PullRequests = [portal], WaitingSince = asked };

    static readonly IReadOnlyDictionary<string, PullRequestArrival> merged =
        new Dictionary<string, PullRequestArrival> { [portal.Key] = new(PullRequestState.Merged, false) };

    static bool NotSelf(PullRequestLink link) => false;

    static readonly IReadOnlySet<string> nobodyBusy = new HashSet<string>();

    static DeployWaitStep Step(TaskItem task, DateTimeOffset now, ImmutableDictionary<Guid, DateTimeOffset>? nudged = null,
        IReadOnlySet<string>? busy = null, IReadOnlyDictionary<string, PullRequestArrival>? states = null, DateTimeOffset? since = null) =>
        DeployWaits.Step(new TaskBoard([task]), busy ?? nobodyBusy, states ?? merged, NotSelf, now, since ?? started,
            nudged ?? ImmutableDictionary<Guid, DateTimeOffset>.Empty);

    [Fact]
    public void The_schedule_backs_off_from_15_minutes_to_every_2_hours()
    {
        Assert.Null(DeployWaits.LatestSlot(asked, asked.AddMinutes(14)));
        Assert.Equal(asked.AddMinutes(15), DeployWaits.LatestSlot(asked, asked.AddMinutes(15)));
        Assert.Equal(asked.AddMinutes(15), DeployWaits.LatestSlot(asked, asked.AddMinutes(44)));
        Assert.Equal(asked.AddMinutes(45), DeployWaits.LatestSlot(asked, asked.AddMinutes(45)));
        Assert.Equal(asked.AddMinutes(105), DeployWaits.LatestSlot(asked, asked.AddMinutes(224)));
        Assert.Equal(asked.AddMinutes(225), DeployWaits.LatestSlot(asked, asked.AddMinutes(225)));
        Assert.Equal(asked.AddMinutes(105 + 120 * 40), DeployWaits.LatestSlot(asked, asked.AddMinutes(105 + 120 * 40 + 119)));
    }

    // The iam.ge case: asked at 15:44, the user deployed, and the session is woken at 15:59 to look.
    [Fact]
    public void An_idle_session_waiting_on_a_deploy_is_woken_15_minutes_after_the_ask()
    {
        Assert.Empty(Step(Waiting(), asked.AddMinutes(14)).Nudge);
        var step = Step(Waiting(), asked.AddMinutes(15));
        Assert.Equal([id1], step.Nudge);
        Assert.Equal(asked.AddMinutes(15), step.Nudged[id1]);
    }

    [Fact]
    public void A_slot_is_nudged_once_and_the_next_comes_30_minutes_later()
    {
        var first = Step(Waiting(), asked.AddMinutes(15));
        Assert.Empty(Step(Waiting(), asked.AddMinutes(30), first.Nudged).Nudge);
        Assert.Equal([id1], Step(Waiting(), asked.AddMinutes(45), first.Nudged).Nudge);
    }

    // A busy session is at work already; its slot is taken once it is quiet, not lost.
    [Fact]
    public void A_busy_session_is_woken_once_it_is_quiet()
    {
        var busy = new HashSet<string> { "s1" };
        var during = Step(Waiting(), asked.AddMinutes(16), busy: busy);
        Assert.Empty(during.Nudge);
        Assert.Equal([id1], Step(Waiting(), asked.AddMinutes(20), during.Nudged).Nudge);
    }

    // A release restarts Deskbar; every task waiting on a deploy must not wake at once.
    [Fact]
    public void A_slot_from_before_Deskbar_started_is_not_nudged()
    {
        var since = asked.AddMinutes(20);
        Assert.Empty(Step(Waiting(), asked.AddMinutes(21), since: since).Nudge);
        Assert.Equal([id1], Step(Waiting(), asked.AddMinutes(45), since: since).Nudge);
    }

    // A task waiting on the user for something else (clinical questions, with an earlier pull request merged) is not woken.
    [Fact]
    public void A_question_that_does_not_mention_a_deploy_is_not_checked()
    {
        Assert.Empty(Step(Waiting("Answer the three clinical questions in PR 578 and merge it."), asked.AddHours(1)).Nudge);
    }

    [Fact]
    public void A_task_with_no_merged_change_waiting_for_a_deploy_is_not_checked()
    {
        var open = new Dictionary<string, PullRequestArrival> { [portal.Key] = new(PullRequestState.Open, false) };
        Assert.Empty(Step(Waiting(), asked.AddHours(1), states: open).Nudge);
        Assert.Empty(Step(Waiting(), asked.AddHours(1), states: new Dictionary<string, PullRequestArrival>()).Nudge);
        var reported = Waiting() with { Deployed = [new DeployedPullRequest(portal.Key, asked.AddMinutes(5), "2026.11.27.53 in the log")] };
        Assert.Empty(Step(reported, asked.AddHours(1)).Nudge);
    }

    // Resuming a dead session every two hours for good costs more than the check is worth.
    [Fact]
    public void A_task_whose_session_has_ended_is_not_checked()
    {
        Assert.Empty(Step(Waiting(session: Ended("s1")), asked.AddHours(1)).Nudge);
    }

    // Review finding: a stop-then-resume delivery, a VS Code reload or a snooze takes the session off the list for a poll.
    // Forgetting the slot then would nudge the same slot again once the session is back.
    [Fact]
    public void A_session_off_the_list_for_a_poll_is_not_nudged_twice_for_one_slot()
    {
        var first = Step(Waiting(), asked.AddMinutes(15));
        var gone = Step(Waiting(session: Ended("s1")), asked.AddMinutes(16), first.Nudged);
        Assert.Empty(gone.Nudge);
        Assert.Empty(Step(Waiting(), asked.AddMinutes(17), gone.Nudged).Nudge);
    }

    [Fact]
    public void A_closed_task_is_forgotten()
    {
        var first = Step(Waiting(), asked.AddMinutes(15));
        var closed = Waiting() with { Status = TaskStatus.Closed, Question = Maybe<string>.None };
        Assert.Empty(Step(closed, asked.AddMinutes(16), first.Nudged).Nudged);
    }

    // The check is Deskbar's, not the user replying: read as a reply it would set aside the question it checks.
    [Fact]
    public void The_check_is_known_as_Deskbars_and_is_more_than_a_Stop_nudge()
    {
        var text = SessionBriefing.DeployCheck(Waiting());
        Assert.True(SessionBriefing.DeliveredByDeskbar(text));
        Assert.False(SessionBriefing.FromTheUser(text));
        Assert.Contains("task answered", text);
        Assert.Contains("task deployed", text);
        Assert.Contains(Redeploy, text);
    }
}
