using System.Text.Json;
using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests.Tasks;

// The chat read out of a Claude transcript (ClaudeChat, ClaudeChatReader). Every line below is MADE UP in the shape of
// the measured ones (see the comment on ClaudeChat); nothing is copied from a real session.
public class ClaudeChatTests
{
    // ---- line builders -----------------------------------------------------------------------------------------------

    static string J(string text) => JsonSerializer.Serialize(text);

    static string Human(string text, string? uuid = null) =>
        $$$"""{"type":"user","uuid":{{{J(uuid ?? "u-" + text)}}},"timestamp":"2026-01-02T03:04:05Z","origin":{"kind":"human"},"message":{"role":"user","content":{{{J(text)}}}}}""";

    static string Said(string text, string messageId = "m1", string? uuid = null) =>
        $$$"""{"type":"assistant","uuid":{{{J(uuid ?? "a-" + text)}}},"timestamp":"2026-01-02T03:04:06Z","message":{"id":{{{J(messageId)}}},"role":"assistant","model":"claude-test","content":[{"type":"text","text":{{{J(text)}}}}]}}""";

    static string Tool(string id, string name, string inputJson = "{}") =>
        $$$"""{"type":"assistant","uuid":"a-{{{id}}}","timestamp":"2026-01-02T03:04:07Z","message":{"id":"m-{{{id}}}","role":"assistant","model":"claude-test","content":[{"type":"tool_use","id":{{{J(id)}}},"name":{{{J(name)}}},"input":{{{inputJson}}}}]}}""";

    static string Result(string id, bool error = false) =>
        $$$"""{"type":"user","uuid":"r-{{{id}}}","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":{{{J(id)}}},"content":"output","is_error":{{{(error ? "true" : "false")}}}}]},"toolUseResult":{"stdout":"output"}}""";

    static string Path(string file) => JsonSerializer.Serialize(new { file_path = file });

    static ChatSteps OnlySteps(ChatItem item) => Assert.IsType<ChatSteps>(item);
    static ChatMessage Message(ChatItem item) => Assert.IsType<ChatMessage>(item);

    // ---- text only ----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_chat_of_text_only_is_the_users_and_claudes_messages_in_order()
    {
        var chat = ClaudeChat.Parse([Human("Fix the sample bug"), Said("Looking at it now"), Human("Thanks"), Said("Done")]);

        Assert.Equal(4, chat.Count);
        Assert.Equal([ChatRole.User, ChatRole.Claude, ChatRole.User, ChatRole.Claude], chat.Select(i => Message(i).Role));
        Assert.Equal("Fix the sample bug", Message(chat[0]).Text);
        Assert.Equal("Done", Message(chat[3]).Text);
        Assert.Equal(DateTimeOffset.Parse("2026-01-02T03:04:05Z"), chat[0].At.Value);
    }

    [Fact]
    public void An_empty_transcript_is_an_empty_chat() => Assert.Empty(ClaudeChat.Parse([]));

    // One reply is several lines with one block each, and thinking is never shown.
    [Fact]
    public void Thinking_blocks_are_not_shown_and_two_text_lines_of_one_message_are_joined()
    {
        const string thinking = """{"type":"assistant","uuid":"t1","message":{"id":"m9","model":"claude-test","content":[{"type":"thinking","thinking":"secret","signature":"x"}]}}""";
        var chat = ClaudeChat.Parse([thinking, Said("First part", "m9", "a1"), Said("Second part", "m9", "a2")]);

        var only = Message(Assert.Single(chat));
        Assert.Equal("First part\n\nSecond part", only.Text);
        Assert.Equal("a1", only.Id);
    }

