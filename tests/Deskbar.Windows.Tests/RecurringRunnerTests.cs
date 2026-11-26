using System.IO;
using System.Reactive.Subjects;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core.Tasks;

namespace Deskbar.Windows.Tests;

// RecurringRunner: the App starting recurring tasks' runs on the snooze sweep (task 0b78269e, spec
// docs/superpowers/specs/2026-11-26-recurring-tasks-design.md, "A run starts"; plan task 7). The board and the sweep's
// decisions are real (TaskService, RecurringRuns.Plan); what touches the machine is faked: `start` records what it was
// asked to start instead of running `claude --bg`, `running` and `folderExists` answer from the test. ClaudeTaskActionsTests
// covers the quiet start itself, CommentWakerTests the comment that asks for a run.
public sealed class RecurringRunnerTests : IDisposable
{
    static readonly DateTimeOffset At = new(2026, 11, 26, 10, 0, 0, TimeSpan.Zero);
    const string Folder = @"C:\repos\Deskbar";

    readonly string inboxFolder = Directory.CreateTempSubdirectory("deskbar-runner-").FullName;
    readonly TaskService tasks;
    readonly List<(Guid Task, string Folder, Maybe<string> Model)> starts = [];
    readonly List<Action<string>> failures = [];
    readonly HashSet<Guid> listed = [];
    readonly HashSet<string> goneFolders = [];
    DateTimeOffset now = At;
    bool enabled = true;

    public RecurringRunnerTests() => tasks = new TaskService(new MemoryStore(), () => now, zone: TimeZoneInfo.Utc);

    public void Dispose() => Directory.Delete(inboxFolder, recursive: true);

    sealed class MemoryStore : ITaskStore
    {
        public Result<TaskBoard> Load() => TaskBoard.Empty;

        public Result Save(TaskBoard board) => Result.Success();
    }

    RecurringRunner Runner() => new(tasks, () => enabled, task => listed.Contains(task.Id), folder => !goneFolders.Contains(folder),
        (task, folder, model, failed) =>
        {
            starts.Add((task.Id, folder, model));
            failures.Add(failed);
            return Result.Success();
        });

    // An hourly note given to Claude, on Haiku, in Folder: its first run is due an hour from At.
    Guid Hourly()
    {
        var id = tasks.AddNote("check the deploy", Maybe<Guid>.None).Value.Id;
        Assert.True(tasks.SetRepeat(id, new TaskRepeat(TaskRepeats.Parse("every 1h").Value, "haiku", Folder)).IsSuccess);
        return id;
    }

    TaskItem Task(Guid id) => tasks.Board.Tasks.Single(t => t.Id == id);

    void Pass(TimeSpan span) => now += span;

    static readonly TimeSpan PastTheHour = TimeSpan.FromMinutes(61);

    [Fact]
    public void RunDue_starts_a_due_run_quietly_in_its_folder_with_its_model()
    {
        var id = Hourly();
        var runner = Runner();

        runner.RunDue();
        Assert.Empty(starts);

        Pass(PastTheHour);
        runner.RunDue();

        Assert.Equal([(id, Folder, Maybe.From("haiku"))], starts);
        Assert.True(Task(id).NextRun.Value > now);
        Assert.False(Task(id).IsSnoozed);
    }

    // At most one run per task (spec): an occurrence that finds the last run's session listed is dropped, not queued, and
    // the next one is worked out from now.
    [Fact]
    public void RunDue_skips_a_task_with_a_running_session_and_reschedules_it()
    {
        var id = Hourly();
        listed.Add(id);
        var runner = Runner();

        Pass(PastTheHour);
        runner.RunDue();

        Assert.Empty(starts);
        Assert.True(Task(id).NextRun.Value > now);
    }

