using Deskbar.Core.Domain;

namespace Deskbar.Core.Tests;

// The window list's floor. Petre: "current workspace should be at least 5 slots tall", then "make that number 5
// configurable". Null is the default of five; anything stored is held between one line and twelve.
public class WindowListLinesTests
{
    [Theory]
    [InlineData(null, 5)]
    [InlineData(3, 3)]
    [InlineData(0, 1)]
    [InlineData(-4, 1)]
    [InlineData(40, 12)]
    public void A_stored_floor_is_held_inside_the_rails(int? stored, int expected) =>
        Assert.Equal(expected, WindowListLines.Clamp(stored));
}
