using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests.Tasks;

// Task e948b383: a usage limit lifted before its reset moment, and sessions that stopped on a wrap_up note.
public class SessionKeeperLimitLiftTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 28, 8, 0, 0, TimeSpan.Zero);

    static TaskSession Session(string id) => new(id, @"E:\repos\X", Now.AddHours(-2), Maybe<DateTimeOffset>.None);

    static TaskItem Task(TaskStatus status, params TaskSession[] sessions) =>
        new(Guid.NewGuid(), "t", Maybe<Guid>.None, status, "", Maybe<string>.None, Now.AddDays(-1),
            Now.AddMinutes(-5), Now.AddDays(-1), sessions, Array.Empty<TaskComment>());

    static ClaudeAgent Background(string sessionId, bool busy = false) => new(sessionId, sessionId[..2], @"E:\repos\X", true, busy, "");

    // The five-hour limit at 07:25, resetting at 14:10, as on 2026-11-28.
    static readonly TurnError Limit = new(Now.AddHours(6).AddMinutes(10)) { At = Now.AddMinutes(-35) };

    static IReadOnlyList<KeeperStep> Plan(TaskItem task, IReadOnlyCollection<ClaudeAgent> live, Maybe<DateTimeOffset> lifted) =>
        SessionKeeper.Plan(new TaskBoard([task]), live, ResumeLedger.Empty, _ => true, _ => true, Now,
            limitedUntil: _ => Limit, limitLifted: lifted);

    [Fact]
    public void Without_proof_a_limited_session_waits_for_its_reset()
    {
        var task = Task(TaskStatus.InProgress, Session("s1"));
        Assert.Empty(Plan(task, [Background("s1")], Maybe<DateTimeOffset>.None));
        Assert.Empty(Plan(task, [], Maybe<DateTimeOffset>.None));
    }

    [Fact]
    public void Proof_older_than_the_error_wakes_nothing() =>
        Assert.Empty(Plan(Task(TaskStatus.InProgress, Session("s1")), [Background("s1")], Now.AddMinutes(-40)));

    // Session 8258f03b on 2026-11-28: listed, idle, in the background, stopped on the limit. Restarted at once.
    [Fact]
    public void Proof_after_the_error_restarts_a_listed_session_before_its_reset()
    {
        var resume = Assert.IsType<KeeperStep.Resume>(Assert.Single(Plan(Task(TaskStatus.InProgress, Session("s1")), [Background("s1")], Now.AddMinutes(-30))));
        Assert.Equal("s1", resume.StopFirst.Value);
        Assert.True(resume.AfterLimit);
        Assert.True(resume.SendContinue);
    }

    [Fact]
    public void Proof_after_the_error_resumes_a_session_not_listed() =>
        Assert.IsType<KeeperStep.Resume>(Assert.Single(Plan(Task(TaskStatus.InProgress, Session("s1")), [], Now.AddMinutes(-30))));

    [Fact]
    public void An_error_with_no_timestamp_waits_for_its_reset() =>
        Assert.False(SessionKeeper.LiftedSince(new TurnError(Now.AddHours(1)), Now));

    // --- wrap_up stops ---------------------------------------------------------------------------------------------------

    static readonly DateTimeOffset StoppedAt = Now.AddMinutes(-35);

    static IReadOnlyList<NoteStopWake> Wakes(TaskItem task, IReadOnlyCollection<ClaudeAgent> live, LimitSigns signs, DateTimeOffset? now = null) =>
        SessionKeeper.NoteStopWakes(new TaskBoard([task]), live, _ => StoppedAt, signs, now ?? Now);

    static LimitSigns LiftedAt(DateTimeOffset at) => new(at, Maybe<LimitErrorSign>.None);

    [Fact]
    public void A_note_stop_is_woken_once_the_api_answers_after_it()
    {
        var task = Task(TaskStatus.InProgress, Session("s1"));
        var wake = Assert.Single(Wakes(task, [Background("s1")], LiftedAt(Now.AddMinutes(-30))));
        Assert.Equal((task.Id, "s1", StoppedAt), (wake.TaskId, wake.SessionId, wake.StoppedAt));
        Assert.Empty(Wakes(task, [Background("s1")], LiftedAt(Now.AddMinutes(-40))));
    }

    [Fact]
    public void Without_proof_a_note_stop_waits_for_the_reset_the_latest_error_names()
    {
        var task = Task(TaskStatus.InProgress, Session("s1"));
        var signs = new LimitSigns(Maybe<DateTimeOffset>.None, new LimitErrorSign(StoppedAt, Now.AddHours(6)));
        Assert.Empty(Wakes(task, [Background("s1")], signs));
        Assert.Single(Wakes(task, [Background("s1")], signs, Now.AddHours(6) + SessionKeeper.LimitMargin));
    }

    // An error whose reset was already behind the stop belongs to an earlier limit.
    [Fact]
    public void With_nothing_known_a_note_stop_is_woken_after_the_fallback()
    {
        var task = Task(TaskStatus.InProgress, Session("s1"));
        var old = new LimitSigns(Maybe<DateTimeOffset>.None, new LimitErrorSign(StoppedAt.AddHours(-9), StoppedAt.AddHours(-4)));
        Assert.Empty(Wakes(task, [Background("s1")], old));
        Assert.Single(Wakes(task, [Background("s1")], old, StoppedAt + SessionKeeper.NoteStopFallback));
    }

    // Session 2c94ab8f asked Petre three questions and stopped: his answer wakes it, the limit does not.
    [Fact]
    public void A_task_waiting_on_petre_is_not_woken() =>
        Assert.Empty(Wakes(Task(TaskStatus.WaitingOnYou, Session("s1")), [Background("s1")], LiftedAt(Now)));

    [Fact]
    public void A_busy_or_missing_session_is_not_woken()
    {
        var task = Task(TaskStatus.InProgress, Session("s1"));
        Assert.Empty(Wakes(task, [Background("s1", busy: true)], LiftedAt(Now)));
        Assert.Empty(Wakes(task, [], LiftedAt(Now)));
    }

    // A session holding itself on a limit its own replies prove gone is woken at once, with no proof from elsewhere.
    [Fact]
    public void A_self_held_session_is_woken_at_once()
    {
        var task = Task(TaskStatus.InProgress, Session("s1"));
        var wake = Assert.Single(SessionKeeper.NoteStopWakes(new TaskBoard([task]), [Background("s1")], _ => Maybe<DateTimeOffset>.None,
            LimitSigns.None, Now, _ => StoppedAt));
        Assert.True(wake.Held);
        Assert.Equal((task.Id, "s1", StoppedAt), (wake.TaskId, wake.SessionId, wake.StoppedAt));
    }

    [Fact]
    public void A_self_held_session_waiting_on_petre_is_not_woken() =>
        Assert.Empty(SessionKeeper.NoteStopWakes(new TaskBoard([Task(TaskStatus.WaitingOnYou, Session("s1"))]), [Background("s1")],
            _ => Maybe<DateTimeOffset>.None, LimitSigns.None, Now, _ => StoppedAt));

    [Theory]
    [InlineData(TaskStatus.Done)]
    [InlineData(TaskStatus.Closed)]
    public void A_finished_task_is_not_woken(TaskStatus status) =>
        Assert.Empty(Wakes(Task(status, Session("s1")), [Background("s1")], LiftedAt(Now)));
}
