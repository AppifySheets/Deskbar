using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CSharpFunctionalExtensions;
using TaskSpaces.App;
using TaskSpaces.Core;
using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;
using Xunit.Abstractions;

namespace TaskSpaces.Windows.Tests;

// Petre: "let's also separate the two halves not by hairline but make them ringed."
//
// Each screen's lane in a row carries an outline of its own now, and the gap that held the hairline is
// the space between two outlines. These tests began as the hairline's ("separator is not always uniform
// in height", "separator on GEPHA is in a wrong place on the second line") and keep its invariants,
// because the ring answers the same questions the hairline did: an empty row draws its boundary where a
// busy one does, every line of a wrapped row agrees, and the boundary does not wander.
//
// A ring is drawn PER LINE, as a slice: the first line of a row owns the top edge, the last line the
// bottom, a middle line only the sides. So a lane that wraps reads as one ring, not a stack of them.
//
// Measured through TransformToAncestor, so the bar's scale and each row's own scale are part of the
// number rather than something to argue about afterwards.
public class FloatingBarLaneRingTests(ITestOutputHelper output)
{
    // An empty workspace's rings are the same height and at the same place as a busy one's.
    //
    // About the line FLOOR as much as anything: a line holding nothing is still one icon line tall, so an
    // empty screen's ring (a drop target, #102) is as big as one with windows in it.
    [Fact]
    public void A_row_with_no_windows_rings_its_lanes_like_a_row_with_icons() => StaThread.Run(() =>
    {
        using var bar = TwoScreens(busyIcons: 2);

        var rings = bar.Rings().Where(r => r.Row is not "Pinned").ToList();
        rings.ForEach(r => output.WriteLine(r.ToString()));

        // Two rows, two screens each, one line each: four closed rings.
        Assert.Equal(4, rings.Count);
        Assert.All(rings, r => Assert.True(r.Closed, r.ToString()));
        Assert.Single(rings.Select(r => Math.Round(r.Height, 1)).Distinct());
        Assert.Single(rings.Select(r => Math.Round(r.InsetInRow, 1)).Distinct());
    });

    // A lane that wraps is ONE ring down both lines: the first line's slice is open at the bottom, the
    // second's at the top, and the second starts exactly where the first ends.
    [Fact]
    public void A_wrapped_lane_is_one_ring_down_its_lines() => StaThread.Run(() =>
    {
        // Seven windows, six of them on the first screen, so that screen's lane runs onto a second line.
        using var bar = TwoScreens(busyIcons: 7);

        var wrapped = bar.Rings().Where(r => r.Row == "Busy").ToList();
        wrapped.ForEach(r => output.WriteLine(r.ToString()));

        // Two screens times two lines. The second screen's lane has nothing on the second line and is
        // still ringed down it, so both lanes stay the row's full height.
        Assert.Equal(4, wrapped.Count);

        var top = wrapped.Where(r => r.InsetInRow == wrapped.Min(x => x.InsetInRow)).ToList();
        var bottom = wrapped.Except(top).ToList();
        Assert.Equal(2, top.Count);
        Assert.All(top, r => Assert.True(r.OpenBelow && !r.OpenAbove, r.ToString()));
        Assert.All(bottom, r => Assert.True(r.OpenAbove && !r.OpenBelow, r.ToString()));

        // Contiguous, lane by lane: same x, and no gap or overlap where the two slices meet.
        top.ForEach(upper =>
        {
            var lower = bottom.Single(r => Math.Abs(r.X - upper.X) < 0.5);
            Assert.Equal(upper.InsetInRow + upper.Height, lower.InsetInRow, 1);
        });
    });

    // Petre, with a screenshot of a wrapped GEPHA row: "separator on GEPHA is in a wrong place on the
    // second line." His own geometry dump said it in two lines:
    //
    //   line 0  cols=[*:74, auto:5, *:74]        zones=[screen1@start, screen2@73]
    //   line 1  cols=[*:50, auto:5, *:50, *:50]  zones=[screen1@5,     screen2@51]
    //
    // A THIRD lane on the second line, so the boundary moved from 73 to 51. It comes from the fallback
    // for a window whose monitor cannot be resolved, which used to get a lane of its own. With rings the
    // same mistake would draw a third ring on that line, so the check is that the second screen's ring
    // starts at the same x on every line of every row.
    [Fact]
    public void A_window_with_no_monitor_does_not_move_the_boundary() => StaThread.Run(() =>
    {
        using var bar = TwoScreens(busyIcons: 7, oneWithNoMonitor: true);

        var rings = bar.Rings().Where(r => r.Row is not "Pinned").ToList();
        rings.ForEach(r => output.WriteLine(r.ToString()));

        // Two rings on every line, never three...
        Assert.All(rings.GroupBy(r => r.Line), line => Assert.Equal(2, line.Count()));
        // ...and the right-hand one starts at the same x everywhere.
        Assert.Single(rings.GroupBy(r => r.Line).Select(line => Math.Round(line.Max(r => r.X), 1)).Distinct());
    });

