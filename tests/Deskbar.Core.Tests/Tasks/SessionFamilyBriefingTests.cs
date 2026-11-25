using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
// System.Threading.Tasks (an implicit using) has a TaskStatus too; the alias says which one this file means.
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests.Tasks;

// SessionBriefing.Family: what a session on a subtask (or on a parent) reads about the tasks around it, so Petre can add
// a subtask with only a title (docs/superpowers/specs/2026-11-24-subtasks-design.md, "What a session reads"). Bounded,
// since the SessionStart briefing carries it and Claude Code caps a hook's context at 10,000 characters.
public sealed class SessionFamilyBriefingTests
{
    static readonly DateTimeOffset At = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
    const string Sid = "92943450-aaaa-bbbb-cccc-1234567890ab";
    static readonly string[] Dashes = ["\u2014", "\u2013"];

    static TaskItem Task(string title, TaskStatus status = TaskStatus.InProgress, params TaskSession[] sessions) =>
        new(Guid.NewGuid(), title, Guid.NewGuid(), status, "", Maybe<string>.None, At, At, At, sessions, []);

    static TaskSession Live(string id, string folder = @"E:\repos\TaskSpaces", int minutes = 0) =>
        new(id, folder, At.AddMinutes(minutes), Maybe<DateTimeOffset>.None);

    static TaskSession Ended(string id, int minutes = 0) =>
        new(id, @"E:\repos\TaskSpaces", At.AddMinutes(minutes), At.AddHours(1));

    static Maybe<string> TranscriptOf(TaskSession s) => s.Id.StartsWith("gone") ? Maybe<string>.None : $@"C:\claude\{s.Id}.jsonl";

    static readonly TaskItem Parent = Task("Subtasks support on deskbar", TaskStatus.InProgress,
            Live("e6b349eb-0000-0000-0000-000000000001"), Ended("gone0000-0000-0000-0000-000000000002"))
        with { StatusLine = "PR 512 open", Description = "Sessions should see the parent and siblings" };

    static readonly TaskItem Child = Task("Bar breadcrumb", TaskStatus.Backlog) with { ParentId = Parent.Id };

    static readonly TaskItem Sibling = Task("Family in the join reply", TaskStatus.InProgress, Live("3f4a5b6c-0000-0000-0000-000000000003"))
        with { ParentId = Parent.Id, StatusLine = "tests green" };

    [Fact]
    public void A_subtask_reads_its_parent_its_sessions_and_its_siblings()
    {
        var text = SessionBriefing.Family(new TaskFamily(Child, Parent, [Sibling], []), TranscriptOf);

        Assert.Contains($"This task is a subtask of {Parent.Id.ToString()[..8]} \"Subtasks support on deskbar\" (in progress, status line: \"PR 512 open\")", text);
        Assert.Contains("The user's description of the parent: \"Sessions should see the parent and siblings\"", text);
        Assert.Contains("Sessions on the parent:", text);
        Assert.Contains(@"e6b349eb (live, folder E:\repos\TaskSpaces), transcript C:\claude\e6b349eb-0000-0000-0000-000000000001.jsonl", text);
        // A session whose transcript is not found is listed without one.
        Assert.Contains(@"gone0000 (ended, folder E:\repos\TaskSpaces)" + "\n", text);
        Assert.Contains("Before you start, read the end of the parent session's transcript to learn what was done and decided, and look at the siblings below so you do not redo or collide with their work.", text);
        Assert.Contains("Its other subtasks:", text);
        Assert.Contains($"{Sibling.Id.ToString()[..8]} \"Family in the join reply\" (in progress, status line: \"tests green\")", text);
        Assert.Contains(@"3f4a5b6c (live, folder E:\repos\TaskSpaces), transcript", text);
        Assert.Contains("A live session on this machine can be reached with Claude Code's own session messaging (ListAgents, SendMessage).", text);
    }

    [Fact]
    public void A_parent_reads_its_subtasks_with_their_sessions()
    {
        var text = SessionBriefing.Family(new TaskFamily(Parent, Maybe<TaskItem>.None, [], [Child, Sibling]), TranscriptOf);

        Assert.Contains("This task has subtasks:", text);
        Assert.Contains("Before you start, look at its subtasks below so you do not redo or collide with their work.", text);
        Assert.Contains("\"Bar breadcrumb\" (backlog", text);
        Assert.Contains("\"Family in the join reply\"", text);
        Assert.Contains("3f4a5b6c (live", text);
        Assert.DoesNotContain("subtask of", text);
    }

    [Fact]
    public void A_closed_parent_is_still_described()
    {
        var closed = Parent with { Status = TaskStatus.Closed };

        var text = SessionBriefing.Family(new TaskFamily(Child, closed, [], []), TranscriptOf);

        Assert.Contains("\"Subtasks support on deskbar\" (closed", text);
    }

    [Fact]
    public void Only_the_last_three_unsettled_comments_of_the_parent_are_read()
    {
        TaskComment Comment(int n, bool settled = false) =>
            new(Guid.NewGuid(), n % 2 == 0 ? Maybe<string>.None : "s1", At.AddMinutes(n), $"comment {n} " + new string('x', 300))
            { Settled = settled ? At.AddHours(2) : Maybe<DateTimeOffset>.None };
        var parent = Parent with { Comments = [Comment(1), Comment(2), Comment(3), Comment(4), Comment(5), Comment(6, settled: true)] };

        var text = SessionBriefing.Family(new TaskFamily(Child, parent, [], []), TranscriptOf);

        Assert.DoesNotContain("comment 1 ", text);
        Assert.DoesNotContain("comment 2 ", text);
        Assert.Contains("comment 3 ", text);
        Assert.Contains("comment 5 ", text);
        Assert.DoesNotContain("comment 6 ", text);
        // Each is cut at 200 characters.
        Assert.DoesNotContain(new string('x', 200), text);
    }

