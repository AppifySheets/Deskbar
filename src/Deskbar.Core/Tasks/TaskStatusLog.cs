namespace Deskbar.Core.Tasks;

// THE STATUS LOG (task 521d2b07; spec docs/superpowers/specs/2026-11-25-task-stats-design.md, "Where the history comes
// from"). Until this existed nothing recorded WHEN a task's status changed, so the stats could only guess a finish time
// from Raised and UpdatedAt. TaskBoard has a dozen paths that move a status (join, ask, answered, done, close, reopen,
// undo, a status by hand, a session's end, a parent reopened by its subtask...), and instrumenting each would leave the
// next one added to forget. So the log is written in ONE place, after the fact: TaskService.Commit hands this the board
// before and the board after every change, and each task whose status differs gets one entry. A task that is new on the
// board gets none (its Created already says when it began), and a deleted one is simply gone.
public static class TaskStatusLog
{
    /// <summary>
    /// <paramref name="after"/>, with a <see cref="StatusChange"/> appended to every task whose status differs from its
    /// status in <paramref name="before"/>. The same board instance when no status moved, so a caller can tell.
    /// </summary>
    public static TaskBoard Logged(TaskBoard before, TaskBoard after, DateTimeOffset at)
    {
        var was = before.Tasks.ToDictionary(t => t.Id, t => t.Status);
        bool Moved(TaskItem t) => was.TryGetValue(t.Id, out var status) && status != t.Status;
        return after.Tasks.Any(Moved)
            ? new TaskBoard(after.Tasks.Select(t => Moved(t)
                ? t with { StatusLog = [.. t.StatusLog, new StatusChange(at, was[t.Id], t.Status)] }
                : t).ToArray())
            : after;
    }
}
