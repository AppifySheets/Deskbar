using CSharpFunctionalExtensions;
using Deskbar.Core.Control;
using Deskbar.Core.Persistence;
using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests;

// `claude-permission` (a permission hook registering its question) and `claude-permissions on|off` (the setting). Hard
// properties held here: no pipe command can answer a question; every case that is not a question Deskbar shows answers
// nothing (the terminal dialog), never allow; traces never hold the question's content.
public sealed class RemoteControlPermissionTests : IDisposable
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();
    readonly string box = Path.Combine(Path.GetTempPath(), "deskbar-perm-rc-" + Guid.NewGuid().ToString("N"));
    readonly TaskService tasks = new(new MemoryTaskStore(), () => new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));
    readonly PermissionDesk desk;

    const string Session = "abcdef12-3456-7890";
    const string Job = @"C:\Users\x\.claude\jobs\abcdef12";
    const string Repo = @"C:\repos\proj1";
    const string Secret = "SECRET-rm-rf-build";

    public RemoteControlPermissionTests() => desk = new PermissionDesk(box, () => new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        if (Directory.Exists(box)) Directory.Delete(box, true);
    }

    sealed class MemoryTaskStore : ITaskStore
    {
        public Result<TaskBoard> Load() => TaskBoard.Empty;

        public Result Save(TaskBoard board) => Result.Success();
    }

    // Everything on: sessions keep tasks, the permission setting, the Inbox look, and a task for the session.
    WorkspaceManager Ready(bool permissions = true, bool inbox = true, bool task = true)
    {
        var manager = new WorkspaceManager(desktops, monitor, titles, store, ownProcessId: 4242);
        Assert.True(manager.Start().IsSuccess);
        manager.AddWorkspace("Deskbar");
        Assert.True(manager.SetClaudePermissions(permissions).IsSuccess);
        manager.SaveTasksPanel(TasksPanelState.Default with { Inbox = inbox });
        if (task) Assert.True(tasks.Create("Ship it", Maybe<Guid>.None, tasks.NewSession(Session, Repo)).IsSuccess);
        return manager;
    }

    RemoteControl Control(WorkspaceManager manager, bool withDesk = true) =>
        new(manager, tasks: tasks, permissions: withDesk ? desk : null);

    static string Hook(string tool = "Bash", string session = Session, string hookEvent = "PermissionRequest") =>
        $$$"""{"session_id":"{{{session}}}","transcript_path":"t","cwd":"C:\\repos\\proj1","permission_mode":"default","hook_event_name":"{{{hookEvent}}}","tool_name":"{{{tool}}}","tool_input":{"command":"{{{Secret}}}","description":"d"}}""";

    string Slot(string name = "req-1-2")
    {
        Directory.CreateDirectory(Path.Combine(box, name));
        return name;
    }

    // The call as the script makes it for a background session: its job folder names the session.
    static string[] Call(string slot, string json, string job = Job, string attended = "0") =>
        ["claude-permission", "--entry", "cli", "--attended", attended, "--job", job, "--from", Repo, "--slot", slot, "--json", json];

    ControlReply Registered(WorkspaceManager manager, string[] call, bool withDesk = true) => Control(manager, withDesk).Execute(call);

    [Fact]
    public void A_background_sessions_question_is_held_and_the_reply_is_its_code_alone()
    {
        var reply = Registered(Ready(), Call(Slot(), Hook()));

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.Matches("^[0-9a-f]{32}\r\n$", reply.Output);
        var held = desk.Of(Session).Single();
        Assert.Equal("req-1-2", held.Slot);
        Assert.Equal(Secret, held.Request.Fields[0].Value);
    }

    // A SESSION IN A TERMINAL OR IN VS CODE GETS THE CARD TOO (task 6306d261, Petre: "Yes, all sessions"): no job folder,
    // attended. `claude agents` does not list it, so its question is not judged by that list (Listed false) but by where
    // its transcript stood when it asked (Mark), and leaves the page once the transcript goes on.
    [Theory]
    [InlineData("cli")]
    [InlineData("claude-vscode")]
    public void A_terminal_or_vs_code_sessions_question_is_held_and_watched_through_its_transcript(string entry)
    {
        var transcript = Path.Combine(box, "session.jsonl");
        Directory.CreateDirectory(box);
        File.WriteAllText(transcript, "{\"type\":\"user\"}\n");
        var json = Hook().Replace("\"transcript_path\":\"t\"", "\"transcript_path\":" + System.Text.Json.JsonSerializer.Serialize(transcript));

        var reply = Registered(Ready(), ["claude-permission", "--entry", entry, "--attended", "1", "--from", Repo, "--slot", Slot(), "--json", json]);

        Assert.Matches("^[0-9a-f]{32}\r\n$", reply.Output);
        var held = desk.Of(Session).Single();
        Assert.False(held.Listed);
        Assert.Equal(new TranscriptMark(transcript, new FileInfo(transcript).Length), held.Mark);

        // Answered in the session's own dialog: the tool's lines land in the transcript, and the question goes.
        File.AppendAllText(transcript, "{\"type\":\"assistant\"}\n");
        Assert.True(desk.Settle());
        Assert.Empty(desk.All);
    }

    // A background session's question is judged by the session list as before, and carries no transcript mark.
    [Fact]
    public void A_background_sessions_question_is_listed_and_carries_no_mark()
    {
        Registered(Ready(), Call(Slot(), Hook()));

        var held = desk.Of(Session).Single();
        Assert.True(held.Listed);
        Assert.Null(held.Mark);
    }

    // Every case that is not a question Deskbar can show answers NOTHING: the script then prints nothing, and the terminal
    // dialog stays. Never an allow, never a code.
    public static TheoryData<string> Refusals =>
    [
        "setting off", "inbox off", "no task", "no desk", "headless", "ask dialog", "plan dialog",
        "not a permission hook", "no slot", "bad slot", "slot already held", "bad json",
    ];

    [Theory]
    [MemberData(nameof(Refusals))]
    public void Every_other_case_answers_nothing(string why)
    {
        var slot = Slot();
        var manager = Ready(permissions: why != "setting off", inbox: why != "inbox off", task: why != "no task");
        if (why == "slot already held") Registered(manager, Call(slot, Hook()));
        string[] call = why switch
        {
            "headless" => ["claude-permission", "--entry", "sdk-cli", "--attended", "0", "--from", Repo, "--slot", slot, "--json", Hook()],
            "ask dialog" => Call(slot, Hook("AskUserQuestion")),
            "plan dialog" => Call(slot, Hook("ExitPlanMode")),
            "not a permission hook" => Call(slot, Hook(hookEvent: "PreToolUse")),
            "no slot" => ["claude-permission", "--entry", "cli", "--attended", "0", "--job", Job, "--from", Repo, "--json", Hook()],
            "bad slot" => Call(@"..\..\x", Hook()),
            "bad json" => Call(slot, "{not json"),
            _ => Call(slot, Hook()),
        };

        var reply = Registered(manager, call, withDesk: why != "no desk");

        Assert.Equal("", reply.Output);
        // A second call on a slot already held (another process of the user replaying it) cannot take it over: the
        // session's newer request takes the held one off first (one card per session), and the slot is gone with it, so
        // both hooks end in the terminal. Nothing is ever held for the replay.
        Assert.Empty(desk.All);
    }

    // ONE CARD PER SESSION (the review's L5): a newer PermissionRequest from the session takes its older card off, even
    // when the newer one is not shown (here Claude Code's own AskUserQuestion dialog): the session no longer waits on the
    // old question, so its slot goes and a click on the old card writes nothing.
    [Theory]
    [InlineData("Bash")]
    [InlineData("AskUserQuestion")]
    public void A_newer_request_of_the_session_takes_its_older_card_off(string tool)
    {
        var manager = Ready();
        var old = Slot("req-1-1");
        Assert.NotEqual("", Registered(manager, Call(old, Hook())).Output);

        var reply = Registered(manager, Call(Slot("req-2-2"), Hook(tool)));

        Assert.False(Directory.Exists(Path.Combine(box, old)));
        Assert.True(desk.Answer(old, PermissionDecision.Allow).IsFailure);
        Assert.Equal(tool == "Bash" ? "req-2-2" : null, desk.Of(Session).SingleOrDefault()?.Slot);
        Assert.Equal(tool == "Bash", reply.Output.Length > 0);
    }

    // Review focus 3 of task b310a649: while the card shows the session's permission prompt, the prompt's Notification
    // six seconds later puts no second question on the task (RemoteControl.PromptAsked).
    [Fact]
    public void A_card_on_show_adds_no_dialog_question()
    {
        var manager = Ready();
        Assert.NotEqual("", Registered(manager, Call(Slot(), Hook())).Output);
        var notified = $$$"""{"session_id":"{{{Session}}}","transcript_path":"t","cwd":"C:\repos\proj1","hook_event_name":"Notification","message":"Claude needs your permission","notification_type":"permission_prompt"}""";

        Control(manager).Execute(["claude-hook", "--entry", "cli", "--attended", "0", "--job", Job, "--from", Repo, "--json", notified]);

        Assert.True(tasks.Board.TaskOf(Session).Value.Question.HasNoValue);
    }

    // The task closed: its session has no open task, so the question stays in the terminal.
    [Fact]
    public void A_closed_task_answers_nothing()
    {
        var manager = Ready();
        var task = tasks.Board.TaskOf(Session).Value;
        // Past "Claude is still working on it": the session is live on the task.
        Assert.True(tasks.Close(task.Id, stoppingClaude: true).IsSuccess);

        Assert.Equal("", Registered(manager, Call(Slot(), Hook())).Output);
    }

    [Fact]
    public void The_setting_needs_the_task_hooks()
    {
        var manager = Ready();
        Assert.True(manager.SetClaudeTasks(false).IsSuccess);

        Assert.False(manager.State.ClaudePermissionsInEffect);
        Assert.Equal("", Registered(manager, Call(Slot(), Hook())).Output);
    }

    // THE HARD PROPERTY: no pipe command answers a question. Every verb the pipe knows is tried with the shapes an
    // answer could take (the slot, the code, a word), and the slot never gets an answer file; the question stays on show
    // and a click still answers it.
    [Fact]
    public void No_pipe_command_can_answer_a_question()
    {
        var manager = Ready();
        var slot = Slot();
        var code = Registered(manager, Call(slot, Hook())).Output.Trim();
        var control = Control(manager);
        var shapes = new[]
        {
            Array.Empty<string>(), [slot], [code], [slot, "allow"], [code, "allow"], [slot, code, "allow"], ["allow", slot],
            [slot, "edits"], ["--slot", slot, "allow"], ["answer", slot, "allow"], ["allow", "--slot", slot, "--code", code],
        };

        foreach (var verb in ControlUsage.All.Select(c => c.Verb).Distinct().Append("permission").Append("allow").Append("answer"))
            foreach (var shape in shapes)
                control.Execute([verb, .. shape]);

        Assert.False(File.Exists(Path.Combine(box, slot, PermissionDesk.AnswerFile)));
        Assert.Equal(slot, desk.All.Single().Slot);
        Assert.True(desk.Answer(slot, PermissionDecision.Deny).IsSuccess);
    }

    // The terminal answered while the card was on show: the session's next event (its turn ending, a new prompt, its end)
    // takes the question off and deletes the slot, so the hook script ends without a word. The permission prompt's own
    // Notification, sent while it still waits, does not.
    [Theory]
    [InlineData("Stop", true)]
    [InlineData("UserPromptSubmit", true)]
    [InlineData("SessionEnd", true)]
    [InlineData("Notification", false)]
    public void The_sessions_next_event_takes_its_question_off(string hookEvent, bool settles)
    {
        var manager = Ready();
        var slot = Slot();
        Registered(manager, Call(slot, Hook()));

        Control(manager).Execute(["claude-hook", "--entry", "cli", "--attended", "0", "--job", Job, "--from", Repo,
            "--json", $$"""{"session_id":"{{Session}}","cwd":"C:\\repos\\proj1","hook_event_name":"{{hookEvent}}"}"""]);

        Assert.Equal(!settles, desk.Asks(Session));
        Assert.Equal(!settles, Directory.Exists(Path.Combine(box, slot)));
    }

    [Fact]
    public void A_registration_never_prints_an_answer()
    {
        var output = Registered(Ready(), Call(Slot(), Hook())).Output;

        Assert.DoesNotContain("allow", output);
        Assert.DoesNotContain("hookSpecificOutput", output);
    }

    // The app's trace line for a `ctl` call is ControlArguments.ForTrace: the event, the session and a length.
    [Fact]
    public void The_trace_never_holds_the_questions_content()
    {
        var line = ControlArguments.ForTrace(Call("req-1-2", Hook()));

        Assert.DoesNotContain(Secret, line);
        Assert.DoesNotContain("command", line);
        Assert.Contains("PermissionRequest", line);
        Assert.True(ControlArguments.IsClaudeHook(Call("req-1-2", Hook())));
    }

    [Fact]
    public void The_slot_option_is_read_and_an_older_copy_would_skip_it()
    {
        Assert.Equal("req-7-8", ControlArguments.ParseClaudeHook(["--slot", "req-7-8", "--json", "{}"]).Value.Slot);
        Assert.Equal("{}", ControlArguments.ParseClaudeHook(["--slot", "req-7-8", "--json", "{}"]).Value.Json);
    }

    // The client adds the caller's folder to the hook verbs, this one too.
    [Fact]
    public void The_client_adds_the_callers_folder() =>
        Assert.Equal(new[] { "claude-permission", "--from", @"C:\here" }, ControlArguments.WithCallerFolder(["claude-permission"], @"C:\here"));

    [Fact]
    public void The_setting_is_off_by_default_and_has_its_twin()
    {
        var manager = new WorkspaceManager(desktops, monitor, titles, store, ownProcessId: 4242);
        Assert.True(manager.Start().IsSuccess);
        Assert.False(manager.State.ClaudePermissions);
        Assert.False(AppState.Empty.ClaudePermissions);

        Assert.Equal(ControlReply.Ok, Control(manager).Execute(["claude-permissions", "on"]).ExitCode);
        Assert.True(manager.State.ClaudePermissions);
        Assert.Contains("\"claudePermissions\": \"on\"", Control(manager).Execute(["claude-permissions", "on"]).Output);
        Assert.Equal(ControlReply.Ok, Control(manager).Execute(["claude-permissions", "off"]).ExitCode);
        Assert.False(manager.State.ClaudePermissions);
        Assert.Equal(ControlReply.Failed, Control(manager).Execute(["claude-permissions", "maybe"]).ExitCode);

        Assert.Contains(ControlUsage.All, c => c.Verb == "claude-permissions");
        Assert.Contains(ControlUsage.All, c => c.Verb == "claude-permission");
    }
}
