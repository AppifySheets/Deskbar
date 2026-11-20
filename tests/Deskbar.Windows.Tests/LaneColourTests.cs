using Deskbar.App;
using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;

namespace Deskbar.Windows.Tests;

// The name a list shows a workspace by (LaneColour.FullName). A group is only a name, so a member is shown
// as "group › member", except when the member is named like its group, where saying it twice is noise.
public class LaneColourTests
{
    static (AppState State, Workspace Member) StateWith(string groupName, string memberName)
    {
        var group = new Group(Guid.NewGuid(), groupName);
        var member = new Workspace(Guid.NewGuid(), memberName, Guid.NewGuid()) { GroupId = group.Id };
        return (AppState.Empty with { Workspaces = [member], Groups = [group] }, member);
    }

    // The case a group made from one workspace creates: both carry the name the workspace had.
    [Fact]
    public void A_member_named_like_its_group_shows_its_bare_name()
    {
        var (state, member) = StateWith("Sparrow", "Sparrow");
        Assert.Equal("Sparrow", LaneColour.FullName(state, member));
    }

    [Fact]
    public void A_member_shows_its_group_first()
    {
        var (state, member) = StateWith("Sparrow", "dice-rolls");
        Assert.Equal("Sparrow › dice-rolls", LaneColour.FullName(state, member));
    }

    [Fact]
    public void An_ungrouped_workspace_shows_its_bare_name()
    {
        var loose = new Workspace(Guid.NewGuid(), "Loose", Guid.NewGuid());
        var state = AppState.Empty with { Workspaces = [loose] };
        Assert.Equal("Loose", LaneColour.FullName(state, loose));
    }
}
