using System.IO;
using System.Reactive.Subjects;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core.Tasks;

namespace Deskbar.Windows.Tests;

// CommentWaker: Petre's comment on a task reaches the task's Claude session (plan
// docs/superpowers/plans/2026-11-24-comment-wakes-session.md, Task 5). The list of running sessions, the transcripts, the
// resume and the stop are fakes here; ClaudeSessionsTests covers the real resume and stop, ClaudeWaitScriptTests the
// waiter that reads the inbox.
public sealed class CommentWakerTests : IDisposable
{
    static readonly DateTimeOffset At = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    readonly string folder = Directory.CreateTempSubdirectory("deskbar-waker-").FullName;
    readonly TaskService tasks = new(new MemoryStore(), () => At);
    readonly ReplaySubject<IReadOnlyCollection<ClaudeAgent>> live = new(1);
    readonly List<(Guid Task, string Session, string Message)> resumed = [];
    readonly List<string> stopped = [];
    readonly CommentInbox inbox;

    Func<string, Result> resumeAnswer = _ => Result.Success();
    bool transcripts = true;
    bool endedOnApiError;

    public CommentWakerTests() => inbox = new CommentInbox(folder, () => At);

    public void Dispose() => Directory.Delete(folder, recursive: true);

    sealed class MemoryStore : ITaskStore
    {
        public Result<TaskBoard> Load() => TaskBoard.Empty;

        public Result Save(TaskBoard board) => Result.Success();
    }

    bool enabled = true;

    // With "Claude Code sessions keep their tasks" off there are no waiters and no list: a comment is only a comment.
    [Fact]
    public void With_sessions_not_keeping_tasks_nothing_is_sent()
    {
        var id = ClaudeTask("aaaaaaaa-1");
        enabled = false;
        live.OnNext([]);
        using var waker = Waker();

        tasks.Comment(id, Maybe<string>.None, "just a note to self");

        Assert.Empty(resumed);
        Assert.DoesNotContain(tasks.Board.Tasks.Single().Comments, c => c.ByDeskbar);
    }

    CommentWaker Waker() => new(tasks, () => enabled, live, _ => transcripts, _ => endedOnApiError, inbox,
        (task, session, message) =>
        {
            resumed.Add((task, session.Id, message));
            return Task.FromResult(resumeAnswer(session.Id));
        },
        shortId =>
        {
            stopped.Add(shortId);
            return Task.FromResult(Result.Success());
        },
        startRun: id =>
        {
            runs.Add(id);
            return Result.Success();
        });

    // The recurring runs a comment asked for (CommentWaker's startRun, task 0b78269e), by task.
    readonly List<Guid> runs = [];

    Guid ClaudeTask(string sessionId) => tasks.Create("paste images", Maybe<Guid>.None, tasks.NewSession(sessionId, @"E:\repos\Work")).Value.Id;

    static ClaudeAgent Running(string sessionId, bool background = true) =>
        new(sessionId, background ? sessionId[..8] : Maybe<string>.None, @"E:\repos\Work", background, false, "");

    string InboxText(string sessionId) => File.ReadAllText(Path.Combine(folder, sessionId + ".md"));

    [Fact]
    public void A_running_sessions_comment_lands_in_its_inbox()
    {
        var id = ClaudeTask("aaaaaaaa-1");
        inbox.Register("aaaaaaaa-1");
        live.OnNext([Running("aaaaaaaa-1")]);
        using var waker = Waker();

        tasks.Comment(id, Maybe<string>.None, "look at this");

        Assert.Contains("look at this", InboxText("aaaaaaaa-1"));
        Assert.StartsWith("Deskbar: the user commented on your task", InboxText("aaaaaaaa-1"));
        Assert.Empty(resumed);
    }

    [Fact]
    public void Two_comments_wait_together()
    {
        var id = ClaudeTask("aaaaaaaa-1");
        inbox.Register("aaaaaaaa-1");
        live.OnNext([Running("aaaaaaaa-1")]);
        using var waker = Waker();

        tasks.Comment(id, Maybe<string>.None, "first");
        tasks.Comment(id, Maybe<string>.None, "second");

        var text = InboxText("aaaaaaaa-1");
        Assert.True(text.IndexOf("first", StringComparison.Ordinal) < text.IndexOf("second", StringComparison.Ordinal));
    }

    [Fact]
    public void A_session_that_does_not_run_is_resumed_with_the_comment()
    {
        var id = ClaudeTask("aaaaaaaa-1");
        live.OnNext([]);
        using var waker = Waker();

        tasks.Comment(id, Maybe<string>.None, "wake up");

        var (task, session, message) = Assert.Single(resumed);
        Assert.Equal((id, "aaaaaaaa-1"), (task, session));
        Assert.Contains("wake up", message);
    }

    // Idle past its waiter's month: a background session is stopped, then resumed with the comment.
    [Fact]
    public void A_lapsed_background_session_is_stopped_then_resumed()
    {
        var id = ClaudeTask("aaaaaaaa-1");
        live.OnNext([Running("aaaaaaaa-1")]);
        using var waker = Waker();

        tasks.Comment(id, Maybe<string>.None, "still there?");

        Assert.Equal(["aaaaaaaa"], stopped);
        Assert.Single(resumed);
    }

