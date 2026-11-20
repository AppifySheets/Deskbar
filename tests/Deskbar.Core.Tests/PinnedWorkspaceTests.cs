using Deskbar.Core.Abstractions;
using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;

namespace Deskbar.Core.Tests;

// Pinning a workspace (docs/superpowers/specs/2026-11-20-pinned-workspaces-design.md). Petre: "i'd have a
// messengers workspace pinned and i'd go to it each time i need to do something there, and have a simple way
// to go back to the previous workspace".
//
// This layer's half: the wish is stored, survives a restart and pulses the bar. What the items say and where
// they go is WorkspaceJumpsTests; how they look is the Windows tests.
public class PinnedWorkspaceTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();

    // Two workspaces: Deskbar, where the work happens, and Messengers, the one jumped into and out of.
    (WorkspaceManager manager, Guid work, Guid messengers) Started()
    {
        var work = new Workspace(Guid.NewGuid(), "Deskbar", Guid.NewGuid());
        var messengers = new Workspace(Guid.NewGuid(), "Messengers", Guid.NewGuid());
        new[] { work, messengers }.ToList().ForEach(w => desktops.Desktops.Add(new DesktopInfo(w.DesktopId!.Value, w.Name)));
        store.Stored = AppState.Empty with { Workspaces = [work, messengers] };

        var manager = new WorkspaceManager(desktops, monitor, titles, store);
        Assert.True(manager.Start().IsSuccess);
        return (manager, work.Id, messengers.Id);
    }

    [Fact]
    public void A_workspace_starts_unpinned() =>
        Assert.DoesNotContain(Started().manager.State.Workspaces, w => w.Pinned);

    [Fact]
    public void Pinning_persists_and_pulses()
    {
        var (manager, _, messengers) = Started();
        var savesBefore = store.SaveCount;
        var pulses = 0;
        using var subscription = manager.StateChanged.Subscribe(_ => pulses++);

        Assert.True(manager.SetWorkspacePinned(messengers, true).IsSuccess);

        Assert.True(manager.State.Workspaces.Single(w => w.Id == messengers).Pinned);
        Assert.True(store.Stored.Workspaces.Single(w => w.Id == messengers).Pinned);
        Assert.Equal(savesBefore + 1, store.SaveCount);
        Assert.Equal(1, pulses);
    }

    [Fact]
    public void Unpinning_clears_it()
    {
        var (manager, _, messengers) = Started();

        Assert.True(manager.SetWorkspacePinned(messengers, true).IsSuccess);
        Assert.True(manager.SetWorkspacePinned(messengers, false).IsSuccess);

        Assert.False(manager.State.Workspaces.Single(w => w.Id == messengers).Pinned);
    }

    [Fact]
    public void Pinning_an_unknown_workspace_fails() =>
        Assert.True(Started().manager.SetWorkspacePinned(Guid.NewGuid(), true).IsFailure);

    // --- jump: in, or back out (the pipe's `jump`, and what a hand-pinned taskbar shortcut runs) ------------------

    // Arrives on a workspace's desktop the way Windows reports it: the current desktop changes and says so.
    void Arrive(WorkspaceManager manager, Guid workspace)
    {
        var desktop = manager.State.Workspaces.Single(w => w.Id == workspace).DesktopId!.Value;
        desktops.CurrentDesktopId = desktop;
        desktops.CurrentChangedSubject.OnNext(desktop);
    }

    Guid DesktopOf(WorkspaceManager manager, Guid workspace) => manager.State.Workspaces.Single(w => w.Id == workspace).DesktopId!.Value;

    [Fact]
    public void Jump_goes_in_from_elsewhere()
    {
        var (manager, work, messengers) = Started();
        Arrive(manager, work);

        Assert.Equal(messengers, manager.Jump(messengers).Value);
        Assert.Equal(DesktopOf(manager, messengers), desktops.Switches.Last());
    }

    [Fact]
    public void Jump_goes_back_from_inside()
    {
        var (manager, work, messengers) = Started();
        Arrive(manager, work);
        Arrive(manager, messengers);

        Assert.Equal(work, manager.Jump(messengers).Value);
        Assert.Equal(DesktopOf(manager, work), desktops.Switches.Last());
    }

    // The only workspace there is: "back" has no answer (ByRecentUse lists every workspace, visited or not, so this
    // is the one way to get none), and the jump says so rather than staying silent.
    [Fact]
    public void Jump_inside_with_nowhere_to_go_back_fails()
    {
        var alone = new Workspace(Guid.NewGuid(), "Messengers", Guid.NewGuid());
        desktops.Desktops.Add(new DesktopInfo(alone.DesktopId!.Value, alone.Name));
        store.Stored = AppState.Empty with { Workspaces = [alone] };
        var manager = new WorkspaceManager(desktops, monitor, titles, store);
        Assert.True(manager.Start().IsSuccess);
        Arrive(manager, alone.Id);

        Assert.True(manager.Jump(alone.Id).IsFailure);
    }

    // --- only ungrouped workspaces pin, and pinned ones are drawn first ------------------------------------------
    // Petre: "they need to be ungrouped, no grouped workspace should be available for pinning", "i may also have
    // multiple pinned workspaces, both should go to the top", and, asked where an unpinned row goes: "back where it
    // was". So pinning changes the order rows are DRAWN in, never the stored order.

    // Four ungrouped workspaces, A B C D, with B and D pinned: drawn B D A C.
    (WorkspaceManager manager, Guid a, Guid b, Guid c, Guid d) Four()
    {
        var all = new[] { "A", "B", "C", "D" }.Select(n => new Workspace(Guid.NewGuid(), n, Guid.NewGuid()) { Pinned = n is "B" or "D" }).ToList();
        all.ForEach(w => desktops.Desktops.Add(new DesktopInfo(w.DesktopId!.Value, w.Name)));
        store.Stored = AppState.Empty with { Workspaces = all };
        var manager = new WorkspaceManager(desktops, monitor, titles, store);
        Assert.True(manager.Start().IsSuccess);
        return (manager, all[0].Id, all[1].Id, all[2].Id, all[3].Id);
    }

    static string Stored(WorkspaceManager manager) => string.Concat(manager.State.Workspaces.Select(w => w.Name));

    [Fact]
    public void A_grouped_workspace_cannot_be_pinned()
    {
        var (manager, work, messengers) = Started();
        Assert.True(manager.CreateGroup("Team", work).IsSuccess);

        Assert.True(manager.SetWorkspacePinned(work, true).IsFailure);
        Assert.False(manager.State.Workspaces.Single(w => w.Id == work).Pinned);
    }

    [Fact]
    public void Joining_a_group_unpins()
    {
        var (manager, work, messengers) = Started();
        Assert.True(manager.SetWorkspacePinned(messengers, true).IsSuccess);
        var team = manager.CreateGroup("Team", work).Value.Id;

        Assert.True(manager.MoveIntoGroup(messengers, team).IsSuccess);

        Assert.False(manager.State.Workspaces.Single(w => w.Id == messengers).Pinned);
    }

    [Fact]
    public void Row_numbers_follow_the_drawn_order()
    {
        var (manager, a, b, c, d) = Four();

        Assert.Equal([1, 2, 3, 4], new[] { b, d, a, c }.Select(id => manager.RowOf(id).Value));
    }

    // Move up on the second pinned row swaps it with the first pinned row, and every unpinned row keeps its place.
    [Fact]
    public void A_pinned_row_moves_among_the_pinned()
    {
        var (manager, _, _, _, d) = Four();

        Assert.True(manager.MoveWorkspace(d, -1).IsSuccess);

        Assert.Equal("ADCB", Stored(manager));
    }

    // Move to top on an unpinned row puts it first among the unpinned, under the pinned ones, and the pinned rows keep
    // their stored places, so unpinning one later still puts it back where it was.
    [Fact]
    public void An_unpinned_row_moves_among_the_unpinned()
    {
        var (manager, _, _, c, _) = Four();

        Assert.True(manager.MoveWorkspaceTo(c, 0).IsSuccess);

        Assert.Equal("CBAD", Stored(manager));
    }

    [Fact]
    public void Unpinning_puts_the_row_back_where_it_was()
    {
        var (manager, _, b, _, _) = Four();

        Assert.True(manager.SetWorkspacePinned(b, false).IsSuccess);

        Assert.Equal("ABCD", Stored(manager));
        Assert.Equal(3, manager.RowOf(b).Value); // drawn D A B C: the pinned D first, then B back between A and C
    }

    // Final review, Important 2: a GROUP moved from its header moves among the rows drawn beside it, the unpinned ones,
    // and the pinned rows keep their stored places. Stored A P G D (P pinned, G a group) is drawn P A G D; "Move group
    // up" must make it P G A D, not reorder the stored list in a way the drawing cannot show.
    [Fact]
    public void A_group_moves_among_the_unpinned_rows()
    {
        var (manager, a, b, c, d) = Four();
        Assert.True(manager.SetWorkspacePinned(d, false).IsSuccess); // A B(p) C D
        var group = manager.CreateGroup("G", c).Value.Id;          // C is now the group's only member

        Assert.True(manager.MoveGroup(group, -1).IsSuccess);

        Assert.Equal("CBAD", Stored(manager));
        Assert.Equal([1, 2, 3, 4], new[] { b, c, a, d }.Select(id => manager.RowOf(id).Value));
    }
}