    // One display: one lane, and a ring round it would only repeat the row's own ring, which already
    // means something else (current / previous workspace). So nothing is drawn.
    [Fact]
    public void One_screen_draws_no_lane_rings() => StaThread.Run(() =>
    {
        using var bar = TwoScreens(busyIcons: 3, everythingOnOneScreen: true);

        Assert.Empty(bar.Rings());
    });

    // Two displays, which is what turns the lanes on at all; one workspace holding busyIcons windows and
    // one holding nothing. The last window goes on the second screen so the busy row uses both lanes,
    // and the rest on the first.
    static Bar TwoScreens(int busyIcons, bool oneWithNoMonitor = false, bool everythingOnOneScreen = false)
    {
        var busyDesktop = Guid.NewGuid();
        var emptyDesktop = Guid.NewGuid();
        var busy = new Workspace(Guid.NewGuid(), "Busy", busyDesktop);
        var empty = new Workspace(Guid.NewGuid(), "Empty", emptyDesktop);

        var desktops = new PulsingDesktops { CurrentId = busyDesktop };
        desktops.Desktops.Add(new DesktopInfo(busyDesktop, "Busy"));
        desktops.Desktops.Add(new DesktopInfo(emptyDesktop, "Empty"));

        var monitor = new StubMonitor();
        var placement = new List<(WindowHandle Window, int Monitor)>();
        Enumerable.Range(1, busyIcons).ToList().ForEach(i =>
        {
            var window = new WindowInfo(new WindowHandle(100 + i), 10 + i, "app" + i, @"C:\app" + i + ".exe", "App " + i, null);
            desktops.Placements[window.Handle] = busyDesktop;
            monitor.Initial.Add(window);
            // The last window goes on the second screen; the one before it can be given no monitor at
            // all, which is the case that used to add a lane.
            if (oneWithNoMonitor && i == busyIcons - 1) return;
            placement.Add((window.Handle, i == busyIcons && !everythingOnOneScreen ? 2 : 1));
        });

        var screens = new StubScreens { Facts = TwoDisplays(placement) };
        // A STORED WIDTH, because that is the bar Petre runs and it selects a different layout: with
        // one, each screen's lane wraps against its own share of the row, and without one the rows keep
        // the old fixed five-icons rule and a lane can overflow its share -- which pushes a star column
        // wider and moves the boundary for reasons that have nothing to do with what is being tested
        // here. That case is known and written down in GroupRow ("still unprotected on a bar that has
        // never been resized"); it resolves itself the moment a width is stored, which is the moment
        // this fixture reproduces.
        //
        // 160 rather than the 202 the hairline tests used: the caption gutter's 58 DIP went back to the
        // icons when the caption moved above them. At the default 0.9 scale, 160 / 0.9 - 12 (padding and
        // ring) leaves 165.8 DIP, so each of the two lanes gets 80 and holds three 24 DIP icons -- the
        // same three that 202 gave with the gutter -- and seven windows still wrap the way these tests
        // need. It is also just above the bar's minimum width (176 * 0.9 = 158.4), which a smaller stored
        // width would be silently raised to.
        var store = new StubStore
        {
            Stored = AppState.Empty with
            {
                Workspaces = [busy, empty],
                FloatingBar = new FloatingBarState(0, 0, true) { Width = 160 },
            },
        };
        var manager = new WorkspaceManager(desktops, monitor, new StubTitles(), store, screenLayout: screens);
        Assert.True(manager.Start().IsSuccess);

        // Parked off the virtual screen, as every bar test does, so the suite never flashes a
        // translucent topmost bar at whoever is running it. WPF still reports it visible.
        var bar = new FloatingBar(manager) { Left = -32000, Top = -32000 };
        bar.Show();
        // SizeToContent: nothing in the tree has a height until a layout pass has run.
        bar.UpdateLayout();
        return new Bar(bar);
    }

