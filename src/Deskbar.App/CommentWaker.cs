using System.Reactive.Linq;
using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// PETRE'S COMMENT REACHES THE TASK'S CLAUDE SESSION, waking it if it is idle. Plan:
// docs/superpowers/plans/2026-11-24-comment-wakes-session.md, Task 5; the roads are CommentWake's (in Core), the inbox is
// CommentInbox's, and the waiter that reads it is claude-wait-hook.cmd (ClaudeSkill.WaitScript).
//
// For each of Petre's own comments (TaskService.Commented, which leaves out a session's comments, Deskbar's own lines and
// his command run from inside a session), on a task that has had a Claude session:
//
//   1. the latest `claude agents --json` (ClaudeSessions.Live, a replay of the last read; the first comment after a start
//      waits for the first read rather than guessing),
//   2. CommentWake.Plan picks the session and the road,
//   3. Inbox: the message is appended to its inbox file; its waiter wakes it, or its next turn's waiter finds it.
//      Resume: it is resumed with the message (ClaudeSessions.ResumeWith), and a resume refused because the list shows
//      it running after all falls back to the inbox, so the comment is never dropped.
//      StopThenResume: `claude stop`, which returns once the process is gone (measured 2026-11-23), then the same resume.
//      Nothing: one line of Deskbar's in the thread saying why.
//      FreshRun: a recurring task between runs with no session listed starts a run now (startRun, RecurringRunner.RunNow;
//      task 0b78269e, spec docs/superpowers/specs/2026-11-26-recurring-tasks-design.md), even one that never had a session.
//
// A plain note (no session ever) is skipped without a word: Petre commenting on his own note is not asking Claude.
// The message is SessionBriefing.Commented, with pasted pictures' paths made absolute (TaskService.ExpandImages).
sealed class CommentWaker : IDisposable
{
    readonly TaskService tasks;
    readonly IObservable<IReadOnlyCollection<ClaudeAgent>> live;
    readonly Func<TaskSession, bool> hasTranscript;
    readonly Func<TaskSession, bool> endedOnApiError;
    readonly CommentInbox inbox;
    readonly Func<Guid, TaskSession, string, Task<Result>> resumeWith;
    readonly Func<string, Task<Result>> stop;
    readonly Action<string>? trace;
    readonly Func<Guid, Result>? startRun;
    readonly IDisposable subscription;

