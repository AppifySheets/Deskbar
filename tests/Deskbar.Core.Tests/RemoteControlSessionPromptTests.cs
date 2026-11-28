using System.Text.Json;
using CSharpFunctionalExtensions;
using Deskbar.Core.Control;
using Deskbar.Core.Tasks;
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests;

// Every dialog a session waits on becomes its task's question, and goes once answered (task b310a649,
// RemoteControl.SessionPrompts.cs). End to end through `claude-hook`, with ONE RemoteControl as in the app, since the
// bookkeeping lives in it.
public sealed class RemoteControlSessionPromptTests : IDisposable
{
    static readonly DateTimeOffset Clock = new(2026, 11, 27, 18, 0, 0, TimeSpan.Zero);
    const string Repo = @"C:\repos\TaskSpaces";
    readonly string folder = Path.Combine(Path.GetTempPath(), "deskbar-prompt-rc-" + Guid.NewGuid().ToString("N"));
    readonly TaskService tasks = new(new MemoryTaskStore(), () => Clock);
    readonly RemoteControl control;
    readonly string transcript;

    sealed class MemoryTaskStore : ITaskStore
    {
        public Result<TaskBoard> Load() => TaskBoard.Empty;

        public Result Save(TaskBoard board) => Result.Success();
    }

    public RemoteControlSessionPromptTests()
    {
        Directory.CreateDirectory(folder);
        transcript = Path.Combine(folder, "s1.jsonl");
        File.WriteAllText(transcript, """{"type":"user"}""" + "\n");
        var manager = new WorkspaceManager(new FakeDesktops(), new FakeMonitor(), new FakeTitles(), new FakeStore(), ownProcessId: 4242);
        Assert.True(manager.Start().IsSuccess);
        control = new RemoteControl(manager, tasks: tasks);
        Run("task", "new", "Ship it", "--session", "s1", "--from", Repo);
    }

    public void Dispose() => Directory.Delete(folder, true);

    ControlReply Run(params string[] args) => control.Execute(args);

    ControlReply Hook(object json) =>
        Run("claude-hook", "--entry", "cli", "--attended", "1", "--from", Repo, "--json", JsonSerializer.Serialize(json));

    ControlReply Permission(string tool = "Bash", object? input = null) => Hook(new
    {
        session_id = "s1", transcript_path = transcript, cwd = Repo, hook_event_name = "PermissionRequest",
        tool_name = tool, tool_input = input ?? new { command = "git push origin x", description = "Push" },
    });

    ControlReply Notified(string message = "Claude needs your permission", string type = "permission_prompt") => Hook(new
    {
        session_id = "s1", transcript_path = transcript, cwd = Repo, hook_event_name = "Notification", message, notification_type = type,
    });

    ControlReply Event(string name, string? prompt = null) => Hook(new
    {
        session_id = "s1", transcript_path = transcript, cwd = Repo, hook_event_name = name, prompt,
    });

    TaskItem Task() => tasks.Board.Tasks.Single();

    void Appended(string type) => File.AppendAllText(transcript, $$"""{"type":"{{type}}"}""" + "\n");

    [Fact]
    public void A_permission_prompt_left_waiting_is_the_tasks_question_naming_the_command()
    {
        Assert.Equal((ControlReply.Ok, ""), (Permission().ExitCode, Permission().Output));
        var reply = Notified();

        Assert.Equal((ControlReply.Ok, ""), (reply.ExitCode, reply.Output));
        Assert.Equal(TaskStatus.WaitingOnYou, Task().Status);
        Assert.Equal("Allow Bash in the session: git push origin x", Task().Question.Value);
    }

    [Fact]
    public void Answering_in_the_session_settles_it_on_the_next_tick()
    {
        Permission();
        Notified();

        Appended("ai-title");
        control.SettlePrompts();
        Assert.Equal(TaskStatus.WaitingOnYou, Task().Status);

        Appended("assistant");
        control.SettlePrompts();
        Assert.Equal(TaskStatus.InProgress, Task().Status);
        Assert.True(Task().Question.HasNoValue);
    }

