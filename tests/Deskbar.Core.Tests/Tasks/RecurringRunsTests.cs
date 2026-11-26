using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
// System.Threading.Tasks (an implicit using) has a TaskStatus too; the alias says which one this file means.
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests.Tasks;

// The sweep's decision (task 0b78269e, spec docs/superpowers/specs/2026-11-26-recurring-tasks-design.md, plan task 4).
// RecurringRuns.Plan is pure: the board, the clock and two questions about the machine (is a session of the task listed,
// does the folder exist) go in, a decision per task that needs one comes out. Nothing here touches a disk or a process.
public sealed class RecurringRunsTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
    static readonly Guid Ws = Guid.Parse("11111111-1111-1111-1111-111111111111");
    static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    static readonly TaskRepeat Hourly = new(TaskRepeats.Parse("every 1h").Value, "sonnet", @"C:\repos\TaskSpaces");

    // A repeating task whose run was due `due` ago (negative: due later; null: no next run yet), in the given status.
    static TaskItem Repeating(TimeSpan? due = null, TaskStatus status = TaskStatus.Backlog, Maybe<string> question = default) =>
        TaskBoard.Empty.AddNote("check the deploy", Ws, Now.AddDays(-1), Guid.NewGuid()).Value.Board.Tasks.Single() with
        {
            Repeat = Hourly,
            NextRun = due is { } d ? Now - d : Maybe<DateTimeOffset>.None,
            Status = status,
            Question = question,
        };

    static IReadOnlyList<RunDecision> Plan(TaskItem task, bool running = false, bool folderExists = true) =>
        RecurringRuns.Plan(new TaskBoard([task]), Now, _ => running, _ => folderExists);

    [Fact]
    public void A_due_task_between_runs_starts_with_its_repeat()
    {
        var task = Repeating(TimeSpan.FromMinutes(1));

        var start = Assert.IsType<RunDecision.Start>(Assert.Single(Plan(task)));

        Assert.Equal(task.Id, start.TaskId);
        Assert.Equal(Hourly, start.Repeat);
    }

    [Fact]
    public void A_task_whose_run_is_later_gets_nothing() =>
        Assert.Empty(Plan(Repeating(TimeSpan.FromMinutes(-5))));

    [Fact]
    public void A_due_task_with_a_listed_session_is_skipped() =>
        Assert.IsType<RunDecision.Skip>(Assert.Single(Plan(Repeating(TimeSpan.Zero), running: true)));

    [Theory]
    [InlineData(TaskStatus.InProgress)]
    [InlineData(TaskStatus.WaitingOnYou)]
    public void A_due_task_in_progress_or_waiting_is_skipped(TaskStatus status) =>
        Assert.IsType<RunDecision.Skip>(Assert.Single(Plan(Repeating(TimeSpan.Zero, status))));

    [Fact]
    public void A_due_task_that_is_done_between_runs_starts() =>
        Assert.IsType<RunDecision.Start>(Assert.Single(Plan(Repeating(TimeSpan.Zero, TaskStatus.Done))));

    [Fact]
    public void A_closed_task_gets_nothing_however_late() =>
        Assert.Empty(Plan(Repeating(TimeSpan.FromDays(3), TaskStatus.Closed)));

    [Fact]
    public void A_due_task_whose_folder_is_gone_says_so_once()
    {
        var task = Repeating(TimeSpan.Zero);

        var gone = Assert.IsType<RunDecision.FolderGone>(Assert.Single(Plan(task, folderExists: false)));
        Assert.Equal(Hourly.Folder, gone.Folder);

        // Once it has said so it is waiting with the question, and the next sweep leaves it be.
        var asked = Repeating(TimeSpan.Zero, TaskStatus.WaitingOnYou, RecurringRuns.FolderGoneQuestion);
        Assert.IsType<RunDecision.Skip>(Assert.Single(Plan(asked, folderExists: false)));
    }

    [Fact]
    public void A_task_without_a_next_run_is_scheduled()
    {
        var task = Repeating();

        var schedule = Assert.IsType<RunDecision.Schedule>(Assert.Single(Plan(task)));

        Assert.Equal(task.Id, schedule.TaskId);
    }

    [Fact]
    public void Six_missed_hours_make_one_run()
    {
        var task = Repeating(TimeSpan.FromHours(6));
        var board = new TaskBoard([task]);

        Assert.IsType<RunDecision.Start>(Assert.Single(RecurringRuns.Plan(board, Now, _ => false, _ => true)));

        var after = board.RunStarted(task.Id, Now, Utc).Value;

        Assert.Empty(RecurringRuns.Plan(after, Now, _ => false, _ => true));
        Assert.Equal(Now.AddHours(1), after.Tasks.Single().NextRun.Value);
    }
}
