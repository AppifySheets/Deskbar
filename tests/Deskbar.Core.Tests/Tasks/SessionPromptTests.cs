using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests.Tasks;

// The words a dialog's question gets (SessionPrompt) and what Deskbar remembers about dialogs (SessionPrompts), task
// b310a649. The pipe end to end is in RemoteControlSessionPromptTests.
public sealed class SessionPromptTests : IDisposable
{
    static readonly DateTimeOffset Clock = new(2026, 11, 27, 18, 0, 0, TimeSpan.Zero);
    readonly string folder = Path.Combine(Path.GetTempPath(), "deskbar-prompts-" + Guid.NewGuid().ToString("N"));

    public SessionPromptTests() => Directory.CreateDirectory(folder);

    public void Dispose() => Directory.Delete(folder, true);

    static PromptDetail Bash(string command, DateTimeOffset? at = null) =>
        PromptDetail.Of(new PermissionRequest("s1", "Bash", @"C:\repos\x", Maybe<string>.None,
            [new PermissionField("command", command), new PermissionField("description", "Push it")]), at ?? Clock);

    static PromptDetail Plan(string plan) =>
        PromptDetail.Of(new PermissionRequest("s1", SessionPrompt.PlanTool, @"C:\repos\x", Maybe<string>.None,
            [new PermissionField("plan", plan)]), Clock);

    [Fact]
    public void A_tool_permission_names_the_tool_and_its_main_field()
    {
        Assert.Equal("Allow Bash in the session: git push origin x",
            SessionPrompt.Text("permission_prompt", "Claude needs your permission", Bash("git push origin x")).Value);
        // The SDK path (VS Code) appends the tool to the same words.
        Assert.Equal("Allow Bash in the session: git push origin x",
            SessionPrompt.Text("permission_prompt", "Claude needs your permission to use Bash", Bash("git push origin x")).Value);
    }

    [Fact]
    public void A_long_or_multiline_command_is_its_first_line_cut_short()
    {
        var text = SessionPrompt.Text("permission_prompt", "Claude needs your permission", Bash("\n  " + new string('a', 300) + "\nsecond")).Value;

        Assert.Equal($"Allow Bash in the session: {new string('a', PromptDetail.SummaryLength - 1)}…", text);
    }

    // The SDK's words name the tool: a detail for another tool does not name it.
    [Fact]
    public void The_sdk_text_must_name_the_details_tool() =>
        Assert.Equal($"{SessionPrompt.Lead} Claude needs your permission to use Edit",
            SessionPrompt.Text("permission_prompt", "Claude needs your permission to use Edit", Bash("ls")).Value);

    [Fact]
    public void A_plan_is_named_by_its_first_line()
    {
        Assert.Equal("Approve the plan in the session: Fix the bar",
            SessionPrompt.Text("permission_prompt", "Claude Code needs your approval for the plan", Plan("\n# Fix the bar\n\nSteps...")).Value);
    }

    // While the plan's card is on the task's page the question says so (task 8dbbaa0b) instead of sending the user to the
    // session.
    [Fact]
    public void A_plan_on_its_card_is_worded_as_approving_it_here()
    {
        const string said = "Claude Code needs your approval for the plan";

        Assert.Equal("Approve Claude's plan: Fix the bar",
            SessionPrompt.Text("permission_prompt", said, Plan("\n# Fix the bar\n\nSteps..."), planOnCard: true).Value);
        Assert.Equal("Approve Claude's plan: Fix the bar", SessionPrompt.Named("permission_prompt", said, Plan("# Fix the bar"), true).Value);
        Assert.Equal(SessionPrompt.PlanOnCardLead, SessionPrompt.Named("permission_prompt", said, Plan(""), true).Value);
    }

    [Fact]
    public void A_plan_off_its_card_still_says_to_approve_it_in_the_session()
    {
        const string said = "Claude Code needs your approval for the plan";

        Assert.Equal("Approve the plan in the session: Fix the bar", SessionPrompt.Text("permission_prompt", said, Plan("# Fix the bar"), planOnCard: false).Value);
        Assert.Equal(SessionPrompt.PlanLead, SessionPrompt.Named("permission_prompt", said, Plan(""), false).Value);
    }

    // The flag changes only a plan's words: any other dialog says what it said before.
    [Fact]
    public void The_plan_flag_changes_no_other_dialogs_words()
    {
        Assert.Equal("Allow Bash in the session: git push",
            SessionPrompt.Text("permission_prompt", "Claude needs your permission", Bash("git push"), planOnCard: true).Value);
        Assert.Equal($"{SessionPrompt.Lead} Session paused",
            SessionPrompt.Text("permission_prompt", "Session paused", Plan("# P"), planOnCard: true).Value);
    }