    [Theory]
    [InlineData("Stop")]
    [InlineData("SessionEnd")]
    [InlineData("UserPromptSubmit")]
    public void The_session_going_on_settles_it_at_once(string name)
    {
        Notified("Session paused");
        Assert.Equal($"{SessionPrompt.Lead} Session paused", Task().Question.Value);

        Event(name, name == "UserPromptSubmit" ? "carry on" : null);

        Assert.Equal(TaskStatus.InProgress, Task().Status);
    }

    // Review focus 4: a prompt Claude Code delivered itself (a background task finishing) may arrive while the dialog
    // still waits, so it settles nothing.
    [Fact]
    public void A_prompt_claude_code_delivered_settles_nothing()
    {
        Notified("Session paused");

        Event("UserPromptSubmit", "<task-notification>\n<task-id>x</task-id>\n</task-notification>");

        Assert.Equal(TaskStatus.WaitingOnYou, Task().Status);
    }

    [Fact]
    public void An_mcp_request_is_named_by_its_server_and_settled_by_its_answer()
    {
        Hook(new { session_id = "s1", transcript_path = transcript, cwd = Repo, hook_event_name = "Elicitation", mcp_server_name = "github", message = "Pick a repository" });
        Notified("Claude Code needs your input", "elicitation_dialog");
        Assert.Equal("Answer github in the session: Pick a repository", Task().Question.Value);

        Notified("Elicitation response for server \"github\": accept", "elicitation_response");

        Assert.Equal(TaskStatus.InProgress, Task().Status);
    }

    // Review focus 2: a `task ask` (a pull request to merge) is not replaced, and the prompt's end does not settle it.
    [Fact]
    public void A_task_already_waiting_keeps_its_question()
    {
        Run("task", "ask", "Merge the pull request", "--session", "s1");

        Notified();
        Event("Stop");

        Assert.Equal(TaskStatus.WaitingOnYou, Task().Status);
        Assert.Equal("Merge the pull request", Task().Question.Value);
    }

    // The session's own earlier dialog question is replaced by its next one (it was never settled: no transcript mark).
    [Fact]
    public void A_newer_dialog_replaces_the_sessions_own_older_question()
    {
        File.Delete(transcript);
        Notified("Session paused");
        Notified("A sandboxed command needs network access");

        Assert.Equal($"{SessionPrompt.Lead} A sandboxed command needs network access", Task().Question.Value);
    }

    [Fact]
    public void A_question_the_session_asked_since_is_left_alone()
    {
        Notified("Session paused");
        Run("task", "ask", "Which branch?", "--session", "s1");

        Appended("assistant");
        control.SettlePrompts();

        Assert.Equal("Which branch?", Task().Question.Value);
    }

    [Fact]
    public void A_done_task_stays_done_and_idle_is_no_question()
    {
        Notified("Claude is waiting for your input", "idle_prompt");
        Assert.Equal(TaskStatus.InProgress, Task().Status);

        Run("task", "done", "--session", "s1");
        Notified();
        Assert.Equal(TaskStatus.Done, Task().Status);
    }

    [Fact]
    public void A_headless_run_asks_nothing()
    {
        Run("claude-hook", "--entry", "sdk-cli", "--attended", "0", "--from", Repo, "--json", JsonSerializer.Serialize(new
        {
            session_id = "s1", transcript_path = transcript, cwd = Repo, hook_event_name = "Notification",
            message = "Claude needs your permission", notification_type = "permission_prompt",
        }));

        Assert.Equal(TaskStatus.InProgress, Task().Status);
    }

    // Step 68a: Esc on the AskUserQuestion dialog may run no PostToolUse, so its question now goes with the transcript.
    [Fact]
    public void The_ask_dialogs_question_is_settled_by_the_transcript_too()
    {
        Hook(new
        {
            session_id = "s1", transcript_path = transcript, cwd = Repo, hook_event_name = "PreToolUse", tool_name = DialogQuestion.Tool,
            tool_input = new { questions = new[] { new { question = "Which one?", header = "h", options = new[] { new { label = "A" } } } } },
        });
        Assert.Equal(TaskStatus.WaitingOnYou, Task().Status);

        Appended("user");
        control.SettlePrompts();

        Assert.Equal(TaskStatus.InProgress, Task().Status);
    }

