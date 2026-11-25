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
        Assert.Contains($"task new - --session {Sid}", text);
        Assert.DoesNotContain(Dashes, d => text.Contains(d));
    }

    [Fact]
    public void A_session_with_a_task_is_told_its_task_and_not_offered_others()
    {
        var own = Task("Tasks board") with { StatusLine = "waiting for CI" };

        var text = SessionBriefing.Start(Start("resume"), own, []);

        Assert.Contains("Tasks board", text);
        Assert.Contains("waiting for CI", text);
        Assert.Contains($"task status-line - --session {Sid}", text);
        Assert.Contains($"task ask - --session {Sid}", text);
        Assert.Contains($"task done --session {Sid}", text);
        Assert.DoesNotContain("task join <id>", text);
    }

    // Review of PR B, I-1: free text goes on stdin through `-` and a heredoc, never on the command line, and the
    // briefing says so in as many words.
    [Fact]
    public void The_briefing_teaches_free_text_on_stdin()
    {
        var text = SessionBriefing.Start(Start(), Maybe<TaskItem>.None, []);

        Assert.Contains($"task ask - --session {Sid} <<'EOF'", text);
        Assert.Contains("Never put free text with quotes or & | < > on the command line", text);
    }

    // Task 06c2538b ("why didn't you set the task as waiting on me?"): sessions with a pull request ready to merge left a
    // comment and marked the task done, because the briefing's own example was a comment saying "merge it" and it kept
    // `task ask` for decisions. Done sits below the waiting block, so the merge waited unseen. The briefing now sends every
    // next move of the user's (a pull request to merge among them) through `task ask`, keeps `task done` for when nothing
    // waits on the user, and says what to do once the pull request is merged.
    [Fact]
    public void A_ready_pull_request_waits_on_the_user_through_ask_not_a_comment_and_done()
    {
        var text = SessionBriefing.Start(Start(), Task("Tasks board"), []);

        Assert.Contains("Merge [PR 512](https://github.com/owner/repo/pull/512): CI is green", text);
        Assert.Contains("when the next move is the user's (a pull request to merge, something to try live before it merges, a decision)", text);
        Assert.Contains("not while a pull request of yours waits to be merged", text);
        Assert.Contains($"task answered --session {Sid}, then task done", text);
        Assert.DoesNotContain("PR 512 is ready", text);
        Assert.DoesNotContain("(a PR to merge", text);
    }

    // Task b326761d (Petre: "when a pr is merged and a session is waiting for me to check it, it should say the task is
    // done and i will only close it if i test it and it works"): sessions read "something to try live" as a reason to ask
    // again once the pull request was merged, so finished work sat in Waiting on you. The briefing and the Stop hook's
    // nudge now say a merged pull request is done, with what to try in the status line, and closing is the user's test.
    [Fact]
    public void A_merged_pull_request_is_done_with_what_to_try_in_the_status_line_not_an_ask_to_test()
    {
        var text = SessionBriefing.Start(Start(), Task("Tasks board"), []);

        Assert.Contains("Once your pull request is merged, the task is done: do not ask the user to test it.", text);
        Assert.Contains("put what to try in the status line", text);
        Assert.Contains("the user closes the task once they have tried it and it works", text);
        Assert.Contains("If your pull request is merged, the task is done", SessionBriefing.ParkedNudge);
        Assert.Contains("task done with what to try in the status line", SessionBriefing.ParkedNudge);
    }

    // Task e52e33cb: a session is told to keep mechanical work out of its own conversation, on a cheaper model, and
    // to have the strongest model review the diff before the pull request. No model is named in either line.
    [Fact]
    public void Mechanical_work_goes_to_a_cheaper_subagent_and_the_diff_is_reviewed_by_the_strongest()
    {
        var text = SessionBriefing.Start(Start(), Task("Tasks board"), []);

        Assert.Contains("give test runs, builds, broad searches, rebases and other mechanical work to a subagent on a cheaper model than yours", text);
        Assert.Contains("Before you open a pull request, have a subagent on the strongest model you can use review the diff", text);
        Assert.DoesNotContain("opus", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sonnet", text, StringComparison.OrdinalIgnoreCase);
    }

    // Task 6e4dc116 (Petre: "still conflicting; you should keep rebasing open prs", then "make that a deskbar level change"):
    // every task session, in any repository, is told to keep its open pull request on top of its base, and to push the
    // rebase at once rather than hold it back while the tests run.
    [Fact]
    public void An_open_pull_request_is_kept_rebased_on_its_base()
    {
        var text = SessionBriefing.Start(Start(), Task("Tasks board"), []);

        Assert.Contains("While your pull request is open, keep it on top of its base without being asked", text);
        Assert.Contains("as soon as the base moves or the pull request conflicts, rebase it onto the latest base", text);
        Assert.Contains("push it straight away with --force-with-lease", text);
        // Task c121bab5: the tests are CI's job after the push, and a base move mid-run means rebasing again at once.
        Assert.Contains("a test run never does: let CI run the tests on what you pushed", text);
        Assert.Contains("if the base moves again while CI runs, rebase and push again at once", text);
    }

    // Task 08cdb052 ("when asking for action, prs should have links, or artifacts; make that clickable"): an ask copied
    // from the old example named "PR 490" with nothing to click. The briefing's ask now gives what to open as a link, and
    // the Stop hook's nudge says so too.
    [Fact]
    public void An_ask_gives_what_the_user_must_open_as_a_link()
    {
        var text = SessionBriefing.Start(Start(), Task("Tasks board"), []);

        Assert.Contains("as a markdown link [text](https://...), which the tasks panel makes clickable", text);
        Assert.DoesNotContain("Merge PR 512:", text);
        Assert.Contains("markdown link", SessionBriefing.ParkedNudge);
    }

    // Fifty offered tasks with long titles, long status lines and long questions (review of PR B, M-7): each is cut at
    // 80 characters, the list cut at 20 with a pointer at the rest, and the whole well under Claude Code's 10,000
    // characters for a hook's context.
    [Fact]
    public void The_briefing_stays_under_10000_characters()
    {
        var offered = Enumerable.Range(1, 50).Select(i => i % 2 == 0
            ? Task($"Task {i} " + new string('x', 300)) with { StatusLine = new string('s', 300) }
            : Task($"Task {i} " + new string('x', 300), TaskStatus.WaitingOnYou) with { Question = new string('q', 300) }).ToArray();

        var text = SessionBriefing.Start(Start(), Maybe<TaskItem>.None, offered);

        Assert.True(text.Length < 10_000, $"{text.Length} characters");
        Assert.Contains("and 30 more: deskbar.cmd task list", text);
        Assert.DoesNotContain(new string('x', 81), text);
        Assert.DoesNotContain(new string('s', 81), text);
        Assert.DoesNotContain(new string('q', 81), text);
        Assert.Contains(new string('s', 70), text);
        Assert.Contains(new string('q', 70), text);
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

    // 2026-11-23: a prompt leaves a Done task Done, so its reminder tells the session how to reopen it when the message is
    // more work (task join with the task's own id), and to leave it alone when it only asks about the finished work.
    [Fact]
    public void A_prompt_for_a_done_task_says_how_to_reopen_it()
    {
        var own = Task("Tasks board", TaskStatus.Done);

        var text = SessionBriefing.Prompt(Sid, own, Maybe<TaskItem>.None);

        Assert.Single(text.Trim().Split('\n'));
        Assert.Contains("done", text);
        Assert.Contains($"task join {own.Id.ToString()[..8]} --session {Sid}", text);
        Assert.DoesNotContain(Dashes, d => text.Contains(d));
    }

    // Ruling 49: a waiting task's reminder keeps the question open and says how to resolve it.
    [Fact]
    public void A_prompt_for_a_waiting_task_names_the_question_and_task_answered()
    {
        var own = Task("Tasks board", TaskStatus.WaitingOnYou) with { Question = "OK to merge? " + new string('q', 300) };

        var text = SessionBriefing.Prompt(Sid, own, Maybe<TaskItem>.None);

        Assert.Single(text.Trim().Split('\n'));
        Assert.Contains("OK to merge?", text);
        Assert.DoesNotContain(new string('q', 81), text);
        Assert.Contains($"task answered --session {Sid}", text);
        Assert.DoesNotContain(Dashes, d => text.Contains(d));
    }

    [Fact]
    public void The_briefing_teaches_task_answered()
    {
        var text = SessionBriefing.Start(Start(), Task("Tasks board"), []);

        Assert.Contains($"task answered --session {Sid}", text);
        Assert.Contains("never leave a question open", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Dashes, d => text.Contains(d));
    }

    [Fact]
    public void A_prompt_after_the_user_closed_the_task_says_new_work_needs_a_new_task()
    {
        var closed = Task("Tasks board", TaskStatus.Closed);

        var text = SessionBriefing.Prompt(Sid, Maybe<TaskItem>.None, closed);

        Assert.Contains("closed", text);
        Assert.Contains("Tasks board", text);
        Assert.Contains($"task new - --session {Sid}", text);
    }

    [Fact]
    public void A_prompt_with_no_task_says_how_to_get_one() =>
        Assert.Contains($"task join <id> --session {Sid}", SessionBriefing.Prompt(Sid, Maybe<TaskItem>.None, Maybe<TaskItem>.None));

    // Petre, of a task whose thread was a long log: "why so long?", "i only want the most important things to be here".
    // The rule in one sentence: say what needs the user, and nothing else.
    [Fact]
    public void The_briefing_keeps_tasks_lean()
    {
        var text = SessionBriefing.Start(Start(), Maybe<TaskItem>.None, []);

        Assert.Contains("say what needs the user, and nothing else", text);
        Assert.Contains("title is a few words", text);
        Assert.Contains("under about 60 characters", text);
        Assert.Contains("Comments are rare", text);
        Assert.Contains("no progress logs, measurements, spike notes or findings", text);
        Assert.Contains("say exactly what the user must do in the first words", text);
        Assert.DoesNotContain("detail worth keeping", text);
        Assert.True(text.Length < 10_000, $"{text.Length} characters");
    }

    [Fact]
    public void The_briefing_says_answered_settles_the_questions_comments()
    {
        var text = SessionBriefing.Start(Start(), Maybe<TaskItem>.None, []);

        Assert.Contains("also settles, that is folds away, your comments written since you asked", text);
        Assert.Contains($"task comment-settle <comment-id> --session {Sid}", text);
        Assert.True(text.Length < 10_000, $"{text.Length} characters");
    }

    // Images in tasks (docs/superpowers/specs/2026-11-24-task-images-design.md): a description is cut at 200 characters,
    // but its image paths follow the cut whole and absolute, since a cut path is useless to a session's Read tool.
    static string Absolute(string markdown) => markdown.Replace("task-images/t/1.png", @"C:\S\task-images\t\1.png");

    [Fact]
    public void Description_images_follow_the_cut_whole()
    {
        var own = Task("Tasks board") with { Description = new string('a', 300) + "\n\n![image](task-images/t/1.png)" };

        var text = SessionBriefing.Prompt(Sid, own, Maybe<TaskItem>.None, Absolute);

        Assert.Contains(new string('a', 197) + "...", text);
        Assert.Contains(@"images in the description: C:\S\task-images\t\1.png; read any you have not looked at yet with your Read tool", text);
        Assert.DoesNotContain("![image]", text);
    }

    [Fact]
    public void A_description_of_images_only_quotes_no_text()
    {
        var own = Task("Tasks board") with { Description = "![image](task-images/t/1.png)" };

        var text = SessionBriefing.Start(Start(), own, [], Absolute);

        Assert.DoesNotContain("the user's description", text);
        Assert.Contains(@"images in the description: C:\S\task-images\t\1.png; read any you have not looked at yet with your Read tool", text);
    }
}
