using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests.Tasks;

// SessionKeeper.Plan is a pure function, so each rule is one board, one list of live sessions and one ledger.
// Review Focus 1 (a listed session is never resumed) and 3 (the grace) are named in the tests below.
public class SessionKeeperTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    static TaskSession Session(string id, int minutesAgo = 60, string folder = @"E:\repos\X") =>
        new(id, folder, Now.AddMinutes(-minutesAgo), Maybe<DateTimeOffset>.None);

    static TaskItem Task(TaskStatus status, int updatedMinutesAgo, params TaskSession[] sessions) =>
        new(Guid.NewGuid(), "t", Maybe<Guid>.None, status, "", Maybe<string>.None, Now.AddDays(-1),
            Now.AddMinutes(-updatedMinutesAgo), Now.AddDays(-1), sessions, Array.Empty<TaskComment>());

    static ClaudeAgent Agent(string sessionId, bool busy = false) =>
        new(sessionId, Maybe<string>.None, @"E:\repos\X", false, busy, "");

    static IReadOnlyList<KeeperStep> Plan(
        IEnumerable<TaskItem> tasks, IReadOnlyCollection<ClaudeAgent>? live = null, ResumeLedger? ledger = null,
        Func<TaskSession, bool>? transcriptExists = null, Func<TaskSession, bool>? cutOff = null) =>
        SessionKeeper.Plan(new TaskBoard(tasks.ToList()), live ?? Array.Empty<ClaudeAgent>(),
            ledger ?? ResumeLedger.Empty, transcriptExists ?? (_ => true), cutOff ?? (_ => false), Now);

    [Theory]
    [InlineData(TaskStatus.Backlog)]
    [InlineData(TaskStatus.InProgress)]
    [InlineData(TaskStatus.WaitingOnYou)]
    public void The_latest_session_of_an_open_task_that_is_not_listed_is_resumed(TaskStatus status)
    {
        var task = Task(status, 5, Session("s1"));

        var step = Assert.IsType<KeeperStep.Resume>(Assert.Single(Plan([task])));

        Assert.Equal(task.Id, step.TaskId);
        Assert.Equal("s1", step.Session.Id);
        Assert.False(step.SendContinue);
    }

    [Theory]
    [InlineData(TaskStatus.Done)]
    [InlineData(TaskStatus.Closed)]
    public void Done_and_closed_tasks_are_never_resumed(TaskStatus status) =>
        Assert.Empty(Plan([Task(status, 5, Session("s1"))]));

    [Fact]
    public void A_task_without_sessions_is_not_claudes_and_is_left_alone() =>
        Assert.Empty(Plan([Task(TaskStatus.Backlog, 5)]));

    [Fact]
    public void A_listed_session_is_not_resumed_even_when_done()
    {
        // Review Focus 1: SessionEnd arrives at the end of every turn while the list still shows the session.
        var task = Task(TaskStatus.InProgress, 5, Session("s1"));

        Assert.Empty(Plan([task], [Agent("s1")]));
        Assert.Empty(Plan([task], [Agent("s1", busy: true)]));
    }

    // Final review, item 9: the latest session is not always the live one (an older one rejoined, a second one started
    // and ended). Any listed session means the task is running, and waking the ended latest would add a second.
    [Fact]
    public void A_task_with_an_older_session_listed_is_left_alone()
    {
        var task = Task(TaskStatus.InProgress, 5, Session("old", 120), Session("new", 10));

        Assert.Empty(Plan([task], [Agent("old")]));
    }

    // Ruling 19: /clear ended that conversation on purpose. It is never resumed, and nothing is asked either.
    [Fact]
    public void A_session_replaced_by_clear_is_never_resumed()
    {
        var cleared = Session("s1") with { Ended = Now.AddMinutes(-30), ReplacedAt = Now.AddMinutes(-30) };

        Assert.Empty(Plan([Task(TaskStatus.InProgress, 5, cleared)]));
        Assert.Empty(Plan([Task(TaskStatus.InProgress, 5, cleared)], transcriptExists: _ => false));
    }

    // Ruling 18: a session missing from the list for less than two minutes may still come back by itself (VS Code
    // restoring or reloading its panel), so it gets no step, not even a give-up; from two minutes on it is resumed.
    [Fact]
    public void A_session_missing_for_less_than_two_minutes_gets_no_step()
    {
        var task = Task(TaskStatus.InProgress, 5, Session("s1"));
        var board = new TaskBoard([task]);
        IReadOnlyList<KeeperStep> PlanGoneFor(TimeSpan gone, Func<TaskSession, bool>? transcript = null) =>
            SessionKeeper.Plan(board, [], ResumeLedger.Empty, transcript ?? (_ => true), _ => false, Now, _ => Now - gone);

        Assert.Equal(TimeSpan.FromMinutes(2), SessionKeeper.Settle);
        Assert.Empty(PlanGoneFor(TimeSpan.Zero));
        Assert.Empty(PlanGoneFor(TimeSpan.FromSeconds(119)));
        Assert.Empty(PlanGoneFor(TimeSpan.FromSeconds(119), transcript: _ => false));
        Assert.IsType<KeeperStep.Resume>(Assert.Single(PlanGoneFor(TimeSpan.FromMinutes(2))));
        Assert.IsType<KeeperStep.GiveUp>(Assert.Single(PlanGoneFor(TimeSpan.FromMinutes(5), transcript: _ => false)));
    }

    [Fact]
    public void Only_the_latest_session_of_a_task_is_resumed()
    {
        var task = Task(TaskStatus.InProgress, 5, Session("old", 120), Session("new", 10), Session("mid", 60));

        var step = Assert.IsType<KeeperStep.Resume>(Assert.Single(Plan([task])));

        Assert.Equal("new", step.Session.Id);
    }

    [Fact]
    public void A_session_resumed_within_the_grace_is_left_alone()
    {
        // Review Focus 3: just after a resume the list may not show the session yet, or shows it busy while idle.
        var ledger = ResumeLedger.Empty.Record("s1", Now - SessionKeeper.Grace + TimeSpan.FromSeconds(1));

        Assert.Empty(Plan([Task(TaskStatus.InProgress, 5, Session("s1"))], ledger: ledger));
    }

    [Fact]
    public void A_session_resumed_longer_ago_than_the_grace_is_resumed_again()
    {
        var ledger = ResumeLedger.Empty.Record("s1", Now - SessionKeeper.Grace - TimeSpan.FromSeconds(1));

        Assert.IsType<KeeperStep.Resume>(Assert.Single(Plan([Task(TaskStatus.InProgress, 5, Session("s1"))], ledger: ledger)));
    }

    [Fact]
    public void A_cut_off_turn_is_resumed_with_continue()
    {
        var step = Assert.IsType<KeeperStep.Resume>(
            Assert.Single(Plan([Task(TaskStatus.InProgress, 5, Session("s1"))], cutOff: _ => true)));

        Assert.True(step.SendContinue);
    }

    [Fact]
    public void The_fourth_resume_within_ten_minutes_is_a_give_up_with_the_reason()
    {
        var ledger = ResumeLedger.Empty
            .Record("s1", Now.AddMinutes(-9)).Record("s1", Now.AddMinutes(-6)).Record("s1", Now.AddMinutes(-3));
        var task = Task(TaskStatus.InProgress, 5, Session("s1"));

        var step = Assert.IsType<KeeperStep.GiveUp>(Assert.Single(Plan([task], ledger: ledger)));

        Assert.Equal(task.Id, step.TaskId);
        Assert.Equal(
            "Deskbar stopped resuming this session: it ended again straight after 3 resumes in 10 minutes.",
            step.Reason);
        Assert.Equal(SessionKeeper.LoopGaveUp, step.Reason);
    }

    [Fact]
    public void Resumes_older_than_the_loop_window_do_not_count()
    {
        var ledger = ResumeLedger.Empty
            .Record("s1", Now.AddMinutes(-30)).Record("s1", Now.AddMinutes(-20)).Record("s1", Now.AddMinutes(-11))
            .Record("s1", Now.AddMinutes(-9));

        Assert.IsType<KeeperStep.Resume>(Assert.Single(Plan([Task(TaskStatus.InProgress, 5, Session("s1"))], ledger: ledger)));
    }

    [Fact]
    public void The_loop_give_up_is_said_once()
    {
        var ledger = ResumeLedger.Empty
            .Record("s1", Now.AddMinutes(-9)).Record("s1", Now.AddMinutes(-6)).Record("s1", Now.AddMinutes(-3));
        var task = Task(TaskStatus.WaitingOnYou, 5, Session("s1")) with { Question = SessionKeeper.LoopGaveUp };

        Assert.Empty(Plan([task], ledger: ledger));
    }

    [Fact]
    public void An_expired_transcript_is_a_give_up_once_not_a_resume()
    {
        var task = Task(TaskStatus.InProgress, 5, Session("s1"));

        var step = Assert.IsType<KeeperStep.GiveUp>(Assert.Single(Plan([task], transcriptExists: _ => false)));

        Assert.Equal(
            "Claude Code no longer has this session's transcript (it keeps sessions for 30 days), so it cannot be resumed.",
            step.Reason);
        Assert.Equal(SessionKeeper.TranscriptGone, step.Reason);

        // Once given up, the task waits on Petre with that reason as its question: no second step.
        var waiting = task with { Status = TaskStatus.WaitingOnYou, Question = SessionKeeper.TranscriptGone };
        Assert.Empty(Plan([waiting], transcriptExists: _ => false));
    }

    [Fact]
    public void A_session_without_a_folder_is_a_give_up_once()
    {
        var task = Task(TaskStatus.InProgress, 5, Session("s1", folder: ""));

        var step = Assert.IsType<KeeperStep.GiveUp>(Assert.Single(Plan([task])));

        Assert.Equal(
            "Deskbar cannot resume this session: Claude Code never said which folder it ran in.", step.Reason);
        Assert.Equal(SessionKeeper.NoFolder, step.Reason);
        Assert.Empty(Plan([task with { Status = TaskStatus.WaitingOnYou, Question = SessionKeeper.NoFolder }]));
    }

    [Fact]
    public void A_listed_session_no_task_knows_is_adopted()
    {
        var known = Task(TaskStatus.Done, 5, Session("s1"));

        var steps = Plan([known], [Agent("s1"), Agent("stranger")]);

        var step = Assert.IsType<KeeperStep.Adopt>(Assert.Single(steps));
        Assert.Equal("stranger", step.Agent.SessionId);
    }

    [Fact]
    public void A_session_closed_tasks_know_is_not_adopted() =>
        Assert.Empty(Plan([Task(TaskStatus.Closed, 5, Session("s1"))], [Agent("s1")]));

    [Fact]
    public void A_session_listed_twice_is_adopted_once() =>
        Assert.Single(Plan([], [Agent("x"), Agent("x")]));

    [Fact]
    public void A_session_on_two_tasks_is_resumed_once()
    {
        var first = Task(TaskStatus.InProgress, 1, Session("s1"));
        var second = Task(TaskStatus.InProgress, 9, Session("s1"));

        var step = Assert.IsType<KeeperStep.Resume>(Assert.Single(Plan([second, first])));

        Assert.Equal(first.Id, step.TaskId);
    }

    [Fact]
    public void Steps_come_in_task_order_waiting_first_so_answers_come_back_first()
    {
        var backlog = Task(TaskStatus.Backlog, 1, Session("b"));
        var progressOld = Task(TaskStatus.InProgress, 30, Session("p-old"));
        var progressNew = Task(TaskStatus.InProgress, 2, Session("p-new"));
        var waiting = Task(TaskStatus.WaitingOnYou, 50, Session("w"));

        var steps = Plan([backlog, progressOld, progressNew, waiting], [Agent("stranger")]);

        Assert.Equal(
            ["w", "p-new", "p-old", "b", "stranger"],
            steps.Select(s => s switch
            {
                KeeperStep.Resume r => r.Session.Id,
                KeeperStep.Adopt a => a.Agent.SessionId,
                _ => "?"
            }).ToList());
    }

    [Fact]
    public void The_ledger_forgets_what_the_rules_no_longer_need()
    {
        var ledger = ResumeLedger.Empty.Record("s1", Now.AddHours(-2)).Record("s1", Now);

        Assert.Single(ledger.Resumes["s1"]);
    }

    [Theory]
    [InlineData(SessionKeeper.LoopGaveUp)]
    [InlineData(SessionKeeper.TranscriptGone)]
    [InlineData(SessionKeeper.NoFolder)]
    [InlineData(SessionKeeper.ResumeFailed)]
    public void A_task_given_up_on_gets_no_step_even_after_the_loop_window(string reason)
    {
        var ledger = ResumeLedger.Empty
            .Record("s1", Now.AddMinutes(-9)).Record("s1", Now.AddMinutes(-6)).Record("s1", Now.AddMinutes(-3));
        var task = Task(TaskStatus.WaitingOnYou, 5, Session("s1")) with { Question = reason };

        // 11 minutes on, the oldest resume has left the window and the count is 2: still no step.
        var later = SessionKeeper.Plan(new TaskBoard([task]), [], ledger, _ => true, _ => false, Now.AddMinutes(11));

        Assert.Empty(later);
    }

    [Fact]
    public void After_forget_the_same_session_is_resumed_again()
    {
        var ledger = ResumeLedger.Empty
            .Record("s1", Now.AddMinutes(-9)).Record("s1", Now.AddMinutes(-6)).Record("s1", Now.AddMinutes(-3));
        var task = Task(TaskStatus.InProgress, 5, Session("s1"));

        Assert.IsType<KeeperStep.GiveUp>(Assert.Single(Plan([task], ledger: ledger)));
        Assert.IsType<KeeperStep.Resume>(Assert.Single(Plan([task], ledger: ledger.Forget("s1"))));
    }
    // --- Stops: finished tasks' idle background sessions (task 87f899ad) ------------------------------------------

    // A background row as the list gives it: busy and working during a turn, idle and done after one, unless the test
    // names the state (busy and done is a turn ended with a background job of its own still running).
    static ClaudeAgent Background(string sessionId, bool busy = false, string? state = null) =>
        new(sessionId, Maybe<string>.None, @"E:\repos\X", true, busy, "")
        {
            State = state ?? (busy ? "working" : "done")
        };

    // Idle, and its turn over, since two minutes ago unless the test says otherwise, so StopAfter (one minute) has
    // passed. A test that passes idleSince alone moves both clocks: an idle session's turn is over too.
    static IReadOnlyList<KeeperStep.Stop> Stops(
        IEnumerable<TaskItem> tasks, IReadOnlyCollection<ClaudeAgent> live,
        Func<string, DateTimeOffset>? idleSince = null, Func<TaskItem, bool>? windowOpen = null,
        Func<string, DateTimeOffset>? turnOverSince = null) =>
        SessionKeeper.Stops(new TaskBoard(tasks.ToList()), live, idleSince ?? (_ => Now.AddMinutes(-2)),
            turnOverSince ?? idleSince ?? (_ => Now.AddMinutes(-2)), windowOpen ?? (_ => false), Now);

    [Theory]
    [InlineData(TaskStatus.Done)]
    [InlineData(TaskStatus.Closed)]
    public void A_finished_tasks_idle_background_session_is_stopped(TaskStatus status)
    {
        var task = Task(status, 5, Session("s1"));

        var stop = Assert.Single(Stops([task], [Background("s1")]));

        Assert.Equal(task.Id, stop.TaskId);
        Assert.Equal("s1", stop.Agent.SessionId);
    }

    // Petre: "i don't want to stop sessions that are asking for my feedback, only those that have been finished".
    [Theory]
    [InlineData(TaskStatus.Backlog)]
    [InlineData(TaskStatus.InProgress)]
    [InlineData(TaskStatus.WaitingOnYou)]
    public void An_open_tasks_session_is_never_stopped(TaskStatus status) =>
        Assert.Empty(Stops([Task(status, 5, Session("s1"))], [Background("s1")]));

    [Fact]
    public void A_busy_session_is_not_stopped_while_it_finishes_its_turn() =>
        Assert.Empty(Stops([Task(TaskStatus.Done, 5, Session("s1"))], [Background("s1", busy: true)]));

    [Fact]
    public void A_session_idle_for_less_than_StopAfter_is_not_stopped_yet()
    {
        var task = Task(TaskStatus.Done, 5, Session("s1"));

        Assert.Empty(Stops([task], [Background("s1")], _ => Now - SessionKeeper.StopAfter + TimeSpan.FromSeconds(1)));
        Assert.Single(Stops([task], [Background("s1")], _ => Now - SessionKeeper.StopAfter));
    }

    // An interactive session is Petre's own VS Code or terminal window, and `claude stop` only knows background ones.
    [Fact]
    public void An_interactive_session_is_never_stopped() =>
        Assert.Empty(Stops([Task(TaskStatus.Done, 5, Session("s1"))], [Agent("s1")]));

    // A Done task's session may still have its report on Petre's screen: not stopped while its window is open.
    [Fact]
    public void A_done_tasks_session_with_a_window_attached_is_not_stopped_until_the_window_closes()
    {
        var task = Task(TaskStatus.Done, 5, Session("s1"));

        Assert.Empty(Stops([task], [Background("s1")], windowOpen: t => t.Id == task.Id));
        Assert.Single(Stops([task], [Background("s1")], windowOpen: _ => false));
    }

    // Task 8556944b (Petre: "can a claude session window close on its own when a task has been closed?"). Only Petre
    // closes a task, so a Closed task's session is stopped with its window open, and `claude stop` closes the window
    // (measured). Still idle first, and still only after StopAfter: a turn in flight finishes.
    [Fact]
    public void A_closed_tasks_session_is_stopped_even_with_its_window_open()
    {
        var task = Task(TaskStatus.Closed, 5, Session("s1"));

        var stop = Assert.Single(Stops([task], [Background("s1")], windowOpen: _ => true));
        Assert.Equal(task.Id, stop.TaskId);
        Assert.Empty(Stops([task], [Background("s1", busy: true)], windowOpen: _ => true));
        Assert.Empty(Stops([task], [Background("s1")], _ => Now - SessionKeeper.StopAfter + TimeSpan.FromSeconds(1), windowOpen: _ => true));
    }

    // Task 607af1db. A session that ended its turn with a background job still running (a Monitor, a Bash left in the
    // background) is listed busy until the job ends, with state "done" or "blocked" (measured). A Closed task's
    // session is stopped anyway, once its turn has been over for StopAfter; the job goes with it.
    [Theory]
    [InlineData("done")]
    [InlineData("blocked")]
    public void A_closed_tasks_session_busy_only_on_its_own_background_job_is_stopped(string state)
    {
        var task = Task(TaskStatus.Closed, 5, Session("s1"));

        // Never idle (busy in every read), its turn over for two minutes.
        var stop = Assert.Single(Stops([task], [Background("s1", busy: true, state: state)], _ => Now, turnOverSince: _ => Now.AddMinutes(-2)));
        Assert.Equal(task.Id, stop.TaskId);
    }

    [Fact]
    public void A_closed_tasks_session_busy_on_a_background_job_waits_StopAfter_from_the_end_of_its_turn()
    {
        var task = Task(TaskStatus.Closed, 5, Session("s1"));
        var agent = Background("s1", busy: true, state: "done");

        Assert.Empty(Stops([task], [agent], _ => Now, turnOverSince: _ => Now - SessionKeeper.StopAfter + TimeSpan.FromSeconds(1)));
        Assert.Single(Stops([task], [agent], _ => Now, turnOverSince: _ => Now - SessionKeeper.StopAfter));
    }

    // A turn in flight still finishes first: busy and working is never stopped, whatever the clocks say.
    [Fact]
    public void A_closed_tasks_session_in_a_turn_is_not_stopped() =>
        Assert.Empty(Stops([Task(TaskStatus.Closed, 5, Session("s1"))], [Background("s1", busy: true, state: "working")]));

    // A busy row with no state says nothing about its turn, so it counts as working.
    [Fact]
    public void A_closed_tasks_busy_session_with_no_state_is_not_stopped() =>
        Assert.Empty(Stops([Task(TaskStatus.Closed, 5, Session("s1"))],
            [new ClaudeAgent("s1", Maybe<string>.None, @"E:\repos\X", true, true, "")]));

    // A Done task is not Petre's "finished with this": its job may be the last of its work, so it waits for idle.
    [Fact]
    public void A_done_tasks_session_busy_on_a_background_job_is_not_stopped() =>
        Assert.Empty(Stops([Task(TaskStatus.Done, 5, Session("s1"))], [Background("s1", busy: true, state: "done")],
            turnOverSince: _ => Now.AddMinutes(-2)));

    [Fact]
    public void A_session_an_open_task_also_holds_is_not_stopped()
    {
        var done = Task(TaskStatus.Done, 5, Session("s1"));
        var open = Task(TaskStatus.InProgress, 5, Session("s1"));

        Assert.Empty(Stops([done, open], [Background("s1")]));
    }

    [Fact]
    public void A_session_on_two_finished_tasks_is_stopped_once()
    {
        var first = Task(TaskStatus.Done, 5, Session("s1"));
        var second = Task(TaskStatus.Closed, 5, Session("s1"));

        Assert.Single(Stops([first, second], [Background("s1")]));
    }

    [Fact]
    public void An_unlisted_session_of_a_finished_task_has_nothing_to_stop() =>
        Assert.Empty(Stops([Task(TaskStatus.Done, 5, Session("s1"))], [Background("other")]));

    [Fact]
    public void Only_the_listed_session_of_a_finished_task_with_several_is_stopped()
    {
        var task = Task(TaskStatus.Done, 5, Session("old", 120), Session("new", 10));

        Assert.Equal("old", Assert.Single(Stops([task], [Background("old")])).Agent.SessionId);
    }
}
