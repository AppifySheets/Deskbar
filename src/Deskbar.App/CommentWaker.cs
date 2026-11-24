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
//
// A plain note (no session ever) is skipped without a word: Petre commenting on his own note is not asking Claude.
// The message is SessionBriefing.Commented, with pasted pictures' paths made absolute (TaskService.ExpandImages).
sealed class CommentWaker : IDisposable
{
    readonly TaskService tasks;
    readonly IObservable<IReadOnlyCollection<ClaudeAgent>> live;
    readonly Func<TaskSession, bool> hasTranscript;
    readonly CommentInbox inbox;
    readonly Func<Guid, TaskSession, string, Task<Result>> resumeWith;
    readonly Func<string, Task<Result>> stop;
    readonly Action<string>? trace;
    readonly IDisposable subscription;

    public CommentWaker(TaskService tasks, Func<bool> enabled, IObservable<IReadOnlyCollection<ClaudeAgent>> live, Func<TaskSession, bool> hasTranscript,
        CommentInbox inbox, Func<Guid, TaskSession, string, Task<Result>> resumeWith, Func<string, Task<Result>> stop,
        Action<string>? trace = null)
    {
        this.tasks = tasks;
        this.live = live;
        this.hasTranscript = hasTranscript;
        this.inbox = inbox;
        this.resumeWith = resumeWith;
        this.stop = stop;
        this.trace = trace;
        // One comment at a time, in order, each with the latest list (Take(1) of the replay).
        // Only while sessions keep their tasks (AppState.ClaudeTasksInEffect): otherwise there is no waiter and no list.
        subscription = tasks.Commented
            .Where(_ => enabled())
            .Select(commented => live.Take(1).SelectMany(listed => Observable.FromAsync(() => Deliver(commented.TaskId, commented.Comment, listed))))
            .Concat()
            .Subscribe(_ => { }, error => trace?.Invoke($"comment waker: stopped by {error.Message}"));
    }

    async Task<System.Reactive.Unit> Deliver(Guid taskId, TaskComment comment, IReadOnlyCollection<ClaudeAgent> listed)
    {
        if (tasks.Board.Tasks.FirstOrDefault(t => t.Id == taskId) is not { Sessions.Count: > 0 } task) return System.Reactive.Unit.Default;
        var message = SessionBriefing.Commented(task, tasks.ExpandImages(comment.Markdown));
        switch (CommentWake.Plan(task, listed.ToList(), hasTranscript, inbox.Lapsed))
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
                tasks.Note(taskId, why);
                break;
        }
        return System.Reactive.Unit.Default;
    }

    // A resume that did not happen (the session runs after all, or Claude refused) leaves the comment in its inbox,
    // where the session's next waiter finds it.
    async Task Resumed(Guid taskId, TaskSession session, string message)
    {
        var result = await resumeWith(taskId, session, message);
        trace?.Invoke($"comment waker: {session.Id} {(result.IsSuccess ? "resumed with the comment" : $"not resumed ({result.Error}), the comment waits in its inbox")}");
        if (result.IsFailure) Into(taskId, session.Id, message);
    }

    void Into(Guid taskId, string sessionId, string message) =>
        inbox.Deliver(sessionId, message)
            .Tap(() => trace?.Invoke($"comment waker: a comment is in {sessionId}'s inbox"))
            .TapError(e => tasks.Note(taskId, e));

    public void Dispose() => subscription.Dispose();
}
