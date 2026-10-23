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
    // The row's Grid for a label, top-level or inside a group's family box: the tagged Grid nearest the label.
    static Grid RowFor(Panel rows, string label) =>
        Descendants(rows).OfType<TextBlock>().Where(t => t.Text == label)
            .Select(t => Ancestors(t).OfType<Grid>().First(g => g.Tag is not null && VisualTreeHelper.GetParent(g) is Border))
            .Distinct()
            .Single();

    static IEnumerable<DependencyObject> Ancestors(DependencyObject node)
    {
        for (var at = VisualTreeHelper.GetParent(node); at is not null; at = VisualTreeHelper.GetParent(at))
            yield return at;
    }

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

    // Petre: "should also work for grouped bars". A group's members sit inside its family box, one level down
    // from the bar's rows, and the strip beside a member names that MEMBER: hover, click and pointer alike.
    [Fact]
    public void The_strip_beside_a_group_member_switches_to_that_member() => StaThread.Run(() =>
    {
        var harness = Harness.Build(grouped: true);
        using var bar = harness.ShowBar();
        var archiveDesktop = harness.Desktops.Desktops.Single(d => d.Name == "Archive").Id;
        var archive = RowFor(bar.Rows, "Archive");
        var beside = BesideOnTheLeft(bar.Bar, archive);
        output.WriteLine($"beside grouped Archive at {beside}");

        Assert.Same(archive, bar.Bar.EdgeRowAt(beside, "left"));
        bar.Bar.EdgePress(beside, "left");
        Assert.True(bar.Bar.EdgeRelease(beside, "left"));
        Assert.Equal([archiveDesktop], harness.Desktops.Switched);
    });

    // Petre: "change bar activation pointer", "show something else; pointer for over-the-icon". The strip shows a
    // hand, where every icon shows the arrow; beside the current row, which goes nowhere, it stays the arrow; and
    // floating, the same strip is the resize grip.
    [Fact]
    public void The_strip_shows_a_hand_except_beside_the_current_row() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = harness.ShowBar();
        var besideSparrow = BesideOnTheLeft(bar.Bar, RowFor(bar.Rows, "Sparrow"));
        var besideGepha = BesideOnTheLeft(bar.Bar, RowFor(bar.Rows, "GEPHA")); // the current workspace

        Assert.Equal(System.Windows.Input.Cursors.Hand, bar.Bar.CursorAt(besideSparrow, "left"));
        Assert.Equal(System.Windows.Input.Cursors.Arrow, bar.Bar.CursorAt(besideGepha, "left"));
        // Docked right or floating, the left edge is the resize grip, and the strip there says so.
        Assert.Equal(System.Windows.Input.Cursors.SizeWE, bar.Bar.CursorAt(besideSparrow, "right"));
        Assert.Equal(System.Windows.Input.Cursors.SizeWE, bar.Bar.CursorAt(besideSparrow, null));

        // And the current row's strip takes no click either.
        bar.Bar.EdgePress(besideGepha, "left");
        Assert.False(bar.Bar.EdgeRelease(besideGepha, "left"));
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
