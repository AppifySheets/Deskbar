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

    [Fact]
    public void An_empty_or_unreadable_transcript_is_not_cut_off()
    {
        Assert.False(ClaudeTranscript.WasCutOff([]));
        Assert.False(ClaudeTranscript.WasCutOff(["not json", "", "{"]));
        // Garbage after a finished turn is skipped, not read as the last entry.
        Assert.False(ClaudeTranscript.WasCutOff([Prompt, EndTurn, "{broken"]));
    }
}
