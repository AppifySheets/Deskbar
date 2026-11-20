using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;

namespace Deskbar.Core.Tests;

// Groups replaced ParentId (#84 needs a group with a name and no parent workspace, which ParentId
// cannot express), so every state.json written while #42 shipped has to keep working.
//
// This is the one piece of the change that can lose a user's data if it is wrong, and the failure
// mode is quiet: nesting would simply be gone on the first launch after an upgrade, which looks
// like a rendering bug rather than like data loss.
public class GroupMigrationTests
{
    static Workspace Workspace(string name) => new(Guid.NewGuid(), name, Guid.NewGuid());

    // A parent with two children and one unrelated workspace, as an older file would have stored it.
    static (AppState state, Workspace parent, Workspace first, Workspace second, Workspace loner) Nested()
    {
        var parent = Workspace("Sparrow");
        var first = Workspace("dice-rolls") with { ParentId = parent.Id };
        var second = Workspace("slip39") with { ParentId = parent.Id };
        var loner = Workspace("Personal");
        return (AppState.Empty with { Workspaces = [parent, first, second, loner] }, parent, first, second, loner);
    }

    // Child workspaces are gone: a parent and its children load as one plain named group, with the
    // parent listed first because that is where it was drawn.
    [Fact]
    public void A_parent_becomes_one_named_group_with_the_parent_first()
    {
        var (state, parent, first, second, _) = Nested();

        var migrated = state.Migrated();

        var group = Assert.Single(migrated.Groups);
        Assert.Null(group.AnchorWorkspaceId);
        Assert.Equal("Sparrow", group.Name);
        Assert.Equal([parent.Id, first.Id, second.Id], migrated.MembersOf(group.Id).Select(w => w.Id));
    }

    // A state.json an anchored build saved: the anchor goes, everything else about the group stays.
    [Fact]
    public void An_anchored_group_loses_its_anchor_and_keeps_name_members_and_colour()
    {
        var a = Workspace("a");
        var b = Workspace("b");
        var g = Guid.NewGuid();
        var state = AppState.Empty with
        {
            Workspaces = [a with { GroupId = g }, b with { GroupId = g }],
            Groups = [new Group(g, "Work") { AnchorWorkspaceId = a.Id, Color = "#123456" }],
        };

        var group = Assert.Single(state.Migrated().Groups);

        Assert.Null(group.AnchorWorkspaceId);
        Assert.Equal("Work", group.Name);
        Assert.Equal("#123456", group.Color);
        Assert.Equal([a.Id, b.Id], state.Migrated().MembersOf(g).Select(w => w.Id));
    }

    // The anchor used to be drawn at the head of its box wherever the list kept it. Without anchors
    // the list order IS the drawn order, so the migration writes it down.
    [Fact]
    public void A_child_listed_above_its_parent_keeps_the_drawn_order()
    {
        var loner = Workspace("loner");
        var parent = Workspace("parent");
        var child = Workspace("child");
        var g = Guid.NewGuid();
        var state = AppState.Empty with
        {
            Workspaces = [loner, child with { GroupId = g }, parent with { GroupId = g }],
            Groups = [new Group(g, "Box") { AnchorWorkspaceId = parent.Id }],
        };

        Assert.Equal([loner.Id, parent.Id, child.Id], state.Migrated().Workspaces.Select(w => w.Id));
    }

    // A group with no colour of its own took its anchor's list position. If moving the anchor to the
    // front changes that position, the colour it was showing is written down so it does not shift.
    [Fact]
    public void A_group_whose_slot_moves_keeps_the_colour_it_was_showing()
    {
        var child = Workspace("child");
        var other = Workspace("other");
        var parent = Workspace("parent");
        var g = Guid.NewGuid();
        var state = AppState.Empty with
        {
            Workspaces = [child with { GroupId = g }, other, parent with { GroupId = g }],
            Groups = [new Group(g, "Box") { AnchorWorkspaceId = parent.Id }],
        };

        Assert.Equal(WorkspacePalette.For((string?)null, 2), Assert.Single(state.Migrated().Groups).Color);
    }

