using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Deskbar.App;
using Xunit.Abstractions;

namespace Deskbar.Windows.Tests;

// Petre, docked left: "can you activate the workspace when i'm on the left edge of the screen?". Docked, the
// bar's screen-side edge is the screen's edge, and the strip between it and a row's box used to be the bar's
// padding: a click there did nothing. Now the strip beside a row counts as the row: hovering it lights the
// row, and a press and release there switch to its workspace. The edge is passed in, so these run without a
// real dock.
public class FloatingBarDockedEdgeTests(ITestOutputHelper output)
{
    static Grid RowFor(Panel rows, string label) =>
        rows.Children.OfType<Border>().Select(b => b.Child).OfType<Grid>()
            .Single(g => Descendants(g).OfType<TextBlock>().Any(t => t.Text == label));

    static IEnumerable<DependencyObject> Descendants(DependencyObject node) =>
        Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(node))
            .Select(i => VisualTreeHelper.GetChild(node, i))
            .SelectMany(child => new[] { child }.Concat(Descendants(child)));

    // A point in the strip beside a row: halfway between the window's left edge and the row's box, at the
    // row's vertical middle.
    static Point BesideOnTheLeft(FloatingBar bar, Grid row)
    {
        var box = (FrameworkElement)VisualTreeHelper.GetParent(row);
        var bounds = box.TransformToAncestor(bar).TransformBounds(new Rect(box.RenderSize));
        Assert.True(bounds.Left > 1, $"the row's box should start inside the bar, not at its edge: {bounds}");
        return new Point(bounds.Left / 2, bounds.Top + bounds.Height / 2);
    }

    [Fact]
    public void The_strip_beside_a_row_names_that_row_only_on_the_docked_side() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = harness.ShowBar();
        var sparrow = RowFor(bar.Rows, "Sparrow");
        var beside = BesideOnTheLeft(bar.Bar, sparrow);
        output.WriteLine($"beside Sparrow at {beside}");

        Assert.Same(sparrow, bar.Bar.EdgeRowAt(beside, "left"));
        Assert.Null(bar.Bar.EdgeRowAt(beside, "right"));
        Assert.Null(bar.Bar.EdgeRowAt(beside, "seam"));
        Assert.Null(bar.Bar.EdgeRowAt(beside, null));
        // Inside the row is not the strip: the row's own handlers take it from there.
        Assert.Null(bar.Bar.EdgeRowAt(new Point(beside.X + 20, beside.Y), "left"));
    });

    [Fact]
    public void A_press_and_release_in_the_strip_switch_to_the_row_beside_it() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = harness.ShowBar();
        var sparrowDesktop = harness.Desktops.Desktops.Single(d => d.Name == "Sparrow").Id;
        var beside = BesideOnTheLeft(bar.Bar, RowFor(bar.Rows, "Sparrow"));
        Assert.Empty(harness.Desktops.Switched);

        bar.Bar.EdgePress(beside, "left");
        Assert.True(bar.Bar.EdgeRelease(beside, "left"));
        Assert.Equal([sparrowDesktop], harness.Desktops.Switched);

        // Floating, the same strip is the resize grip and nothing else.
        bar.Bar.EdgePress(beside, null);
        Assert.False(bar.Bar.EdgeRelease(beside, null));
        Assert.Equal([sparrowDesktop], harness.Desktops.Switched);
    });

    // The press and the release must be beside the SAME row: a press beside one and a release beside another
    // is not a click on either.
    [Fact]
    public void A_release_beside_a_different_row_than_the_press_switches_nothing() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = harness.ShowBar();
        var besideSparrow = BesideOnTheLeft(bar.Bar, RowFor(bar.Rows, "Sparrow"));
        var besideGepha = BesideOnTheLeft(bar.Bar, RowFor(bar.Rows, "GEPHA"));

        bar.Bar.EdgePress(besideGepha, "left");
        Assert.False(bar.Bar.EdgeRelease(besideSparrow, "left"));
        Assert.Empty(harness.Desktops.Switched);
    });

    // Hovering the strip lights the row as hovering the row does, through the row's own enter and leave, and
    // moving off the strip puts it out.
    [Fact]
    public void Hovering_the_strip_lights_the_row_beside_it_and_leaving_puts_it_out() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = harness.ShowBar();
        var sparrow = RowFor(bar.Rows, "Sparrow");
        var label = Descendants(sparrow).OfType<TextBlock>().First(t => t.Text == "Sparrow");
        var resting = label.Foreground;
        var beside = BesideOnTheLeft(bar.Bar, sparrow);

        bar.Bar.EdgeHover(beside, "left");
        Assert.NotSame(resting, label.Foreground);

        bar.Bar.EdgeHover(new Point(-1, -1), "left");
        Assert.Same(resting, label.Foreground);
    });
}
