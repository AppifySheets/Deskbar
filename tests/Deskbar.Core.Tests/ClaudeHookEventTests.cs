using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests;

// What Claude Code's hooks hand Deskbar on stdin (ClaudeHookEvent), read with the samples measured on 2026-11-22
// (docs/superpowers/notes/2026-11-22-claude-hook-probes.md), and the rule that tells a headless session from one Petre
// is sitting in (Ruling 33 in the tasks-board ledger).
public class ClaudeHookEventTests
{
    // The probe's samples, session ids and paths shortened the way the note shortens them. transcript_path and the
    // resume-only fields are there because Claude Code sends them; Parse has to step over them.
    const string StartupJson =
        """{"session_id":"92943450","transcript_path":"C:\\Users\\u\\.claude\\projects\\p\\92943450.jsonl","cwd":"C:\\repos\\hookproj","hook_event_name":"SessionStart","source":"startup"}""";

    const string ResumeJson =
        """{"session_id":"92943450","transcript_path":"x","cwd":"C:\\repos\\hookproj","hook_event_name":"SessionStart","source":"resume","seconds_since_last_response":102,"context_tokens":28259,"prompt_cache_likely_expired":false,"estimated_cache_write_usd":0.2261}""";

    const string PromptJson =
        """{"session_id":"92943450","transcript_path":"x","cwd":"C:\\repos\\hookproj","prompt_id":"0e3c47f3","permission_mode":"default","hook_event_name":"UserPromptSubmit","prompt":"say ok"}""";

    const string EndJson =
        """{"session_id":"92943450","transcript_path":"x","cwd":"C:\\repos\\hookproj","prompt_id":"0e3c47f3","hook_event_name":"SessionEnd","reason":"other"}""";

    // An attended VS Code session: the values every test that is not about the headless rule uses.
    static ClaudeHookEvent Parsed(string json) => ClaudeHookEvent.Parse(json, "claude-vscode", "1").Value;

    [Fact]
    public void Parse_reads_the_documented_fields()
    {
        var startup = Parsed(StartupJson);
        Assert.Equal("SessionStart", startup.Event);
        Assert.Equal("92943450", startup.SessionId);
        Assert.Equal(@"C:\repos\hookproj", startup.Folder);
        Assert.Equal("startup", startup.Source.Value);
        Assert.False(startup.Headless);

        Assert.Equal("resume", Parsed(ResumeJson).Source.Value);

        var prompt = Parsed(PromptJson);
        Assert.Equal("UserPromptSubmit", prompt.Event);
        Assert.Equal("92943450", prompt.SessionId);
        Assert.True(prompt.Source.HasNoValue);

        var end = Parsed(EndJson);
        Assert.Equal("SessionEnd", end.Event);
        Assert.Equal(@"C:\repos\hookproj", end.Folder);
    }

    // The folder is kept exactly as Claude Code sent it. The SDK run in the probes reported an 8.3 short path; matching
    // it against a VS Code window's title is Task 8's job, which normalises both sides first.
    [Fact]
    public void The_folder_is_kept_as_given()
    {
        var json = """{"session_id":"s","cwd":"C:\\Users\\PETRE~1.CHI\\proj","hook_event_name":"Stop"}""";

        Assert.Equal(@"C:\Users\PETRE~1.CHI\proj", Parsed(json).Folder);
    }

    // CLAUDE_CODE_SESSION_ATTENDED decides when it arrived; only exactly "1" is attended. When it did not arrive (an older
    // Claude Code: empty, or cmd's literal %NAME% for an unset variable), the entrypoint decides: the SDK and -p
    // entrypoints, and no entrypoint at all, are headless, anything else is a person's session.
    [Theory]
    [InlineData("1", "claude-vscode", false)]
    [InlineData("1", "cli", false)]
    [InlineData("1", "sdk-cli", false)]
    // Run 6 of the probes: `claude -p` started from inside a VS Code session inherits claude-vscode, and ATTENDED says 0.
    [InlineData("0", "claude-vscode", true)]
    [InlineData("0", "cli", true)]
    [InlineData("yes", "cli", true)]
    [InlineData(null, "sdk-cli", true)]
    [InlineData(null, "sdk-ts", true)]
    [InlineData(null, "sdk-py", true)]
    [InlineData(null, "", true)]
    [InlineData(null, null, true)]
    [InlineData(null, "cli", false)]
    [InlineData(null, "claude-vscode", false)]
    [InlineData("", "claude-vscode", false)]
    [InlineData("", "sdk-ts", true)]
    [InlineData("%CLAUDE_CODE_SESSION_ATTENDED%", "claude-vscode", false)]
    [InlineData("%CLAUDE_CODE_SESSION_ATTENDED%", "sdk-cli", true)]
    [InlineData("%CLAUDE_CODE_SESSION_ATTENDED%", "%CLAUDE_CODE_ENTRYPOINT%", true)]
    public void Parse_marks_headless_entrypoints(string? attended, string? entrypoint, bool headless) =>
        Assert.Equal(headless, ClaudeHookEvent.Parse(StartupJson, entrypoint, attended).Value.Headless);

    // Anything that is not a hook's JSON is refused, so the verb can exit 1 and print nothing for the session to read.
    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"cwd":"C:\\x","hook_event_name":"Stop"}""")]
    [InlineData("""{"session_id":"s","cwd":"C:\\x"}""")]
    [InlineData("""{"session_id":5,"cwd":"C:\\x","hook_event_name":"Stop"}""")]
    [InlineData("""{"session_id":"","cwd":"C:\\x","hook_event_name":"Stop"}""")]
    public void Parse_refuses_what_is_not_a_hooks_json(string json) =>
        Assert.True(ClaudeHookEvent.Parse(json, "claude-vscode", "1").IsFailure);

    // Every event carries cwd, but a hook without one is still a hook: its folder is empty, and the verb still has
    // --from (CLAUDE_PROJECT_DIR, or the folder the client was started in).
    [Fact]
    public void A_missing_cwd_leaves_the_folder_empty() =>
        Assert.Equal("", Parsed("""{"session_id":"s","hook_event_name":"Stop"}""").Folder);
}
