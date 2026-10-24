using Deskbar.Core.Persistence;

namespace Deskbar.Core.Tests;

public sealed class StateVersionGateTests
{
    [Theory]
    [InlineData(null, "2.1.44")]
    [InlineData("2.1.44", "2.1.44")]
    [InlineData("2.1.43+abc", "2.1.44")]
    [InlineData("garbage", "2.1.44")]
    [InlineData("2.1.44", "2.1.44+deadbeef")]
    [InlineData("2.1.44-beta", "2.1.44")]
    public void Same_older_missing_or_unreadable_WrittenBy_passes(string? writtenBy, string running) =>
        Assert.True(StateVersionGate.Check(writtenBy, running).IsSuccess);

    [Fact]
    public void Newer_WrittenBy_refuses_with_both_versions_named()
    {
        var result = StateVersionGate.Check("2.1.50", "2.1.44");
        Assert.True(result.IsFailure);
        Assert.Equal("Your settings were saved by Deskbar 2.1.50; this is 2.1.44. Start the newer copy, or remove it and delete the WrittenBy line from state.json to go on with this one.", result.Error);
    }

    [Theory]
    [InlineData("2.2.0", "2.1.44")]
    [InlineData("3.0.0", "2.9.99")]
    [InlineData("2.1.50+abc", "2.1.44+def")]
    public void Newer_is_compared_numerically_ignoring_metadata(string writtenBy, string running) =>
        Assert.True(StateVersionGate.Check(writtenBy, running).IsFailure);
}
