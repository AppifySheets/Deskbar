using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// Recurring tasks (task 0b78269e, spec docs/superpowers/specs/2026-11-26-recurring-tasks-design.md). A repeating task
// starts a quiet Claude session each time its NextRun passes. This file holds what the board and the sweep both need to
// agree on: the two questions a task asks when a run cannot start (and how to recognise them, so setting the repeat
// again can answer them), and the note left on the thread when a run is skipped. Task 4 adds the sweep itself here.
public static class RecurringRuns
{
    /// <summary>Asked when the folder a repeat's runs start in no longer exists.</summary>
    public const string FolderGoneQuestion = "This task repeats, but the folder its runs start in is gone. Set the repeat again with a folder (Repeat in its menu, or task repeat <id> <schedule> --folder <path>).";

    /// <summary>Asked when a run could not be started; the thread carries what Claude Code said.</summary>
    public const string StartFailedQuestion = "This task repeats, but its last run could not start; what Claude Code said is in the thread. Set the repeat again, or wake it, once it can run.";

    /// <summary>
    /// Whether a question is one of the two above. SetRepeat uses it: setting the repeat again is the answer to either, so
    /// a task waiting on one goes back to Backlog. Any other question is Petre's to answer and stays.
    /// </summary>
    public static bool IsRepeatQuestion(Maybe<string> question) =>
        question.HasValue && question.Value is FolderGoneQuestion or StartFailedQuestion;

    /// <summary>The thread note for an occurrence skipped because the repeat's folder is gone.</summary>
    public static string FolderGoneNote(string folder) => $"Scheduled run skipped: {folder} no longer exists.";

    // THE SWEEP'S DECISION (task 0b78269e, plan task 4). Pure: the keeper (a later task) asks the machine the two
    // questions it passes in, applies what comes back and never decides anything itself, so every rule below is testable
    // without a disk, a process or a clock.
    //
    // Rules, in this order, for each OPEN task that has a repeat (a closed task gets nothing, however late):
    //   1. No NextRun yet            -> Schedule (the keeper works the next occurrence out from now and stores it).
    //   2. NextRun still ahead       -> nothing.
    //   3. A session of it is listed -> Skip. At most one run per task: an occurrence that finds the last one alive is
    //      dropped, never queued, because a queue would answer a long run with a burst of runs behind it.
    //   4. In progress or waiting    -> Skip. In progress is a run in flight (the keeper looks after it); waiting is a
    //      question Petre has to answer. This also covers a task already waiting with either repeat question, so a
    //      folder that stays gone says so once and not on every sweep.
    //   5. The folder is missing     -> FolderGone (the keeper asks the question and leaves a note on the thread).
    //   6. Otherwise                 -> Start, carrying the repeat so the keeper starts exactly what was scheduled.
    // A Done task between runs reaches rule 6: a recurring task's done ends a run, it does not end the task.
    //
    // Every skip is a skip, not a queue, and none of them moves NextRun here: the keeper re-schedules from now after a
    // skip (spec: no catch-up, ever), which is also why six missed hours make ONE Start and not six.
    public static IReadOnlyList<RunDecision> Plan(
        TaskBoard board, DateTimeOffset now, Func<TaskItem, bool> running, Func<string, bool> folderExists) =>
        board.Tasks
            .Where(t => t.IsOpen && t.Repeat.HasValue)
            .Select(t => Decide(t, now, running, folderExists))
            .OfType<RunDecision>()
            .ToList();

    static RunDecision? Decide(TaskItem task, DateTimeOffset now, Func<TaskItem, bool> running, Func<string, bool> folderExists) =>
        task.NextRun.HasNoValue ? new RunDecision.Schedule(task.Id)
        : task.NextRun.Value > now ? null
        : running(task) ? new RunDecision.Skip(task.Id, "a session is still running")
        : task.Status is TaskStatus.InProgress ? new RunDecision.Skip(task.Id, "a run is already in progress")
        : task.Status is TaskStatus.WaitingOnYou ? new RunDecision.Skip(task.Id, "the task is waiting on a question")
        : !folderExists(task.Repeat.Value.Folder) ? new RunDecision.FolderGone(task.Id, task.Repeat.Value.Folder)
        : new RunDecision.Start(task.Id, task.Repeat.Value);
}

/// <summary>What the sweep decided for one recurring task (RecurringRuns.Plan).</summary>
public abstract record RunDecision(Guid TaskId)
{
    /// <summary>Start a run now with this repeat's model and folder.</summary>
    public sealed record Start(Guid TaskId, TaskRepeat Repeat) : RunDecision(TaskId);

    /// <summary>The occurrence is dropped for this reason; the keeper schedules the next one from now.</summary>
    public sealed record Skip(Guid TaskId, string Why) : RunDecision(TaskId);

    /// <summary>The repeat's folder no longer exists: ask the question, leave the note.</summary>
    public sealed record FolderGone(Guid TaskId, string Folder) : RunDecision(TaskId);

    /// <summary>The task repeats but has no NextRun: work one out from now.</summary>
    public sealed record Schedule(Guid TaskId) : RunDecision(TaskId);
}
