using Deskbar.App;
using Deskbar.Core;
using Deskbar.Core.Abstractions;
using Deskbar.Windows.Monitoring;
using Xunit.Abstractions;

namespace Deskbar.Windows.Tests;

// "Save debug dump" (DebugDump): a file describing a machine nobody here can probe. The one thing it must
// never do is lose the facts it was written for, so these build a real bar window and read the real
// screens and z-order, and check the dump finds the bar among them.
public class DebugDumpTests(ITestOutputHelper output)
{
    [Fact]
    public void The_dump_describes_the_screens_the_bar_and_finds_the_bar_in_the_z_order() => StaThread.Run(() =>
    {
        var desktops = new PulsingDesktops { CurrentId = Guid.NewGuid() };
        desktops.Desktops.Add(new DesktopInfo(desktops.CurrentId, "Work"));
        var manager = new WorkspaceManager(desktops, new StubMonitor(), new StubTitles(), new StubStore());
        Assert.True(manager.Start().IsSuccess);
        // Parked off the virtual screen like every other bar test, so nothing flashes at whoever runs this.
        var bar = new FloatingBar(manager) { Left = -32000, Top = -32000, SendShellKeys = _ => { }, PressSpace = () => { } };
        bar.ShowBar();

        var dump = DebugDump.Compose(bar, manager);
        output.WriteLine(dump);

        Assert.Contains("== Screens", dump);
        Assert.Contains("screen 1", dump);
        Assert.Contains("== The bar", dump);
        Assert.Contains($"0x{bar.DumpHandle:X}", dump);
        Assert.Contains("fullscreen notices         none since the bar started", dump);
        Assert.Contains("strip held by work area    n/a (not docked on an edge)", dump);
        // The bar's own line in the window list, marked, and topmost as it always is.
        Assert.Contains("<<< THE BAR", dump);
        Assert.DoesNotContain("this section failed", dump);
        bar.Close();
    });

    static NativeMethods.RECT R(int left, int top, int right, int bottom) => new() { Left = left, Top = top, Right = right, Bottom = bottom };

    // The fullscreen test: the window reaches every edge of its screen. Maximized beside a docked bar does not,
    // and neither does a window a few pixels in from a corner, like the shadow viewer in the first screenshot
    // this was written for.
    [Theory]
    [InlineData(0, 0, 1920, 1080, true)]          // exactly the screen
    [InlineData(-8, -8, 1928, 1088, true)]        // past it on every side, as an oversized window is
    [InlineData(0, 0, 1660, 1080, false)]         // maximized beside a docked bar
    [InlineData(8, 20, 1925, 1270, false)]        // a captioned window a few pixels in from the corner
    public void A_window_covers_its_screen_only_when_it_reaches_every_edge(int l, int t, int r, int b, bool covers) =>
        Assert.Equal(covers, DebugDump.CoversScreen(R(l, t, r, b), R(0, 0, 1920, 1080)));
}