    // A removed worktree: one note in the thread and the question, and the occurrences after it say nothing more while the
    // question waits.
    [Fact]
    public void RunDue_asks_once_when_the_folder_is_gone()
    {
        var id = Hourly();
        goneFolders.Add(Folder);
        var runner = Runner();

        Pass(PastTheHour);
        runner.RunDue();

        Assert.Empty(starts);
        Assert.Equal(RecurringRuns.FolderGoneNote(Folder), Assert.Single(Task(id).Comments, c => c.ByDeskbar).Markdown);
        Assert.Equal(TaskStatus.WaitingOnYou, Task(id).Status);
        Assert.Equal(RecurringRuns.FolderGoneQuestion, Task(id).Question.GetValueOrDefault());

        Pass(PastTheHour);
        runner.RunDue();

        Assert.Single(Task(id).Comments, c => c.ByDeskbar);
        Assert.Empty(starts);
    }

    // A start that fails after it was sent (an untrusted folder, no Claude Code) says Claude's words in the thread and asks
    // the question, and the task waits on it: the next occurrence starts nothing until Petre answers.
    [Fact]
    public void A_start_that_fails_later_asks_and_the_runs_wait()
    {
        var id = Hourly();
        var runner = Runner();
        Pass(PastTheHour);
        runner.RunDue();

        Assert.Single(failures)("Workspace not trusted.");

        Assert.Equal(TaskStatus.WaitingOnYou, Task(id).Status);
        Assert.Equal(RecurringRuns.StartFailedQuestion, Task(id).Question.GetValueOrDefault());
        Assert.Contains(Task(id).Comments, c => c.ByDeskbar && c.Markdown.Contains("Workspace not trusted."));

        Pass(PastTheHour);
        runner.RunDue();

        Assert.Single(starts);
    }

    // "Claude Code sessions keep their tasks" off: no session can join its task, so no run starts and nothing moves.
    [Fact]
    public void RunDue_does_nothing_while_claude_tasks_are_off()
    {
        var id = Hourly();
        var next = Task(id).NextRun;
        enabled = false;
        var runner = Runner();

        Pass(PastTheHour);
        runner.RunDue();

        Assert.Empty(starts);
        Assert.Equal(next, Task(id).NextRun);
    }

    // "Wake now", and a comment between runs: the run starts at once, not at the next sweep.
    [Fact]
    public void RunNow_starts_a_task_between_runs_at_once()
    {
        var id = Hourly();
        var runner = Runner();

        Assert.True(runner.RunNow(id).IsSuccess);

        Assert.Equal([(id, Folder, Maybe.From("haiku"))], starts);
    }

    // A START IN FLIGHT COUNTS AS RUNNING. Between the start and the new session's `task join` the task is still Backlog with
    // no session listed, so it looks between runs, and a comment in that moment would start a second run beside the first.
    [Fact]
    public void A_comment_while_a_run_is_starting_starts_nothing_more()
    {
        var id = Hourly();
        var runner = Runner();
        using var waker = Waker(runner);
        Pass(PastTheHour);
        runner.RunDue();

        tasks.Comment(id, Maybe<string>.None, "also look at the logs");

        Assert.Single(starts);
        Assert.DoesNotContain(Task(id).Comments, c => c.ByDeskbar);
    }

    // A start that never showed up in `claude agents` and never failed stops counting after five minutes, so a run that
    // went missing does not hold the task forever: a comment then starts one.
    [Fact]
    public void A_start_that_never_shows_stops_counting_after_five_minutes()
    {
        var id = Hourly();
        var runner = Runner();
        using var waker = Waker(runner);
        Pass(PastTheHour);
        runner.RunDue();

        Pass(RecurringRunner.StartingFor);
        tasks.Comment(id, Maybe<string>.None, "anyone?");

        Assert.Equal(2, starts.Count);
    }

    // A real CommentWaker over an empty list, whose fresh runs go to the runner the way the App wires them.
    CommentWaker Waker(RecurringRunner runner)
    {
        var live = new ReplaySubject<IReadOnlyCollection<ClaudeAgent>>(1);
        live.OnNext([]);
        return new CommentWaker(tasks, () => enabled, live, _ => true, _ => false, new CommentInbox(inboxFolder, () => now),
            (_, _, _) => System.Threading.Tasks.Task.FromResult(Result.Success()), _ => System.Threading.Tasks.Task.FromResult(Result.Success()),
            startRun: runner.RunNow);
    }
}