    // Task daeaa26c, session 152ba85d: its waiter fired, the turn it woke died on the usage limit, and a turn that ends on
    // an API error runs no Stop hook, so no waiter followed. The gen file still says one is alive; the comment must not
    // go into an inbox nobody reads.
    [Fact]
    public void A_background_session_whose_last_turn_died_on_an_api_error_is_stopped_then_resumed()
    {
        var id = ClaudeTask("aaaaaaaa-1");
        inbox.Register("aaaaaaaa-1");
        endedOnApiError = true;
        live.OnNext([Running("aaaaaaaa-1")]);
        using var waker = Waker();

        tasks.Comment(id, Maybe<string>.None, "carry on");

        Assert.Equal(["aaaaaaaa"], stopped);
        Assert.Contains("carry on", Assert.Single(resumed).Message);
        Assert.False(File.Exists(Path.Combine(folder, "aaaaaaaa-1.md")));
    }

    // A resume refused because the list still shows the session (it came back meanwhile): the comment waits in its inbox.
    [Fact]
    public void A_refused_resume_falls_back_to_the_inbox()
    {
        var id = ClaudeTask("aaaaaaaa-1");
        live.OnNext([]);
        resumeAnswer = _ => Result.Failure("still running");
        using var waker = Waker();

        tasks.Comment(id, Maybe<string>.None, "keep this");

        Assert.Contains("keep this", InboxText("aaaaaaaa-1"));
    }

    // Final review, I5: one delivery that throws (a resume whose call faulted) does not stop the waker; the next comment
    // still goes, and the one that faulted waits in the inbox.
    [Fact]
    public void A_fault_in_one_delivery_does_not_stop_the_next()
    {
        var id = ClaudeTask("aaaaaaaa-1");
        live.OnNext([]);
        var calls = 0;
        resumeAnswer = _ => ++calls == 1 ? throw new InvalidOperationException("the call faulted") : Result.Success();
        using var waker = Waker();

        tasks.Comment(id, Maybe<string>.None, "first");
        tasks.Comment(id, Maybe<string>.None, "second");

        Assert.Equal(2, resumed.Count);
        Assert.Contains("first", InboxText("aaaaaaaa-1"));
    }

    [Fact]
    public void A_task_with_nothing_to_resume_says_so_in_its_thread()
    {
        var id = ClaudeTask("aaaaaaaa-1");
        transcripts = false;
        live.OnNext([]);
        using var waker = Waker();

        tasks.Comment(id, Maybe<string>.None, "anyone?");

        Assert.Empty(resumed);
        Assert.Contains(tasks.Board.Tasks.Single().Comments, c => c.ByDeskbar && c.Markdown.Contains("was not sent to Claude"));
    }

    // Only Petre's own comments wake: a session's comment does nothing, and neither does a note with no session (a plain
    // note of his has no session to wake either, and says nothing about it).
    [Fact]
    public void A_sessions_comment_and_a_plain_note_wake_nothing()
    {
        var id = ClaudeTask("aaaaaaaa-1");
        var note = tasks.AddNote("Dishwasher tablets", Maybe<Guid>.None).Value.Id;
        live.OnNext([]);
        using var waker = Waker();

        tasks.Comment(id, "aaaaaaaa-1", "a session's own");
        tasks.Comment(note, Maybe<string>.None, "his note's comment");

        Assert.Empty(resumed);
        Assert.DoesNotContain(tasks.Board.Tasks.Single(t => t.Id == note).Comments, c => c.ByDeskbar);
    }

    // --- recurring tasks between runs (task 0b78269e, spec docs/superpowers/specs/2026-11-26-recurring-tasks-design.md) ---

    static TaskRepeat Hourly => new(TaskRepeats.Parse("every 1h").Value, TaskRepeat.DefaultModel, @"E:\repos\Work");

    // "A run ends": a comment on a recurring task between runs starts a run (a fresh session that reads the thread, the
    // comment included), as Wake now does. The last run's session, stopped and not listed, is not resumed with it.
    [Fact]
    public void A_comment_between_runs_starts_a_run_and_resumes_nothing()
    {
        var id = ClaudeTask("aaaaaaaa-1");
        Assert.True(tasks.SetRepeat(id, Hourly).IsSuccess);
        Assert.True(tasks.SetStatus(id, Deskbar.Core.Tasks.TaskStatus.Backlog).IsSuccess);
        Assert.True(tasks.Board.Tasks.Single().IsBetweenRuns);
        live.OnNext([]);
        using var waker = Waker();

        tasks.Comment(id, Maybe<string>.None, "check the staging one too");

        Assert.Equal([id], runs);
        Assert.Empty(resumed);
        Assert.Empty(stopped);
        Assert.DoesNotContain(tasks.Board.Tasks.Single().Comments, c => c.ByDeskbar);
    }

    // A recurring task that has never run has no session at all, and a comment on it still starts its first run: the
    // "no session, nothing to wake" shortcut is for plain notes only.
    [Fact]
    public void A_comment_on_a_recurring_task_that_never_ran_starts_a_run()
    {
        var id = tasks.AddNote("check the deploy", Maybe<Guid>.None).Value.Id;
        Assert.True(tasks.SetRepeat(id, Hourly).IsSuccess);
        live.OnNext([]);
        using var waker = Waker();

        tasks.Comment(id, Maybe<string>.None, "start with the logs");

        Assert.Equal([id], runs);
        Assert.DoesNotContain(tasks.Board.Tasks.Single().Comments, c => c.ByDeskbar);
    }
}