    // ---- steps ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Steps_between_two_texts_are_one_run_and_the_next_text_starts_a_new_one()
    {
        var chat = ClaudeChat.Parse([
            Human("Change the sample"),
            Said("I will look"),
            Tool("t1", "Read", Path(@"C:\somewhere\deep\Sample.cs")), Result("t1"),
            Tool("t2", "Edit", Path("/home/x/Sample.cs")), Result("t2"),
            Tool("t3", "Bash", """{"command":"dotnet test","description":"run"}"""), Result("t3"),
            Said("Tests pass", "m-later"),
            Tool("t4", "Grep", """{"pattern":"TODO"}"""), Result("t4"),
        ]);

        Assert.Equal(5, chat.Count);
        var first = OnlySteps(chat[2]);
        Assert.Equal("t1", first.Id);
        Assert.Equal([StepKind.Read, StepKind.Edit, StepKind.Run], first.Steps.Select(s => s.Kind));
        Assert.Equal(["Sample.cs", "Sample.cs", "dotnet test"], first.Steps.Select(s => s.Label));
        Assert.All(first.Steps, s => Assert.True(s.Done && !s.Failed));
        Assert.Equal("Tests pass", Message(chat[3]).Text);
        Assert.Equal([StepKind.Search], OnlySteps(chat[4]).Steps.Select(s => s.Kind));
    }

    [Fact]
    public void A_failed_tool_result_marks_its_step_failed_and_a_step_without_a_result_is_not_done()
    {
        var chat = ClaudeChat.Parse([
            Tool("ok", "Read", Path("A.cs")), Result("ok"),
            Tool("bad", "Bash", """{"command":"dotnet build"}"""), Result("bad", error: true),
            Tool("running", "Bash", """{"command":"sleep 60"}"""),
        ]);

        var steps = OnlySteps(Assert.Single(chat)).Steps;
        Assert.Equal([false, true, false], steps.Select(s => s.Failed));
        Assert.Equal([true, true, false], steps.Select(s => s.Done));
    }

    [Fact]
    public void An_unknown_tool_is_other_and_its_label_is_its_name()
    {
        var steps = OnlySteps(Assert.Single(ClaudeChat.Parse([
            Tool("a", "FrobnicateWidget", """{"size":3}"""),
            Tool("b", "mcp__sample__lookup", """{"q":"x"}"""),
            Tool("c", "Bash", "{}")]))).Steps;

        Assert.Equal([StepKind.Other, StepKind.Other, StepKind.Run], steps.Select(s => s.Kind));
        Assert.Equal(["FrobnicateWidget", "mcp__sample__lookup", "Bash"], steps.Select(s => s.Label));
    }

    // One test per kind of label, the examples a UI developer will want.
    [Theory]
    [InlineData("Read", """{"file_path":"C:\\projects\\sample\\src\\Dock.cs"}""", StepKind.Read, "Dock.cs")]
    [InlineData("Write", """{"file_path":"/tmp/out/notes.md","content":"x"}""", StepKind.Write, "notes.md")]
    [InlineData("MultiEdit", """{"file_path":"a\\b\\C.cs","edits":[]}""", StepKind.Edit, "C.cs")]
    [InlineData("NotebookEdit", """{"notebook_path":"/n/lab.ipynb"}""", StepKind.Edit, "lab.ipynb")]
    [InlineData("PowerShell", """{"command":"cd \"C:\\work dir\" && dotnet test --filter Sample"}""", StepKind.Run, "dotnet test --filter Sample")]
    [InlineData("Bash", """{"command":"cd /w; git status\ngit diff"}""", StepKind.Run, "git status")]
    [InlineData("Grep", """{"pattern":"class\\s+Dock","path":"src"}""", StepKind.Search, "class\\s+Dock")]
    [InlineData("Glob", """{"pattern":"**/*.cs"}""", StepKind.Search, "**/*.cs")]
    [InlineData("WebFetch", """{"url":"https://example.test/docs/page","prompt":"x"}""", StepKind.Web, "example.test/docs/page")]
    [InlineData("WebSearch", """{"query":"sample query"}""", StepKind.Web, "sample query")]
    [InlineData("Agent", """{"description":"Check the sample","subagent_type":"tester","prompt":"p"}""", StepKind.Subagent, "Check the sample")]
    [InlineData("TodoWrite", """{"todos":[]}""", StepKind.Plan, "TodoWrite")]
    [InlineData("TaskCreate", """{"subject":"Write the sample"}""", StepKind.Plan, "Write the sample")]
    [InlineData("Skill", """{"skill":"sample-skill"}""", StepKind.Other, "Skill sample-skill")]
    [InlineData("Read", """{}""", StepKind.Read, "Read")]
    public void A_step_is_labelled_from_its_tool_input(string tool, string input, StepKind kind, string label)
    {
        var step = Assert.Single(OnlySteps(Assert.Single(ClaudeChat.Parse([Tool("x", tool, input)]))).Steps);

        Assert.Equal(kind, step.Kind);
        Assert.Equal(label, step.Label);
    }