    [Fact]
    public void A_large_family_is_bounded()
    {
        var sessions = Enumerable.Range(0, 5).Select(i => Live($"{i}{new string('a', 7)}-0000-0000-0000-00000000000{i}", minutes: i)).ToArray();
        var subtasks = Enumerable.Range(0, 20)
            .Select(i => Task(new string('t', 300), TaskStatus.InProgress, sessions) with { ParentId = Parent.Id, Description = new string('d', 1000) })
            .ToArray();
        var parent = Parent with { Sessions = sessions, Title = new string('p', 300), Description = new string('d', 1000) };

        var text = SessionBriefing.Family(new TaskFamily(subtasks[0], parent, subtasks[1..], []), TranscriptOf);

        Assert.True(text.Length <= 4000, $"{text.Length} characters");
        Assert.Contains($"deskbar.cmd task family {subtasks[0].Id.ToString()[..8]}", text);
        // At most three sessions per task: the parent's list stops after three.
        var parentSessions = text.Split('\n').SkipWhile(l => l != "Sessions on the parent:").Skip(1).TakeWhile(l => l.StartsWith("  ")).Count();
        Assert.Equal(3, parentSessions);
    }

    [Fact]
    public void More_than_ten_siblings_points_at_task_family()
    {
        var siblings = Enumerable.Range(0, 12).Select(i => Task($"sibling {i}", TaskStatus.Backlog) with { ParentId = Parent.Id }).ToArray();

        var text = SessionBriefing.Family(new TaskFamily(Child, Parent, siblings, []), TranscriptOf);

        Assert.Contains("sibling 9", text);
        Assert.DoesNotContain("sibling 10", text);
        Assert.Contains($"and 2 more: deskbar.cmd task family {Child.Id.ToString()[..8]}", text);
    }

    [Fact]
    public void Live_sessions_come_first_then_the_latest_joined()
    {
        var parent = Parent with { Sessions = [Ended("aaaaaaaa-1", 1), Ended("bbbbbbbb-2", 5), Live("cccccccc-3", minutes: 0), Ended("dddddddd-4", 9)] };

        var text = SessionBriefing.Family(new TaskFamily(Child, parent, [], []), TranscriptOf);

        var lines = text.Split('\n').SkipWhile(l => l != "Sessions on the parent:").Skip(1).Take(3).ToArray();
        Assert.StartsWith("  cccccccc", lines[0]);
        Assert.StartsWith("  dddddddd", lines[1]);
        Assert.StartsWith("  bbbbbbbb", lines[2]);
    }

    [Fact]
    public void No_live_session_no_messaging_line()
    {
        var parent = Parent with { Sessions = [Ended("aaaaaaaa-1")] };

        var text = SessionBriefing.Family(new TaskFamily(Child, parent, [], []), TranscriptOf);

        Assert.DoesNotContain("SendMessage", text);
    }

    [Fact]
    public void The_family_text_has_no_dashes()
    {
        var text = SessionBriefing.Family(new TaskFamily(Child, Parent, [Sibling], []), TranscriptOf);
        Assert.DoesNotContain(Dashes, d => text.Contains(d));
    }

    // --- in the SessionStart briefing and the prompt line ----------------------------------------

    static ClaudeHookEvent Start(string source = "resume") =>
        new("SessionStart", Sid, @"C:\repos\TaskSpaces", source, Headless: false);

    [Fact]
    public void The_start_briefing_carries_the_family_after_the_own_task()
    {
        var text = SessionBriefing.Start(Start(), Child, [], family: "FAMILY BLOCK");

        Assert.True(text.IndexOf("Your task is", StringComparison.Ordinal) < text.IndexOf("FAMILY BLOCK", StringComparison.Ordinal));
        Assert.True(text.IndexOf("FAMILY BLOCK", StringComparison.Ordinal) < text.IndexOf("Free text", StringComparison.Ordinal));
    }

    [Fact]
    public void The_start_briefing_without_a_family_is_unchanged() =>
        Assert.Equal(SessionBriefing.Start(Start(), Child, []), SessionBriefing.Start(Start(), Child, [], family: ""));

    [Fact]
    public void A_subtasks_prompt_line_points_at_its_family()
    {
        var text = SessionBriefing.Prompt(Sid, Maybe.From(Child with { Status = TaskStatus.InProgress }), Maybe<TaskItem>.None);

        Assert.Contains($"It is a subtask of {Parent.Id.ToString()[..8]}: deskbar.cmd task family --session {Sid} shows the tasks around it.", text);
        Assert.Equal(1, text.Count(c => c == '\n'));
    }

    [Fact]
    public void A_top_tasks_prompt_line_says_nothing_of_family()
    {
        var text = SessionBriefing.Prompt(Sid, Maybe.From(Parent), Maybe<TaskItem>.None);

        Assert.DoesNotContain("subtask", text);
    }

    // Task aa42794f: a Deskbar-authored comment on the parent reads as "Deskbar", not as a session.
    [Fact]
    public void A_deskbar_comment_on_the_parent_is_labelled_deskbar()
    {
        var parent = Parent with { Comments = [new TaskComment(Guid.NewGuid(), TaskComment.DeskbarAuthor, At, "Resumed.")] };

        var text = SessionBriefing.Family(new TaskFamily(Child, parent, [], []), TranscriptOf);

        Assert.Contains("  Deskbar: \"Resumed.\"", text);
        Assert.DoesNotContain("a session: \"Resumed.\"", text);
    }
}