    // startRun: a run of a recurring task between runs, started now (RecurringRunner.RunNow in the App; task 0b78269e,
    // spec docs/superpowers/specs/2026-11-26-recurring-tasks-design.md, "A run ends"). CommentWake.Plan answers FreshRun
    // for such a task when none of its sessions is listed, and the comment then starts a fresh run that reads the thread,
    // as "Wake now" does. Without one (tests that do not need it), such a comment starts nothing and the thread says so.
    public CommentWaker(TaskService tasks, Func<bool> enabled, IObservable<IReadOnlyCollection<ClaudeAgent>> live, Func<TaskSession, bool> hasTranscript,
        Func<TaskSession, bool> endedOnApiError, CommentInbox inbox, Func<Guid, TaskSession, string, Task<Result>> resumeWith, Func<string, Task<Result>> stop,
        Action<string>? trace = null, Func<Guid, Result>? startRun = null)
    {
        this.tasks = tasks;
        this.live = live;
        this.hasTranscript = hasTranscript;
        this.endedOnApiError = endedOnApiError;
        this.inbox = inbox;
        this.resumeWith = resumeWith;
        this.stop = stop;
        this.trace = trace;
        this.startRun = startRun;
        // One comment at a time, in order, each with the latest list (Take(1) of the replay).
        // Only while sessions keep their tasks (AppState.ClaudeTasksInEffect): otherwise there is no waiter and no list.
        // REEVALUATE (task 3dd07539) rides the same roads: a press of a task's Reevaluate button (TaskService.Reevaluations)
        // is delivered exactly as a comment is, with SessionBriefing.Reevaluate as its message, in the same one-at-a-time
        // queue, so a press and a comment never race each other into one session.
        var comments = tasks.Commented.Select(commented => new Delivery(commented.TaskId, "a comment",
            task => SessionBriefing.Commented(task, tasks.ExpandImages(commented.Comment.Markdown), commented.Comment.ByManager)));
        var reevaluations = tasks.Reevaluations.Select(taskId => new Delivery(taskId, "a reevaluate request", SessionBriefing.Reevaluate)
        {
            // CommentWake's own words name a comment, which a press of Reevaluate is not.
            Unsent = "No session of this task can be resumed, so Claude was not asked to reevaluate it.",
        });
        // A STALLED TASK'S ASK (task 8710ac09) rides them too: a Claude task quiet for 15 minutes (TaskService.StalledNudges)
        // has its session asked whether it waits on Petre (SessionBriefing.StalledNudge), and only a session that does not
        // answer has its task handed to him (StalledTasks). When no session can be reached at all, nobody can answer, so
        // the task is handed to him at once (TaskService.NudgeUndelivered) instead of after the answer's five minutes.
        var stalled = tasks.StalledNudges.Select(taskId => new Delivery(taskId, "a stalled task's ask", SessionBriefing.StalledNudge)
        {
            Unsent = "No session of this task could be reached to ask whether it waits on you, so it came to you.",
            Undelivered = () => tasks.NudgeUndelivered(taskId),
        });
        // A SET-ASIDE QUESTION WRITTEN AFRESH (task 0cac3082) rides them too: a session whose turn ended with its question
        // still set aside is asked to look at the facts and ask again (SessionBriefing.Regenerate) instead of having the old
        // text put back. When no session can be reached, the old question comes back to Petre at once
        // (TaskService.RegenerateUndelivered), so it is never left hidden.
        var regenerations = tasks.Regenerations.Select(taskId => new Delivery(taskId, "a request to write the question afresh", SessionBriefing.Regenerate)
        {
            Unsent = "No session of this task could be asked to write its question afresh, so the question came back as it was.",
            Undelivered = () => tasks.RegenerateUndelivered(taskId),
            // Dropped when it is no longer due (TaskService.IsRegenerating): the grace already put the old question back,
            // or the session asked or answered meanwhile, while this waited behind another delivery.
            StillDue = () => tasks.IsRegenerating(taskId),
        });
        // A DEPLOY ANOTHER TASK REPORTED (task 43765c61, DeployNotices) rides them too: the session of a task whose merged
        // pull request may be in that deploy is asked to check and report it (SessionBriefing.DeployNotice). Deskbar
        // already wrote why in the thread (TaskService.MarkDeployed); a task no session can be reached on just keeps that line.
        var deploys = tasks.DeployNoticed.Select(notice => new Delivery(notice.TaskId, "a deploy notice",
            task => SessionBriefing.DeployNotice(task, notice))
        {
            Unsent = "No session of this task could be reached to tell it about that deploy.",
            // Dropped when the task reported the deploy, finished or was put aside while this waited behind another delivery.
            StillDue = () => tasks.StillWaitsOnDeploy(notice),
        });
        // A NUDGE FROM DESKBAR'S REPOSITORY WATCH (task c8e6967d, RepoWatch) rides them too: the base of the task's pull
        // request moved, it conflicts, it merged, or the runs on its merge commit finished. Quiet: it leaves no line in the
        // thread, delivered or not, because a minute-by-minute watch would otherwise fill every thread with its own news.
        // A session that cannot be reached just misses it; the stalled ask and the briefing's own rules still cover it.
        var watched = tasks.WatchNudges.Select(nudge => new Delivery(nudge.TaskId, "a watch nudge", task => SessionBriefing.WatchNudge(task, nudge.Text))
        {
            Quiet = true,
            // Review finding (task c8e6967d): dropped when the task turned Done or was closed while this waited behind
            // another delivery. A nudge reaching a finished task resumes its session for nothing.
            StillDue = () => tasks.Board.Tasks.Any(t => t.Id == nudge.TaskId && t is { IsOpen: true } && t.Status != Deskbar.Core.Tasks.TaskStatus.Done),
        });
        subscription = comments.Merge(reevaluations).Merge(stalled).Merge(regenerations).Merge(deploys).Merge(watched)
            .Where(_ => enabled())
            .Select(delivery => live.Take(1).SelectMany(listed => Observable.FromAsync(() => Deliver(delivery, listed)))
                // One delivery that throws must not end the waker for the rest of the run (final review, I5): it is traced,
                // and the comments after it still go.
                .Catch((Exception e) =>
                {
                    trace?.Invoke($"comment waker: {delivery.What} on {delivery.TaskId} was not delivered: {e.Message}");
                    return Observable.Empty<System.Reactive.Unit>();
                }))
            .Concat()
            .Subscribe(_ => { }, error => trace?.Invoke($"comment waker: stopped by {error.Message}"));
    }

    // What goes to a task's session: its task, what it is (for the trace) and its words, made from the task as it stands
    // when it is delivered.
    // Unsent, when set, is the thread's line in place of CommentWake's when no session can take it.
    // Undelivered, when set, runs when no session can take it, before the thread's line.
    // StillDue, when set, is asked just before delivery; false drops the delivery without a word.
    sealed record Delivery(Guid TaskId, string What, Func<TaskItem, string> Message)
    {
        public string? Unsent { get; init; }
        public Action? Undelivered { get; init; }
        public Func<bool>? StillDue { get; init; }
        // Quiet: when no session can take it, write no thread line (a watch nudge, task c8e6967d).
        public bool Quiet { get; init; }
    }

