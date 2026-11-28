using System.Text.Json.Nodes;
using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests.Tasks;

// Claude Code's ExitPlanMode dialog as Deskbar answers it (PlanAsk, task 8dbbaa0b): what is parsed, what is refused, the
// line the permission hook prints for each of the three decisions, and the thread's words. The shapes are the ones
// measured with Claude Code 2.1.332 (PlanAsk's header).
public sealed class PlanAskTests
{
    // The measured tool_input: the plan's markdown and the file it was read from.
    const string Measured = """{"plan":"# Fix the bar\n\n1. Do it","planFilePath":"C:\\Users\\x\\.claude\\plans\\fix-the-bar.md"}""";

    static PlanAsk Parsed(string json = Measured) => PlanAsk.Parse(json).Value;

    static JsonNode Decision(string line) => JsonNode.Parse(line)!["hookSpecificOutput"]!["decision"]!;

    [Fact]
    public void The_measured_shape_parses_into_the_plan_and_keeps_the_input_text()
    {
        var plan = Parsed();

        Assert.Equal("# Fix the bar\n\n1. Do it", plan.Plan);
        Assert.Equal(Measured, plan.Input);
    }

    [Fact]
    public void Of_reads_the_plan_of_an_exit_plan_mode_request_and_nothing_of_another_tool()
    {
        PermissionRequest Request(string tool) =>
            new("s1", tool, @"C:\repos\x", "plan", [new PermissionField("plan", "p")], Input: Measured);

        Assert.Equal("# Fix the bar\n\n1. Do it", PlanAsk.Of(Request(PlanAsk.Tool)).Value.Plan);
        Assert.True(PlanAsk.Of(Request("Bash")).HasNoValue);
        Assert.True(PlanAsk.Of(Request("AskUserQuestion")).HasNoValue);
    }

    [Theory]
    [InlineData("""{"planFilePath":"x.md"}""")]
    [InlineData("""{"plan":""}""")]
    [InlineData("""{"plan":"   \n\t "}""")]
    [InlineData("""{"plan":42}""")]
    [InlineData("""{"plan":null}""")]
    [InlineData("""{"plan":["a"]}""")]
    [InlineData("[]")]
    [InlineData("\"plan\"")]
    [InlineData("{not json")]
    [InlineData("")]
    public void Input_the_card_cannot_show_is_refused(string json) => Assert.True(PlanAsk.Parse(json).HasNoValue);

    // --- the line the hook prints ------------------------------------------------------------------------

