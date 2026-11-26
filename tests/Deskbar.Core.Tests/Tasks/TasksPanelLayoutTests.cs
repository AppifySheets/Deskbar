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

    // 850: where the full line's readable title and "⋯" both fit (task 39d58e5b). It was 710, where the full line merely
    // fitted and its title had 100 DIP; 710 to 849 are slim now, the width of Petre's "wider renders worse" screenshot.
    [Theory]
    [InlineData(620, true)]
    [InlineData(710, true)]
    [InlineData(849.9, true)]
    [InlineData(850, false)]
    public void Lines_are_slim_below_850(double w, bool slim) =>
        Assert.Equal(slim, TasksPanelLayout.Slim(w));

    [Theory]
    [InlineData(779.9, false)]
    [InlineData(780, true)]
    public void The_Matrix_is_offered_from_780(double w, bool ok) =>
        Assert.Equal(ok, TasksPanelLayout.MatrixAllowed(w));
}