    [Fact]
    public void A_long_command_is_trimmed_to_sixty_characters_with_an_ellipsis()
    {
        var command = "dotnet test tests\\Sample.Tests -c Release --filter \"FullyQualifiedName~SomeVeryLongNamespace.SomeVeryLongClass\"";
        var label = Assert.Single(OnlySteps(Assert.Single(ClaudeChat.Parse([Tool("x", "Bash", JsonSerializer.Serialize(new { command }))]))).Steps).Label;

        Assert.Equal(ClaudeChat.MaxLabelLength, label.Length);
        Assert.EndsWith("\u2026", label);
        Assert.StartsWith("dotnet test tests", label);
    }

    // ---- what is not chat ---------------------------------------------------------------------------------------------

    [Fact]
    public void Lines_that_are_not_chat_are_skipped()
    {
        string[] noise =
        [
            """{"type":"cost-state","totalCostUSD":1}""",
            """{"type":"ai-title","aiTitle":"t"}""",
            """{"type":"attachment","attachment":{"type":"hook_success","content":"x"}}""",
            """{"type":"system","subtype":"stop_hook_summary","hookCount":1}""",
            """{"type":"api-request-blob","message":{"role":"user","content":"blob"}}""",
            // a skill body and hook feedback: Claude Code to the model, flagged isMeta
            """{"type":"user","isMeta":true,"message":{"role":"user","content":"Base directory for this skill: x"}}""",
            """{"type":"user","isMeta":true,"message":{"role":"user","content":"Stop hook feedback: carry on"}}""",
            // a slash command's own entries
            """{"type":"user","message":{"role":"user","content":"<command-name>/model</command-name>"}}""",
            """{"type":"user","message":{"role":"user","content":"<local-command-stdout>Set model</local-command-stdout>"}}""",
            // a background task and another session talking, not the owner
            """{"type":"user","origin":{"kind":"task-notification","producer":"session-task"},"message":{"role":"user","content":"<task-notification>done</task-notification>"}}""",
            """{"type":"user","origin":{"kind":"peer","from":"s2"},"message":{"role":"user","content":"Another Claude session sent a message: hi"}}""",
            // an old version's task notification with no origin
            """{"type":"user","message":{"role":"user","content":"<task-notification>done</task-notification>"}}""",
            // the summary /compact writes
            """{"type":"user","isCompactSummary":true,"message":{"role":"user","content":"This session is being continued from a previous conversation"}}""",
            // Claude Code's own made-up reply
            """{"type":"assistant","message":{"id":"s","model":"<synthetic>","content":[{"type":"text","text":"No response requested."}]}}""",
        ];

        Assert.Empty(ClaudeChat.Parse([.. noise]));
        Assert.Equal("Real", Message(Assert.Single(ClaudeChat.Parse([.. noise, Human("Real")]))).Text);
    }