    [Fact]
    public void Approve_is_an_allow_whose_updated_input_is_the_tool_input_unchanged()
    {
        var line = Parsed().AnswerLine(PlanDecision.Approve).Value;

        var root = JsonNode.Parse(line)!;
        Assert.Equal("PermissionRequest", (string)root["hookSpecificOutput"]!["hookEventName"]!);
        var decision = Decision(line).AsObject();
        Assert.Equal(["behavior", "updatedInput"], decision.Select(p => p.Key));
        Assert.Equal("allow", (string)decision["behavior"]!);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Measured), decision["updatedInput"]));
    }

    [Fact]
    public void Approve_and_accept_edits_adds_the_session_set_mode_entry()
    {
        var decision = Decision(Parsed().AnswerLine(PlanDecision.ApproveEdits).Value).AsObject();

        Assert.Equal("allow", (string)decision["behavior"]!);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Measured), decision["updatedInput"]));
        var expected = JsonNode.Parse("""[{"type":"setMode","mode":"acceptEdits","destination":"session"}]""");
        Assert.True(JsonNode.DeepEquals(expected, decision["updatedPermissions"]));
    }

    [Fact]
    public void Keep_planning_is_a_deny_whose_message_is_the_lead_and_the_note()
    {
        var decision = Decision(Parsed().AnswerLine(PlanDecision.KeepPlanning, "  Split step 2  ").Value).AsObject();

        Assert.Equal(["behavior", "message"], decision.Select(p => p.Key));
        Assert.Equal("deny", (string)decision["behavior"]!);
        Assert.Equal($"{PlanAsk.KeepPlanningLead} Split step 2", (string)decision["message"]!);
    }

    // Whatever the tool was called with comes back as it came, an unknown field included: an allow that changes the
    // input would change what Claude Code runs.
    [Fact]
    public void The_input_is_echoed_back_with_every_field_it_had()
    {
        var input = """{"plan":"# P","planFilePath":"C:\\plans\\p.md","extra":{"k":[1,2,{"z":null}]},"n":1.5}""";

        var line = Parsed(input).AnswerLine(PlanDecision.Approve).Value;

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(input), Decision(line)["updatedInput"]));
    }

    // The line travels through cmd's output as one line: a plan's line breaks and non-ASCII text are escapes, not bytes.
    [Theory]
    [InlineData(PlanDecision.Approve)]
    [InlineData(PlanDecision.ApproveEdits)]
    [InlineData(PlanDecision.KeepPlanning)]
    public void The_line_is_one_line_of_pure_ascii_whatever_the_plan_and_note_hold(PlanDecision decision)
    {
        var input = JsonNode.Parse(Measured)!.AsObject();
        input["plan"] = "# Plan \u00e9\u4e2d\u6587 \ud83d\ude80\r\nline two\n\tindented <b> & \"quoted\"";
        var plan = Parsed(input.ToJsonString());

        var line = plan.AnswerLine(decision, "note \u00fc\nsecond line").Value;

        Assert.All(line, c => Assert.InRange(c, ' ', '~'));
        Assert.DoesNotContain('\n', line);
        // And still round-trips to the same text.
        if (decision != PlanDecision.KeepPlanning)
            Assert.Equal((string)input["plan"]!, (string)Decision(line)["updatedInput"]!["plan"]!);
        else
            Assert.Equal($"{PlanAsk.KeepPlanningLead} note \u00fc\nsecond line", (string)Decision(line)["message"]!);
    }

    // --- keep planning needs a note ----------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t ")]
    public void Keep_planning_without_a_note_fails(string note) =>
        Assert.True(Parsed().AnswerLine(PlanDecision.KeepPlanning, note).IsFailure);

    [Fact]
    public void Keep_planning_with_no_note_argument_fails() =>
        Assert.True(Parsed().AnswerLine(PlanDecision.KeepPlanning).IsFailure);

    [Fact]
    public void A_note_longer_than_the_limit_fails_and_one_at_the_limit_goes()
    {
        Assert.True(Parsed().AnswerLine(PlanDecision.KeepPlanning, new string('a', PlanAsk.NoteLength + 1)).IsFailure);
        Assert.True(Parsed().AnswerLine(PlanDecision.KeepPlanning, new string('a', PlanAsk.NoteLength)).IsSuccess);
    }

    // The limit counts the trimmed note: padding is not part of what is sent.
    [Fact]
    public void The_note_is_trimmed_before_its_length_counts()
    {
        var note = "  " + new string('a', PlanAsk.NoteLength) + "  ";

        var message = (string)Decision(Parsed().AnswerLine(PlanDecision.KeepPlanning, note).Value)["message"]!;

        Assert.Equal($"{PlanAsk.KeepPlanningLead} {new string('a', PlanAsk.NoteLength)}", message);
    }

    // Approving takes no note: one typed and left in the box is not sent.
    [Theory]
    [InlineData(PlanDecision.Approve)]
    [InlineData(PlanDecision.ApproveEdits)]
    public void Approving_ignores_a_note(PlanDecision decision) =>
        Assert.Equal(Parsed().AnswerLine(decision).Value, Parsed().AnswerLine(decision, "not sent").Value);

    // --- the thread's words ------------------------------------------------------------------------------

    [Fact]
    public void The_thread_line_says_what_was_decided()
    {
        Assert.Equal("Approved Claude's plan.", PlanAsk.ThreadLine(PlanDecision.Approve));
        Assert.Equal("Approved Claude's plan, with edits accepted from now on.", PlanAsk.ThreadLine(PlanDecision.ApproveEdits));
        Assert.Equal("Asked Claude to keep planning:\nSplit step 2", PlanAsk.ThreadLine(PlanDecision.KeepPlanning, "  Split step 2 \n"));
    }
}
