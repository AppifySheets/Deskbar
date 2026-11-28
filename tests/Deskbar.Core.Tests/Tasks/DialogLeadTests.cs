using System.Text.Json;
using CSharpFunctionalExtensions;
using Deskbar.Core.Control;
using Deskbar.Core.Persistence;
using Deskbar.Core.Tasks;
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests.Tasks;

// WHERE A DIALOG QUESTION SAYS TO ANSWER (task 28fd8550, Petre: "it shouldn't ask me to do it in the terminal if the
// question can be answered from the chat"). An AskUserQuestion the card on the task's page will draw is led by
// DialogQuestion.PageLead; one only the terminal can take keeps DialogQuestion.Lead; and once the card has left the page
// while the dialog still waits (held too long, handed back, never registered), the question is reworded to the terminal's
// lead in place. One RemoteControl serves every call of a test, as the app's one copy does, since the placements live in it.
public sealed class DialogLeadTests : IDisposable
{
    const string Session = "abcdef12-3456-7890";
    const string Repo = @"C:\repos\proj1";

    // The tool_input of a dialog the card can draw: one question with options.
    const string Input = """{"questions":[{"question":"Pick a colour","header":"Colour","options":[{"label":"Red","description":"warm"},{"label":"Blue","description":"cool"}],"multiSelect":false}]}""";

    DateTimeOffset now = new(2026, 11, 28, 9, 0, 0, TimeSpan.Zero);
    readonly string box = Path.Combine(Path.GetTempPath(), "deskbar-dialog-lead-" + Guid.NewGuid().ToString("N"));
    readonly TaskService tasks;
    readonly PermissionDesk desk;

    public DialogLeadTests()
    {
        tasks = new TaskService(new MemoryTaskStore(), () => now);
        desk = new PermissionDesk(box, () => now);
    }

    public void Dispose()
    {
        if (Directory.Exists(box)) Directory.Delete(box, true);
        if (Directory.Exists(box + "-2")) Directory.Delete(box + "-2", true);
    }

    sealed class MemoryTaskStore : ITaskStore
    {
        public Result<TaskBoard> Load() => TaskBoard.Empty;

        public Result Save(TaskBoard board) => Result.Success();
    }

    // Sessions keep tasks, the Inbox look on, the permission card as asked, and a task for the session.
    (WorkspaceManager Manager, RemoteControl Control) Ready(bool card = true)
    {
        var manager = new WorkspaceManager(new FakeDesktops(), new FakeMonitor(), new FakeTitles(), new FakeStore(), ownProcessId: 4242);
        Assert.True(manager.Start().IsSuccess);
        Assert.True(manager.SetClaudePermissions(card).IsSuccess);
        manager.SaveTasksPanel(TasksPanelState.Default with { Inbox = true });
        Assert.True(tasks.Create("Ship it", Maybe<Guid>.None, tasks.NewSession(Session, Repo)).IsSuccess);
        return (manager, new RemoteControl(manager, tasks: tasks, permissions: desk));
    }

    static string ToolHook(string hookEvent, string input = Input) =>
        $$$"""{"session_id":"{{{Session}}}","transcript_path":"t","cwd":"C:\\repos\\proj1","hook_event_name":"{{{hookEvent}}}","tool_name":"AskUserQuestion","tool_input":{{{input}}}}""";

    static ControlReply Hook(RemoteControl control, string json) =>
        control.Execute(["claude-hook", "--entry", "cli", "--attended", "1", "--from", Repo, "--json", json]);

    // The card's own hook registering the dialog (claude-permission-hook.cmd), as a background session's would.
    ControlReply Card(RemoteControl control)
    {
        Directory.CreateDirectory(Path.Combine(box, "req-1-2"));
        var json = $$$"""{"session_id":"{{{Session}}}","transcript_path":"t","cwd":"C:\\repos\\proj1","permission_mode":"default","hook_event_name":"PermissionRequest","tool_name":"AskUserQuestion","tool_input":{{{Input}}}}""";
        return control.Execute(["claude-permission", "--entry", "cli", "--attended", "0", "--job", @"C:\Users\x\.claude\jobs\abcdef12",
            "--from", Repo, "--slot", "req-1-2", "--json", json]);
    }

    TaskItem Task() => tasks.Board.Tasks.Single();

    static string Page => $"{DialogQuestion.PageLead} Pick a colour (Red / Blue)";

