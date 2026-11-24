using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
// System.Threading.Tasks (an implicit using) has a TaskStatus too; the alias says which one this file means.
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests.Tasks;

// How a comment Petre posts reaches the task's Claude session (CommentWake; docs/superpowers/specs/2026-11-24-task-images-design.md,
// PR 2, and the plan docs/superpowers/plans/2026-11-24-comment-wakes-session.md): to which session, by which road, and
// with what words.
public sealed class CommentWakeTests
{
    static readonly DateTimeOffset At = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
    static readonly Guid Id = Guid.Parse("d4ea6cbe-12ef-420c-8847-fa40c302ac48");

    static TaskSession Session(string id, int joinedMinutes, bool live = true) =>
        new(id, @"E:\repos\TaskSpaces", At.AddMinutes(joinedMinutes), live ? Maybe<DateTimeOffset>.None : At.AddMinutes(joinedMinutes + 30));

    static TaskItem Task(TaskStatus status, params TaskSession[] sessions) =>
        new(Id, "paste images", Guid.NewGuid(), status, "", Maybe<string>.None, At, At, At, sessions, []);

    static ClaudeAgent Listed(string sessionId, bool background = true) =>
        new(sessionId, background ? sessionId[..8] : Maybe<string>.None, @"E:\repos\TaskSpaces", background, false, "");

    static bool Everyone(TaskSession _) => true;

    static bool NeverLapsed(string _) => false;

    [Fact]
    public void A_listed_session_with_a_waiter_gets_the_comment_in_its_inbox()
    {
        var plan = CommentWake.Plan(Task(TaskStatus.InProgress, Session("aaaaaaaa-1", 0)), [Listed("aaaaaaaa-1")], Everyone, NeverLapsed);

        Assert.Equal(new WakePlan.Inbox("aaaaaaaa-1"), plan);
    }

    // Its waiter has lapsed (idle past the hook's timeout) and it runs in the background: stopped, then resumed with the
    // comment, because resuming a running session starts a copy (measured 2026-11-23).
    [Fact]
    public void A_lapsed_background_session_is_stopped_then_resumed()
    {
        var session = Session("aaaaaaaa-1", 0);

        var plan = CommentWake.Plan(Task(TaskStatus.InProgress, session), [Listed("aaaaaaaa-1")], Everyone, _ => true);

        Assert.Equal(new WakePlan.StopThenResume(session, "aaaaaaaa"), plan);
    }

    // A terminal or VS Code session cannot be restarted from outside, so the comment waits in its inbox for its next turn.
    [Fact]
    public void A_lapsed_interactive_session_gets_the_comment_in_its_inbox()
    {
        var plan = CommentWake.Plan(Task(TaskStatus.InProgress, Session("aaaaaaaa-1", 0)), [Listed("aaaaaaaa-1", background: false)], Everyone, _ => true);

        Assert.Equal(new WakePlan.Inbox("aaaaaaaa-1"), plan);
    }

    [Fact]
    public void A_session_that_is_not_running_is_resumed_with_the_comment()
    {
        var session = Session("aaaaaaaa-1", 0, live: false);

        var plan = CommentWake.Plan(Task(TaskStatus.Done, session), [], Everyone, NeverLapsed);

        Assert.Equal(new WakePlan.Resume(session), plan);
    }

    [Fact]
    public void A_task_with_no_session_that_can_be_resumed_wakes_nothing()
    {
        Assert.IsType<WakePlan.Nothing>(CommentWake.Plan(Task(TaskStatus.InProgress, Session("aaaaaaaa-1", 0, live: false)), [], _ => false, NeverLapsed));
        Assert.IsType<WakePlan.Nothing>(CommentWake.Plan(Task(TaskStatus.Backlog), [], Everyone, NeverLapsed));
    }

    // One comment, one session: the live one first, then the newest, as a double-click goes to (TaskViews.SessionToGoTo),
    // among those with a transcript.
    [Fact]
    public void The_live_newest_session_with_a_transcript_is_the_one()
    {
        var old = Session("aaaaaaaa-1", 0, live: false);
        var live = Session("bbbbbbbb-2", 5);
        var newestEnded = Session("cccccccc-3", 10, live: false);

        Assert.Equal(new WakePlan.Inbox("bbbbbbbb-2"), CommentWake.Plan(Task(TaskStatus.InProgress, old, live, newestEnded), [Listed("bbbbbbbb-2")], Everyone, NeverLapsed));
        Assert.Equal(new WakePlan.Resume(old), CommentWake.Plan(Task(TaskStatus.InProgress, old, newestEnded), [], s => s.Id != "cccccccc-3", NeverLapsed));
    }

    // --- the words ----------------------------------------------------------------------------------------------------

    [Fact]
    public void The_message_names_the_task_and_carries_the_comment_whole()
    {
        var text = SessionBriefing.Commented(Task(TaskStatus.InProgress), "Look at **this**\nsecond line");

        Assert.StartsWith("Deskbar: the user commented on your task d4ea6cbe \"paste images\":\n\nLook at **this**\nsecond line", text);
        Assert.DoesNotContain("Read tool", text);
        Assert.DoesNotContain("\u2014", text);
    }

    [Fact]
    public void A_comment_with_a_picture_says_to_read_it()
    {
        var text = SessionBriefing.Commented(Task(TaskStatus.InProgress), "See\n\n![image](<C:\\S\\task-images\\t\\1.png>)");

        Assert.Contains("![image](<C:\\S\\task-images\\t\\1.png>)", text);
        Assert.Contains("Read each image path above with your Read tool.", text);
    }

    // A comment on a finished task may be a question about it or more work; the session decides, as for a prompt.
    [Fact]
    public void A_done_task_carries_the_done_rule()
    {
        var text = SessionBriefing.Commented(Task(TaskStatus.Done), "is it shipped?");

        Assert.Contains("task join d4ea6cbe --session", text);
        Assert.Contains("leave it done", text);
    }
}
