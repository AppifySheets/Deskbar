using System.Windows;
using System.Windows.Threading;
using Deskbar.App;

namespace Deskbar.Windows.Tests;

// The surface layers behind the bar (FloatingBar.Surface.cs) must never hold the bar at a height its content no longer
// needs. Deskbar task 888cd56b, "undocked bar is needlessly tall": after an undock the floating bar kept the docked
// screen's height, an empty band between the last workspace row and the task list. The cause was the layers' own
// desired heights: PlaceSurfaces gives them explicit margins and heights after every layout, the foot layer's adding up
// to the whole bar, so on a SizeToContent bar they held it at whatever height it last had. Docked to floating is the
// case Petre saw; any content that shrinks on a floating bar (a line that goes away, a shorter window list) is the same
// latch, and that is the case tested here, because it needs no app bar registered with the shell.
//
// The harness: GEPHA (current) and Sparrow, one window each (see Harness in FloatingBarRebuildTests).
public class FloatingBarSurfaceHeightTests
{
    // A layout pass and the LayoutUpdated work it queues (PlaceSurfaces moves the layers, which asks for another pass),
    // run to the end so the height read afterwards is the settled one.
    static void Settle(FloatingBar bar)
    {
        foreach (var _ in Enumerable.Range(0, 4))
        {
            bar.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
        }
    }

    [Fact]
    public void The_bar_shrinks_back_when_its_content_does() => StaThread.Run(() =>
    {
        var due = false;
        var bar = new FloatingBar(Harness.Build().Manager) { Left = -32000, Top = -32000, ProReminderDue = () => due };
        using var shown = new BarUnderTest(bar);
        bar.Show();
        bar.PaintProReminder();
        Settle(bar);
        var before = bar.ActualHeight;

        // The Pro reminder line is a few wrapped lines: the bar grows by it...
        due = true;
        bar.PaintProReminder();
        Settle(bar);
        Assert.True(bar.ActualHeight > before + 10, $"the reminder should grow the bar: {before} -> {bar.ActualHeight}");

        // ...and gives the height back when it goes. Before the fix the foot layer still asked for the taller
        // height, so the bar stayed where it was with an empty band above the footer.
        due = false;
        bar.PaintProReminder();
        Settle(bar);
        Assert.Equal(before, bar.ActualHeight, 1);
    });
}
