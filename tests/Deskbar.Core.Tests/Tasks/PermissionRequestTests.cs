using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests.Tasks;

// A PermissionRequest hook's JSON as Deskbar reads it. The shapes are the ones the live spike of 2026-11-26 recorded
// (session_id, transcript_path, cwd, permission_mode, effort, tool_name, tool_input, permission_suggestions; no
// tool_use_id), plus an edit tool and an MCP tool in the hooks reference's form.
public class PermissionRequestTests
{
    static string Hook(string tool, string input, string hookEvent = "PermissionRequest", string session = "432aa319-aaaa") =>
        $$"""
        {"session_id":"{{session}}","transcript_path":"C:\\t.jsonl","cwd":"C:\\repos\\proj1","permission_mode":"default",
         "effort":{"level":"medium"},"hook_event_name":"{{hookEvent}}","tool_name":"{{tool}}","tool_input":{{input}},
         "permission_suggestions":[{"type":"addDirectories","directories":["C:\\repos\\proj1"],"destination":"session"}]}
        """;

    [Fact]
    public void A_shell_command_is_read_with_every_field_in_order()
    {
        var request = PermissionRequest.Parse(Hook("Bash", """{"command":"echo hello > made.txt","description":"Write hello","timeout":5000}""")).Value;

        Assert.Equal("432aa319-aaaa", request.SessionId);
        Assert.Equal("Bash", request.Tool);
        Assert.Equal(@"C:\repos\proj1", request.Folder);
        Assert.Equal("default", request.Mode.Value);
        Assert.Equal(new[] { "command", "description", "timeout" }, request.Fields.Select(f => f.Name));
        Assert.Equal("echo hello > made.txt", request.Fields[0].Value);
        Assert.Equal("5000", request.Fields[2].Value);
    }

    [Fact]
    public void PowerShell_is_read_the_same_way() =>
        Assert.Equal("Get-ChildItem", PermissionRequest.Parse(Hook("PowerShell", """{"command":"Get-ChildItem"}""")).Value.Fields.Single().Value);

    [Fact]
    public void An_edit_keeps_its_path_and_its_text()
    {
        var request = PermissionRequest.Parse(Hook("Edit", """{"file_path":"C:\\a\\b.cs","old_string":"x","new_string":"y\nz"}""")).Value;

        Assert.Equal(new[] { "file_path", "old_string", "new_string" }, request.Fields.Select(f => f.Name));
        Assert.Equal("y\nz", request.Fields[2].Value);
    }

    // An MCP tool's input can be anything; a nested value is kept whole, as indented JSON.
    [Fact]
    public void A_nested_value_is_kept_as_json()
    {
        var request = PermissionRequest.Parse(Hook("mcp__db__query", """{"sql":"drop table t","options":{"dry":false,"rows":[1,2]}}""")).Value;

        Assert.Contains("\"dry\": false", request.Fields[1].Value);
        Assert.Contains("2", request.Fields[1].Value);
    }

    [Fact]
    public void An_input_that_is_not_an_object_is_one_field() =>
        Assert.Equal("input", PermissionRequest.Parse(Hook("Odd", "\"just text\"")).Value.Fields.Single().Name);

    [Fact]
    public void No_input_at_all_is_no_fields() =>
        Assert.Empty(PermissionRequest.Parse("""{"session_id":"s","hook_event_name":"PermissionRequest","tool_name":"Bash"}""").Value.Fields);

    [Theory]
    [InlineData("""{"session_id":"s","hook_event_name":"PreToolUse","tool_name":"Bash"}""")]
    [InlineData("""{"hook_event_name":"PermissionRequest","tool_name":"Bash"}""")]
    [InlineData("""{"session_id":"s","hook_event_name":"PermissionRequest"}""")]
    [InlineData("""[1,2]""")]
    [InlineData("""{"session_id":""")]
    public void Anything_but_a_permission_request_with_a_session_and_a_tool_is_refused(string json) =>
        Assert.True(PermissionRequest.Parse(json).IsFailure);

    // The PM's ruling Q8: Claude Code's own dialogs are never Deskbar's to answer.
    [Theory]
    [InlineData("AskUserQuestion", true)]
    [InlineData("ExitPlanMode", true)]
    [InlineData("Bash", false)]
    public void The_dialog_tools_are_told_apart(string tool, bool dialog) =>
        Assert.Equal(dialog, PermissionRequest.Parse(Hook(tool, "{}")).Value.IsDialog);
}
