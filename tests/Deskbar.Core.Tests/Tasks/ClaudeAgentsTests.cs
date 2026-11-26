using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests.Tasks;

// `claude agents --json` as Deskbar reads it. The first row's shape is the one measured on this machine: interactive
// rows carry no "id" and no "state", background rows carry both, and a finished background session stays listed.
public class ClaudeAgentsTests
{
    [Fact]
    public void An_interactive_and_a_background_session_are_read()
    {
        var json = """
            [{"pid":1,"cwd":"E:\\repos\\X","kind":"interactive","sessionId":"aaaa","name":"A","status":"idle"},
             {"pid":2,"id":"884c127f","cwd":"E:\\repos\\X","kind":"background","sessionId":"884c127f-96","name":"B","status":"busy","state":"working"}]
            """;

        var agents = ClaudeAgents.Parse(json).Value;

        Assert.False(agents[0].Background);
        Assert.True(agents[1].Background);
        Assert.True(agents[0].ShortId.HasNoValue);
        Assert.Equal("884c127f", agents[1].ShortId.Value);
        Assert.False(agents[0].Busy);
        Assert.True(agents[1].Busy);
        Assert.Equal(@"E:\repos\X", agents[0].Folder);
        Assert.Equal("B", agents[1].Name);
        Assert.Equal("884c127f-96", agents[1].SessionId);
    }

    [Fact]
    public void A_background_session_that_is_done_is_still_listed()
    {
        var json = """[{"pid":3,"id":"abcd1234","cwd":"E:\\x","kind":"background","sessionId":"abcd1234-1","name":"C","status":"idle","state":"done"}]""";

        var agent = Assert.Single(ClaudeAgents.Parse(json).Value);

        Assert.True(agent.Background);
        Assert.False(agent.Busy);
        Assert.Equal("done", agent.State.Value);
        Assert.True(agent.TurnOver);
    }

    // Task 607af1db, measured 2026-11-26: a session whose turn ended with a Bash of its own still running in the
    // background is listed busy, with state "done", until the job ends. Its turn is over; one busy and working is not,
    // and neither is a busy row with no state, which says nothing about its turn.
    [Theory]
    [InlineData("""{"status":"busy","state":"done"}""", true)]
    [InlineData("""{"status":"busy","state":"blocked"}""", true)]
    [InlineData("""{"status":"busy","state":"working"}""", false)]
    [InlineData("""{"status":"busy"}""", false)]
    [InlineData("""{"status":"idle","state":"blocked"}""", true)]
    [InlineData("""{"status":"waiting","state":"blocked"}""", true)]
    public void A_turn_is_over_unless_the_row_is_busy_and_not_done_or_blocked(string fields, bool turnOver)
    {
        var json = "[" + fields.Replace("{", """{"id":"abcd1234","cwd":"E:\\x","kind":"background","sessionId":"abcd1234-1",""") + "]";

        Assert.Equal(turnOver, Assert.Single(ClaudeAgents.Parse(json).Value).TurnOver);
    }

    // startedAt (milliseconds since 1970, as measured) tells the newest of several sessions in the manager's folder
    // (ManagerSession.Next); a row without it, or with something else there, is still read, with no start time.
    [Fact]
    public void The_start_time_is_read_when_the_row_has_one()
    {
        var json = """
            [{"pid":1,"id":"884c127f","cwd":"E:\\x","kind":"background","startedAt":1795424165340,"sessionId":"884c127f-1","status":"idle"},
             {"pid":2,"id":"abcd1234","cwd":"E:\\x","kind":"background","sessionId":"abcd1234-1","status":"idle"},
             {"pid":3,"id":"abcd5678","cwd":"E:\\x","kind":"background","startedAt":"soon","sessionId":"abcd5678-1","status":"idle"}]
            """;

        var agents = ClaudeAgents.Parse(json).Value;

        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1795424165340), agents[0].StartedAt.Value);
        Assert.True(agents[1].StartedAt.HasNoValue);
        Assert.True(agents[2].StartedAt.HasNoValue);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("")]
    [InlineData("[1]")]
    [InlineData("[{}]")]
    [InlineData("not json")]
    public void Anything_but_an_array_of_objects_with_a_session_id_is_a_failure(string json) =>
        Assert.True(ClaudeAgents.Parse(json).IsFailure);

    [Fact]
    public void An_empty_list_is_a_success_with_no_sessions() =>
        Assert.Empty(ClaudeAgents.Parse("[]").Value);

    [Fact]
    public void Unknown_fields_are_ignored_and_a_missing_name_is_empty()
    {
        var json = """[{"sessionId":"s1","kind":"daemon","cwd":"E:\\y","status":"busy","somethingNew":{"a":1},"pid":9}]""";

        var agent = Assert.Single(ClaudeAgents.Parse(json).Value);

        Assert.False(agent.Background);
        Assert.Equal("", agent.Name);
        Assert.True(agent.Busy);
    }

    [Fact]
    public void The_short_id_is_read_from_the_start_output()
    {
        Assert.Equal("6785e6b5",
            ClaudeAgents.ShortIdFromStart("Starting background service…\nbackgrounded · 6785e6b5 · deskbar probe\n").Value);
        Assert.True(ClaudeAgents.ShortIdFromStart("Workspace not trusted. Run `claude` in X once").HasNoValue);
    }

    // What Petre's Deskbar actually received on 2026-11-23 when the pipe was decoded with the ANSI code page.
    [Fact]
    public void The_short_id_is_read_when_the_separator_was_decoded_with_the_wrong_code_page() =>
        Assert.Equal("d5edbad6",
            ClaudeAgents.ShortIdFromStart("backgrounded Â· d5edbad6\n  claude agents             list sessions\n").Value);
}
