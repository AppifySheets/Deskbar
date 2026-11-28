using System.Text.Json;
using Deskbar.Core.Control;
using Deskbar.Core.Tasks;
using CSharpFunctionalExtensions;
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests.Tasks;

// A question asked in Claude Code's multiple-choice dialog (the AskUserQuestion tool) becomes the session's task question,
// and answering the dialog settles it (task 493b7ba9; DialogQuestion.cs). The text first, then the pipe end to end.
public sealed class DialogQuestionTests
{
    static readonly DateTimeOffset clock = new(2026, 11, 25, 13, 0, 0, TimeSpan.Zero);

    // The tool_input shape from Claude Code's hooks reference: questions[] of question, header, options[], multiSelect.
    static object Question(string text, params string[] labels) => new
    {
        question = text,
        header = "Duplicate",
        options = labels.Select(l => new { label = l, description = "a long explanation the dialog shows" }).ToArray(),
        multiSelect = false,
    };

    static JsonElement Input(params object[] questions) => JsonSerializer.SerializeToElement(new { questions });

    [Fact]
    public void One_question_is_one_line_with_its_options()
    {
        var text = DialogQuestion.From(Input(Question("What should Duplicate do?", "Closes it", "A new open status")));

        Assert.Equal($"{DialogQuestion.Lead} What should Duplicate do? (Closes it / A new open status)", text.Value);
    }

    [Fact]
    public void Several_questions_are_a_list()
    {
        var text = DialogQuestion.From(Input(Question("Duplicate?", "Close"), Question("Who marks?")));

        Assert.Equal($"{DialogQuestion.Lead}\n- Duplicate? (Close)\n- Who marks?", text.Value);
    }

    [Fact]
    public void An_input_with_no_question_text_gives_none()
    {
        Assert.True(DialogQuestion.From(JsonSerializer.SerializeToElement(new { questions = Array.Empty<object>() })).HasNoValue);
        Assert.True(DialogQuestion.From(JsonSerializer.SerializeToElement(new { questions = new[] { new { header = "h" } } })).HasNoValue);
        Assert.True(DialogQuestion.From(JsonSerializer.SerializeToElement(new { other = 1 })).HasNoValue);
    }

    // --- the pipe, end to end -------------------------------------------------------------------------

    readonly TaskService tasks = new(new MemoryTaskStore(), () => clock);

    sealed class MemoryTaskStore : ITaskStore
    {
        public Result<TaskBoard> Load() => TaskBoard.Empty;

        public Result Save(TaskBoard board) => Result.Success();
    }

