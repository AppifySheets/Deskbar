using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Deskbar.App;
using Deskbar.Core;
using Deskbar.Core.Abstractions;
using Deskbar.Core.Domain;

namespace Deskbar.Windows.Tests.Tiling;

// The bar's half of final review item 1. Spec: "A drop on one of the bar's workspace rows is the bar's gesture
// (move to that workspace) and wins over a tile swap." The bar marks a title-bar drop as pending from the moment
// the drag ends on a row until the drop has returned, and the Tiler (TilerTests) leaves a pending window alone.
// These tests prove the mark's lifetime: set as soon as the drop starts (before the 350 ms Snap wait), still set
// while the drop itself runs, and gone afterwards, also when the drop throws part way.
//
// FinishHeaderDrop is driven directly: a real drag cannot be made from a test, and its end is only the call
// below (OnWindowDragEnded hands the aimed row to it). The window handle names no window; the manager has no
// screen layout, so both put-backs fail quietly, as they do for a window that closed mid-drop.
public class HeaderDropPendingTests
{
    // Parked off the virtual screen like every other bar test here, and never shown.
    static FloatingBar Bar()
    {
        var desktops = new PulsingDesktops { CurrentId = Guid.NewGuid() };
        desktops.Desktops.Add(new DesktopInfo(desktops.CurrentId, "Work"));
        var manager = new WorkspaceManager(desktops, new StubMonitor(), new StubTitles(), new StubStore());
        Assert.True(manager.Start().IsSuccess);
        return new FloatingBar(manager) { Left = -32000, Top = -32000 };
    }

    // FinishHeaderDrop awaits its Snap wait and continues on the captured context, which on this bare STA thread
    // is only the dispatcher once one is installed; the test then runs the dispatcher until the drop is done.
    static void PumpUntil(Func<bool> done, TimeSpan bound)
    {
        var clock = Stopwatch.StartNew();
        while (!done() && clock.Elapsed < bound)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
    }

    static readonly WindowHandle Dropped = new(0x4242);
    static readonly WindowRect Start = new(100, 100, 500, 400);

    [Fact]
    public void A_drop_is_pending_from_the_release_until_the_drop_has_returned() => StaThread.Run(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var bar = Bar();
        bool? pendingDuringDrop = null;
        var target = new FloatingBar.HeaderDropTarget(new Grid(), "g", "Beta", Brushes.Transparent,
            (w, _) => pendingDuringDrop = bar.IsHeaderDropPending(w));
        Assert.False(bar.IsHeaderDropPending(Dropped));

        var drop = bar.FinishHeaderDrop(Dropped, Start, wasMaximized: false, target, screen: null);

        // Straight after the release, while the drop waits for Snap: pending.
        Assert.True(bar.IsHeaderDropPending(Dropped));
        PumpUntil(() => drop.IsCompleted, TimeSpan.FromSeconds(5));

        Assert.True(drop.IsCompletedSuccessfully);
        Assert.True(pendingDuringDrop);
        Assert.False(bar.IsHeaderDropPending(Dropped));
        bar.Close();
    });

    [Fact]
    public void An_abandoned_drop_leaves_nothing_pending() => StaThread.Run(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var bar = Bar();
        var target = new FloatingBar.HeaderDropTarget(new Grid(), "g", "Beta", Brushes.Transparent,
            (_, _) => throw new InvalidOperationException("the move failed (test)"));

        var drop = bar.FinishHeaderDrop(Dropped, Start, wasMaximized: false, target, screen: null);
        Assert.True(bar.IsHeaderDropPending(Dropped));
        PumpUntil(() => drop.IsCompleted, TimeSpan.FromSeconds(5));

        Assert.True(drop.IsFaulted);
        Assert.False(bar.IsHeaderDropPending(Dropped));
        bar.Close();
    });
}