    [Fact]
    public void A_group_whose_slot_stays_still_follows_its_position()
    {
        var parent = Workspace("parent");
        var child = Workspace("child");
        var g = Guid.NewGuid();
        var state = AppState.Empty with
        {
            Workspaces = [parent with { GroupId = g }, child with { GroupId = g }],
            Groups = [new Group(g, "Box") { AnchorWorkspaceId = parent.Id }],
        };

        Assert.Null(Assert.Single(state.Migrated().Groups).Color);
    }

    // A hand-edited file can name an anchor that is not there: it is simply dropped.
    [Fact]
    public void An_anchor_that_no_longer_exists_is_simply_dropped()
    {
        var a = Workspace("a");
        var b = Workspace("b");
        var g = Guid.NewGuid();
        var state = AppState.Empty with
        {
            Workspaces = [a with { GroupId = g }, b with { GroupId = g }],
            Groups = [new Group(g, "Box") { AnchorWorkspaceId = Guid.NewGuid() }],
        };

        var migrated = state.Migrated();

        Assert.Null(Assert.Single(migrated.Groups).AnchorWorkspaceId);
        Assert.Equal([a.Id, b.Id], migrated.MembersOf(g).Select(w => w.Id));
    }

    // Built so the first pass does BOTH things it can do, reorder the list and write a colour, which
    // is what a second pass would have to undo or repeat if it were not a no-op.
    [Fact]
    public void Migrating_twice_changes_nothing()
    {
        var child = Workspace("child");
        var other = Workspace("other");
        var parent = Workspace("parent");
        var g = Guid.NewGuid();
        var state = AppState.Empty with
        {
            Workspaces = [child with { GroupId = g }, other, parent with { GroupId = g }],
            Groups = [new Group(g, "Box") { AnchorWorkspaceId = parent.Id }],
        };
        var once = state.Migrated();
        Assert.NotNull(Assert.Single(once.Groups).Color);
        Assert.NotEqual(state.Workspaces.Select(w => w.Id), once.Workspaces.Select(w => w.Id));

        var twice = once.Migrated();

        Assert.Equal(once.Groups, twice.Groups);
        Assert.Equal(once.Workspaces, twice.Workspaces);
    }

    // The group takes the parent's name at migration time, and keeps its own copy afterwards.
    [Fact]
    public void The_group_is_named_after_the_parent()
    {
        var (state, _, _, _, _) = Nested();

        Assert.Equal("Sparrow", Assert.Single(state.Migrated().Groups).Name);
    }

    // The parent joins its own group. Under ParentId it sat outside the relationship and was merely
    // pointed at from below; a group is a set, and the anchor is in it.
    [Fact]
    public void The_parent_and_its_children_all_end_up_in_the_group()
    {
        var (state, parent, first, second, _) = Nested();

        var migrated = state.Migrated();
        var group = Assert.Single(migrated.Groups).Id;

        Assert.All(new[] { parent.Id, first.Id, second.Id },
            id => Assert.Equal(group, migrated.Workspaces.Single(w => w.Id == id).GroupId));
    }

    [Fact]
    public void A_workspace_that_was_never_nested_stays_ungrouped()
    {
        var (state, _, _, _, loner) = Nested();

        Assert.Null(state.Migrated().Workspaces.Single(w => w.Id == loner.Id).GroupId);
    }

    [Fact]
    public void Two_parents_become_two_groups()
    {
        var first = Workspace("Sparrow");
        var second = Workspace("GEPHA");
        var state = AppState.Empty with
        {
            Workspaces =
            [
                first, Workspace("dice-rolls") with { ParentId = first.Id },
                second, Workspace("HRIS") with { ParentId = second.Id },
            ],
        };

        var migrated = state.Migrated();

        Assert.Equal(2, migrated.Groups.Count);
        Assert.Equal(["Sparrow", "GEPHA"], migrated.Groups.Select(g => g.Name));
    }

    // Running it twice must not produce a second set of groups for the same parents, because the
    // manager persists after loading and the next load would migrate again.
    [Fact]
    public void Migrating_twice_changes_nothing_the_second_time()
    {
        var once = Nested().state.Migrated();

        var twice = once.Migrated();

        Assert.Equal(once.Groups, twice.Groups);
        Assert.Equal(once.Workspaces, twice.Workspaces);
    }

