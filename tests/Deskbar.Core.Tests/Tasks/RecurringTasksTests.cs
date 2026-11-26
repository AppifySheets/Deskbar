using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
// System.Threading.Tasks (an implicit using) has a TaskStatus too; the alias says which one this file means.
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests.Tasks;

// Recurring tasks (task 0b78269e, spec docs/superpowers/specs/2026-11-26-recurring-tasks-design.md). This section pins
// the two fields a task gains, Repeat and NextRun, how they are written to tasks.json and how tolerantly they are read
// back (a hand-edited or newer-build value must never fail the whole file), and IsBetweenRuns.
public sealed class RecurringTasksTests : IDisposable
{
    static readonly DateTimeOffset At = new(2026, 11, 26, 10, 0, 0, TimeSpan.Zero);
    static readonly Guid Ws = Guid.Parse("11111111-1111-1111-1111-111111111111");

    readonly string dir = Path.Combine(Path.GetTempPath(), "deskbar-recurring-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    static TaskItem Note(Func<TaskItem, TaskItem>? change = null) =>
        (change ?? (t => t))(TaskBoard.Empty.AddNote("check the deploy", Ws, At, Guid.NewGuid()).Value.Board.Tasks.Single());

    // --- the file ------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_repeat_and_its_next_run_survive_a_save_and_load()
    {
        var schedule = TaskRepeats.Parse("every 1h 09:00-19:00").Value;
        var next = new DateTimeOffset(2026, 11, 26, 11, 0, 0, TimeSpan.FromHours(4));
        var task = Note(t => t with
        {
            Repeat = new TaskRepeat(schedule, "sonnet", @"C:\repos\TaskSpaces"),
            NextRun = next,
        });
        var store = new JsonTaskStore(dir);

        Assert.True(store.Save(new TaskBoard([task])).IsSuccess);
        var loaded = store.Load().Value.Tasks.Single();

        Assert.True(loaded.Repeat.HasValue);
        Assert.Equal(schedule.Text, loaded.Repeat.Value.Schedule.Text);
        Assert.Equal("sonnet", loaded.Repeat.Value.Model);
        Assert.Equal(@"C:\repos\TaskSpaces", loaded.Repeat.Value.Folder);
        Assert.Equal(next, loaded.NextRun.Value);
        Assert.Equal(TimeSpan.Zero, loaded.NextRun.Value.Offset);
    }

    [Fact]
    public void A_task_without_a_repeat_writes_neither_field()
    {
        var store = new JsonTaskStore(dir);

        store.Save(new TaskBoard([Note()]));
        var text = File.ReadAllText(Path.Combine(dir, "tasks.json"));

        Assert.DoesNotContain("\"repeat\"", text);
        Assert.DoesNotContain("\"nextRun\"", text);
    }

    [Theory]
    [InlineData("\"repeat\":42")]
    [InlineData("\"repeat\":{\"schedule\":\"every 2m\",\"model\":\"sonnet\",\"folder\":\"C:\\\\r\"}")]
    [InlineData("\"repeat\":{\"schedule\":\"daily 09:00\"}")]
    [InlineData("\"nextRun\":\"soon\"")]
    public void A_bad_repeat_reads_as_none_and_never_fails_the_file(string field)
    {
        Directory.CreateDirectory(dir);
        var id = Guid.NewGuid();
        File.WriteAllText(Path.Combine(dir, "tasks.json"),
            $$"""{"version":2,"tasks":[{"id":"{{id}}","title":"t","status":"Backlog","created":"2026-11-26T10:00:00Z","updatedAt":"2026-11-26T10:00:00Z",{{field}}}]}""");

        var loaded = new JsonTaskStore(dir).Load();

        Assert.True(loaded.IsSuccess);
        var task = loaded.Value.Tasks.Single();
        Assert.Equal("t", task.Title);
        if (field.StartsWith("\"nextRun\"")) Assert.False(task.NextRun.HasValue);
        else Assert.False(task.Repeat.HasValue);
    }

    // --- between runs --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(TaskStatus.Backlog, true)]
    [InlineData(TaskStatus.InProgress, false)]
    [InlineData(TaskStatus.WaitingOnYou, false)]
    [InlineData(TaskStatus.Done, true)]
    [InlineData(TaskStatus.Closed, false)]
    public void Between_runs_is_an_open_backlog_or_done_task_with_a_repeat(TaskStatus status, bool expected)
    {
        var repeat = new TaskRepeat(TaskRepeats.Parse("daily 09:00").Value, "sonnet", @"C:\r");

        Assert.Equal(expected, Note(t => t with { Status = status, Repeat = repeat }).IsBetweenRuns);
        Assert.False(Note(t => t with { Status = status }).IsBetweenRuns);
    }
}
