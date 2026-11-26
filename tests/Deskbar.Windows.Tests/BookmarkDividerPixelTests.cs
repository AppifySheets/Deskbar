using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CSharpFunctionalExtensions;
using Deskbar.Core.Bookmarks;
using Deskbar.Core.Persistence;
using Xunit.Abstractions;

namespace Deskbar.Windows.Tests;

// The bar between a row's windows and its bookmark chips, measured in PIXELS. Petre asked for "a vertical bar
// between" them, and the divider's properties (a 1 DIP bright Border on the first chip) were all in place while his
// live bar showed nothing at all between the Edge icon and the first chip. Properties are not pixels (the lesson of
// RowRingPixelTests next door), so this renders the bar and reads the columns in the gap.
//
// Both cases the gap meets on a real bar: a collapsed row, whose window wears the landing marker, and the current
// row with the window list on the screens (Petre's setting), whose window is the focused one with its ring, fill and
// glow, as the Edge icon was on his GPH row.
public class BookmarkDividerPixelTests(ITestOutputHelper output)
{
    static IEnumerable<DependencyObject> Descendants(DependencyObject node) =>
        LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()
            .SelectMany(child => new[] { child }.Concat(Descendants(child)));

    static Grid RowNamed(Panel rows, string name) =>
        rows.Children.OfType<Border>().Select(b => b.Child).OfType<Grid>()
            .Single(row => AutomationProperties.GetName(row) == name);

    [Theory]
    [InlineData("Sparrow", false)]
    [InlineData("GEPHA", true)]
    public void A_bright_bar_stands_in_the_gap_between_the_windows_and_the_first_chip(string workspace, bool focusedOnScreens) => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        var id = harness.Manager.State.Workspaces.Single(w => w.Name == workspace).Id;
        Assert.True(harness.Manager.AddBookmark(id, BookmarkRules.Link("https://github.com", "GitHub").Value).IsSuccess);
        if (focusedOnScreens)
        {
            // The list on the screens leaves the current row as icons, and the foreground window is the focused one.
            Assert.True(harness.Manager.SetWindowListPlace(WindowListPlace.Screens).IsSuccess);
            harness.Monitor.ForegroundWindow = Maybe<Core.Domain.WindowHandle>.From(harness.First.Handle);
        }
        using var bar = harness.ShowBar();
        bar.Bar.UpdateLayout();

        var window = bar.Bar;
        var row = RowNamed(bar.Rows, workspace);
        var buttons = Descendants(row).OfType<Button>().ToList();
        var icon = buttons.Last(b => b.Tag is "icon");
        var chip = buttons.First(b => b.Tag is "bookmark");

        // In the WINDOW's coordinates, which are the bitmap's: the bar scales its content (a layout transform above
        // the rows), so a point relative to the bar's root is not where it is drawn. The window's artwork stops 2 DIP
        // short of its cell's right edge and the chip's face starts 2 DIP into its cell: the gap is between the two.
        Point At(FrameworkElement element, double x, double y) => element.TransformToAncestor(window).Transform(new Point(x, y));
        var artEnds = At(icon, icon.ActualWidth - 2, 0).X;
        var faceStarts = At(chip, 2, 0).X;
        // Only the pixels wholly inside the gap: one that the artwork or the chip's outline reaches into is lit by them.
        var gap = Enumerable.Range((int)Math.Ceiling(artEnds), (int)Math.Floor(faceStarts) - (int)Math.Ceiling(artEnds)).ToList();
        // The middle two thirds of the chip's height: the bar should run nearly the icon's height, so every one of
        // these has to be lit in the column that carries it.
        var top = (int)Math.Ceiling(At(chip, 0, 4).Y);
        var bottom = (int)Math.Floor(At(chip, 0, chip.ActualHeight - 4).Y);
        var rows = Enumerable.Range(top, bottom - top).ToList();
        output.WriteLine($"art ends {artEnds:0.##}, face starts {faceStarts:0.##}, gap columns [{string.Join(",", gap)}], rows {top}..{bottom}");

        // The bar's ROOT, not the window: the window carries the bar's idle fade (it is never under the pointer here),
        // which dims everything alike and says nothing about the divider. The root sits at the window's origin and is
        // drawn with the scale above the rows, so the window's coordinates above are the bitmap's.
        var root = (FrameworkElement)window.Content;
        Assert.Equal(new Point(0, 0), root.TransformToAncestor(window).Transform(new Point(0, 0)));
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);

        int Brightness(int x, int y)
        {
            var pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
            return (pixel[0] + pixel[1] + pixel[2]) / 3;
        }

        // A picture of the neighbourhood, one hex digit of brightness per pixel, so a failure shows where the light is.
        var left = Math.Max(0, gap[0] - 22);
        Enumerable.Range(Math.Max(0, top - 8), rows.Count + 16).Where(y => y < bitmap.PixelHeight).ToList().ForEach(y =>
            output.WriteLine($"y={y,4} " + string.Concat(Enumerable.Range(left, 44).Select(x => (Brightness(x, y) / 16).ToString("X")))));
        output.WriteLine($"(picture starts at x={left}; gap is x={gap[0]}..{gap[^1]})");
        gap.ForEach(x => output.WriteLine($"x={x} " + string.Join(" ", rows.Select(y => Brightness(x, y).ToString("X2")))));

        // A column in the gap lit bright (well above the bar's dark tint, a faded chip outline or a focused icon's
        // glow) down the whole span.
        var lit = gap.Where(x => rows.All(y => Brightness(x, y) > 0xA0)).ToList();
        Assert.True(lit.Count is >= 1 and <= 2, $"columns lit down the gap: [{string.Join(",", lit)}] of [{string.Join(",", gap)}]");

        // ...and standing apart from the window's icon: the column before it is dark, or the bar reads as the icon's
        // own frame (the unsnapped 1 DIP line lit two columns at partial strength, one of them against the artwork).
        var before = lit[0] - 1;
        Assert.True(rows.All(y => Brightness(before, y) * 2 < Brightness(lit[0], y)),
            $"column {before} should be dark beside the bar at {lit[0]}");
    });
}