    static ScreenFacts TwoDisplays(IReadOnlyList<(WindowHandle Window, int Monitor)> frontToBack) =>
        new(frontToBack.ToDictionary(x => x.Window, x => x.Monitor),
            new HashSet<WindowHandle>(),
            frontToBack.Select((x, i) => (x.Window, i)).ToDictionary(x => x.Window, x => x.i),
            PrimaryMonitor: 1);

    // One measured slice of a lane ring: where it starts, how tall it is, how far down its row it
    // begins, and which of its horizontal edges it draws. Every number is in the bar's own coordinates,
    // transforms included. `Line` is the line grid it sits in, to group slices by line.
    sealed record Ring(string Row, object Line, double X, double Height, double InsetInRow, bool OpenAbove, bool OpenBelow)
    {
        public bool Closed => !OpenAbove && !OpenBelow;

        public override string ToString() =>
            "row=" + Row + " x=" + X.ToString("0.##") + " height=" + Height.ToString("0.##") +
            " insetInRow=" + InsetInRow.ToString("0.##") + " openAbove=" + OpenAbove + " openBelow=" + OpenBelow;
    }

    sealed class Bar(FloatingBar bar) : IDisposable
    {
        public void Dispose() => bar.Close();

        public List<Ring> Rings() =>
            Slices((Panel)bar.FindName("Rows")!)
                .Select(slice =>
                {
                    var row = Row(slice);
                    return new Ring(Label(row), Ancestor<Grid>(slice), Left(slice), Extent(slice), Top(slice) - Top(row),
                        OpenAbove: slice.BorderThickness.Top == 0, OpenBelow: slice.BorderThickness.Bottom == 0);
                })
                .ToList();

        // A ring slice: a childless Border with a stroke on both sides that does not take the mouse.
        // Found by shape, because none of the bar's elements are named; nothing else on the bar is a
        // hit-test-invisible, childless, side-stroked Border.
        static List<Border> Slices(DependencyObject root) =>
            Descendants(root).OfType<Border>()
                .Where(b => b is { Child: null, IsHitTestVisible: false, BorderBrush: not null }
                            && b.BorderThickness.Left > 0 && b.BorderThickness.Right > 0)
                .ToList();

        static IEnumerable<DependencyObject> Descendants(DependencyObject node) =>
            Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(node))
                .Select(i => VisualTreeHelper.GetChild(node, i))
                .SelectMany(child => new[] { child }.Concat(Descendants(child)));

        // The row: the outermost Border wrapping a Grid on the way up, which is what GroupRow builds
        // around every row's container.
        static FrameworkElement Row(DependencyObject node) =>
            Ancestors(node).OfType<Border>().Last(b => b.Child is Grid);

        static T Ancestor<T>(DependencyObject node) where T : FrameworkElement =>
            Ancestors(node).OfType<T>().First();

        static IEnumerable<DependencyObject> Ancestors(DependencyObject node)
        {
            for (var at = VisualTreeHelper.GetParent(node); at is not null; at = VisualTreeHelper.GetParent(at))
                yield return at;
        }

        double Extent(FrameworkElement element) =>
            element.TransformToAncestor(bar).TransformBounds(new Rect(0, 0, 1, element.ActualHeight)).Height;

        double Top(FrameworkElement element) => element.TransformToAncestor(bar).Transform(new Point(0, 0)).Y;

        double Left(FrameworkElement element) => element.TransformToAncestor(bar).Transform(new Point(0, 0)).X;

        // The row's accessible name first, read off the Grid inside the row's Border, because since
        // #173 the pinned row has no text at all -- its caption cell holds the bar's two buttons -- and
        // a text-first reading would label it "↩". Text second, since a workspace row is still
        // identified by its caption.
        static string Label(DependencyObject row) =>
            (row is Border { Child: { } inner }
                ? System.Windows.Automation.AutomationProperties.GetName(inner)
                : System.Windows.Automation.AutomationProperties.GetName(row)) is { Length: > 0 } name
                ? name
                : Descendants(row).OfType<TextBlock>().Select(t => t.Text).FirstOrDefault(t => t.Length > 0) ?? "unnamed";
    }
}

// Monitor numbers for the bar and nothing else: the window moves this interface also carries are no
// part of this question.
sealed class StubScreens : IScreenLayout
{
    public ScreenFacts Facts { get; set; } = ScreenFacts.Empty;
    public ScreenFacts Snapshot() => Facts;
    public Maybe<WindowRect> RectOf(WindowHandle window) => Maybe<WindowRect>.None;
    public Result MoveTo(WindowHandle window, WindowRect rect, bool mayChangeShowState) => Result.Success();
}
