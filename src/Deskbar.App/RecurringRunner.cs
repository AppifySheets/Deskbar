using System.Collections.Immutable;
using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// RECURRING TASKS' RUNS START HERE (task 0b78269e, spec docs/superpowers/specs/2026-11-26-recurring-tasks-design.md,
// "A run starts"; plan task 7). The decisions are Core's (RecurringRuns.Plan, through TaskService.DueRuns); this class
// only answers the machine's two questions for them (is a session of the task running, does the folder exist) and
// carries each decision out:
//
//   Start      -> TaskService.RunStarted (the snooze ends, NextRun moves on), then `start`: the App's quiet start
//                 (ClaudeTaskActions.Start(..., quiet: true)) in the repeat's folder with the repeat's model. No window,
//                 no focus, no workspace switch: nobody asked for this run at this moment. A start that fails, at once
//                 or once `claude` has answered, leaves Claude's words in the thread and asks StartFailedQuestion, so
//                 the task waits on Petre and the following occurrences skip until he sets the repeat again or wakes it.
//   Skip       -> TaskService.Rescheduled (the next occurrence, worked out from now: no queue, no catch-up) and a trace
//                 line saying why.
//   FolderGone -> Rescheduled, a note naming the folder and FolderGoneQuestion. Once: the task then waits on the
//                 question, and Plan skips a waiting task without a word.
//   Schedule   -> Rescheduled (a repeat with no NextRun yet gets one).
//
// When it runs: on the App's 30-second snooze sweep, before TaskService.WakeDue (so a task whose run is due is started
// rather than first shown awake for a sweep), and at once for RunNow: a comment on a task between runs (CommentWaker,
// WakePlan.FreshRun). Nothing at all while Claude sessions do not keep their tasks (`enabled`, the App's
// AppState.ClaudeTasksInEffect): a session started then could not join its task.
//
// A START IN FLIGHT COUNTS AS RUNNING. Between RunStarted and the new session's `task join` the task is still Backlog
// with no session of its own listed, so it looks between runs: a comment in those seconds would get FreshRun, and the
// next sweep would see nothing running either, and a second run would start beside the first. So the ids of the starts
// sent are kept, with when they were sent, and count as running both for Plan and for RunNow. An id leaves when
// `running` says a session of the task is listed (the run has joined and shows in `claude agents`), when its start
// fails, or after StartingFor, so a start that vanished without a word cannot hold the task for good.
//
// Everything runs on the UI thread (the sweep, the comment waker and the start's answer are all observed there), which
// is what makes swapping the immutable map safe without a lock.
sealed class RecurringRunner(
    TaskService tasks,
    Func<bool> enabled,
    Func<TaskItem, bool> running,
    Func<string, bool> folderExists,
    Func<TaskItem, string, Maybe<string>, Action<string>, Result> start,
    Action<string>? trace = null)
{
    /// <summary>
    /// How long a start sent counts as running without its session being listed. `claude --bg` answers in seconds and the
    /// session joins on its first turn (measured: well under a minute), so five minutes is room, not a guess at the edge.
    /// </summary>
    public static readonly TimeSpan StartingFor = TimeSpan.FromMinutes(5);

    // The starts in flight: task id to when its start was sent, by TaskService's clock.
    ImmutableDictionary<Guid, DateTimeOffset> starting = ImmutableDictionary<Guid, DateTimeOffset>.Empty;

    /// <summary>Starts every run that is due and carries out the sweep's other decisions (see the header).</summary>
    public void RunDue()
    {
        if (!enabled()) return;
        Forget();
        tasks.DueRuns(Busy, folderExists).ToList().ForEach(Apply);
    }

    /// <summary>
    /// A run now, for a task between runs (a comment on it: WakePlan.FreshRun): TaskService.Wake makes its NextRun now, and
    /// RunDue starts it, under every rule a scheduled occurrence goes through. A task whose run is already starting, or
    /// whose session is listed, gets nothing more: the starting run reads the thread when it joins, the comment included.
    /// </summary>
    public Result RunNow(Guid id)
    {
        if (!enabled()) return Result.Failure(ClaudeTaskActions.FeatureOff);
        Forget();
        return tasks.Board.Tasks.TryFirst(t => t.Id == id).ToResult($"No task {id}")
            .Bind(task => Busy(task)
                ? Result.Success().Tap(() => trace?.Invoke($"recurring: {id} not started again, a run of it is starting or running"))
                : tasks.Wake(id).Tap(RunDue));
    }

    // A session of the task is listed, or its start is in flight (see the header).
    bool Busy(TaskItem task) => starting.ContainsKey(task.Id) || running(task);

    // The starts that no longer need holding: the run is listed, the task is gone or closed, or StartingFor has passed.
    void Forget() =>
        starting = starting
            .Where(pair => tasks.Now - pair.Value < StartingFor
                && tasks.Board.Tasks.TryFirst(t => t.Id == pair.Key) is { HasValue: true } task && task.Value.IsOpen && !running(task.Value))
            .ToImmutableDictionary();

    void Apply(RunDecision decision)
    {
        switch (decision)
        {
            case RunDecision.Start(var id, var repeat):
                Start(id, repeat);
                break;
            case RunDecision.Skip(var id, var why):
                Rescheduled(id);
                trace?.Invoke($"recurring: {id} skipped this occurrence, {why}");
                break;
            case RunDecision.FolderGone(var id, var folder):
                Rescheduled(id);
                Say(id, RecurringRuns.FolderGoneNote(folder));
                tasks.Ask(id, RecurringRuns.FolderGoneQuestion).TapError(e => trace?.Invoke($"recurring: {id} not asked about its folder: {e}"));
                break;
            case RunDecision.Schedule(var id):
                Rescheduled(id);
                break;
        }
    }

    // RunStarted first, so the board has moved NextRun on before anything else can look at it; then the start, on the
    // task as RunStarted left it. A start refused at once (the task closed meanwhile) goes the way of one that fails later.
    void Start(Guid id, TaskRepeat repeat) =>
        tasks.RunStarted(id)
            .Bind(() => tasks.Board.Tasks.TryFirst(t => t.Id == id).ToResult($"No task {id}"))
            .Tap(task =>
            {
                starting = starting.SetItem(id, tasks.Now);
                trace?.Invoke($"recurring: starting a run of {id} in {repeat.Folder} on {repeat.Model}");
                start(task, repeat.Folder, Maybe.From(repeat.Model), error => Failed(id, repeat.Folder, error))
                    .TapError(error => Failed(id, repeat.Folder, error));
            })
            .TapError(e => trace?.Invoke($"recurring: {id} not started: {e}"));

    // Claude's own words in the thread, then the question, which keeps the next occurrences from trying again on their own.
    void Failed(Guid id, string folder, string error)
    {
        starting = starting.Remove(id);
        Say(id, $"Could not start a scheduled run in {folder}:\n\n{error}");
        tasks.Ask(id, RecurringRuns.StartFailedQuestion).TapError(e => trace?.Invoke($"recurring: {id} not asked after a failed start: {e}"));
    }

    void Rescheduled(Guid id) => tasks.Rescheduled(id).TapError(e => trace?.Invoke($"recurring: {id} not rescheduled: {e}"));

    void Say(Guid id, string markdown) => tasks.Note(id, markdown).TapError(e => trace?.Invoke($"recurring: no comment on {id}: {e}"));
}