    // A colour the user chose is theirs: moving the anchor must not overwrite it with the old slot's.
    [Fact]
    public void A_group_with_its_own_colour_keeps_it_when_its_slot_moves()
    {
        var child = Workspace("child");
        var other = Workspace("other");
        var parent = Workspace("parent");
        var g = Guid.NewGuid();
        var state = AppState.Empty with
        {
            Workspaces = [child with { GroupId = g }, other, parent with { GroupId = g }],
            Groups = [new Group(g, "Box") { AnchorWorkspaceId = parent.Id, Color = "#abcdef" }],
        };

        Assert.Equal("#abcdef", Assert.Single(state.Migrated().Groups).Color);
    }

    // A GroupId that names no group is drawn as ungrouped. Rebuilding the list from the drawn blocks
    // must not lose or duplicate it, whatever else reorders.
    [Fact]
    public void A_workspace_whose_group_is_missing_survives_the_migration()
    {
        var stray = Workspace("stray") with { GroupId = Guid.NewGuid() };
        var child = Workspace("child");
        var parent = Workspace("parent");
        var g = Guid.NewGuid();
        var state = AppState.Empty with
        {
            Workspaces = [stray, child with { GroupId = g }, parent with { GroupId = g }],
            Groups = [new Group(g, "Box") { AnchorWorkspaceId = parent.Id }],
        };

        var migrated = state.Migrated();

        Assert.Equal(3, migrated.Workspaces.Count);
        Assert.Equal(state.Workspaces.Select(w => w.Id).Order(), migrated.Workspaces.Select(w => w.Id).Order());
    }

    [Fact]
    public void A_file_with_no_nesting_is_left_alone()
    {
        var state = AppState.Empty with { Workspaces = [Workspace("Personal"), Workspace("Work")] };

        var migrated = state.Migrated();

        Assert.Empty(migrated.Groups);
        Assert.All(migrated.Workspaces, w => Assert.Null(w.GroupId));
    }

    // A hand-edited state.json can point at a parent that is not there. The children come out
    // ungrouped, which is what the bar already drew for them, rather than joining a group whose
    // anchor cannot be found.
    [Fact]
    public void A_parent_that_does_not_exist_is_dropped_rather_than_anchoring_a_group()
    {
        var orphan = Workspace("dice-rolls") with { ParentId = Guid.NewGuid() };
        var state = AppState.Empty with { Workspaces = [orphan] };

        var migrated = state.Migrated();

        Assert.Empty(migrated.Groups);
        Assert.Null(migrated.Workspaces.Single().GroupId);
    }

    // Nesting no longer exists, so there is no downgrade to keep ParentId for. It is cleared once the
    // groups have been built from it.
    [Fact]
    public void The_old_parent_id_is_cleared_once_groups_are_built()
    {
        var (state, _, _, _, _) = Nested();

        var migrated = state.Migrated();

        Assert.All(migrated.Workspaces, w => Assert.Null(w.ParentId));
    }

    // The ParentId step runs whenever Groups is empty. If ParentId survived, a user who ungrouped
    // everything would get the groups back on the next start, rebuilt from values nothing else
    // keeps up to date.
    [Fact]
    public void Ungrouped_parent_ids_do_not_come_back()
    {
        var (state, _, _, _, _) = Nested();
        var migrated = state.Migrated();
        Assert.NotEmpty(migrated.Groups);

        var ungrouped = migrated with
        {
            Groups = [],
            Workspaces = migrated.Workspaces.Select(w => w with { GroupId = null }).ToList(),
        };

        Assert.Empty(ungrouped.Migrated().Groups);
    }

    // A file written by a build that kept ParentId beside the groups it built from it: the groups
    // win, and the stale ParentIds are cleared so they cannot rebuild anything later.
    [Fact]
    public void A_stale_parent_id_beside_existing_groups_is_cleared()
    {
        var (state, _, _, _, _) = Nested();
        var withStale = state.Migrated();
        var stale = withStale with
        {
            Workspaces = withStale.Workspaces.Select(w => w.GroupId is null ? w : w with { ParentId = Guid.NewGuid() }).ToList(),
        };

        var migrated = stale.Migrated();

        Assert.All(migrated.Workspaces, w => Assert.Null(w.ParentId));
        Assert.Equal(withStale.Groups, migrated.Groups);
    }
}