    static string Terminal => $"{DialogQuestion.Lead} Pick a colour (Red / Blue)";

    [Fact]
    public void The_text_takes_either_lead_and_a_page_led_one_turns_to_the_terminal()
    {
        var input = JsonDocument.Parse(Input).RootElement;

        Assert.Equal(Page, DialogQuestion.From(input, onPage: true).Value);
        Assert.Equal(Terminal, DialogQuestion.From(input).Value);
        Assert.Equal(Terminal, DialogQuestion.ToTerminal(Page).Value);
        Assert.True(DialogQuestion.ToTerminal(Terminal).HasNoValue);
        Assert.True(DialogQuestion.ToTerminal("Merge the pull request").HasNoValue);
    }

    // The card is on: the question names the task's page, and the answer settles it.
    [Fact]
    public void With_the_card_on_the_question_names_the_page_and_settles()
    {
        var (_, control) = Ready();

        Hook(control, ToolHook("PreToolUse"));
        Assert.Equal(Page, Task().Question.Value);

        Hook(control, ToolHook("PostToolUse"));
        Assert.Equal(TaskStatus.InProgress, Task().Status);
        Assert.True(Task().Question.HasNoValue);
    }

    // The card is off: only the terminal can answer, so the lead says so.
    [Fact]
    public void With_the_card_off_the_question_names_the_terminal()
    {
        var (_, control) = Ready(card: false);

        Hook(control, ToolHook("PreToolUse"));

        Assert.Equal(Terminal, Task().Question.Value);
    }

    // A dialog the card cannot draw (an option-less question) stays in the terminal, card setting or not.
    [Fact]
    public void A_dialog_the_card_cannot_draw_names_the_terminal()
    {
        var (_, control) = Ready();

        Hook(control, ToolHook("PreToolUse", """{"questions":[{"question":"Who marks?","options":[]}]}"""));

        Assert.Equal($"{DialogQuestion.Lead} Who marks?", Task().Question.Value);
    }

    // While the card is on the page, the tick leaves the page's lead alone, however long it has been up.
    [Fact]
    public void A_card_on_show_keeps_the_page_lead()
    {
        var (_, control) = Ready();
        Hook(control, ToolHook("PreToolUse"));
        Assert.Matches("^[0-9a-f]{32} dialog\r\n$", Card(control).Output);

        now += RemoteControl.CardGrace + TimeSpan.FromSeconds(5);
        control.SettlePrompts();

        Assert.Equal(Page, Task().Question.Value);
    }

    // The card left (held past its time) while the dialog still waits: the lead goes back to the terminal, in place: no
    // second question bubble, the line's Raised and Pin as they were. The dialog's answer still settles it.
    [Fact]
    public void A_card_that_left_gives_the_terminal_lead_back_in_place()
    {
        var (_, control) = Ready();
        Hook(control, ToolHook("PreToolUse"));
        Card(control);
        var before = Task();

        now += PermissionDesk.HeldFor + TimeSpan.FromSeconds(1);
        desk.Expire();
        control.SettlePrompts();

        var after = Task();
        Assert.Equal(Terminal, after.Question.Value);
        Assert.Equal(TaskStatus.WaitingOnYou, after.Status);
        Assert.Equal((before.Raised, before.Pin, before.WaitingSince), (after.Raised, after.Pin, after.WaitingSince));
        Assert.Equal(before.Comments.Count, after.Comments.Count);
        Assert.Equal(Terminal, Assert.Single(after.Comments, c => c.Asks).Markdown);

        Hook(control, ToolHook("PostToolUse"));
        Assert.Equal(TaskStatus.InProgress, Task().Status);
    }

    // No card ever registered (the hook missing, a refused slot): after the grace the terminal's lead comes back; within it,
    // the card may still be on its way, so nothing changes.
    [Fact]
    public void A_card_that_never_came_gives_the_terminal_lead_back_after_the_grace()
    {
        var (_, control) = Ready();
        Hook(control, ToolHook("PreToolUse"));

        now += TimeSpan.FromSeconds(5);
        control.SettlePrompts();
        Assert.Equal(Page, Task().Question.Value);

        now += RemoteControl.CardGrace;
        control.SettlePrompts();
        Assert.Equal(Terminal, Task().Question.Value);
    }

