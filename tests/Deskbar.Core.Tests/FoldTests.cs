using Deskbar.Core.Abstractions;
using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;

namespace Deskbar.Core.Tests;

// Folding a row and a whole group, and moving a whole group. Petre: "now i'm not working on deskbar and i want
// to minimize it, to a single line, so i don't see any icons in it", "i should be able to minimize the entire
// group", and "move them around easily".
//
// How a folded row LOOKS is the bar's business (FoldedRowTests in the Windows tests); that the wish survives a
// restart, disturbs nothing else, and that a group moves as one block is this layer's.
public class FoldTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();

    // Four rows: Alpha, then the group "Clients" holding Beta and Gamma, then Delta.
    (WorkspaceManager manager, Guid a, Guid b, Guid c, Guid d, Guid group) Started()
    {
        var a = new Workspace(Guid.NewGuid(), "Alpha", Guid.NewGuid());
        var b = new Workspace(Guid.NewGuid(), "Beta", Guid.NewGuid());
        var c = new Workspace(Guid.NewGuid(), "Gamma", Guid.NewGuid());
        var d = new Workspace(Guid.NewGuid(), "Delta", Guid.NewGuid());
        new[] { a, b, c, d }.ToList().ForEach(w => desktops.Desktops.Add(new DesktopInfo(w.DesktopId!.Value, w.Name)));
        store.Stored = AppState.Empty with { Workspaces = [a, b, c, d] };

        var manager = new WorkspaceManager(desktops, monitor, titles, store);
        Assert.True(manager.Start().IsSuccess);
        var group = manager.CreateGroup("Clients", b.Id).Value.Id;
        Assert.True(manager.MoveIntoGroup(c.Id, group).IsSuccess);
        return (manager, a.Id, b.Id, c.Id, d.Id, group);
    }

    static IReadOnlyList<string> NamesOf(WorkspaceManager manager) =>
        manager.State.Workspaces.Select(w => w.Name).ToList();

    // --- a row ---------------------------------------------------------------------------------

    [Fact]
    public void A_workspace_starts_unfolded() =>
        Assert.DoesNotContain(Started().manager.State.Workspaces, w => w.Folded);

    [Fact]
    public void Folding_a_workspace_persists_and_pulses()
    {
        var (manager, a, _, _, _, _) = Started();
        var savesBefore = store.SaveCount;
        var pulses = 0;
        using var subscription = manager.StateChanged.Subscribe(_ => pulses++);

        Assert.True(manager.SetWorkspaceFolded(a, true).IsSuccess);

        Assert.True(manager.State.Workspaces.Single(w => w.Id == a).Folded);
        Assert.True(store.Stored.Workspaces.Single(w => w.Id == a).Folded);
        Assert.Equal(savesBefore + 1, store.SaveCount);
        Assert.Equal(1, pulses);
    }

    [Fact]
    public void Unfolding_a_workspace_undoes_it()
    {
        var (manager, a, _, _, _, _) = Started();
        Assert.True(manager.SetWorkspaceFolded(a, true).IsSuccess);

        Assert.True(manager.SetWorkspaceFolded(a, false).IsSuccess);

        Assert.False(manager.State.Workspaces.Single(w => w.Id == a).Folded);
    }

    // Presentational and nothing more: a folded workspace keeps its name, its place in the order and -- the
    // one that would actually lose windows -- its bound desktop.
    [Fact]
    public void Folding_changes_nothing_but_the_flag()
    {
        var (manager, a, _, _, _, _) = Started();
        var before = manager.State.Workspaces.Single(w => w.Id == a);

        Assert.True(manager.SetWorkspaceFolded(a, true).IsSuccess);

        Assert.Equal(before with { Folded = true }, manager.State.Workspaces.Single(w => w.Id == a));
        Assert.Equal(["Alpha", "Beta", "Gamma", "Delta"], NamesOf(manager));
    }

    [Fact]
    public void Folding_an_unknown_workspace_fails() =>
        Assert.True(Started().manager.SetWorkspaceFolded(Guid.NewGuid(), true).IsFailure);

    // --- a group -------------------------------------------------------------------------------

    [Fact]
    public void Folding_a_group_persists_it_on_the_group()
    {
        var (manager, _, _, _, _, group) = Started();

        Assert.True(manager.SetGroupFolded(group, true).IsSuccess);

        Assert.True(manager.State.Groups.Single(g => g.Id == group).Folded);
        Assert.True(store.Stored.Groups.Single(g => g.Id == group).Folded);
    }

    // The group's fold is its own, so unfolding the group puts every member back exactly as it was: one
    // folded on its own stays folded, the others open.
    [Fact]
    public void Folding_a_group_leaves_each_members_own_fold_alone()
    {
        var (manager, _, b, c, _, group) = Started();
        Assert.True(manager.SetWorkspaceFolded(b, true).IsSuccess);

        Assert.True(manager.SetGroupFolded(group, true).IsSuccess);
        Assert.True(manager.SetGroupFolded(group, false).IsSuccess);

        Assert.True(manager.State.Workspaces.Single(w => w.Id == b).Folded);
        Assert.False(manager.State.Workspaces.Single(w => w.Id == c).Folded);
    }

    [Fact]
    public void Folding_an_unknown_group_fails() =>
        Assert.True(Started().manager.SetGroupFolded(Guid.NewGuid(), true).IsFailure);

    // --- moving a whole group --------------------------------------------------------------------

    [Fact]
    public void Moving_a_group_up_takes_every_member_past_the_row_above()
    {
        var (manager, _, _, _, _, group) = Started();

        Assert.True(manager.MoveGroup(group, -1).IsSuccess);

        Assert.Equal(["Beta", "Gamma", "Alpha", "Delta"], NamesOf(manager));
    }

    [Fact]
    public void Moving_a_group_down_keeps_its_members_in_their_order()
    {
        var (manager, _, _, _, _, group) = Started();

        Assert.True(manager.MoveGroup(group, +1).IsSuccess);

        Assert.Equal(["Alpha", "Delta", "Beta", "Gamma"], NamesOf(manager));
    }

    // Up from the top does nothing and says nothing, as a row's Move up does: no error at someone who clicked.
    [Fact]
    public void Moving_the_top_group_up_is_a_quiet_no_op()
    {
        var (manager, _, _, _, _, group) = Started();
        Assert.True(manager.MoveGroupTo(group, 0).IsSuccess);
        var savesBefore = store.SaveCount;

        Assert.True(manager.MoveGroup(group, -1).IsSuccess);

        Assert.Equal(["Beta", "Gamma", "Alpha", "Delta"], NamesOf(manager));
        Assert.Equal(savesBefore, store.SaveCount);
    }

    // "Move group to end" passes the workspace count, which is past the last top-level row; it clamps.
    [Fact]
    public void Moving_a_group_to_the_end_clamps_to_the_last_row()
    {
        var (manager, _, _, _, _, group) = Started();

        Assert.True(manager.MoveGroupTo(group, manager.State.Workspaces.Count).IsSuccess);

        Assert.Equal(["Alpha", "Delta", "Beta", "Gamma"], NamesOf(manager));
    }

    [Fact]
    public void Moving_an_unknown_group_fails() =>
        Assert.True(Started().manager.MoveGroup(Guid.NewGuid(), -1).IsFailure);
}

