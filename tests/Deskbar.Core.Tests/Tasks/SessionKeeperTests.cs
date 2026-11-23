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
}