    // Task 0978f3f4: Claude Code wrote the asking AskUserQuestion line after PreToolUse had placed the question, and the
    // next tick settled it while the dialog still waited. That line is the question, not an answer; the answer's is.
    [Fact]
    public void The_ask_dialogs_own_line_written_after_the_hook_leaves_it_waiting()
    {
        Hook(new
        {
            session_id = "s1", transcript_path = transcript, cwd = Repo, hook_event_name = "PreToolUse", tool_name = DialogQuestion.Tool,
            tool_input = new { questions = new[] { new { question = "Which one?", header = "h", options = new[] { new { label = "A" } } } } },
        });
        File.AppendAllText(transcript,
            "{\"type\":\"assistant\",\"message\":{\"id\":\"msg_1\",\"content\":[{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":\"AskUserQuestion\",\"input\":{}}]}}\n");
        control.SettlePrompts();

        Assert.Equal(TaskStatus.WaitingOnYou, Task().Status);

        Appended("user");
        control.SettlePrompts();

        Assert.Equal(TaskStatus.InProgress, Task().Status);
    }

    // Task 0978f3f4, the same late line seen from the dialog's noted detail: it must not spend the detail, or the
    // Notification six seconds on no longer knows the dialog and puts a permission question over the dialog's own.
    [Fact]
    public void The_ask_dialogs_late_line_leaves_its_detail_so_its_notification_changes_nothing()
    {
        var input = new { questions = new[] { new { question = "Which one?", header = "h", options = new[] { new { label = "A" } } } } };
        Hook(new { session_id = "s1", transcript_path = transcript, cwd = Repo, hook_event_name = "PreToolUse", tool_name = DialogQuestion.Tool, tool_input = input });
        var asked = Task().Question.Value;
        Permission(DialogQuestion.Tool, input);
        File.AppendAllText(transcript,
            "{\"type\":\"assistant\",\"message\":{\"id\":\"msg_1\",\"content\":[{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":\"AskUserQuestion\",\"input\":{}}]}}\n");
        Notified();

        Assert.Equal((TaskStatus.WaitingOnYou, asked), (Task().Status, Task().Question.Value));
    }

    // Review finding 1: Claude's multiple-choice dialog is a permission dialog in Claude Code's registry too, so its own
    // Notification ("Claude needs your permission") and PermissionRequest come with it. Its question stays the dialog's
    // and its answer settles it.
    [Fact]
    public void The_ask_dialogs_own_notification_leaves_its_question()
    {
        var input = new { questions = new[] { new { question = "Which one?", header = "h", options = new[] { new { label = "A" } } } } };
        Hook(new { session_id = "s1", transcript_path = transcript, cwd = Repo, hook_event_name = "PreToolUse", tool_name = DialogQuestion.Tool, tool_input = input });
        var asked = Task().Question.Value;
        Permission(DialogQuestion.Tool, input);
        Notified();
        Assert.Equal(asked, Task().Question.Value);

        Hook(new { session_id = "s1", transcript_path = transcript, cwd = Repo, hook_event_name = "PostToolUse", tool_name = DialogQuestion.Tool, tool_input = input });

        Assert.Equal(TaskStatus.InProgress, Task().Status);
    }

    // Review finding 3: a prompt answered at once leaves its detail behind; a later dialog with the same words (one that
    // fires no PermissionRequest of its own) must not be named after that spent command.
    [Fact]
    public void A_decided_prompts_detail_names_nothing_later()
    {
        Permission();
        Appended("assistant");

        Notified();

        Assert.Equal($"{SessionPrompt.Lead} Claude needs your permission", Task().Question.Value);
    }

    // Review finding 2: a task Petre took is not handed back to Claude by a dialog's question being settled.
    [Fact]
    public void A_task_petre_took_is_left_alone()
    {
        Assert.True(tasks.Assign(Task().Id, Assignee.Me).IsSuccess);

        Notified("Session paused");

        Assert.True(Task().Question.HasNoValue);
        Assert.Equal(Assignee.Me, Task().Assignee);
    }
}