// The fold is stored under the key the minimized row used, so a state.json written before the rename keeps its
// rows the way they were instead of unfolding every one of them on the first start of the new build.
public sealed class FoldPersistenceTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), $"deskbar-tests-{Guid.NewGuid():N}");

    public void Dispose() { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }

    [Fact]
    public void A_row_minimized_by_an_older_build_loads_folded()
    {
        Directory.CreateDirectory(dir);
        var id = Guid.NewGuid();
        File.WriteAllText(Path.Combine(dir, "state.json"),
            $$$"""{"Workspaces":[{"Id":"{{{id}}}","Name":"Deskbar","DesktopId":null,"Minimized":true}],"WorkspaceRules":[],"RenameRules":[],"Inventory":{}}""");

        Assert.True(new JsonPersistenceStore(dir).Load().Value.Workspaces.Single().Folded);
    }

    [Fact]
    public void A_fold_round_trips_for_rows_and_groups()
    {
        var store = new JsonPersistenceStore(dir);
        var group = new Group(Guid.NewGuid(), "Clients") { Folded = true };
        var workspace = new Workspace(Guid.NewGuid(), "Beta", null) { Folded = true, GroupId = group.Id };
        Assert.True(store.Save(AppState.Empty with { Workspaces = [workspace], Groups = [group] }).IsSuccess);

        var loaded = store.Load().Value;

        Assert.True(loaded.Workspaces.Single().Folded);
        Assert.True(loaded.Groups.Single().Folded);
        Assert.Contains("\"Minimized\": true", File.ReadAllText(Path.Combine(dir, "state.json")));
    }
}
