using System.Windows;
using System.Windows.Controls;
using TaskSpaces.App;

namespace TaskSpaces.Windows.Tests;

// Today's time beside each workspace's name. Petre: "i want it next to workspace titles". The number is the
// Time tab's "today" column, the same TimeTracker reading, so the two can never disagree.
public class FloatingBarWorkspaceTimeTests
{
    // Under a minute says nothing: "0 min" on every row you have not visited would be noise.
    [Theory]
    [InlineData(0, null)]
    [InlineData(59, null)]
    [InlineData(60, "1 min")]
    [InlineData(45 * 60 + 30, "45 min")]
    [InlineData(60 * 60, "1 h")]
    [InlineData(131 * 60, "2 h 11 min")]
    [InlineData(10 * 3600 + 5 * 60, "10 h 5 min")]
    public void Time_reads_in_hours_and_minutes(int seconds, string? expected) =>
        Assert.Equal(expected, FloatingBar.TimeMark(TimeSpan.FromSeconds(seconds)));

    static IEnumerable<DependencyObject> Descendants(DependencyObject node) =>
        LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()
            .SelectMany(child => new[] { child }.Concat(Descendants(child)));

    static IReadOnlyList<string> Texts(Panel rows) =>
        Descendants(rows).OfType<TextBlock>().Where(t => t.Visibility == Visibility.Visible).Select(t => t.Text).ToList();

    // The bar shows it on the workspace's own caption line, and the name's own Text stays the bare name, which
    // is what screen readers and everything that finds a row by name read.
    [Fact]
    public void A_workspace_with_time_today_shows_it_beside_its_name() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        var sparrow = harness.Manager.State.Workspaces.Single(w => w.Name == "Sparrow").Id;
        using var bar = harness.ShowBar(timeToday: id => id == sparrow ? TimeSpan.FromMinutes(131) : TimeSpan.Zero);

        var texts = Texts(bar.Rows);
        Assert.Contains("Sparrow", texts);
        Assert.Contains(" · 2 h 11 min", texts);
        Assert.DoesNotContain(texts, t => t.Contains("GEPHA") && t.Contains("min"));
    });

    // An empty workspace you have used today shows its time and not "empty". Petre, on "Mess… · empty · 1 min":
    // "empty is extra there". The time already says the row is a workspace you work in, and two marks pushed the
    // name into an ellipsis. One you have not used today keeps "· empty". And the swap follows the minute
    // refresh, so a row that gains its first minute drops "empty" without waiting for a rebuild.
    [Fact]
    public void Time_replaces_the_empty_mark() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        // A workspace with no windows, so its row carries "· empty".
        var messaging = harness.Manager.AddWorkspace("Messaging").Value.Id;
        var minutes = 0;
        using var bar = harness.ShowBar(timeToday: id => id == messaging ? TimeSpan.FromMinutes(minutes) : TimeSpan.Zero);

        Assert.Contains(" · empty", Texts(bar.Rows));

        minutes = 4;
        bar.Bar.RefreshWorkspaceTimes();

        var texts = Texts(bar.Rows);
        Assert.Contains(" · 4 min", texts);
        Assert.DoesNotContain(" · empty", texts);
    });

    // The clock runs while nothing on the bar changes, so the number is refreshed in place, without a rebuild.
    [Fact]
    public void The_time_is_refreshed_in_place() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        var sparrow = harness.Manager.State.Workspaces.Single(w => w.Name == "Sparrow").Id;
        var minutes = 5;
        using var bar = harness.ShowBar(timeToday: id => id == sparrow ? TimeSpan.FromMinutes(minutes) : TimeSpan.Zero);

        minutes = 6;
        bar.Bar.RefreshWorkspaceTimes();

        Assert.Contains(" · 6 min", Texts(bar.Rows));
    });
}
