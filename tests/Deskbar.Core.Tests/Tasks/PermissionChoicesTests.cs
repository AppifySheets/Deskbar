using System.Text.Json;
using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests.Tasks;

// The card offers what the terminal offers (task bd55bd54). The hook JSON below is what Claude Code 2.1.332 handed a
// PermissionRequest hook on 2026-11-28 (trimmed to the fields that matter), and each expected list is the dialog the
// terminal drew for it in the same run.
public sealed class PermissionChoicesTests : IDisposable
{
    static string Hook(string tool, string command, string suggestions, string mode = "default") =>
        $$"""
        {"session_id":"s1","cwd":"C:\\p","permission_mode":"{{mode}}","hook_event_name":"PermissionRequest",
         "tool_name":"{{tool}}","tool_input":{"command":{{JsonSerializer.Serialize(command)}}},"permission_suggestions":{{suggestions}}}
        """;

    const string GitInitRule = """[{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"git init *"}],"behavior":"allow","destination":"localSettings"}]""";

    const string MkdirSuggestions = """
        [{"type":"addDirectories","directories":["C:\\p"],"destination":"session"},
         {"type":"setMode","mode":"acceptEdits","destination":"session"}]
        """;

    static PermissionRequest Parsed(string json) => PermissionRequest.Parse(json).Value;

    static IReadOnlyList<string> Words(PermissionRequest request) => PermissionChoices.Of(request).Select(c => c.Words).ToList();

    [Fact]
    public void A_command_with_a_variable_offers_yes_auto_mode_and_no() =>
        Assert.Equal(["Yes", "Yes, and switch to auto mode", "No"], Words(Parsed(Hook("Bash", "echo \"$APPDATA\" > out.txt", "[]"))));

    [Fact]
    public void A_suggested_rule_is_offered_in_the_terminals_words_before_auto_mode() =>
        Assert.Equal(["Yes", "Yes, and don't ask again for: git init *", "Yes, and switch to auto mode", "No"],
            Words(Parsed(Hook("Bash", "git init", GitInitRule))));

    // The terminal showed the folder and auto mode for mkdir, but not the setMode entry it was handed.
    [Fact]
    public void A_folder_is_offered_and_a_mode_switch_suggestion_is_not() =>
        Assert.Equal(["Yes", @"Yes, and allow access to C:\p for this session", "Yes, and switch to auto mode", "No"],
            Words(Parsed(Hook("Bash", "mkdir sub1", MkdirSuggestions))));

    // A file write got Yes / No only: no auto mode for a tool that is not a shell, and no edits switch.
    [Fact]
    public void A_file_write_offers_yes_and_no_only() =>
        Assert.Equal(["Yes", "No"],
            Words(Parsed(Hook("Write", "x", """[{"type":"setMode","mode":"acceptEdits","destination":"session"}]"""))));

    [Theory]
    [InlineData("auto")]
    [InlineData("bypassPermissions")]
    [InlineData("plan")]
    public void A_session_not_in_default_or_accept_edits_is_not_offered_auto_mode(string mode) =>
        Assert.DoesNotContain(PermissionChoices.AutoText, Words(Parsed(Hook("PowerShell", "Get-Date", "[]", mode))));

    [Fact]
    public void PowerShell_in_accept_edits_is_offered_auto_mode() =>
        Assert.Contains(PermissionChoices.AutoText, Words(Parsed(Hook("PowerShell", "Get-Date", "[]", "acceptEdits"))));

    // Each choice says how it is answered: Yes is the primary, a suggestion carries its index, No denies.
    [Fact]
    public void Each_choice_names_its_answer()
    {
        var choices = PermissionChoices.Of(Parsed(Hook("Bash", "mkdir sub1", MkdirSuggestions)));

        Assert.Equal((PermissionDecision.Allow, (int?)null, true), (choices[0].Decision, choices[0].Suggestion, choices[0].Primary));
        Assert.Equal((PermissionDecision.Allow, (int?)0), (choices[1].Decision, choices[1].Suggestion));
        Assert.Equal(PermissionDecision.AllowAuto, choices[2].Decision);
        Assert.Equal(PermissionDecision.Deny, choices[3].Decision);
        Assert.Single(choices, c => c.Primary);
    }

    [Fact]
    public void Suggestions_that_are_not_objects_or_have_no_type_are_left_out()
    {
        var request = Parsed(Hook("Bash", "ls", """[1, "x", {"rules":[]}, {"type":"addRules","rules":[{"toolName":"Bash"}],"behavior":"allow"}]"""));

        Assert.Equal("addRules", Assert.Single(request.Suggestions).Type);
        // A rule with no content is named by its tool, as the terminal would.
        Assert.Contains("Yes, and don't ask again for: Bash", Words(request));
    }

