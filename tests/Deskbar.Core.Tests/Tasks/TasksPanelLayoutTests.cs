using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests.Tasks;

// The tasks panel's width rules (TasksPanelLayout): the pure numbers the panel and its lines consult.
public class TasksPanelLayoutTests
{
    // Never narrower than 360, never wider than half the screen, and on a screen under 720 DIP the minimum wins.
    [Theory]
    [InlineData(100, 2560, 360)]
    [InlineData(460, 2560, 460)]
    [InlineData(5000, 2560, 1280)]
    [InlineData(460, 600, 360)]
    public void The_width_is_kept_between_the_minimum_and_half_the_screen(double width, double screen, double expected) =>
        Assert.Equal(expected, TasksPanelLayout.Clamp(width, screen));

    // 710: the full line's floor since the snooze moon (18 DIP) joined the toggles; 692 after the status chip column
    // (72 DIP), 620 before that.
    [Theory]
    [InlineData(620, true)]
    [InlineData(709.9, true)]
    [InlineData(710, false)]
    public void Lines_are_slim_below_710(double w, bool slim) =>
        Assert.Equal(slim, TasksPanelLayout.Slim(w));

    [Theory]
    [InlineData(779.9, false)]
    [InlineData(780, true)]
    public void The_Matrix_is_offered_from_780(double w, bool ok) =>
        Assert.Equal(ok, TasksPanelLayout.MatrixAllowed(w));
}
