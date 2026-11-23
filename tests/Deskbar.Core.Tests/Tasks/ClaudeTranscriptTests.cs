using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests.Tasks;

// Where a Claude session's transcript lives, and whether its last turn was cut off (ClaudeTranscript). The line shapes
// are the measured ones (docs/superpowers/notes/2026-11-23-claude-background-probes.md, "A crash mid-reply").
public class ClaudeTranscriptTests
{
    const string Prompt = """{"type":"user","message":{"role":"user","content":"Write a 1200-word story"}}""";
    const string ToolResult = """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","content":"x"}]}}""";
    const string ToolUse = """{"type":"assistant","message":{"role":"assistant","stop_reason":"tool_use","content":[{"type":"tool_use","name":"Bash"}]}}""";
    const string EndTurn = """{"type":"assistant","message":{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"ok"}]}}""";
    const string Duration = """{"type":"system","subtype":"turn_duration"}""";
    const string Cost = """{"type":"cost-state"}""";
    // Carries a message with a role, yet is not a user or assistant entry: only `type` decides.
    const string Blob = """{"type":"api-request-blob","message":{"role":"user","content":"x"}}""";

    [Fact]
    public void The_path_encodes_every_character_but_letters_and_digits_as_a_dash()
    {
        Assert.Equal(@"C:\h\projects\E--repos-TaskSpaces\6785e6b5.jsonl", ClaudeTranscript.PathOf(@"C:\h", @"E:\repos\TaskSpaces", "6785e6b5"));
        Assert.EndsWith(@"C--Users-petre-chitashvili-AppData-Local-Temp-claude-hl-probe\x.jsonl",
            ClaudeTranscript.PathOf(@"C:\h", @"C:\Users\petre.chitashvili\AppData\Local\Temp\claude\hl-probe", "x"));
        Assert.EndsWith(@"E--repos-TaskSpaces--claude-worktrees-probe-bg\x.jsonl",
            ClaudeTranscript.PathOf(@"C:\h", @"E:\repos\TaskSpaces\.claude\worktrees\probe-bg", "x"));
    }

    [Fact] public void A_turn_ending_in_an_assistant_end_turn_is_finished() => Assert.False(ClaudeTranscript.WasCutOff([Prompt, ToolUse, ToolResult, EndTurn, Duration, Cost]));
    [Fact] public void A_tool_use_without_its_result_is_cut_off() => Assert.True(ClaudeTranscript.WasCutOff([Prompt, ToolUse, Cost]));
    [Fact] public void A_tool_result_with_no_reply_after_it_is_cut_off() => Assert.True(ClaudeTranscript.WasCutOff([Prompt, ToolUse, ToolResult]));
    [Fact] public void A_prompt_with_no_reply_is_cut_off() => Assert.True(ClaudeTranscript.WasCutOff([Prompt, Cost, Blob]));

    [Fact]
    public void Metadata_lines_after_the_reply_do_not_count() =>
        Assert.False(ClaudeTranscript.WasCutOff([Prompt, EndTurn, """{"type":"ai-title","aiTitle":"t"}""", """{"type":"agent-name"}""", """{"type":"last-prompt"}""", Blob]));

    // Shapes copied from real transcripts (2026-11-23, final review item 1).
    const string PartialReply = """{"type":"assistant","isSidechain":false,"message":{"role":"assistant","stop_reason":null,"content":[{"type":"text","text":"Setting up a fresh worktree"}]}}""";
    const string Interrupted = """{"type":"user","isSidechain":false,"message":{"role":"user","content":[{"type":"text","text":"[Request interrupted by user]"}]}}""";
    const string InterruptedForTool = """{"type":"user","isSidechain":false,"message":{"role":"user","content":[{"type":"text","text":"[Request interrupted by user for tool use]"}]}}""";
    const string InterruptedAsString = """{"type":"user","message":{"role":"user","content":"[Request interrupted by user]"}}""";
    const string Caveat = """{"type":"user","isMeta":true,"isSidechain":false,"message":{"role":"user","content":"<local-command-caveat>The command below was run directly in Claude Code, not sent to you as a request.</local-command-caveat>"}}""";
    const string ModelCommand = """{"type":"user","isSidechain":false,"message":{"role":"user","content":"<command-name>/model</command-name>\n            <command-message>model</command-message>\n            <command-args>opus</command-args>"}}""";
    const string ModelStdout = """{"type":"user","isSidechain":false,"message":{"role":"user","content":"<local-command-stdout>Set model to `claude-opus-5-5`</local-command-stdout>"}}""";
    const string CompactSummary = """{"type":"user","isCompactSummary":true,"isSidechain":false,"message":{"role":"user","content":"This session is being continued from a previous conversation that ran out of context."}}""";
    const string CompactStdout = """{"type":"user","isSidechain":false,"message":{"role":"user","content":"<local-command-stdout>Compacted </local-command-stdout>"}}""";
    const string SidechainPrompt = """{"type":"user","isSidechain":true,"message":{"role":"user","content":"Explore the repo"}}""";
    const string SidechainToolUse = """{"type":"assistant","isSidechain":true,"message":{"role":"assistant","stop_reason":"tool_use","content":[{"type":"tool_use","name":"Grep"}]}}""";

    [Fact]
    public void A_turn_Petre_interrupted_with_Esc_is_finished_not_cut_off()
    {
        Assert.False(ClaudeTranscript.WasCutOff([Prompt, PartialReply, Interrupted, Cost]));
        Assert.False(ClaudeTranscript.WasCutOff([Prompt, ToolUse, ToolResult, InterruptedForTool]));
        Assert.False(ClaudeTranscript.WasCutOff([Prompt, PartialReply, InterruptedAsString]));
    }

    [Fact]
    public void A_prompt_after_an_interrupt_with_no_reply_is_cut_off() =>
        Assert.True(ClaudeTranscript.WasCutOff([Prompt, PartialReply, Interrupted, Prompt]));

    [Fact]
    public void A_local_slash_command_after_a_finished_turn_leaves_it_finished() =>
        Assert.False(ClaudeTranscript.WasCutOff([Prompt, EndTurn, Caveat, ModelCommand, ModelStdout]));

    [Fact]
    public void A_compact_summary_and_its_command_do_not_count() =>
        Assert.False(ClaudeTranscript.WasCutOff([Prompt, EndTurn, CompactSummary, Caveat, CompactStdout]));

    [Fact]
    public void A_local_command_after_a_cut_off_turn_leaves_it_cut_off() =>
        Assert.True(ClaudeTranscript.WasCutOff([Prompt, ToolUse, Caveat, ModelCommand, ModelStdout]));

    [Fact]
    public void Sidechain_entries_do_not_count()
    {
        Assert.False(ClaudeTranscript.WasCutOff([Prompt, EndTurn, SidechainPrompt, SidechainToolUse]));
        // The main turn is waiting on the subagent's tool result: still cut off.
        Assert.True(ClaudeTranscript.WasCutOff([Prompt, ToolUse, SidechainPrompt, SidechainToolUse]));
    }

    [Fact]
    public void An_empty_or_unreadable_transcript_is_not_cut_off()
    {
        Assert.False(ClaudeTranscript.WasCutOff([]));
        Assert.False(ClaudeTranscript.WasCutOff(["not json", "", "{"]));
        // Garbage after a finished turn is skipped, not read as the last entry.
        Assert.False(ClaudeTranscript.WasCutOff([Prompt, EndTurn, "{broken"]));
    }
}