    [Fact]
    public void Blocks_Claude_Code_injects_into_a_prompt_are_dropped_from_its_text()
    {
        const string line = """{"type":"user","origin":{"kind":"human"},"message":{"role":"user","content":[{"type":"image","source":{"type":"base64"}},{"type":"text","text":"<browser_instruction># Browser tools\nuse them</browser_instruction>"},{"type":"text","text":"[Image: source: C:\\x\\a.png]"},{"type":"text","text":"Look at this picture<system-reminder>hidden</system-reminder>"}]}}""";

        var message = Message(Assert.Single(ClaudeChat.Parse([line])));

        Assert.Equal("Look at this picture", message.Text);
        Assert.Equal(1, message.Images);
    }

    [Fact]
    public void A_prompt_with_only_an_image_is_still_a_message()
    {
        const string line = """{"type":"user","message":{"role":"user","content":[{"type":"image","source":{}}]}}""";

        var message = Message(Assert.Single(ClaudeChat.Parse([line])));

        Assert.Equal("", message.Text);
        Assert.Equal(1, message.Images);
    }

    [Fact]
    public void A_prompt_typed_while_claude_was_busy_is_a_queued_command_and_is_not_shown_twice()
    {
        const string queued = """{"type":"attachment","uuid":"q1","timestamp":"2026-01-02T03:04:09Z","attachment":{"type":"queued_command","commandMode":"prompt","origin":{"kind":"human"},"prompt":[{"type":"text","text":"Also check the footer"}]}}""";
        const string notification = """{"type":"attachment","attachment":{"type":"queued_command","commandMode":"task-notification","origin":{"kind":"task-notification"},"prompt":"<task-notification>x</task-notification>"}}""";

        var chat = ClaudeChat.Parse([Said("Working"), queued, notification, Human("Also check the footer")]);

        Assert.Equal(["Working", "Also check the footer"], chat.Select(i => Message(i).Text));
    }

    [Fact]
    public void An_interrupt_is_a_marker_and_a_compaction_is_a_marker_with_the_history_kept()
    {
        const string interrupted = """{"type":"user","message":{"role":"user","content":[{"type":"text","text":"[Request interrupted by user]"}]}}""";
        const string boundary = """{"type":"system","subtype":"compact_boundary","uuid":"cb","content":"Conversation compacted","compactMetadata":{"trigger":"auto"}}""";
        const string summary = """{"type":"user","isCompactSummary":true,"message":{"role":"user","content":"summary"}}""";

        var chat = ClaudeChat.Parse([Human("Before"), Said("Reply"), interrupted, boundary, summary, Human("After")]);

        Assert.Equal(
            [typeof(ChatMessage), typeof(ChatMessage), typeof(ChatMarker), typeof(ChatMarker), typeof(ChatMessage)],
            chat.Select(i => i.GetType()));
        Assert.Equal([ChatMarkerKind.Interrupted, ChatMarkerKind.Compacted], chat.OfType<ChatMarker>().Select(m => m.Kind));
        Assert.Equal("After", Message(chat[^1]).Text);
    }

    [Fact]
    public void A_subagents_inner_messages_are_skipped_unless_they_are_all_there_is()
    {
        static string Inner(string line) => line.Replace("\"type\":", "\"isSidechain\":true,\"type\":");

        var withMain = ClaudeChat.Parse([Human("Main"), Inner(Said("Inner reply")), Inner(Tool("i1", "Read", Path("X.cs"))), Said("Main reply")]);
        Assert.Equal(["Main", "Main reply"], withMain.Select(i => Message(i).Text));

        var onlyInner = ClaudeChat.Parse([Inner(Human("Task for the subagent")), Inner(Said("Subagent says")), Inner(Tool("i2", "Read", Path("Y.cs")))]);
        Assert.Equal(3, onlyInner.Count);
        Assert.Equal("Subagent says", Message(onlyInner[1]).Text);
    }

