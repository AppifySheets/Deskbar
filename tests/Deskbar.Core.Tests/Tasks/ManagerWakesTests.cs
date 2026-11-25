using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
using Xunit;
// System.Threading.Tasks (an implicit using) has a TaskStatus too; the alias says which one this file means.
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests.Tasks;

// DESKBAR WAKES THE MANAGER (docs/superpowers/specs/2026-11-25-manager-event-wakes-design.md): batched, at most once per
// 30 minutes, only for a hand-back or a question two hours old, never while the manager is busy, only today's manager.
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

    static ManagerWake Plan(ManagerRecord record, TaskBoard board, DateTimeOffset now, IReadOnlyCollection<ClaudeAgent>? listed = null) =>
        ManagerWakes.Plan(record, board, listed ?? [], today, now);

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

    [Fact]
    public void A_question_two_hours_old_wakes_once()
    {
        // Asked at 08:00; since is 09:00; it turns two hours old at 10:00.
        var board = Board(Waiting(id2, At(-60)));
        Assert.IsType<ManagerWake.Nothing>(Plan(Record(), board, At(59)));
        var wake = Assert.IsType<ManagerWake.Wake>(Plan(Record(), board, At(60)));
        Assert.Contains("Unanswered for 2 hours or more", wake.Message);
        Assert.Contains("Merge PR 512?", wake.Message);
        // The next tick counts from this wake: the same question does not wake it again.
        Assert.IsType<ManagerWake.Nothing>(Plan(wake.Record, board, At(120)));
    }

    [Fact]
    public void A_new_question_alone_does_not_wake() =>
        Assert.IsType<ManagerWake.Nothing>(Plan(Record(), Board(Waiting(id2, At(10))), At(60)));

    [Fact]
    public void A_new_question_rides_along_with_a_trigger()
    {
        var wake = Assert.IsType<ManagerWake.Wake>(Plan(Record(), Board(HandedBack(id1, At(10)), Waiting(id2, At(20))), At(60)));
        Assert.Contains("New questions for the user", wake.Message);
        Assert.Contains("22222222 \"Bar blink\"", wake.Message);
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
    public void A_wake_moves_since_marks_it_unread_and_remembers_the_hand_back()
    {
        var wake = Assert.IsType<ManagerWake.Wake>(Plan(Record(), Board(HandedBack(id1, At(10))), At(45)));
        Assert.Equal(Record(At(45), [id1]) with { Unread = true }, wake.Record);
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
