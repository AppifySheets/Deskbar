using Deskbar.Core.QuickFind;

namespace Deskbar.Core.Tests.QuickFind;

// Layout B, Petre's pick from three mockups: results sit under a header per workspace, in the workspace's
// colour, like the bar's rows. The ranking still decides the order: groups appear in the order their best
// result ranked, and rows keep their ranked order inside a group.
public class FindGroupingTests
{
    static readonly Guid Accounting = Guid.NewGuid(), Home = Guid.NewGuid();

    static FindResult Row(string key, Guid? workspace, string label, FindKind kind = FindKind.Window) =>
        new(key, kind, key, new FindPlace(workspace, label), key);

    [Fact]
    public void Groups_follow_the_rank_of_their_best_result()
    {
        var groups = FindGrouping.Group([Row("a1", Accounting, "Accounting"), Row("h1", Home, "Home"), Row("a2", Accounting, "Accounting")]);
        Assert.Equal(["Accounting", "Home"], groups.Select(g => g.Header));
        Assert.Equal(["a1", "a2"], groups[0].Results.Select(r => r.Key));
    }

    [Fact]
    public void Places_without_a_workspace_group_by_their_label()
    {
        var groups = FindGrouping.Group([Row("d1", null, "Desktop 1"), Row("p1", null, "all desktops"), Row("d2", null, "Desktop 1")]);
        Assert.Equal(["Desktop 1", "all desktops"], groups.Select(g => g.Header));
    }

    [Fact]
    public void What_would_start_gets_its_own_group_saying_so()
    {
        var groups = FindGrouping.Group([Row("w", Accounting, "Accounting"), Row("r", Accounting, "Accounting", FindKind.Remembered)]);
        Assert.Equal(["Accounting", "Accounting, would start here"], groups.Select(g => g.Header));
        Assert.True(groups[0].Open);
        Assert.False(groups[1].Open);
    }

    [Fact]
    public void Rows_in_display_order_put_the_top_result_first() =>
        Assert.Equal(["a1", "a2", "h1"], FindGrouping.Group([Row("a1", Accounting, "Accounting"), Row("h1", Home, "Home"), Row("a2", Accounting, "Accounting")])
            .SelectMany(g => g.Results).Select(r => r.Key));
}