    // Review focus 1: "Session paused" also arrives as permission_prompt (the registry's default type), and must not be
    // named after a Bash prompt that was answered a moment before.
    [Theory]
    [InlineData("Session paused")]
    [InlineData("A sandboxed command needs network access")]
    [InlineData("Claude Code wants to enter plan mode")]
    public void Any_other_dialog_says_its_own_words_whatever_detail_is_noted(string message) =>
        Assert.Equal($"{SessionPrompt.Lead} {message}", SessionPrompt.Text("permission_prompt", message, Bash("git push")).Value);

    // A plan detail never names a tool permission, and a Bash detail never names a plan.
    [Fact]
    public void A_detail_names_only_its_own_kind_of_dialog()
    {
        Assert.Equal($"{SessionPrompt.Lead} Claude needs your permission",
            SessionPrompt.Text("permission_prompt", "Claude needs your permission", Plan("# P")).Value);
        Assert.Equal($"{SessionPrompt.Lead} Claude Code needs your approval for the plan",
            SessionPrompt.Text("permission_prompt", "Claude Code needs your approval for the plan", Bash("ls")).Value);
    }

    [Fact]
    public void An_mcp_request_names_its_server_and_message()
    {
        var detail = PromptDetail.Elicitation("github", "Pick a\nrepository", Maybe<string>.None, Clock);

        Assert.Equal("Answer github in the session: Pick a repository",
            SessionPrompt.Text("elicitation_dialog", "Claude Code needs your input", detail).Value);
        // A Bash detail does not name an MCP dialog.
        Assert.Equal($"{SessionPrompt.Lead} Claude Code needs your input",
            SessionPrompt.Text("elicitation_dialog", "Claude Code needs your input", Bash("ls")).Value);
    }

    [Theory]
    [InlineData("worker_permission_prompt", "tester needs permission for Bash")]
    [InlineData("agent_needs_input", "Claude wants to use your computer")]
    [InlineData("elicitation_url_dialog", "An MCP server needs your input")]
    public void Every_question_type_asks_in_its_own_words(string type, string message) =>
        Assert.Equal($"{SessionPrompt.Lead} {message}", SessionPrompt.Text(type, message, Maybe<PromptDetail>.None).Value);

    [Theory]
    [InlineData("idle_prompt", "Claude is waiting for your input")]
    [InlineData("auth_success", "Signed in")]
    [InlineData("agent_completed", "x finished")]
    [InlineData("elicitation_response", "Elicitation response for server \"github\": accept")]
    [InlineData("permission_prompt", "  ")]
    public void A_notice_or_an_empty_text_is_no_question(string type, string message) =>
        Assert.True(SessionPrompt.Text(type, message, Maybe<PromptDetail>.None).HasNoValue);

    [Fact]
    public void A_detail_is_used_only_while_fresh()
    {
        var now = Clock;
        var prompts = new SessionPrompts(() => now);
        prompts.Note("s1", Bash("ls"));

        now = Clock + SessionPrompts.DetailFor;
        Assert.True(prompts.Detail("s1").HasValue);
        now += TimeSpan.FromSeconds(1);
        Assert.True(prompts.Detail("s1").HasNoValue);
        Assert.True(prompts.Detail("s2").HasNoValue);
    }

    [Fact]
    public void Moved_hands_back_only_placements_whose_transcript_went_on()
    {
        var prompts = new SessionPrompts(() => Clock);
        var still = Transcript("still");
        var moving = Transcript("moving");
        prompts.Placed("a", Guid.NewGuid(), "q a", TranscriptMark.Take(still));
        prompts.Placed("b", Guid.NewGuid(), "q b", TranscriptMark.Take(moving));
        prompts.Placed("c", Guid.NewGuid(), "q c", Maybe<TranscriptMark>.None);

        File.AppendAllText(still, """{"type":"ai-title"}""" + "\n");
        File.AppendAllText(moving, """{"type":"assistant"}""" + "\n");

        Assert.Equal(["b"], prompts.Moved().Select(p => p.Session));
        Assert.Equal(["a", "c"], prompts.All.Select(p => p.Session));
    }

    [Fact]
    public void Take_empties_the_session_and_a_newer_placement_replaces_its_older_one()
    {
        var prompts = new SessionPrompts(() => Clock);
        prompts.Note("a", Bash("ls"));
        prompts.Placed("a", Guid.NewGuid(), "first", Maybe<TranscriptMark>.None);
        prompts.Placed("a", Guid.NewGuid(), "second", Maybe<TranscriptMark>.None);
        prompts.Placed("b", Guid.NewGuid(), "other", Maybe<TranscriptMark>.None);

        Assert.Equal(["second"], prompts.Take("a").Select(p => p.Question));
        Assert.True(prompts.Detail("a").HasNoValue);
        Assert.Equal(["b"], prompts.All.Select(p => p.Session));
    }

    string Transcript(string name)
    {
        var path = Path.Combine(folder, name + ".jsonl");
        File.WriteAllText(path, """{"type":"user"}""" + "\n");
        return path;
    }
}
