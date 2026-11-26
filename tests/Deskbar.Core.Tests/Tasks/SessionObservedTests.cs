using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests.Tasks;

// What a session runs on as Claude Code itself reports it, for a session whose settings Deskbar did not set: its hooks'
// permission_mode, effort and model (ClaudeHookEvent), the list's status (ClaudeAgent.Status), and the model the last
// reply came from (ClaudeTranscript.LastModel).
public class SessionObservedTests
{
    // A PermissionRequest hook's input as measured on 2026-11-26 (paths and ids shortened), and a SessionStart that names
    // its model, as the hooks docs say one can.
    const string PermissionJson =
        """{"session_id":"432aa319","transcript_path":"x","cwd":"C:\\repos\\proj1","prompt_id":"8d6c","permission_mode":"default","effort":{"level":"medium"},"hook_event_name":"PermissionRequest","tool_name":"Bash"}""";

    const string StartJson =
        """{"session_id":"432aa319","transcript_path":"x","cwd":"C:\\repos\\proj1","hook_event_name":"SessionStart","source":"fork","model":"claude-sonnet-5-5","permission_mode":"acceptEdits","effort":{"level":"low"}}""";

    static ClaudeHookEvent Parsed(string json) => ClaudeHookEvent.Parse(json, "cli", "1").Value;

    [Fact]
    public void A_hook_reports_the_mode_and_effort_and_a_start_sometimes_the_model()
    {
        var asked = Parsed(PermissionJson);
        Assert.Equal("default", asked.PermissionMode.Value);
        Assert.Equal("medium", asked.Effort.Value);
        Assert.True(asked.Model.HasNoValue);

        var started = Parsed(StartJson);
        Assert.Equal(new SessionSettings("claude-sonnet-5-5", "low", "acceptEdits"), SessionSettings.Observed(started));
    }

    // Kept as given: an observed value is shown, never put on a command line, so even a mode the picker refuses is kept.
    [Fact]
    public void Observed_values_are_kept_as_given_and_odd_shapes_read_as_none()
    {
        var bypass = Parsed("""{"session_id":"s","hook_event_name":"Stop","permission_mode":"bypassPermissions","effort":"high","model":{"id":"claude-opus-5-5"}}""");
        Assert.Equal(new SessionSettings("claude-opus-5-5", "high", "bypassPermissions"), SessionSettings.Observed(bypass));

        var odd = Parsed("""{"session_id":"s","hook_event_name":"Stop","permission_mode":7,"effort":{"level":3},"model":["x"]}""");
        Assert.Equal(SessionSettings.None, SessionSettings.Observed(odd));
    }

    [Fact]
    public void The_list_row_keeps_its_status_so_a_permission_prompt_is_not_read_as_idle()
    {
        const string json = """
            [{"pid":1,"id":"8c42135c","cwd":"C:\\repos\\p","kind":"background","sessionId":"8c42135c-1","status":"waiting","waitingFor":"permission prompt","state":"blocked"},
             {"id":"b7e00689","cwd":"C:\\repos\\p","kind":"background","sessionId":"b7e00689-1"}]
            """;

        var agents = ClaudeAgents.Parse(json).Value;

        Assert.Equal("waiting", agents[0].Status.Value);
        Assert.False(agents[0].Busy);
        Assert.True(agents[1].Status.HasNoValue);
    }

    [Fact]
    public void The_last_model_is_the_last_real_reply_s_and_errors_and_subagents_do_not_count()
    {
        string[] lines =
        [
            """{"type":"user","message":{"content":"hi"}}""",
            """{"type":"assistant","message":{"model":"claude-haiku-5-5","stop_reason":"end_turn"}}""",
            """{"type":"assistant","message":{"model":"claude-sonnet-5-5","stop_reason":"end_turn"}}""",
            """{"type":"assistant","isSidechain":true,"message":{"model":"claude-haiku-5-5"}}""",
            """{"type":"assistant","isApiErrorMessage":true,"message":{"model":"<synthetic>"}}""",
            "not json",
            "",
        ];

        Assert.Equal("claude-sonnet-5-5", ClaudeTranscript.LastModel(lines).Value);
        Assert.True(ClaudeTranscript.LastModel(["""{"type":"user","message":{"content":"hi"}}"""]).HasNoValue);
    }
}
