using Deskbar.Core.Abstractions;
using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;

namespace Deskbar.Core.Tests;

// A first install's workspaces (WorkspaceManager.SeedFirstWorkspaces). Petre: "add multiple workspaces the first time
// it's installed", then Work, Personal and Projects, with the desktops already there kept and named first.
//
// The shell reports a desktop nobody has named with an EMPTY name (measured on Petre's machine: four of his desktops
// have no Name value in the registry), so the fakes here use "" for an unnamed desktop.
public class SeedFirstWorkspacesTests
{
    static readonly IReadOnlyList<string> Defaults = ["Work", "Personal", "Projects"];

    readonly FakeDesktops desktops = new();
    readonly FakeStore store = new();

    WorkspaceManager Started(params (Guid Id, string Name)[] existing)
    {
        existing.ToList().ForEach(d => desktops.Desktops.Add(new DesktopInfo(d.Id, d.Name)));
        var manager = new WorkspaceManager(desktops, new FakeMonitor(), new FakeTitles(), store);
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    // The usual first install: one desktop, the one everybody starts on. It becomes Work with its windows, and two new
    // desktops are made for Personal and Projects.
    [Fact]
    public void One_unnamed_desktop_becomes_work_and_two_more_are_created()
    {
        var first = Guid.NewGuid();
        var manager = Started((first, ""));

        var made = manager.SeedFirstWorkspaces(Defaults);

        Assert.Equal(["Work", "Personal", "Projects"], made.Select(w => w.Name));
        Assert.Equal(first, made[0].DesktopId);
        Assert.Equal(3, desktops.Desktops.Count);
        Assert.Equal(["Work", "Personal", "Projects"], manager.State.Workspaces.Select(w => w.Name));
    }

    // A desktop the user named in Task View keeps its name, and the default it would have taken goes to the next one.
    // A default the user already uses is skipped rather than clashing.
    [Fact]
    public void Named_desktops_keep_their_names_and_used_defaults_are_skipped()
    {
        var mail = Guid.NewGuid();
        var unnamed = Guid.NewGuid();
        var personal = Guid.NewGuid();
        var manager = Started((mail, "Mail"), (unnamed, ""), (personal, "personal"));

        var made = manager.SeedFirstWorkspaces(Defaults);

        Assert.Equal(["Mail", "Work", "personal"], made.Select(w => w.Name));
        Assert.Equal([mail, unnamed, personal], made.Select(w => w.DesktopId!.Value));
        Assert.Equal(3, desktops.Desktops.Count);
    }

    // More desktops than defaults: the defaults run out, the extra unnamed desktops stay as they were, and nothing new
    // is created.
    [Fact]
    public void Extra_unnamed_desktops_stay_unnamed()
    {
        var manager = Started((Guid.NewGuid(), ""), (Guid.NewGuid(), ""), (Guid.NewGuid(), ""), (Guid.NewGuid(), ""));

        var made = manager.SeedFirstWorkspaces(Defaults);

        Assert.Equal(["Work", "Personal", "Projects"], made.Select(w => w.Name));
        Assert.Equal(4, desktops.Desktops.Count);
        Assert.Single(desktops.Desktops, d => d.Name == "");
    }

    // Never over somebody's workspaces: with any workspace already there, nothing is named or created.
    [Fact]
    public void Existing_workspaces_are_never_touched()
    {
        var bound = Guid.NewGuid();
        store.Stored = AppState.Empty with { Workspaces = [new Workspace(Guid.NewGuid(), "Mine", bound)] };
        var manager = Started((bound, "Mine"), (Guid.NewGuid(), ""));

        Assert.Empty(manager.SeedFirstWorkspaces(Defaults));
        Assert.Equal(["Mine"], manager.State.Workspaces.Select(w => w.Name));
        Assert.Equal(2, desktops.Desktops.Count);
    }
}
