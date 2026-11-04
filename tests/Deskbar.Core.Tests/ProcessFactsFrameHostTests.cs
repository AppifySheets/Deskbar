using Deskbar.Core.Domain;

namespace Deskbar.Core.Tests;

public class ProcessFactsFrameHostTests
{
    [Theory]
    [InlineData("ApplicationFrameHost")]
    [InlineData("applicationframehost")]
    [InlineData("APPLICATIONFRAMEHOST")]
    public void The_frame_host_is_recognised_in_any_case(string name) =>
        Assert.True(ProcessFacts.IsFrameHost(name));

    [Theory]
    [InlineData("Calculator")]
    [InlineData("ApplicationFrameHost2")]
    [InlineData("MyApplicationFrameHost")]
    [InlineData("")]
    public void Anything_else_is_not_the_frame_host(string name) =>
        Assert.False(ProcessFacts.IsFrameHost(name));
}
