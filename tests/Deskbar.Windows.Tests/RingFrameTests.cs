using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Deskbar.App;
using Deskbar.Core.Overview;
using Xunit.Abstractions;

namespace Deskbar.Windows.Tests;

// The focus ring's trail on the screen (FocusRings.Trail): Petre, "workspaces as inspiration, can you ring windows
// similarly?", so the window you were in before the focused one is outlined dashed and the one before that dotted,
// the bar's own "current - solid - previous - dashed; one before that - dotted".
//
// Drawn by RingFrame, and read back as PIXELS here rather than as properties, because the property was never the
// question: a dash array is in multiples of the stroke's thickness, so a wrong division gives a line that is
// technically dashed and looks solid, or dots too small to see. Rendered off-screen (RenderTargetBitmap), so nothing
// reaches the screen and no window is shown.
public class RingFrameTests(ITestOutputHelper output)
{
    // Petre's main screen is at 225%, and the trail line is 2 physical pixels there (WindowRings.TrailPixels).
    const double Dpi = 216;
    const int Pixels = 2;

    // The ink along the frame's top edge, as (ink, gap) run lengths in physical pixels, corners left out.
    IReadOnlyList<(bool Ink, int Length)> TopEdgeRuns(FocusRings.Line pattern)
    {
        IReadOnlyList<(bool, int)> runs = [];
        StaThread.Run(() =>
        {
            var frame = new RingFrame();
            frame.Draw("#FFFFFF", Pixels * 96.0 / Dpi, pattern, rounded: false);
            var line = frame.Line;
            // 200 x 100 DIP: 450 x 225 physical pixels at 225%.
            line.Measure(new Size(200, 100));
            line.Arrange(new Rect(0, 0, 200, 100));
            var bitmap = new RenderTargetBitmap(450, 225, Dpi, Dpi, PixelFormats.Pbgra32);
            bitmap.Render(line);

            // The top row of pixels, alpha only, from x = 30 to 420: well clear of both corners.
            var row = new byte[450 * 4];
            bitmap.CopyPixels(new Int32Rect(0, 0, 450, 1), row, 450 * 4, 0);
            var inked = Enumerable.Range(30, 390).Select(x => row[x * 4 + 3] >= 128).ToList();
            runs = inked.Aggregate(new List<(bool Ink, int Length)>(), (acc, ink) =>
            {
                if (acc.Count > 0 && acc[^1].Ink == ink) acc[^1] = (ink, acc[^1].Length + 1);
                else acc.Add((ink, 1));
                return acc;
            });
            frame.Close();
        });
        output.WriteLine($"{pattern}: " + string.Join(" ", runs.Select(r => (r.Item1 ? "#" : ".") + r.Item2)));
        return runs;
    }

    // Inner runs only: the first and last are cut by the edges of the sample.
    static IReadOnlyList<int> Inner(IReadOnlyList<(bool Ink, int Length)> runs, bool ink) =>
        runs.Skip(1).SkipLast(1).Where(r => r.Ink == ink).Select(r => r.Length).ToList();

    [Fact]
    public void A_solid_ring_is_unbroken() =>
        Assert.Equal([(true, 390)], TopEdgeRuns(FocusRings.Line.Solid));

    // 6 DIP dashes with 4 DIP gaps, the bar's row ring: 13.5 and 9 physical pixels at 225%.
    [Fact]
    public void A_dashed_ring_is_dashes_with_gaps()
    {
        var runs = TopEdgeRuns(FocusRings.Line.Dashed);

        Assert.NotEmpty(Inner(runs, ink: true));
        Assert.All(Inner(runs, ink: true), length => Assert.InRange(length, 12, 15));
        Assert.All(Inner(runs, ink: false), length => Assert.InRange(length, 8, 11));
    }

    // A dot is the line's own thickness, a 2 px square, with the same 4 DIP gap: told from a dash by the length of the
    // ink, as on the bar.
    [Fact]
    public void A_dotted_ring_is_dots_the_size_of_the_line()
    {
        var runs = TopEdgeRuns(FocusRings.Line.Dotted);

        Assert.NotEmpty(Inner(runs, ink: true));
        Assert.All(Inner(runs, ink: true), length => Assert.InRange(length, 1, 3));
        Assert.All(Inner(runs, ink: false), length => Assert.InRange(length, 8, 11));
    }
}
