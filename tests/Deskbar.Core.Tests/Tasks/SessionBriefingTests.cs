using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
// System.Threading.Tasks (an implicit using) has a TaskStatus too; the alias says which one this file means.
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests.Tasks;

// SessionBriefing: the text a Claude Code session reads from Deskbar's hooks (plan Task 8). Plain sentences for Claude,
// naming the session's id and the exact commands, never an em dash, and short: Claude Code puts a hook's output into
// the session's context, so a long one costs every session that starts.
public sealed class SessionBriefingTests
{
    static readonly DateTimeOffset At = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
    const string Sid = "92943450-aaaa-bbbb-cccc-1234567890ab";

    static ClaudeHookEvent Start(string source = "startup") =>
        new("SessionStart", Sid, @"C:\repos\TaskSpaces", source, Headless: false);

    static TaskItem Task(string title, TaskStatus status = TaskStatus.InProgress, Maybe<Guid>? workspace = null) =>
        new(Guid.NewGuid(), title, workspace ?? Guid.NewGuid(), status, "", Maybe<string>.None, At, At, At, [], []);

    static readonly string[] Dashes = ["\u2014", "\u2013"];

    [Fact]
    public void A_new_session_is_told_its_id_the_offered_tasks_and_how_to_join_or_create()
    {
        var offered = new[] { Task("Tasks board"), Task("Dishwasher tablets", TaskStatus.Backlog) };

        var text = SessionBriefing.Start(Start(), Maybe<TaskItem>.None, offered);

        Assert.Contains(Sid, text);
        Assert.Contains("Tasks board", text);
        Assert.Contains("Dishwasher tablets", text);
        Assert.Contains(offered[0].Id.ToString()[..8], text);
        Assert.Contains($"task join <id> --session {Sid}", text);
        Assert.Contains($"task new \"<title>\" --session {Sid}", text);
        Assert.DoesNotContain(Dashes, d => text.Contains(d));
    }

    [Fact]
    public void A_session_with_a_task_is_told_its_task_and_not_offered_others()
    {
        var own = Task("Tasks board") with { StatusLine = "waiting for CI" };

        var text = SessionBriefing.Start(Start("resume"), own, []);

        Assert.Contains("Tasks board", text);
        Assert.Contains("waiting for CI", text);
        Assert.Contains($"task status-line \"<what you are doing now>\" --session {Sid}", text);
        Assert.Contains($"task ask \"<question>\" --session {Sid}", text);
        Assert.Contains($"task done --session {Sid}", text);
        Assert.DoesNotContain("task join <id>", text);
    }

    // Fifty offered tasks with long titles: titles cut at 80 characters, the list cut at 20 with a pointer at the rest,
    // and the whole well under Claude Code's 10,000 characters for a hook's context.
    [Fact]
    public void The_briefing_stays_under_10000_characters()
    {
        var offered = Enumerable.Range(1, 50).Select(i => Task($"Task {i} " + new string('x', 300))).ToArray();

        var text = SessionBriefing.Start(Start(), Maybe<TaskItem>.None, offered);

        Assert.True(text.Length < 10_000, $"{text.Length} characters");
        Assert.Contains("and 30 more: deskbar.cmd task list", text);
        Assert.DoesNotContain(new string('x', 81), text);
        Assert.Contains("Task 20 ", text);
        Assert.DoesNotContain("Task 21 ", text);
    }

    [Fact]
    public void A_prompt_reminds_the_session_of_its_task_in_one_line()
    {
        var own = Task("Tasks board");

        var text = SessionBriefing.Prompt(Sid, own, Maybe<TaskItem>.None);

        Assert.Single(text.Trim().Split('\n'));
        Assert.Contains("Tasks board", text);
        Assert.Contains(Sid, text);
        Assert.DoesNotContain(Dashes, d => text.Contains(d));
    }

    [Fact]
    public void A_prompt_after_the_user_closed_the_task_says_new_work_needs_a_new_task()
    {
        var closed = Task("Tasks board", TaskStatus.Closed);

        var text = SessionBriefing.Prompt(Sid, Maybe<TaskItem>.None, closed);

        Assert.Contains("closed", text);
        Assert.Contains("Tasks board", text);
        Assert.Contains($"task new \"<title>\" --session {Sid}", text);
    }

    [Fact]
    public void A_prompt_with_no_task_says_how_to_get_one() =>
        Assert.Contains($"task join <id> --session {Sid}", SessionBriefing.Prompt(Sid, Maybe<TaskItem>.None, Maybe<TaskItem>.None));
}
