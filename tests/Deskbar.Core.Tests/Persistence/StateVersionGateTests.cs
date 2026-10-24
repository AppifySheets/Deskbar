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
    [InlineData("2.1.44+a", "2.1.44+b")]
    public void Same_older_missing_or_unreadable_WrittenBy_passes(string? writtenBy, string running) =>
        Assert.True(StateVersionGate.Check(writtenBy, running).IsSuccess);

    // Within a major a newer stamp loads: the Store copy lags the GitHub copy by a patch or a minor nearly
    // every day, and refusing that would block the Store upgrade path.
    [Theory]
    [InlineData("2.1.50", "2.1.44")]
    [InlineData("2.2.0", "2.1.49")]
    [InlineData("2.9.0+abc", "2.0.0+def")]
    public void Newer_patch_or_minor_WrittenBy_passes(string writtenBy, string running) =>
        Assert.True(StateVersionGate.Check(writtenBy, running).IsSuccess);

    [Fact]
    public void Newer_major_WrittenBy_refuses_with_both_versions_named()
    {
        var result = StateVersionGate.Check("3.0.0", "2.9.99");
        Assert.True(result.IsFailure);
        Assert.Equal("Your settings were saved by Deskbar 3.0.0; this is 2.9.99. Start the newer copy, or remove it and delete the WrittenBy line from state.json to go on with this one.", result.Error);
    }

    [Theory]
    [InlineData("3.0.0", "2.9.99")]
    [InlineData("10.0.0", "9.5.0")]
    [InlineData("3.0.0+abc", "2.1.44+def")]
    public void Newer_major_is_compared_numerically_ignoring_metadata(string writtenBy, string running) =>
        Assert.True(StateVersionGate.Check(writtenBy, running).IsFailure);

    [Fact]
    public void IsRefusal_recognises_only_the_refusal_message()
    {
        Assert.True(StateVersionGate.IsRefusal(StateVersionGate.RefusalMessage("3.0.0", "2.9.99")));
        Assert.False(StateVersionGate.IsRefusal("Could not read state.json: bad"));
    }
}
