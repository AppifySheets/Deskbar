using System.Text.Json;
using System.Text.Json.Nodes;
using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests.Tasks;

// Claude Code's AskUserQuestion dialog as Deskbar answers it (DialogAsk, task c047d0a0): what is parsed, what is refused,
// the line the permission hook prints, the answers read back from the tool result, and the thread's words.
public sealed class DialogAskTests
{
    // The real shape from the hooks reference.
    const string Colour =
        """{"questions":[{"question":"Pick a colour","header":"Colour","options":[{"label":"Red","description":"warm"},{"label":"Blue","description":"cool"}],"multiSelect":false}]}""";

    static IReadOnlyDictionary<string, string> Answers(params (string Question, string Answer)[] pairs) =>
        pairs.ToDictionary(p => p.Question, p => p.Answer);

    static DialogAsk Parsed(string json) => DialogAsk.Parse(json).Value;

    [Fact]
    public void The_real_shape_parses_into_questions_and_options()
    {
        var item = Assert.Single(Parsed(Colour).Questions);

        Assert.Equal(("Pick a colour", "Colour", false), (item.Question, item.Header, item.MultiSelect));
        Assert.Equal(["Red", "Blue"], item.Options.Select(o => o.Label));
        Assert.Equal(["warm", "cool"], item.Options.Select(o => o.Description));
        Assert.All(item.Options, o => Assert.True(o.Preview.HasNoValue));
    }

    [Fact]
    public void Previews_and_multi_select_are_kept()
    {
        var json = """{"questions":[{"question":"Layout?","header":"Layout","multiSelect":true,"options":[{"label":"A","description":"d","preview":"+---+"},{"label":"B","description":"d"}]}]}""";

        var item = Assert.Single(Parsed(json).Questions);

        Assert.True(item.MultiSelect);
        Assert.Equal("+---+", item.Options[0].Preview.Value);
        Assert.True(item.Options[1].Preview.HasNoValue);
    }

    [Fact]
    public void Several_questions_keep_their_order()
    {
        var json = """{"questions":[{"question":"One?","options":[{"label":"a"}]},{"question":"Two?","options":[{"label":"b"}]}]}""";

        Assert.Equal(["One?", "Two?"], Parsed(json).Questions.Select(q => q.Question));
    }

    [Theory]
    [InlineData("""{"questions":[]}""")]
    [InlineData("""{"questions":[{"question":"Q?","options":[]}]}""")]
    [InlineData("""{"questions":[{"question":"Q?"}]}""")]
    [InlineData("""{"questions":[{"question":"","options":[{"label":"a"}]}]}""")]
    [InlineData("""{"questions":[{"question":"Q?","options":[{"description":"no label"}]}]}""")]
    [InlineData("""{"questions":[{"question":"Q?","options":[{"label":"a"}]},{"question":"Q?","options":[{"label":"b"}]}]}""")]
    [InlineData("""{"questions":{"question":"Q?"}}""")]
    [InlineData("""{"other":1}""")]
    [InlineData("[]")]
    [InlineData("{not json")]
    [InlineData("")]
    public void Input_the_card_cannot_show_is_refused(string json) => Assert.True(DialogAsk.Parse(json).HasNoValue);

    // --- the line the hook prints ----------------------------------------------------------------------

