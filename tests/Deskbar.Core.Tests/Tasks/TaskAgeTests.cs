using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests.Tasks;

// How long ago a task appeared, as each line says it (TaskAge; Petre: "tasks should say how long ago they appeared -
// 2h3m"). One test per band and its boundaries.
public class TaskAgeTests
{
    [Theory]
    [InlineData(0, "now")]
    [InlineData(59, "now")]
    [InlineData(60, "1m")]
    [InlineData(45 * 60, "45m")]
    [InlineData(59 * 60 + 59, "59m")]
    [InlineData(60 * 60, "1h")]
    [InlineData(2 * 3600 + 3 * 60, "2h3m")]
    [InlineData(23 * 3600 + 59 * 60, "23h59m")]
    [InlineData(24 * 3600, "1d")]
    [InlineData(3 * 86400 + 2 * 3600, "3d2h")]
    [InlineData(27 * 86400 + 23 * 3600, "27d23h")]
    [InlineData(28 * 86400, "4w")]
    [InlineData(35 * 86400 + 5 * 3600, "5w")]
    public void Each_band_reads_compactly(int seconds, string expected) =>
        Assert.Equal(expected, TaskAge.Format(TimeSpan.FromSeconds(seconds)));

    // A clock that is a little behind the task's own stamp (another machine, a later sync) says "now", never a
    // negative age.
    [Fact]
    public void A_future_stamp_reads_now() => Assert.Equal("now", TaskAge.Format(TimeSpan.FromMinutes(-3)));
}