    // A question the session asked since is the session's: the tick never rewords it.
    [Fact]
    public void A_later_ask_is_never_reworded()
    {
        var (_, control) = Ready();
        Hook(control, ToolHook("PreToolUse"));
        var id = Task().Id;
        Assert.True(tasks.Ask(id, $"{DialogQuestion.PageLead} something else", Session).IsSuccess);

        now += RemoteControl.CardGrace + TimeSpan.FromSeconds(5);
        control.SettlePrompts();

        Assert.Equal($"{DialogQuestion.PageLead} something else", Task().Question.Value);
    }

    // Send on the card takes it off the desk seconds before the dialog's PostToolUse arrives (the branch's review, finding
    // 2): a tick in that gap must not say "Answer in the session's terminal" about the question just answered on the page.
    [Fact]
    public void A_dialog_just_answered_on_the_card_keeps_the_page_lead()
    {
        var (_, control) = Ready();
        Hook(control, ToolHook("PreToolUse"));
        Card(control);
        now += RemoteControl.CardGrace + TimeSpan.FromSeconds(5);

        Assert.True(desk.AnswerDialog("req-1-2", new Dictionary<string, string> { ["Pick a colour"] = "Blue" }).IsSuccess);
        Assert.Empty(desk.Of(Session));
        now += TimeSpan.FromSeconds(5);
        control.SettlePrompts();

        Assert.Equal(Page, Task().Question.Value);
    }

    // DESKBAR RESTARTED while the dialog waited (the branch's review, finding 1): placements and cards live in memory, so the
    // new copy holds neither, and the terminal keeps the dialog. The orphaned page-led question gets the terminal's lead on
    // the first tick, and the dialog's answer still settles it.
    [Fact]
    public void A_restart_gives_an_orphaned_page_lead_the_terminal_lead()
    {
        var (manager, control) = Ready();
        Hook(control, ToolHook("PreToolUse"));
        Card(control);

        var restarted = new RemoteControl(manager, tasks: tasks, permissions: new PermissionDesk(box + "-2", () => now));
        restarted.SettlePrompts();
        Assert.Equal(Terminal, Task().Question.Value);

        Hook(restarted, ToolHook("PostToolUse"));
        Assert.Equal(TaskStatus.InProgress, Task().Status);
    }

    // The dialog answered right after a restart, before any tick: the page-led question settles by its own text, with no
    // placement to go by (IsDialogQuestion's page branch).
    [Fact]
    public void A_page_led_question_settles_by_its_text_after_a_restart()
    {
        var (manager, control) = Ready();
        Hook(control, ToolHook("PreToolUse"));

        Hook(new RemoteControl(manager, tasks: tasks, permissions: desk), ToolHook("PostToolUse"));

        Assert.Equal(TaskStatus.InProgress, Task().Status);
        Assert.True(Task().Question.HasNoValue);
    }

    // A session's own `task ask` that copied a dialog's lead loses it: the ask is answered on the task's page anyway, and
    // only a dialog question may start with the page's lead (the restart rule above depends on it).
    [Theory]
    [InlineData("Answer in the session's terminal: should it remind? Yes or no.")]
    [InlineData("Answer on the task's page:   should it remind? Yes or no.")]
    public void A_copied_lead_is_dropped_from_a_sessions_ask(string asked)
    {
        var (_, control) = Ready();

        control.Execute(["task", "ask", asked, "--session", Session]);

        Assert.Equal("should it remind? Yes or no.", Task().Question.Value);
        Assert.Equal("Merge it", DialogQuestion.WithoutLead("Merge it"));
        Assert.Equal(DialogQuestion.Lead, DialogQuestion.WithoutLead(DialogQuestion.Lead));
    }

    // The board's own rule: only the very question rewords, and a task waiting on another keeps its board.
    [Fact]
    public void The_board_rewords_only_the_question_it_was_given()
    {
        Ready();
        var id = Task().Id;
        Assert.True(tasks.Ask(id, "Merge it", Session).IsSuccess);
        var board = tasks.Board;

        Assert.Same(board, board.RewordQuestion(id, "Something else", "x", now).Value);
        Assert.Same(board, board.RewordQuestion(id, "Merge it", "Merge it", now).Value);
        Assert.Equal("Merge it now", board.RewordQuestion(id, "Merge it", "Merge it now", now).Value.Tasks.Single().Question.Value);
    }
}
