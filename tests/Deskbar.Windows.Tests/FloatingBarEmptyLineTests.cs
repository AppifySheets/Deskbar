using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Xunit.Abstractions;

namespace Deskbar.Windows.Tests;

// Petre, with an expanded bar's empty lines under the pointer and dark: "i want the empty area of the active bar
// to also be highlighted", "activate the empty part of the bar". On a bar with no screen lanes (one screen, or
// none known, as in this harness), an empty line used to hold nothing but the drop target. Now it is one empty
// lane across the line: free space that lights on hover, as an empty half does on a two-screen bar.
public class FloatingBarEmptyLineTests(ITestOutputHelper output)
{
    static IEnumerable<DependencyObject> Descendants(DependencyObject node) =>
        Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(node))
            .Select(i => VisualTreeHelper.GetChild(node, i))
            .SelectMany(child => new[] { child }.Concat(Descendants(child)));

    [Fact]
    public void The_empty_lines_of_the_current_expanded_bar_light_on_hover() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = harness.ShowBar();
        // GEPHA is the current workspace: expanded, one window, and the list's floor of empty lines below it.
        var gepha = bar.Rows.Children.OfType<Border>().Select(b => b.Child).OfType<Grid>()
            .Single(g => Descendants(g).OfType<TextBlock>().Any(t => t.Text == "GEPHA"));
        List<Border> Lit() => Descendants(gepha).OfType<Border>()
            .Where(b => b.Child is null && b.Background is SolidColorBrush { Color: { R: 255, G: 255, B: 255, A: > 0 and < 0x40 } })
            .ToList();

        Assert.Empty(Lit());
        gepha.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseEnterEvent });
        var lit = Lit();
        lit.ForEach(b => output.WriteLine($"lit {b.ActualWidth:0}x{b.ActualHeight:0}"));

        // Every empty line lights across the whole line, not only the window line's right third.
        var fullWidth = lit.Max(b => b.ActualWidth);
        Assert.True(lit.Count(b => b.ActualWidth >= fullWidth - 1) >= 4, "each empty line should light across its width");
    });
}