    async Task<System.Reactive.Unit> Deliver(Delivery delivery, IReadOnlyCollection<ClaudeAgent> listed)
    {
        var taskId = delivery.TaskId;
        // A recurring task between runs passes even with no session at all (task 0b78269e): one that has never run has
        // none, and the comment still starts its first run (FreshRun below). Any other task without a session is a plain
        // note, which has nothing to wake.
        if (tasks.Board.Tasks.FirstOrDefault(t => t.Id == taskId) is not { } task || (task.Sessions.Count == 0 && !task.IsBetweenRuns))
            return System.Reactive.Unit.Default;
        // A delivery that waited in the queue can be moot by now (StillDue, set by the regenerate request only).
        if (delivery.StillDue is { } due && !due())
        {
            trace?.Invoke($"comment waker: {delivery.What} on {taskId} dropped, no longer due");
            return System.Reactive.Unit.Default;
        }
        var message = delivery.Message(task);
        // A WAITER CAN BE GONE WHILE ITS GEN FILE SAYS IT IS ALIVE (task daeaa26c). A turn that ends on an API error, the
        // usage limit above all, runs no Stop hook, so no waiter is started after it, and the one before it may be the
        // very waiter whose comment woke the session for that turn. The gen file still names that waiter, good for a
        // month, so the comment went into an inbox nobody read (session 152ba85d, 2026-11-26: two of the manager's
        // comments waited there for an hour, until Petre typed in its window). So a session whose last turn ended on an
        // API error counts as lapsed: a background one is stopped and resumed with the comment, and anything else gets
        // the inbox as before. Woken before a usage limit resets, the turn hits the limit again, but the comment is then
        // in the conversation, and the keeper resumes the session once the limit resets (SessionKeeper's header).
        bool Lapsed(string sessionId) =>
            inbox.Lapsed(sessionId) || task.Sessions.Any(session => session.Id == sessionId && endedOnApiError(session));
        switch (CommentWake.Plan(task, listed.ToList(), hasTranscript, Lapsed))
        {
            case WakePlan.Inbox(var sessionId):
                Into(taskId, sessionId, message);
                break;
            case WakePlan.Resume(var session):
                await Resumed(taskId, session, message);
                break;
            case WakePlan.StopThenResume(var session, var shortId):
                (await stop(shortId)).TapError(e => trace?.Invoke($"comment waker: stop of {shortId} failed: {e}"));
                await Resumed(taskId, session, message);
                break;
            case WakePlan.Nothing(var why):
                delivery.Undelivered?.Invoke();
                if (!delivery.Quiet) tasks.Note(taskId, delivery.Unsent ?? why);
                break;
            // A recurring task between runs (see startRun at the constructor): the comment is already in the thread, which
            // the new run reads, so nothing is carried. A run that cannot start says why in the thread, as Nothing does.
            case WakePlan.FreshRun:
                (startRun?.Invoke(taskId) ?? Result.Failure("Deskbar cannot start runs yet"))
                    .Tap(() => trace?.Invoke($"comment waker: {delivery.What} on {taskId} starts a run of the recurring task"))
                    .TapError(e => tasks.Note(taskId, $"No run was started for {delivery.What} between runs: {e}"));
                break;
        }
        return System.Reactive.Unit.Default;
    }

    // A resume that did not happen (the session runs after all, or Claude refused) leaves the comment in its inbox,
    // where the session's next waiter finds it.
    async Task Resumed(Guid taskId, TaskSession session, string message)
    {
        // A resume whose call threw is a resume that did not happen: the comment goes to the inbox like any other refusal.
        Result result;
        try { result = await resumeWith(taskId, session, message); }
        catch (Exception e) { result = Result.Failure(e.Message); }
        trace?.Invoke($"comment waker: {session.Id} {(result.IsSuccess ? "resumed with the comment" : $"not resumed ({result.Error}), the comment waits in its inbox")}");
        if (result.IsFailure) Into(taskId, session.Id, message);
    }

    void Into(Guid taskId, string sessionId, string message) =>
        inbox.Deliver(sessionId, message)
            .Tap(() => trace?.Invoke($"comment waker: a comment is in {sessionId}'s inbox"))
            .TapError(e => tasks.Note(taskId, e));

    public void Dispose() => subscription.Dispose();
}
