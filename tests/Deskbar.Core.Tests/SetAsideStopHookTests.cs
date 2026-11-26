using CSharpFunctionalExtensions;
using Deskbar.Core.Control;
using Deskbar.Core.Domain;
using Deskbar.Core.Tasks;
using Xunit;
// System.Threading.Tasks (an implicit using) has a TaskStatus too; the alias says which one this file means.
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests;

// The claude-hook side of a question Deskbar set aside while its session worked after Petre's reply (task 8bd18246): the
// Stop hook asks the session once what became of the question, and a UserPromptSubmit is remembered as a reply unless it
// is Deskbar's own resume message. The fixture is RemoteControlClaudeHookTests' in small, with a clock the test moves
// (that one's is fixed, so a reply could never come after the ask).
public class SetAsideStopHookTests
{
    const string Repo = @"C:\repos\TaskSpaces";
    const string Question = "Review and merge the pull request?";

    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();
    DateTimeOffset clock = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);
    readonly TaskService tasks;

    sealed class MemoryTaskStore : ITaskStore
    {
        public Result<TaskBoard> Load() => TaskBoard.Empty;

        public Result Save(TaskBoard board) => Result.Success();
    }

    public SetAsideStopHookTests() => tasks = new TaskService(new MemoryTaskStore(), () => clock);

    static readonly IReadOnlySet<string> S1Works = new HashSet<string> { "s1" };

    // A manager with an editor on the Deskbar workspace and session s1's task asking Question.
    WorkspaceManager Asked()
    {
        var manager = new WorkspaceManager(desktops, monitor, titles, store, ownProcessId: 4242);
        Assert.True(manager.Start().IsSuccess);
        manager.AddWorkspace("Deskbar");
        var workspace = manager.State.Workspaces.Single(w => w.Name == "Deskbar").DesktopId!.Value;
        var window = new WindowInfo(new WindowHandle(0x10), 100, "Code", @"C:\VS Code\Code.exe", "main.cs - TaskSpaces - Visual Studio Code", null);
        desktops.WindowPlacements[window.Handle] = workspace;
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, window));
        Said(manager, "new", "Tasks board");
        clock = clock.AddMinutes(10);
        Said(manager, "ask", Question);
        return manager;
    }

    ControlReply Said(WorkspaceManager manager, params string[] form) =>
        new RemoteControl(manager, tasks: tasks, longPath: p => p).Execute(["task", .. form, "--session", "s1", "--from", Repo]);

    // One hook for session s1, as the script runs it for an attended VS Code session; `extra` is more of the hook's
    // fields, each with its leading comma.
    string Hooked(WorkspaceManager manager, string hookEvent, string extra = "")
    {
        var json = $$"""{"session_id":"s1","cwd":"C:\\repos\\TaskSpaces","hook_event_name":"{{hookEvent}}"{{extra}}}""";
        var reply = new RemoteControl(manager, tasks: tasks, longPath: p => p)
            .Execute(["claude-hook", "--entry", "claude-vscode", "--attended", "1", "--from", Repo, "--json", json]);
        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        return reply.Output;
    }

    // Petre replies on the task and the session works on it: Deskbar sets the question aside.
    WorkspaceManager SetAside()
    {
        var manager = Asked();
        clock = clock.AddMinutes(1);
        Assert.True(tasks.Comment(tasks.Board.Tasks.Single().Id, Maybe<string>.None, "remove the footnote", wakes: false).IsSuccess);
        clock = clock.AddMinutes(1);
        tasks.Stalled(S1Works, S1Works);
        Assert.Equal(Question, tasks.Board.Tasks.Single().QuestionSetAside.Value);
        return manager;
    }

    [Fact]
    public void The_hook_event_reads_the_prompt_and_none_when_absent()
    {
        const string prompt = """{"session_id":"s1","cwd":"C:\\x","hook_event_name":"UserPromptSubmit","prompt":"say ok"}""";
        const string stop = """{"session_id":"s1","cwd":"C:\\x","hook_event_name":"Stop"}""";

        Assert.Equal("say ok", ClaudeHookEvent.Parse(prompt, "claude-vscode", "1").Value.Prompt.Value);
        Assert.True(ClaudeHookEvent.Parse(stop, "claude-vscode", "1").Value.Prompt.HasNoValue);
    }

    [Fact]
    public void DeliveredByDeskbar_tells_deskbars_own_prompts_from_the_users()
    {
        var task = new TaskItem(Guid.NewGuid(), "paste images", Guid.NewGuid(), TaskStatus.InProgress, "", Maybe<string>.None,
            clock, clock, clock, [], []);

        Assert.True(SessionBriefing.DeliveredByDeskbar(SessionKeeper.ContinueMessage));
        Assert.True(SessionBriefing.DeliveredByDeskbar(SessionBriefing.Commented(task, "look at this")));
        Assert.True(SessionBriefing.DeliveredByDeskbar(SessionBriefing.Commented(task, "look at this", byManager: true)));
        // The Reevaluate button's request (task 3dd07539) is Deskbar's, not Petre replying: read as his reply it would set
        // aside the very question the session is asked to check.
        Assert.True(SessionBriefing.DeliveredByDeskbar(SessionBriefing.Reevaluate(task)));
        Assert.False(SessionBriefing.DeliveredByDeskbar("remove the footnote"));
    }

    // Task 3dd07539: the Reevaluate message shows the session what Deskbar shows (status, question, status line, pull
    // requests) and names the command for each thing that can be stale.
    [Fact]
    public void Reevaluate_message_carries_what_the_board_shows_and_the_commands()
    {
        var task = new TaskItem(Guid.NewGuid(), "usage limit", Guid.NewGuid(), TaskStatus.WaitingOnYou, "PR open, waiting on merge",
            Maybe<string>.From("Merge #599"), clock, clock, clock, [], [])
        {
            PullRequests = [new PullRequestLink("AppifySheets", "Deskbar", 599)],
        };

        var message = SessionBriefing.Reevaluate(task);

        Assert.Contains("status: waiting on the user", message);
        Assert.Contains("your question: \"Merge #599\"", message);
        Assert.Contains("status line: \"PR open, waiting on merge\"", message);
        Assert.Contains("https://github.com/AppifySheets/Deskbar/pull/599", message);
        Assert.Contains("task answered --session", message);
        Assert.Contains("task done --session", message);
        Assert.Contains("task status-line - --session", message);
    }

    // The openings of prompts Claude Code delivers itself, copied from this machine's transcripts (task b846f5ad): a
    // subagent's hand-back, a background shell's notice, a Monitor event.
    static readonly IReadOnlyList<string> ClaudeCodePrompts =
    [
        "Another Claude session sent a message:\n<agent-message from=\"ad1408d297de9c093\">\n[Subagent hand-back] The text below is the final report of a subagent this session delegated to.\n</agent-message>",
        "<task-notification>\n<task-id>bz48gzrv3</task-id>\n<status>completed</status>\n<summary>Background shell completed</summary>\n</task-notification>",
        "\n<task-notification>\n<task-id>b1prytrwa</task-id>\n<summary>Monitor event: \"PR 570: CI result\"</summary>\n</task-notification>",
        "[Cross-session idle notice] The session \"Deskbar: manager\" went idle.",
        "[MESSAGE FROM NON-USER SOURCE - NOT USER INPUT]\nBrowser disconnected.",
        // A hand-back behind a system-reminder block, in case the hook ever gets the two together.
        "<system-reminder>\n<tools-used-before-summary>Grep</tools-used-before-summary>\n</system-reminder>\n"
        + "Another Claude session sent a message:\n<agent-message from=\"ad6a44ec5f6e90512\">\n[Subagent hand-back] done\n</agent-message>",
        "<system-reminder>\n<tools-used-before-summary>SubagentHandback</tools-used-before-summary>\n</system-reminder>",
    ];

    [Fact]
    public void FromTheUser_leaves_out_what_deskbar_and_claude_code_deliver()
    {
        Assert.All(ClaudeCodePrompts, prompt => Assert.True(SessionBriefing.DeliveredByClaudeCode(prompt)));
        Assert.All(ClaudeCodePrompts, prompt => Assert.False(SessionBriefing.FromTheUser(prompt)));
        Assert.False(SessionBriefing.FromTheUser(SessionKeeper.ContinueMessage));
        Assert.True(SessionBriefing.FromTheUser("remove the footnote"));
        // Only the opening counts: the user quoting a notice in the middle of a message is still the user.
        Assert.True(SessionBriefing.FromTheUser("why did this show up? <task-notification> ..."));
        // The user's words after a leading system-reminder block are still the user's.
        Assert.True(SessionBriefing.FromTheUser("<system-reminder>\nx\n</system-reminder>\nremove the footnote"));
    }

    // The bug of task b846f5ad: a session whose task waits delegates a review, the subagent hands back while the session
    // works, and the hand-back set the question aside as if the user had replied. Neither it nor a background job's notice
    // is a reply now; the user's own message after them still is.
    [Fact]
    public void A_subagent_hand_back_or_a_background_notice_does_not_set_the_question_aside()
    {
        var manager = Asked();
        ClaudeCodePrompts.ToList().ForEach(prompt =>
        {
            clock = clock.AddMinutes(1);
            Hooked(manager, "UserPromptSubmit", $",\"prompt\":{System.Text.Json.JsonSerializer.Serialize(prompt)}");
            clock = clock.AddMinutes(1);
            tasks.Stalled(S1Works, S1Works);
            Assert.Equal(TaskStatus.WaitingOnYou, tasks.Board.Tasks.Single().Status);
        });

        Hooked(manager, "UserPromptSubmit", ",\"prompt\":\"remove the footnote\"");
        clock = clock.AddMinutes(1);
        tasks.Stalled(S1Works, S1Works);
        Assert.Equal(Question, tasks.Board.Tasks.Single().QuestionSetAside.Value);
    }

    static System.Text.Json.JsonElement Json(string output) => System.Text.Json.JsonDocument.Parse(output).RootElement;

    [Fact]
    public void Stop_on_a_set_aside_question_asks_the_session_what_became_of_it()
    {
        var output = Hooked(SetAside(), "Stop");

        Assert.Equal("block", Json(output).GetProperty("decision").GetString());
        Assert.Equal(SessionBriefing.SetAsideNudge(Question), Json(output).GetProperty("reason").GetString());
        Assert.Contains(Question, Json(output).GetProperty("reason").GetString());
    }

    // The nudge tells the session to run `task answered` when the reply settled the question. That has to clear the set-aside
    // question, or the next Stop asks about it again, and again on every turn after (seen live on 2026-11-25).
    [Fact]
    public void Task_answered_settles_a_set_aside_question_and_the_next_stop_leaves_it_alone()
    {
        var manager = SetAside();

        Assert.Equal(ControlReply.Ok, Said(manager, "answered").ExitCode);

        var task = tasks.Board.Tasks.Single();
        Assert.Equal(TaskStatus.InProgress, task.Status);
        Assert.False(task.QuestionSetAside.HasValue);
        // The next Stop may still give the ordinary parked nudge (an In progress task of Claude's), never this question again.
        Assert.DoesNotContain(Question, Hooked(manager, "Stop"));
    }

    [Fact]
    public void Stop_after_the_nudge_does_not_ask_about_the_set_aside_question_again() =>
        Assert.Equal("", Hooked(SetAside(), "Stop", ",\"stop_hook_active\":true"));

    [Fact]
    public void Stop_with_background_work_still_asks_about_the_set_aside_question()
    {
        var output = Hooked(SetAside(), "Stop", ",\"background_tasks\":[{\"type\":\"monitor\"}]");

        Assert.Equal(SessionBriefing.SetAsideNudge(Question), Json(output).GetProperty("reason").GetString());
    }

    // A prompt is how a reply typed in the terminal reaches Deskbar; Deskbar's own resume message is not one.
    [Fact]
    public void A_prompt_is_remembered_as_a_reply_but_the_resume_message_is_not()
    {
        var manager = Asked();
        clock = clock.AddMinutes(1);
        Hooked(manager, "UserPromptSubmit", $",\"prompt\":\"{SessionKeeper.ContinueMessage}\"");
        clock = clock.AddMinutes(1);
        tasks.Stalled(S1Works, S1Works);
        Assert.Equal(TaskStatus.WaitingOnYou, tasks.Board.Tasks.Single().Status);

        // A comment Deskbar delivers to the session (Petre's or the manager's) is not him typing either.
        var task = tasks.Board.Tasks.Single();
        new[] { false, true }.ToList().ForEach(byManager =>
        {
            var delivered = System.Text.Json.JsonSerializer.Serialize(SessionBriefing.Commented(task, "remove the footnote", byManager));
            clock = clock.AddMinutes(1);
            Hooked(manager, "UserPromptSubmit", $",\"prompt\":{delivered}");
            tasks.Stalled(S1Works, S1Works);
            Assert.Equal(TaskStatus.WaitingOnYou, tasks.Board.Tasks.Single().Status);
        });

        Hooked(manager, "UserPromptSubmit", ",\"prompt\":\"remove the footnote\"");
        clock = clock.AddMinutes(1);
        tasks.Stalled(S1Works, S1Works);
        Assert.Equal(TaskStatus.InProgress, tasks.Board.Tasks.Single().Status);
        Assert.Equal(Question, tasks.Board.Tasks.Single().QuestionSetAside.Value);
    }
}
