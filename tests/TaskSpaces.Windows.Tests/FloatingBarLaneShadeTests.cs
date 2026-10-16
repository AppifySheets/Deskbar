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

// Petre, on separating the two screens' halves of a row: rings were "too much", he asked for "softer
// separation", and of five mockups he picked the shade step. Every other screen's lane is a shade darker
// than the row's tint, with a 5 DIP gradient across the gap where the hairline used to be, and nothing
// else is drawn.
//
// These tests began as the hairline's ("separator is not always uniform in height", "separator on GEPHA is
// in a wrong place on the second line") and keep its invariants, because the shade answers the same
// questions: an empty row marks its boundary where a busy one does, every line of a wrapped row agrees, and
// the boundary does not wander.
//
// The shade is painted per LINE in the lane's own column, so a wrapped row's shade is a stack of slices
// that must meet edge to edge. Measured through TransformToAncestor, so the bar's scale and each row's own
// scale are part of the number rather than something to argue about afterwards.
public class FloatingBarLaneShadeTests(ITestOutputHelper output)
{
    // An empty workspace's shade is the same height and at the same x as a busy one's.
    //
    // About the line FLOOR as much as anything: a line holding nothing is still one icon line tall, so an
    // empty screen's half (a drop target, #102) is shaded exactly as fully as one with windows in it.
    [Fact]
    public void A_row_with_no_windows_is_shaded_like_a_row_with_icons() => StaThread.Run(() =>
    {
        using var bar = TwoScreens(busyIcons: 2);

        var lanes = bar.Shades().Where(s => s.Row is not "Pinned" && !s.IsFade).ToList();
        lanes.ForEach(s => output.WriteLine(s.ToString()));

        // Two rows, one line each, and only the second screen's half shaded in each.
        Assert.Equal(2, lanes.Count);
        Assert.Single(lanes.Select(s => Math.Round(s.Height, 1)).Distinct());
        Assert.Single(lanes.Select(s => Math.Round(s.X, 1)).Distinct());
    });

    // It is the SECOND screen that is shaded, and its shade starts where the gap's soft edge ends, so the
    // tone changes across the gap and nowhere else.
    [Fact]
    public void The_second_screen_is_shaded_and_the_change_is_softened_across_the_gap() => StaThread.Run(() =>
    {
        using var bar = TwoScreens(busyIcons: 2);

        var busy = bar.Shades().Where(s => s.Row == "Busy").ToList();
        busy.ForEach(s => output.WriteLine(s.ToString()));

        var fade = Assert.Single(busy, s => s.IsFade);
        var lane = Assert.Single(busy, s => !s.IsFade);
        Assert.Equal(fade.X + fade.Width, lane.X, 1);

        // To the right of the row's middle: the right-hand half, not the left.
        Assert.True(lane.X > lane.RowWidth / 2 - 5, lane.ToString());
    });

    // A wrapped row is shaded down every line, the slices meeting with no gap or overlap, and only the
    // last line's slice rounds the row's bottom-right corner.
    [Fact]
    public void A_wrapped_row_is_shaded_down_every_line() => StaThread.Run(() =>
    {
        // Seven windows, six of them on the first screen, so that screen's lane runs onto a second line.
        using var bar = TwoScreens(busyIcons: 7);

        var slices = bar.Shades().Where(s => s.Row == "Busy" && !s.IsFade).OrderBy(s => s.InsetInRow).ToList();
        slices.ForEach(s => output.WriteLine(s.ToString()));

        // The second screen has nothing on the second line and is still shaded down it, so the half is
        // the row's full height.
        Assert.Equal(2, slices.Count);
        Assert.Equal(slices[0].X, slices[1].X, 1);
        Assert.Equal(slices[0].InsetInRow + slices[0].Height, slices[1].InsetInRow, 1);
        Assert.False(slices[0].RoundedBottomRight);
        Assert.True(slices[1].RoundedBottomRight);
    });