    static WorkspaceManager Started()
    {
        var manager = new WorkspaceManager(new FakeDesktops(), new FakeMonitor(), new FakeTitles(), new FakeStore(), ownProcessId: 4242);
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    ControlReply Ran(WorkspaceManager manager, params string[] args) => new RemoteControl(manager, tasks: tasks).Execute(args);

    ControlReply Hooked(WorkspaceManager manager, string hookEvent, string session, string tool, params object[] questions) =>
        Ran(manager, "claude-hook", "--entry", "claude-vscode", "--attended", "1", "--from", @"C:\repos\TaskSpaces", "--json",
            JsonSerializer.Serialize(new
            {
                session_id = session,
                cwd = @"C:\repos\TaskSpaces",
                hook_event_name = hookEvent,
                tool_name = tool,
                tool_input = new { questions },
            }));

    static readonly object Asked = Question("What should Duplicate do?", "Closes it", "A new open status");

    TaskItem Task() => tasks.Board.Tasks.Single();

    // The dialog opening puts its question on the task, waiting on Petre; the dialog returning settles it. The session
    // reads nothing from either hook.
    [Fact]
    public void The_dialog_asks_and_its_answer_settles()
    {
        var manager = Started();
        Ran(manager, "task", "new", "Task links", "--session", "s1", "--from", @"C:\repos\TaskSpaces");

        var asked = Hooked(manager, "PreToolUse", "s1", DialogQuestion.Tool, Asked);

        Assert.Equal((ControlReply.Ok, ""), (asked.ExitCode, asked.Output));
        Assert.Equal(TaskStatus.WaitingOnYou, Task().Status);
        Assert.Equal($"{DialogQuestion.Lead} What should Duplicate do? (Closes it / A new open status)", Task().Question.Value);

        var answered = Hooked(manager, "PostToolUse", "s1", DialogQuestion.Tool, Asked);

        Assert.Equal((ControlReply.Ok, ""), (answered.ExitCode, answered.Output));
        Assert.Equal(TaskStatus.InProgress, Task().Status);
        Assert.True(Task().Question.HasNoValue);
    }

    // A question the session asked with `task ask` is the session's to settle: a dialog opened and answered after it
    // neither replaces nor settles it (review of the branch: it used to replace it, then settle it unseen).
    [Fact]
    public void The_dialog_leaves_another_question_alone()
    {
        var manager = Started();
        Ran(manager, "task", "new", "Task links", "--session", "s1", "--from", @"C:\repos\TaskSpaces");
        Ran(manager, "task", "ask", "Merge the pull request", "--session", "s1");

        Hooked(manager, "PreToolUse", "s1", DialogQuestion.Tool, Asked);
        Assert.Equal("Merge the pull request", Task().Question.Value);
        Hooked(manager, "PostToolUse", "s1", DialogQuestion.Tool, Asked);

        Assert.Equal(TaskStatus.WaitingOnYou, Task().Status);
        Assert.Equal("Merge the pull request", Task().Question.Value);
    }

    // THE PICKS ON THE THREAD (task c047d0a0): when the dialog closes, wherever it was answered, its PostToolUse carries the
    // answers in tool_response, and Deskbar puts one note with them on the session's task.
    ControlReply AnsweredWith(WorkspaceManager manager, string session, object response) =>
        Ran(manager, "claude-hook", "--entry", "claude-vscode", "--attended", "1", "--from", @"C:\repos\TaskSpaces", "--json",
            JsonSerializer.Serialize(new
            {
                session_id = session,
                cwd = @"C:\repos\TaskSpaces",
                hook_event_name = "PostToolUse",
                tool_name = DialogQuestion.Tool,
                tool_input = new { questions = new[] { Question("Pick a colour", "Red", "Blue") } },
                tool_response = response,
            }));

    [Fact]
    public void An_answered_dialog_leaves_a_deskbar_note_with_the_picks()
    {
        var manager = Started();
        Ran(manager, "task", "new", "Task links", "--session", "s1", "--from", @"C:\repos\TaskSpaces");

        var reply = AnsweredWith(manager, "s1", new { answers = new Dictionary<string, string> { ["Pick a colour"] = "Blue" } });

        Assert.Equal((ControlReply.Ok, ""), (reply.ExitCode, reply.Output));
        var note = Assert.Single(Task().Comments, c => c.ByDeskbar);
        Assert.Equal("Answered Claude's question:\n- Duplicate: Blue", note.Markdown);
    }

    // No answers in the result (Esc, a result of another shape), another session, or another tool: no note.
    [Fact]
    public void A_dialog_closed_without_answers_leaves_no_note()
    {
        var manager = Started();
        Ran(manager, "task", "new", "Task links", "--session", "s1", "--from", @"C:\repos\TaskSpaces");

        AnsweredWith(manager, "s1", new { answers = new Dictionary<string, string>() });
        AnsweredWith(manager, "s1", "User declined to answer questions");
        AnsweredWith(manager, "s2", new { answers = new Dictionary<string, string> { ["Pick a colour"] = "Blue" } });

        Assert.DoesNotContain(Task().Comments, c => c.ByDeskbar && c.Markdown.StartsWith("Answered Claude's question"));
    }

    // A Done task stays Done when its session opens a dialog.
    [Fact]
    public void A_done_task_stays_done()
    {
        var manager = Started();
        Ran(manager, "task", "new", "Task links", "--session", "s1", "--from", @"C:\repos\TaskSpaces");
        Ran(manager, "task", "done", "--session", "s1");

        Hooked(manager, "PreToolUse", "s1", DialogQuestion.Tool, Asked);

        Assert.Equal(TaskStatus.Done, Task().Status);
        Assert.True(Task().Question.HasNoValue);
    }

    // Another tool, another session, or a session with no task: nothing changes.
    [Fact]
    public void Nothing_else_asks()
    {
        var manager = Started();
        Ran(manager, "task", "new", "Task links", "--session", "s1", "--from", @"C:\repos\TaskSpaces");

        Hooked(manager, "PreToolUse", "s1", "Bash", Asked);
        Hooked(manager, "PreToolUse", "s2", DialogQuestion.Tool, Asked);

        Assert.Equal(TaskStatus.InProgress, Task().Status);
        Assert.True(Task().Question.HasNoValue);
    }
}