    // ---- bad input ----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_line_that_is_not_json_or_not_an_object_is_skipped_and_the_rest_still_reads()
    {
        var chat = ClaudeChat.Parse([
            Human("One"), "{ this is not json", "", "   ", "[1,2,3]", "\"text\"", "null", "42",
            """{"type":"assistant","message":"not an object"}""",
            """{"type":"assistant","message":{"content":[{"type":"tool_use","name":5}]}}""",
            """{"type":"user","message":{"content":[7,null,{"type":"tool_result"}]}}""",
            Said("Two")]);

        Assert.Equal(["One", "Two"], chat.Select(i => Message(i).Text));
    }

    // ---- the cap ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Only_the_last_items_are_kept()
    {
        var lines = Enumerable.Range(1, 50).Select(i => i % 2 == 0 ? Human($"msg {i}") : Said($"msg {i}", $"m{i}")).ToList();

        var chat = ClaudeChat.Parse(lines, maxItems: 5);

        Assert.Equal(["msg 46", "msg 47", "msg 48", "msg 49", "msg 50"], chat.Select(i => Message(i).Text));
    }

    [Fact]
    public void A_result_for_a_step_that_was_trimmed_away_changes_nothing()
    {
        var lines = new List<string> { Tool("old", "Read", Path("Old.cs")) };
        lines.AddRange(Enumerable.Range(1, 30).Select(i => Human($"msg {i}")));
        lines.Add(Result("old", error: true));

        var chat = ClaudeChat.Parse(lines, maxItems: 4);

        Assert.Equal(4, chat.Count);
        Assert.All(chat, i => Assert.IsType<ChatMessage>(i));
    }

    [Fact]
    public void A_very_long_text_is_cut()
    {
        var chat = ClaudeChat.Parse([Said(new string('x', ClaudeChat.MaxTextLength * 2))]);

        Assert.Equal(ClaudeChat.MaxTextLength, Message(Assert.Single(chat)).Text.Length);
    }

    // ---- reading a file that grows ---------------------------------------------------------------------------------

