using CSharpFunctionalExtensions;
using Deskbar.Core.Abstractions;
using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;

namespace Deskbar.Core.Tests;

// Where a workspace goes when it is inserted beside a row, and how the bar's move up, move down,
// move to top and move to end behave inside a group's box (#74, #85). Group membership is
// app-level metadata over flat OS desktops, so all of it is decidable here without a desktop shell.
public class GroupOrderTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();

    (WorkspaceManager manager, Guid first, Guid second, Guid other) Started()
    {
        var first = new Workspace(Guid.NewGuid(), "Project", Guid.NewGuid());
        var second = new Workspace(Guid.NewGuid(), "Project docs", Guid.NewGuid());
        var other = new Workspace(Guid.NewGuid(), "Personal", Guid.NewGuid());
        new[] { first, second, other }.ToList()
            .ForEach(w => desktops.Desktops.Add(new DesktopInfo(w.DesktopId!.Value, w.Name)));
        store.Stored = AppState.Empty with { Workspaces = [first, second, other] };

        var manager = new WorkspaceManager(desktops, monitor, titles, store);
        Assert.True(manager.Start().IsSuccess);
        return (manager, first.Id, second.Id, other.Id);
    }

    [Fact]
    public void A_workspace_starts_at_the_top_level() =>
        Assert.All(Started().manager.State.Workspaces, w => Assert.Null(w.GroupId));

    // #74. Petre: "before/after is relative to the row it was invoked on, at that row's depth."
    //
    // Insert-before/after on a row inside a group used to make an ungrouped workspace, which then
    // rendered inside the group's box: a stranger drawn among its members.
    //
    // The third argument is the GROUP to join. "Same depth as this row" is "same group as this
    // row", which is one answer for every row rather than a lookup only some rows could answer.
    [Fact]
    public void Inserting_beside_a_grouped_row_creates_a_member_of_the_same_group()
    {
        var (manager, first, second, _) = Started();
        var group = manager.CreateGroup("Clients", first).Value.Id;
        Assert.True(manager.MoveIntoGroup(second, group).IsSuccess);
        var at = manager.State.Workspaces.ToList().FindIndex(w => w.Id == second);

        var created = manager.InsertWorkspace("Project notes", at + 1, group);

        Assert.True(created.IsSuccess);
        Assert.Equal(group, created.Value.GroupId);
        // Beside the row it was invoked on, not appended after every member the group has.
        Assert.Equal(at + 1, manager.State.Workspaces.ToList().FindIndex(w => w.Id == created.Value.Id));
    }

    // The other half of "at that row's depth": beside a row that is in no group, the new workspace
    // is in no group either.
    [Fact]
    public void Inserting_beside_a_top_level_row_still_creates_a_top_level_workspace()
    {
        var (manager, first, _, _) = Started();
        var at = manager.State.Workspaces.ToList().FindIndex(w => w.Id == first);

        var created = manager.InsertWorkspace("Elsewhere", at, null);

        Assert.True(created.IsSuccess);
        Assert.Null(created.Value.GroupId);
    }

    // The UI only offers groups that exist, but a UI is not a guarantee: a rebuild between opening
    // the menu and choosing from it can dissolve the group that was on screen.
    [Fact]
    public void Inserting_into_a_group_that_does_not_exist_is_refused() =>
        Assert.True(Started().manager.InsertWorkspace("Homeless", 0, Guid.NewGuid()).IsFailure);

    // --- moving inside a group (#85) -----------------------------------------------------------
    //
    // Petre: "move up / move down on a workspace inside a group doesn't work."
    //
    // The moves predate grouping and shifted a workspace one place in the FLAT list. Since the bar
    // draws a whole group as one box, a move that only swapped list neighbours across the box edge
    // changed nothing visible. Every member is movable inside its box, the first one included.

    // A group of four, in list order, plus one ungrouped workspace after it so "did this disturb
    // anything else" can be asked.
    (WorkspaceManager manager, Guid[] members, Guid outsider) Family()
    {
        var (manager, first, second, other) = Started();
        var group = manager.CreateGroup("Clients", first).Value.Id;
        Assert.True(manager.MoveIntoGroup(second, group).IsSuccess);
        var third = manager.AddWorkspaceToGroup("Second", group);
        var fourth = manager.AddWorkspaceToGroup("Third", group);
        Assert.True(third.IsSuccess && fourth.IsSuccess);
        return (manager, [first, second, third.Value.Id, fourth.Value.Id], other);
    }

    // The group's members in the order the box draws them.
    static IReadOnlyList<string> MemberNames(WorkspaceManager manager, Guid member) =>
        manager.State.MembersOf(manager.State.GroupOf(member)!.Id).Select(w => w.Name).ToList();

    static IReadOnlyList<string> ListOrder(WorkspaceManager manager) =>
        manager.State.Workspaces.Select(w => w.Name).ToList();

    [Fact]
    public void A_member_moves_up_among_its_peers()
    {
        var (manager, members, _) = Family();
        Assert.Equal(["Project", "Project docs", "Second", "Third"], MemberNames(manager, members[0]));

        Assert.True(manager.MoveWorkspace(members[2], -1).IsSuccess);

        Assert.Equal(["Project", "Second", "Project docs", "Third"], MemberNames(manager, members[0]));
    }

    [Fact]
    public void A_member_moves_down_among_its_peers()
    {
        var (manager, members, _) = Family();

        Assert.True(manager.MoveWorkspace(members[1], +1).IsSuccess);

        Assert.Equal(["Project", "Second", "Project docs", "Third"], MemberNames(manager, members[0]));
    }

    // Up on the first member has nowhere to go inside the box. It must neither fail nor pull the
    // member out across the box's edge.
    [Fact]
    public void Up_on_the_first_member_does_nothing()
    {
        var (manager, members, _) = Family();
        var before = ListOrder(manager);

        Assert.True(manager.MoveWorkspace(members[0], -1).IsSuccess);

        Assert.Equal(before, ListOrder(manager));
    }

    [Fact]
    public void Down_on_the_last_member_does_nothing()
    {
        var (manager, members, _) = Family();
        var before = ListOrder(manager);

        Assert.True(manager.MoveWorkspace(members[3], +1).IsSuccess);

        Assert.Equal(before, ListOrder(manager));
    }

    // Top and end mean top and end OF THE GROUP, not of the bar.
    [Fact]
    public void A_member_moves_to_the_top_of_its_group()
    {
        var (manager, members, _) = Family();

        Assert.True(manager.MoveWorkspaceTo(members[3], 0).IsSuccess);

        Assert.Equal(["Third", "Project", "Project docs", "Second"], MemberNames(manager, members[0]));
        Assert.Equal(members[3], manager.State.Workspaces.First().Id);
    }

    [Fact]
    public void A_member_moves_to_the_end_of_its_group()
    {
        var (manager, members, _) = Family();

        // The bar passes the whole list's length for "end"; it clamps to the last peer.
        Assert.True(manager.MoveWorkspaceTo(members[0], manager.State.Workspaces.Count - 1).IsSuccess);

        Assert.Equal(["Project docs", "Second", "Third", "Project"], MemberNames(manager, members[0]));
    }

    // Lane colours follow list position, so a move that dragged unrelated workspaces along would
    // recolour rows nobody touched. Reordering only inside the box is what prevents that.
    [Fact]
    public void Moving_a_member_leaves_every_other_workspace_where_it_was()
    {
        var (manager, members, outsider) = Family();
        var outsiderAt = manager.State.Workspaces.ToList().FindIndex(w => w.Id == outsider);

        Assert.True(manager.MoveWorkspaceTo(members[2], 0).IsSuccess);

        Assert.Equal(outsiderAt, manager.State.Workspaces.ToList().FindIndex(w => w.Id == outsider));
    }

    // The other half: an ungrouped workspace moves among the bar's other top-level rows, stepping
    // over a whole group rather than swapping with one of its members.
    [Fact]
    public void An_ungrouped_workspace_moves_past_a_whole_group()
    {
        var (manager, _, outsider) = Family();

        Assert.True(manager.MoveWorkspace(outsider, -1).IsSuccess);

        // The group travelled intact and stayed in its own order; only the two top-level rows
        // traded places.
        Assert.Equal(["Personal", "Project", "Project docs", "Second", "Third"], ListOrder(manager));
    }

    // Joining moves the workspace in the LIST too, to the bottom of its new box. Membership alone
    // would leave it where it sat, and since the box draws its members in list order, a workspace
    // joining from a row above the group would appear in the middle of it.
    [Fact]
    public void Joining_a_group_from_above_it_lands_at_the_bottom_of_the_box()
    {
        var (manager, first, second, _) = Started();
        var group = manager.CreateGroup("Clients", second).Value.Id;

        Assert.True(manager.MoveIntoGroup(first, group).IsSuccess);

        Assert.Equal(["Project docs", "Project"], manager.State.MembersOf(group).Select(w => w.Name).ToList());
        Assert.Equal(["Project docs", "Project", "Personal"], ListOrder(manager));
    }

    // A crash while a window was borrowed by a child workspace (a feature that no longer exists)
    // leaves it pinned to every desktop. state.json from that version still lists the pin, so the
    // next start undoes it: unpin, and put the window back on the desktop it came from.
    [Fact]
    public void A_borrow_left_by_a_crash_is_still_repaired_at_startup()
    {
        var (_, first, _, _) = Started();
        var home = store.Stored.Workspaces.Single(w => w.Id == first).DesktopId!.Value;
        var window = new WindowHandle(0xA);
        desktops.PinnedWindows.Add(window);
        store.Stored = store.Stored with { InheritedPins = [new InheritedPin(0xA, home)] };

        var restarted = new WorkspaceManager(desktops, monitor, titles, store);
        Assert.True(restarted.Start().IsSuccess);

        Assert.DoesNotContain(window, desktops.PinnedWindows);
        Assert.Equal(home, desktops.WindowPlacements[window]);
        Assert.Empty(restarted.State.InheritedPins);
    }
}