    [Fact]
    public void The_answer_line_is_an_allow_decision_whose_updated_input_echoes_the_input_with_answers()
    {
        var input = """{"questions":[{"question":"Pick a colour","header":"Colour","options":[{"label":"Red","description":"warm"}],"multiSelect":false}],"extra":{"k":[1,2]}}""";

        var line = Parsed(input).AnswerLine(Answers(("Pick a colour", "Red"))).Value;

        var decision = JsonNode.Parse(line)!["hookSpecificOutput"]!;
        Assert.Equal("PermissionRequest", (string)decision["hookEventName"]!);
        Assert.Equal("allow", (string)decision["decision"]!["behavior"]!);
        var updated = decision["decision"]!["updatedInput"]!.AsObject();
        // Every field the tool was called with comes back untouched, and "answers" is added.
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(input)!["questions"], updated["questions"]));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(input)!["extra"], updated["extra"]));
        Assert.Equal("Red", (string)updated["answers"]!["Pick a colour"]!);
    }

    [Fact]
    public void Answers_are_keyed_by_the_question_text_and_trimmed()
    {
        var json = """{"questions":[{"question":"One?","options":[{"label":"a"}]},{"question":"Two?","options":[{"label":"b"}]}]}""";

        var line = Parsed(json).AnswerLine(Answers(("Two?", " b, c "), ("One?", "a"))).Value;

        var answers = JsonNode.Parse(line)!["hookSpecificOutput"]!["decision"]!["updatedInput"]!["answers"]!.AsObject();
        Assert.Equal(["One?", "Two?"], answers.Select(a => a.Key));
        Assert.Equal("b, c", (string)answers["Two?"]!);
    }

    // One line of pure ASCII, so cmd's `type` hands it over unchanged on any code page, and still the same JSON.
    [Theory]
    [InlineData("ფერი")]
    [InlineData("say \"hi\" and \\ back")]
    [InlineData("two\nlines\tand a tab")]
    [InlineData("<b>&amp;</b> é 😀")]
    public void The_line_is_single_line_pure_ascii_and_round_trips_whatever_the_answer(string answer)
    {
        var json = """{"questions":[{"question":"ფერი?","header":"ფერი","options":[{"label":"წითელი","description":"d"}]}]}""";

        var line = Parsed(json).AnswerLine(Answers(("ფერი?", answer))).Value;

        Assert.All(line, c => Assert.True(c is >= ' ' and < (char)127, $"U+{(int)c:X4} in the line"));
        var node = JsonNode.Parse(line)!;
        Assert.Equal("allow", (string)node["hookSpecificOutput"]!["decision"]!["behavior"]!);
        var updated = node["hookSpecificOutput"]!["decision"]!["updatedInput"]!;
        Assert.Equal(answer.Trim(), (string)updated["answers"]!["ფერი?"]!);
        Assert.Equal("წითელი", (string)updated["questions"]![0]!["options"]![0]!["label"]!);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("blank")]
    [InlineData("unknown")]
    public void An_unanswered_blank_or_unknown_answer_fails(string why)
    {
        var json = """{"questions":[{"question":"One?","options":[{"label":"a"}]},{"question":"Two?","options":[{"label":"b"}]}]}""";
        var answers = why switch
        {
            "none" => Answers(("One?", "a")),
            "blank" => Answers(("One?", "a"), ("Two?", "  ")),
            _ => Answers(("One?", "a"), ("Two?", "b"), ("Three?", "c")),
        };

        Assert.True(Parsed(json).AnswerLine(answers).IsFailure);
    }

    // --- the answers read back from the tool result ----------------------------------------------------

    static JsonElement Result(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void String_answers_are_read_as_they_are()
    {
        var found = DialogAsk.AnswersIn(Result("""{"questions":[],"answers":{"Pick a colour":"Blue","Size?":"L"}}"""));

        Assert.Equal(new Dictionary<string, string> { ["Pick a colour"] = "Blue", ["Size?"] = "L" }, found.Value);
    }

    [Fact]
    public void An_array_answer_is_joined_with_comma_space()
    {
        var found = DialogAsk.AnswersIn(Result("""{"answers":{"Which?":["Red","Blue"]}}"""));

        Assert.Equal("Red, Blue", found.Value["Which?"]);
    }

    [Theory]
    [InlineData("""{"questions":[]}""")]
    [InlineData("""{"answers":{}}""")]
    [InlineData("""{"answers":{"Q?":"  "}}""")]
    [InlineData("""{"answers":{"Q?":3}}""")]
    [InlineData("""{"answers":[]}""")]
    [InlineData("\"just text\"")]
    public void A_result_without_an_answer_gives_none(string json) => Assert.True(DialogAsk.AnswersIn(Result(json)).HasNoValue);

    // --- the thread's words ----------------------------------------------------------------------------

    [Fact]
    public void The_summary_is_one_line_per_answered_question_headed_by_its_header_or_question()
    {
        var json = """{"questions":[{"question":"Pick a colour","header":"Colour","options":[{"label":"Red"}]},{"question":"Size?","options":[{"label":"L"}]},{"question":"Skipped?","header":"Skip","options":[{"label":"x"}]}]}""";

        var summary = Parsed(json).Summary(Answers(("Pick a colour", "Blue"), ("Size?", " L ")));

        Assert.Equal("- Colour: Blue\n- Size?: L", summary);
    }

    [Fact]
    public void The_thread_line_leads_with_the_summary() =>
        Assert.Equal("Answered Claude's question:\n- Colour: Blue", DialogAsk.ThreadLine("- Colour: Blue"));
}
