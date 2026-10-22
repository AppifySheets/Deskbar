using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Deskbar.App;

namespace Deskbar.Windows.Tests;

// Start and search on both halves of a bar docked across two screens. Petre: "start and search buttons on both
// sides of the bar, if docked in between screens". The layout is driven directly rather than by docking, so the
// suite never registers a real app bar on the machine running it.
public class FloatingBarShellTwinTests
{
    static IEnumerable<DependencyObject> Descendants(DependencyObject node) =>
        LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()
            .SelectMany(child => new[] { child }.Concat(Descendants(child)));

    static IReadOnlyList<Button> Named(FloatingBar bar, string name) =>
        Descendants(bar).OfType<Button>().Where(b => AutomationProperties.GetName(b) == name).ToList();

    [Fact]
    public void A_single_screen_bar_has_one_start_and_one_search() => StaThread.Run(() =>
    {
        using var bar = Harness.Build().ShowBar();

        bar.Bar.LayOutHeader(acrossSeam: false);
        bar.Bar.UpdateLayout();

        Assert.Single(Named(bar.Bar, "Start"), b => b.IsVisible);
        Assert.Single(Named(bar.Bar, "Search"), b => b.IsVisible);
    });

    [Fact]
    public void Across_the_seam_each_half_has_its_own_start_and_search() => StaThread.Run(() =>
    {
        using var bar = Harness.Build().ShowBar();

        bar.Bar.LayOutHeader(acrossSeam: true);
        bar.Bar.UpdateLayout();

        var starts = Named(bar.Bar, "Start").Where(b => b.IsVisible).ToList();
        Assert.Equal(2, starts.Count);
        Assert.Equal(2, Named(bar.Bar, "Search").Count(b => b.IsVisible));
        // One in each half: the second begins at or past the middle of the bar's content.
        var root = (FrameworkElement)bar.Bar.Content;
        var xs = starts.Select(b => b.TranslatePoint(new Point(0, 0), root).X).OrderBy(x => x).ToList();
        Assert.True(xs[1] >= root.ActualWidth / 2 - 8, $"the second Start is at {xs[1]:F0} of {root.ActualWidth:F0}");
    });

    // The second pair is the same buttons in effect: it sends the same shortcut.
    [Fact]
    public void The_second_start_opens_start() => StaThread.Run(() =>
    {
        var sent = new List<string>();
        var harness = Harness.Build();
        var bar = new FloatingBar(harness.Manager) { Left = -32000, Top = -32000, SendShellKeys = f => sent.Add(f.ToString()) };
        bar.Show();
        bar.LayOutHeader(acrossSeam: true);
        bar.UpdateLayout();

        var second = Named(bar, "Start").Where(b => b.IsVisible).OrderBy(b => b.TranslatePoint(new Point(0, 0), (UIElement)bar.Content).X).Last();
        second.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        Assert.Equal("Start", sent.First());
        bar.Close();
    });
}
