using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace TaskSpaces.Windows.Tests;

// Folded rows and folded groups on the bar. Petre: "i want to minimize it, to a single line, so i don't see any
// icons in it", "i should be able to minimize the entire group". From the mockup he picked: a folded row is its
// name, a window count and the attention dot; a folded group is one line with every member's name still a
// click; and the workspace you are on shows its icons whatever was folded.
//
// The harness: GEPHA (current, holding "Remote Desktop Manager"), Sparrow ("Beeper") and Archive ("Archive").
public class FloatingBarFoldTests
{
    static IEnumerable<DependencyObject> Descendants(DependencyObject node) =>
        LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()
            .SelectMany(child => new[] { child }.Concat(Descendants(child)));

    static IReadOnlyList<string> Texts(Panel rows) =>
        Descendants(rows).OfType<TextBlock>().Where(t => t.Visibility == Visibility.Visible).Select(t => t.Text).ToList();

    // The window icons on the bar, by the title each is named after. The literal matches FloatingBar's IconTag.
    static IReadOnlyList<string> IconTitles(Panel rows) =>
        Descendants(rows).OfType<Button>().Where(b => b.Tag is "icon")
            .Select(System.Windows.Automation.AutomationProperties.GetName).ToList();

    // A folded group's member names are Buttons whose content is the name's TextBlock.
    static Button NameButton(Panel rows, string name) =>
        Descendants(rows).OfType<Button>().Single(b => b.Content is TextBlock { Text: var text } && text == name);

    static Guid Id(Harness harness, string name) => harness.Manager.State.Workspaces.Single(w => w.Name == name).Id;

    [Fact]
    public void A_folded_row_shows_its_name_and_count_and_no_icons() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        Assert.True(harness.Manager.SetWorkspaceFolded(Id(harness, "Sparrow"), true).IsSuccess);
        using var bar = harness.ShowBar();

        var texts = Texts(bar.Rows);
        Assert.Contains("Sparrow", texts);
        Assert.Contains(" · 1", texts);
        Assert.DoesNotContain(harness.Second.Title, IconTitles(bar.Rows));
        // Nothing else is touched: the row you are on still has its icon.
        Assert.Contains(harness.First.Title, IconTitles(bar.Rows));
    });

    // You fold a workspace because you are not working in it; once you are on it, you are.
    [Fact]
    public void The_workspace_you_are_on_shows_its_icons_even_when_folded() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        Assert.True(harness.Manager.SetWorkspaceFolded(Id(harness, "GEPHA"), true).IsSuccess);
        using var bar = harness.ShowBar();

        Assert.Contains(harness.First.Title, IconTitles(bar.Rows));
        Assert.DoesNotContain(" · 1", Texts(bar.Rows));
    });

    [Fact]
    public void A_folded_group_is_one_line_naming_every_member() => StaThread.Run(() =>
    {
        var harness = Harness.Build(extraWorkspace: true);
        var group = harness.Manager.CreateGroup("Clients", Id(harness, "Sparrow")).Value.Id;
        Assert.True(harness.Manager.MoveIntoGroup(Id(harness, "Archive"), group).IsSuccess);
        Assert.True(harness.Manager.SetGroupFolded(group, true).IsSuccess);
        using var bar = harness.ShowBar();

        var texts = Texts(bar.Rows);
        Assert.Contains("CLIENTS ▸ ", texts);
        Assert.Contains(" · 2", texts);
        Assert.NotNull(NameButton(bar.Rows, "Sparrow"));
        Assert.NotNull(NameButton(bar.Rows, "Archive"));
        Assert.DoesNotContain(harness.Second.Title, IconTitles(bar.Rows));
        Assert.DoesNotContain("Archive", IconTitles(bar.Rows));
    });

    // Every member stays one click away: the name on the folded line switches, as a row's caption does.
    [Fact]
    public void Clicking_a_name_on_a_folded_group_switches_there() => StaThread.Run(() =>
    {
        var harness = Harness.Build(extraWorkspace: true);
        var group = harness.Manager.CreateGroup("Clients", Id(harness, "Sparrow")).Value.Id;
        Assert.True(harness.Manager.MoveIntoGroup(Id(harness, "Archive"), group).IsSuccess);
        Assert.True(harness.Manager.SetGroupFolded(group, true).IsSuccess);
        using var bar = harness.ShowBar();

        NameButton(bar.Rows, "Archive").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        var archiveDesktop = harness.Manager.State.Workspaces.Single(w => w.Name == "Archive").DesktopId!.Value;
        Assert.Equal([archiveDesktop], harness.Desktops.Switched);
    });

    // The member you are on keeps its full row under the folded line, icons and all; the others stay hidden.
    [Fact]
    public void A_folded_group_still_draws_the_member_you_are_on() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        var group = harness.Manager.CreateGroup("Clients", Id(harness, "GEPHA")).Value.Id;
        Assert.True(harness.Manager.MoveIntoGroup(Id(harness, "Sparrow"), group).IsSuccess);
        Assert.True(harness.Manager.SetGroupFolded(group, true).IsSuccess);
        using var bar = harness.ShowBar();

        Assert.Contains("CLIENTS ▸ ", Texts(bar.Rows));
        Assert.Contains(harness.First.Title, IconTitles(bar.Rows));
        Assert.DoesNotContain(harness.Second.Title, IconTitles(bar.Rows));
    });
}