    // A deny rule is not a "Yes, and ...", and the kinds no run offered are not drawn.
    [Theory]
    [InlineData("""[{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"rm *"}],"behavior":"deny","destination":"session"}]""")]
    [InlineData("""[{"type":"removeRules","rules":[{"toolName":"Bash","ruleContent":"x"}],"behavior":"allow","destination":"session"}]""")]
    [InlineData("""[{"type":"addDirectories","directories":[],"destination":"session"}]""")]
    public void Suggestions_the_card_does_not_offer_are_not_drawn(string suggestions) =>
        Assert.Equal(["Yes", "Yes, and switch to auto mode", "No"], Words(Parsed(Hook("Bash", "x", suggestions))));

    // --- the answer: the entry handed back as Claude Code offered it -----------------------------------------------------

    readonly string box = Path.Combine(Path.GetTempPath(), "deskbar-choices-" + Guid.NewGuid().ToString("N"));
    readonly DateTimeOffset clock = new(2026, 11, 28, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        if (Directory.Exists(box)) Directory.Delete(box, true);
    }

    (PermissionDesk Desk, string Code) Registered(PermissionRequest request)
    {
        Directory.CreateDirectory(Path.Combine(box, "req-1-2"));
        var desk = new PermissionDesk(box, () => clock);
        return (desk, desk.Register("req-1-2", request).Value);
    }

    string AnswerText() => File.ReadAllText(Path.Combine(box, "req-1-2", PermissionDesk.AnswerFile));

    [Fact]
    public void A_picked_suggestion_writes_fetch_and_hands_its_entry_back_once()
    {
        var (desk, code) = Registered(Parsed(Hook("Bash", "git init", GitInitRule)));

        Assert.True(desk.AnswerSuggestion("req-1-2", 0).IsSuccess);

        Assert.Equal($"{code} {PermissionAnswer.FetchWord}\n", AnswerText());
        Assert.Empty(desk.All);
        var line = desk.Fetch("req-1-2", code).Value;
        Assert.Equal(line, PermissionAnswer.ToolLineFor(AnswerText(), code, line).Value);
        Assert.True(desk.Fetch("req-1-2", code).IsFailure);

        using var printed = JsonDocument.Parse(line);
        var decision = printed.RootElement.GetProperty("hookSpecificOutput").GetProperty("decision");
        Assert.Equal("allow", decision.GetProperty("behavior").GetString());
        var entry = Assert.Single(decision.GetProperty("updatedPermissions").EnumerateArray().ToList());
        Assert.Equal("git init *", entry.GetProperty("rules")[0].GetProperty("ruleContent").GetString());
        Assert.Equal("localSettings", entry.GetProperty("destination").GetString());
    }

    // The line is one line of pure ASCII, whatever the entry holds, as the script needs.
    [Fact]
    public void A_suggestions_line_is_one_ascii_line()
    {
        var request = Parsed(Hook("Bash", "x", """[{"type":"addDirectories","directories":["C:\\ფოლდერი\\a\"b & c"],"destination":"session"}]"""));

        var line = PermissionAnswer.SuggestionLine(request.Suggestions[0]);

        Assert.All(line, c => Assert.True(c is >= ' ' and <= '~', $"U+{(int)c:X4}"));
        using var parsed = JsonDocument.Parse(line);
        Assert.Equal("C:\\ფოლდერი\\a\"b & c", parsed.RootElement.GetProperty("hookSpecificOutput").GetProperty("decision")
            .GetProperty("updatedPermissions")[0].GetProperty("directories")[0].GetString());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void An_index_the_request_does_not_have_is_refused_and_writes_nothing(int index)
    {
        var (desk, _) = Registered(Parsed(Hook("Bash", "git init", GitInitRule)));

        Assert.True(desk.AnswerSuggestion("req-1-2", index).IsFailure);

        Assert.False(File.Exists(Path.Combine(box, "req-1-2", PermissionDesk.AnswerFile)));
        Assert.Single(desk.All);
    }

    // A tool slot never takes the dialog word, and fetch prints nothing when the fetch gave nothing.
    [Fact]
    public void The_tool_rule_takes_fetch_only_with_a_fetched_line()
    {
        const string code = "0123456789abcdef0123456789abcdef";

        Assert.True(PermissionAnswer.ToolLineFor($"{code} fetch\n", code, CSharpFunctionalExtensions.Maybe<string>.None).HasNoValue);
        Assert.True(PermissionAnswer.ToolLineFor($"{code} fetch\n", code, "\r\n").HasNoValue);
        Assert.True(PermissionAnswer.ToolLineFor($"{code} dialog\n", code, "{}").HasNoValue);
        Assert.True(PermissionAnswer.ToolLineFor($"fedcba9876543210fedcba9876543210 fetch\n", code, "{}").HasNoValue);
        Assert.Equal("{}", PermissionAnswer.ToolLineFor($"{code} fetch\n", code, "{}\r\n").Value);
        // The dialog rule never takes fetch.
        Assert.True(PermissionAnswer.LineFor($"{code} fetch\n", code, "{}").HasNoValue);
    }

    [Fact]
    public void The_auto_line_switches_the_session_only() =>
        Assert.Contains("""{"type":"setMode","mode":"auto","destination":"session"}""", PermissionAnswer.AllowAutoLine);
}
