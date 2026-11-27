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
        IReadOnlyList<RedRun>? red = null) =>
        ManagerWakes.Plan(record, board, listed ?? [], today, now, red);

    static RedRun Red(Guid id, DateTimeOffset at) => new(id, "AppifySheets/Deskbar", "Release", "https://example.test/runs/1", at);

    // Deskbar task c8e6967d: a red run on a followed merge commit wakes the manager, once per gap, with its own instruction.
    [Fact]
    public void RedRunWakesTheManager()
    {
        var plan = Assert.IsType<ManagerWake.Wake>(Plan(Record(), Board(Task(id1, "Tasks redesign")), At(30), red: [Red(id1, At(10))]));

        Assert.Contains("Failed runs on merge commits:", plan.Message);
        Assert.Contains("- AppifySheets/Deskbar: Release https://example.test/runs/1, task 11111111 \"Tasks redesign\"", plan.Message);
        Assert.Contains("Tell the user in one line only if several tasks are blocked by it; the task's session was nudged.", plan.Message);
        Assert.StartsWith(ManagerWakes.Prefix, plan.Message);
    }

    [Fact]
    public void OldRedRunWakesNothing()
    {
        var record = Record(since: At(20));
        Assert.Equal(new ManagerWake.Nothing(record), Plan(record, Board(Task(id1, "Tasks redesign")), At(60), red: [Red(id1, At(10))]));
    }

    [Fact]
    public void RedRunWaitsForTheGap()
    {
        var record = Record();
        Assert.Equal(new ManagerWake.Nothing(record), Plan(record, Board(Task(id1, "Tasks redesign")), At(29), red: [Red(id1, At(10))]));
    }

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

    [Fact]
    public void A_second_hand_back_today_says_do_not_nudge()
    {
        var wake = Assert.IsType<ManagerWake.Wake>(Plan(Record(handedBack: [id1]), Board(HandedBack(id1, At(10))), At(45)));
        Assert.Contains("handed back AGAIN today. Do not nudge", wake.Message);
        Assert.DoesNotContain("PR 512 green", wake.Message);
        Assert.Equal([id1], wake.Record.HandedBack);
    }
}
