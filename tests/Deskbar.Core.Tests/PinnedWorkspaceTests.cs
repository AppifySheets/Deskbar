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
}
