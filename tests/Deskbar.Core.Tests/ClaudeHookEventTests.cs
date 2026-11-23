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

    // A background session (`claude --bg`) runs its hooks with ATTENDED=0 although a person can attach to it, so the
    // attended flag alone drops its hooks. Its hooks carry CLAUDE_JOB_DIR, whose last segment is the first 8 characters
    // of its own session id (measured 2026-11-23, also after a resume). Entrypoint is `cli`, as measured.
    const string OwnSessionJson =
        """{"session_id":"1a28e41b-d0ed-42b8-a876-da75a6a38715","cwd":"C:\\repos\\hookproj","hook_event_name":"SessionStart","source":"startup"}""";

    const string OtherSessionJson =
        """{"session_id":"99999999-d0ed-42b8-a876-da75a6a38715","cwd":"C:\\repos\\hookproj","hook_event_name":"SessionStart","source":"startup"}""";

    [Theory]
    [InlineData(@"C:\Users\u\.claude\jobs\1a28e41b")]
    [InlineData(@"C:\Users\u\.claude\jobs\1A28E41B\")]
    public void A_background_session_whose_job_directory_is_its_own_is_attended_though_ATTENDED_is_0(string job) =>
        Assert.False(ClaudeHookEvent.Parse(OwnSessionJson, "cli", "0", job).Value.Headless);

    // CLAUDE_JOB_DIR is an ordinary variable of the background process, so a `claude -p` run from its Bash tool inherits
    // it. That directory names ANOTHER session and must override nothing (Ruling 33).
    [Fact]
    public void A_claude_p_that_inherited_another_sessions_job_directory_stays_headless() =>
        Assert.True(ClaudeHookEvent.Parse(OtherSessionJson, "sdk-cli", "0", @"C:\Users\u\.claude\jobs\1a28e41b").Value.Headless);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("%CLAUDE_JOB_DIR%")]
    public void A_claude_p_session_without_a_job_directory_is_still_headless(string? job) =>
        Assert.True(ClaudeHookEvent.Parse(StartupJson, "sdk-cli", "0", job).Value.Headless);

    // Final review, item 13: only a job folder that is the hook's OWN marks it as from a background session, which is
    // what lets RemoteControl skip the per-turn SessionEnd. An inherited one, or none, does not.
    [Fact]
    public void Background_is_set_only_by_the_sessions_own_job_folder()
    {
        Assert.True(ClaudeHookEvent.Parse(OwnSessionJson, "cli", "0", @"C:\Users\u\.claude\jobs\1a28e41b").Value.Background);
        Assert.False(ClaudeHookEvent.Parse(OtherSessionJson, "cli", "0", @"C:\Users\u\.claude\jobs\1a28e41b").Value.Background);
        Assert.False(ClaudeHookEvent.Parse(OwnSessionJson, "claude-vscode", "1", null).Value.Background);
    }

    // Ruling 19: SessionEnd's reason is read, so a /clear can be told from any other end.
    [Fact]
    public void SessionEnd_carries_its_reason()
    {
        Assert.Equal("clear", Parsed("""{"session_id":"s","hook_event_name":"SessionEnd","reason":"clear"}""").Reason.GetValueOrDefault());
        Assert.True(Parsed("""{"session_id":"s","hook_event_name":"SessionEnd"}""").Reason.HasNoValue);
    }

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

    // The Stop hook's own fields (stalled tasks, spec 2026-11-23-stalled-claude-tasks-design.md, part 3): whether Claude is
    // already continuing because a Stop hook said so, and whether it has background work or scheduled wake-ups of its own.
    [Fact]
    public void Stop_hook_active_is_read() =>
        Assert.True(Parsed("""{"session_id":"s","hook_event_name":"Stop","stop_hook_active":true}""").StopHookActive);

    [Theory]
    [InlineData("""{"session_id":"s","hook_event_name":"Stop","background_tasks":[{"type":"monitor"}]}""")]
    [InlineData("""{"session_id":"s","hook_event_name":"Stop","session_crons":[{"id":"c"}]}""")]
    public void Background_tasks_or_session_crons_mean_background_work(string json) =>
        Assert.True(Parsed(json).BackgroundWork);

    [Theory]
    [InlineData("""{"session_id":"s","hook_event_name":"Stop"}""")]
    [InlineData("""{"session_id":"s","hook_event_name":"Stop","stop_hook_active":false,"background_tasks":[],"session_crons":[]}""")]
    [InlineData("""{"session_id":"s","hook_event_name":"Stop","stop_hook_active":"yes","background_tasks":5}""")]
    public void Missing_empty_or_odd_stop_fields_read_as_false(string json)
    {
        var hook = Parsed(json);

        Assert.False(hook.StopHookActive);
        Assert.False(hook.BackgroundWork);
    }
}
