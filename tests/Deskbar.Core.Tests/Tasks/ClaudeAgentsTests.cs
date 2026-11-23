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
}
