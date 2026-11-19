using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests.Tasks;

// The timer lengths are four fixed presets (see "The model" in
// docs/superpowers/specs/2026-11-19-tasks-in-the-bar-design.md); this parses what a control-pipe caller types.
public class TaskPresetsTests
{
    [Theory, InlineData("1m", 1), InlineData("5m", 5), InlineData("10m", 10), InlineData("30m", 30)]
    public void Parses_the_four_presets(string text, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), TaskPresets.Parse(text).Value);

    [Theory, InlineData("2m"), InlineData("10"), InlineData(""), InlineData("1h")]
    public void Refuses_anything_else(string text) => Assert.True(TaskPresets.Parse(text).IsFailure);

    [Fact]
    public void Labels_read_as_minutes() => Assert.Equal("10 min", TaskPresets.Label(TimeSpan.FromMinutes(10)));
}