    // Petre, with a screenshot of a wrapped GEPHA row: "separator on GEPHA is in a wrong place on the
    // second line." His own geometry dump said it in two lines:
    //
    //   line 0  cols=[*:74, auto:5, *:74]        zones=[screen1@start, screen2@73]
    //   line 1  cols=[*:50, auto:5, *:50, *:50]  zones=[screen1@5,     screen2@51]
    //
    // A THIRD lane on the second line, so the boundary moved from 73 to 51. It comes from the fallback
    // for a window whose monitor cannot be resolved, which used to get a lane of its own. With shading the
    // same mistake would move or narrow the shade on that line, so the check is one shade per line,
    // starting at the same x everywhere.
    [Fact]
    public void A_window_with_no_monitor_does_not_move_the_boundary() => StaThread.Run(() =>
    {
        using var bar = TwoScreens(busyIcons: 7, oneWithNoMonitor: true);

        var lanes = bar.Shades().Where(s => s.Row is not "Pinned" && !s.IsFade).ToList();
        lanes.ForEach(s => output.WriteLine(s.ToString()));

        Assert.All(lanes.GroupBy(s => s.Line), line => Assert.Single(line));
        Assert.Single(lanes.Select(s => Math.Round(s.X, 1)).Distinct());
    });

    // The shade sits UNDER everything else in its line, #89's drop highlight included, so dragging a window
    // over the shaded half lights it up as brightly as the other half instead of through a darkening.
    [Fact]
    public void The_shade_is_painted_beneath_the_drop_highlight() => StaThread.Run(() =>
    {
        using var bar = TwoScreens(busyIcons: 2);

        Assert.All(bar.Shades(), s =>
        {
            var children = s.LineGrid.Children.OfType<UIElement>().ToList();
            var shadeAt = children.IndexOf(s.Element);
            Assert.All(children.Where(c => !Bar.IsShade(c)), other => Assert.True(children.IndexOf(other) > shadeAt));
        });
    });

    // One display: one lane, nothing to tell apart, so nothing is shaded.
    [Fact]
    public void One_screen_shades_nothing() => StaThread.Run(() =>
    {
        using var bar = TwoScreens(busyIcons: 3, everythingOnOneScreen: true);

        Assert.Empty(bar.Shades());
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

    // One measured slice of shade: a lane's (solid) or the gap's soft edge (a gradient). Every number is in
    // the bar's own coordinates, transforms included.
    sealed record Shade(string Row, Grid LineGrid, Border Element, bool IsFade, double X, double Width, double Height,
        double InsetInRow, double RowWidth, bool RoundedBottomRight)
    {
        public object Line => LineGrid;

        public override string ToString() =>
            "row=" + Row + (IsFade ? " fade" : " lane") + " x=" + X.ToString("0.##") + " width=" + Width.ToString("0.##") +
            " height=" + Height.ToString("0.##") + " insetInRow=" + InsetInRow.ToString("0.##") +
            " rowWidth=" + RowWidth.ToString("0.##") + " rounded=" + RoundedBottomRight;
    }

    sealed class Bar(FloatingBar bar) : IDisposable
    {
        public void Dispose() => bar.Close();

        public List<Shade> Shades() =>
            Descendants((Panel)bar.FindName("Rows")!).OfType<Border>().Where(IsShade)
                .Select(shade =>
                {
                    var row = Row(shade);
                    return new Shade(Label(row), Ancestor<Grid>(shade), shade, shade.Background is LinearGradientBrush,
                        Left(shade), Width(shade), Extent(shade), Top(shade) - Top(row), Width(row),
                        shade.CornerRadius.BottomRight > 0);
                })
                .ToList();

        // A shade: a childless Border that does not take the mouse and paints a black, translucent fill
        // (solid for a lane, a gradient for the gap's soft edge). Found by shape, because none of the bar's
        // elements are named, and nothing else on the bar is a hit-test-invisible, childless, black fill.
        public static bool IsShade(object node) =>
            node is Border { Child: null, IsHitTestVisible: false } border
            && border.Background switch
            {
                SolidColorBrush solid => solid.Color is { R: 0, G: 0, B: 0, A: > 0 },
                LinearGradientBrush gradient => gradient.GradientStops.All(s => s.Color is { R: 0, G: 0, B: 0 }),
                _ => false,
            };

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

        double Width(FrameworkElement element) =>
            element.TransformToAncestor(bar).TransformBounds(new Rect(0, 0, element.ActualWidth, 1)).Width;

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