    sealed class Scratch : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "deskbar-chat-" + Guid.NewGuid().ToString("N") + ".jsonl");
        public void Append(string text) => File.AppendAllText(Path, text);
        public void Dispose() { try { File.Delete(Path); } catch (IOException) { } }
    }

    [Fact]
    public void A_missing_file_is_no_change_and_no_exception()
    {
        var update = new ClaudeChatReader().Read(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "deskbar-chat-nothing-here.jsonl"));

        Assert.False(update.Changed);
        Assert.Empty(update.Items);
        Assert.Equal(0, update.Position);
    }

    [Fact]
    public void The_reader_returns_only_what_is_new_and_a_poll_with_nothing_new_is_unchanged()
    {
        using var file = new Scratch();
        var reader = new ClaudeChatReader();
        file.Append(Human("Start") + "\n" + Said("Working") + "\n");

        var first = reader.Read(file.Path);
        Assert.True(first.Changed);
        Assert.Equal(2, first.Items.Count);
        Assert.Equal(new FileInfo(file.Path).Length, first.Position);

        var idle = reader.Read(file.Path);
        Assert.False(idle.Changed);
        Assert.Equal(first.Position, idle.Position);
        Assert.Equal(2, idle.Items.Count);

        file.Append(Human("More") + "\n");
        var second = reader.Read(file.Path);
        Assert.True(second.Changed);
        Assert.Equal(["Start", "Working", "More"], second.Items.Select(i => Message(i).Text));
    }

    [Fact]
    public void A_half_written_last_line_is_not_consumed_until_it_is_finished()
    {
        using var file = new Scratch();
        var reader = new ClaudeChatReader();
        var whole = Said("Second");
        file.Append(Human("First") + "\n" + whole[..(whole.Length / 2)]);

        var half = reader.Read(file.Path);
        Assert.Equal(["First"], half.Items.Select(i => Message(i).Text));
        Assert.Equal((Human("First") + "\n").Length, half.Position);

        file.Append(whole[(whole.Length / 2)..] + "\n");
        var done = reader.Read(file.Path);
        Assert.True(done.Changed);
        Assert.Equal(["First", "Second"], done.Items.Select(i => Message(i).Text));
    }

    // The failure arrives in a later poll than the step, and a run goes on across polls: both must land on the same
    // run item, with the same Id, rather than a failure with no step or a second run.
    [Fact]
    public void A_result_in_a_later_poll_updates_its_step_and_a_run_grows_across_polls()
    {
        using var file = new Scratch();
        var reader = new ClaudeChatReader();
        file.Append(Human("Go") + "\n" + Tool("t1", "Bash", """{"command":"dotnet build"}""") + "\n");

        var first = reader.Read(file.Path);
        var run = OnlySteps(first.Items[1]);
        Assert.False(Assert.Single(run.Steps).Done);

        file.Append(Result("t1", error: true) + "\n" + Tool("t2", "Read", Path("A.cs")) + "\n");
        var second = reader.Read(file.Path);

        var grown = OnlySteps(Assert.Single(second.Items.Skip(1)));
        Assert.Equal(run.Id, grown.Id);
        Assert.Equal([true, false], grown.Steps.Select(s => s.Failed));
        Assert.Equal([true, false], grown.Steps.Select(s => s.Done));
    }

    [Fact]
    public void A_file_that_became_shorter_is_read_again_from_the_start()
    {
        using var file = new Scratch();
        var reader = new ClaudeChatReader();
        file.Append(Human("Old one") + "\n" + Said("Old two") + "\n");
        reader.Read(file.Path);

        File.WriteAllText(file.Path, Human("New") + "\n");
        var update = reader.Read(file.Path);

        Assert.True(update.Changed);
        Assert.Equal(["New"], update.Items.Select(i => Message(i).Text));
    }

    [Fact]
    public void A_big_file_is_read_from_its_tail_only()
    {
        using var file = new Scratch();
        file.Append(string.Concat(Enumerable.Range(1, 300).Select(i => Human($"line {i:000}") + "\n")));
        var oneLine = (Human("line 001") + "\n").Length;
        var reader = new ClaudeChatReader(maxItems: 1000, initialTailBytes: oneLine * 10 + 5);

        var update = reader.Read(file.Path);

        var texts = update.Items.Select(i => Message(i).Text).ToList();
        Assert.Equal("line 300", texts[^1]);
        Assert.InRange(texts.Count, 9, 11);
        Assert.Equal(new FileInfo(file.Path).Length, update.Position);
    }

    [Fact]
    public void Multi_byte_text_survives_the_reader()
    {
        using var file = new Scratch();
        file.Append(Human("\u10d2\u10d0\u10db\u10d0\u10e0\u10ef\u10dd\u10d1\u10d0 \u2014 caf\u00e9") + "\n");

        var update = new ClaudeChatReader().Read(file.Path);

        Assert.Equal("\u10d2\u10d0\u10db\u10d0\u10e0\u10ef\u10dd\u10d1\u10d0 \u2014 caf\u00e9", Message(Assert.Single(update.Items)).Text);
    }

    // ---- secrets never reach a label ---------------------------------------------------------------------------------

    static string LabelOfCommand(string command) =>
        Assert.Single(OnlySteps(Assert.Single(ClaudeChat.Parse([Tool("x", "Bash", JsonSerializer.Serialize(new { command }))]))).Steps).Label;

    // Every value below is made up.
    [Theory]
    [InlineData("curl -H \"Authorization: Bearer fakeValue123\" https://example.test/a", "fakeValue123")]
    [InlineData("curl -H 'authorization: Basic ZmFrZTpmYWtl' https://example.test", "ZmFrZTpmYWtl")]
    [InlineData("gh api x --header Bearer madeUpTokenText", "madeUpTokenText")]
    [InlineData("run --token=abc-not-real-1", "abc-not-real-1")]
    [InlineData("run --API_KEY=notARealKey", "notARealKey")]
    [InlineData("tool password=\"two words\" --x", "two words")]
    [InlineData("tool SECRET='made up'", "made up")]
    [InlineData("tool ACCESS_TOKEN=zzz111", "zzz111")]
    [InlineData("deploy 0123456789abcdef0123456789abcdef01234567", "0123456789abcdef0123456789abcdef01234567")]
    [InlineData("echo QWxhZGRpbjpvcGVuU2VzYW1lMTIzNDU2Nzg5MA9z", "QWxhZGRpbjpvcGVuU2VzYW1lMTIzNDU2Nzg5MA9z")]
    public void A_secret_in_a_command_never_reaches_its_label(string command, string secret)
    {
        var label = LabelOfCommand(command);

        Assert.DoesNotContain(secret, label);
        Assert.Contains("***", label);
    }

    [Fact]
    public void A_secret_that_straddles_the_cut_is_not_shown_in_part()
    {
        // The value starts before character 60 and ends after it: redacting after the cut would leave its first half.
        var command = "run --option-with-a-long-name-xxxxxxxxxxxxxxxxxxxxxxxxxxx --token=ABCDEFGHIJKLMNOP1234";

        Assert.DoesNotContain("ABCDE", LabelOfCommand(command));
    }

    [Fact]
    public void Ordinary_commands_paths_and_urls_are_left_alone()
    {
        Assert.Equal("dotnet test tests/Sample.Tests -c Release", LabelOfCommand("dotnet test tests/Sample.Tests -c Release"));
        Assert.Equal("git log --oneline -5", LabelOfCommand("git log --oneline -5"));
        Assert.Equal("grep -rn \"monkey\" src", LabelOfCommand("grep -rn \"monkey\" src"));
        var step = Assert.Single(OnlySteps(Assert.Single(ClaudeChat.Parse([Tool("u", "WebFetch", """{"url":"https://example.test/docs/page?id=7"}""")]))).Steps);
        Assert.Equal("example.test/docs/page?id=7", step.Label);
    }

    [Fact]
    public void A_url_with_a_token_in_its_query_is_redacted()
    {
        var step = Assert.Single(OnlySteps(Assert.Single(ClaudeChat.Parse([Tool("u", "WebFetch", """{"url":"https://example.test/x?token=fakeFake9&page=2"}""")]))).Steps);

        Assert.DoesNotContain("fakeFake9", step.Label);
    }

    // ---- Deskbar's own housekeeping is flagged --------------------------------------------------------------------

    [Theory]
    [InlineData("Bash", "deskbar.cmd task status-line 'checking' --session s1")]
    [InlineData("Bash", "\"$APPDATA/Deskbar/deskbar.cmd\" task comment - --session s1")]
    [InlineData("PowerShell", "& \"C:\\x\\Deskbar\\deskbar.cmd\" task ask - --session s1")]
    [InlineData("Bash", "cd /w/repo && deskbar.cmd task join abc12345 --session s1")]
    [InlineData("Bash", "DESKBAR.CMD TASK done --session s1")]
    public void A_call_of_deskbars_own_shim_is_housekeeping(string tool, string command)
    {
        var step = Assert.Single(OnlySteps(Assert.Single(ClaudeChat.Parse([Tool("h", tool, JsonSerializer.Serialize(new { command }))]))).Steps);

        Assert.True(step.Housekeeping);
    }

    [Theory]
    [InlineData("git log --oneline")]
    [InlineData("dotnet test")]
    [InlineData("echo deskbar.cmd task is the shim")]
    [InlineData("cat notes-on-deskbar.cmd")]
    [InlineData("deskbar.cmd status")]
    public void Any_other_command_is_not_housekeeping(string command)
    {
        var step = Assert.Single(OnlySteps(Assert.Single(ClaudeChat.Parse([Tool("h", "Bash", JsonSerializer.Serialize(new { command }))]))).Steps);

        Assert.False(step.Housekeeping);
    }}
