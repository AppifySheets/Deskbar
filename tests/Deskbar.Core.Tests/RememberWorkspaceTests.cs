using Deskbar.Core.Abstractions;
using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;

namespace Deskbar.Core.Tests;

// Workspace memory is opt-in per app (2026-11-22). Petre: "mstsc opened in another workspace" /
// "should in the same", then "placement memory has to always consider the context that the window is
// in" and "let's keep memory, but only for specific apps". The rule itself is PlacementMemory.For;
// these tests drive it through WorkspaceManager the way the icon menu and `remember-workspace` do.
public class RememberWorkspaceTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeStore store = new();

    const string MstscPath = @"C:\WINDOWS\system32\mstsc.exe";
    const string MstscCmd = @"""C:\WINDOWS\system32\mstsc.exe"" ";
    const string EdgePath = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";

    // A different handle per window, the way a real app makes a fresh window each time it is opened.
    static WindowInfo Mstsc(nint handle) =>
        new(new WindowHandle(handle), 73624, "mstsc", MstscPath, "Remote Desktop Connection", MstscCmd);

    readonly Workspace meta = new(Guid.NewGuid(), "Meta", Guid.NewGuid());
    readonly Workspace site = new(Guid.NewGuid(), "Deskbar site", Guid.NewGuid());

    WorkspaceManager Started(AppState? state = null)
    {
        new[] { meta, site }.ToList().ForEach(w => desktops.Desktops.Add(new DesktopInfo(w.DesktopId!.Value, w.Name)));
        desktops.CurrentDesktopId = site.DesktopId!.Value; // Petre is on Deskbar site
        store.Stored = (state ?? AppState.Empty) with { Workspaces = [meta, site] };
        var manager = new WorkspaceManager(desktops, monitor, new FakeTitles(), store);
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    void Appears(WindowInfo window) => monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, window));
    void Closes(WindowInfo window) => monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Disappeared, window));

    // THE report, as it happened: mstsc dragged to Meta once, then opened again from the Run box on
    // Deskbar site. Before the change it landed on Meta 35 ms later; now it stays where it was opened.
    [Fact]
    public void A_dragged_app_that_was_not_opted_in_opens_where_it_is_opened()
    {
        var manager = Started();
        Appears(Mstsc(0x10));
        Assert.True(manager.AssignWindow(new WindowHandle(0x10), meta.Id).IsSuccess);
        Closes(Mstsc(0x10));
        desktops.WindowPlacements.Clear();

        Appears(Mstsc(0x11));

        Assert.DoesNotContain(new WindowHandle(0x11), desktops.WindowPlacements.Keys);
    }

    // Opting in records where the window is NOW, and the next window of the app goes there.
    [Fact]
    public void Opting_in_remembers_the_workspace_the_window_is_in()
    {
        var manager = Started();
        Appears(Mstsc(0x20));
        desktops.WindowPlacements[new WindowHandle(0x20)] = meta.DesktopId!.Value;

        Assert.True(manager.RememberWorkspace(new WindowHandle(0x20), true).IsSuccess);
        Closes(Mstsc(0x20));
        desktops.WindowPlacements.Clear();
        Appears(Mstsc(0x21));

        Assert.Contains("mstsc", store.Stored.RememberedApps);
        Assert.True(manager.RemembersWorkspace("mstsc"));
        Assert.Equal(meta.DesktopId, desktops.WindowPlacements[new WindowHandle(0x21)]);
    }

    // The opt-in replaces whatever the roster learned before, because the window he opened the menu on
    // is the one he is looking at. His file had Teams under Rustdesk from one meeting window.
    [Fact]
    public void Opting_in_overrides_an_older_roster_answer()
    {
        var manager = Started(AppState.Empty with
        {
            Inventory = new Dictionary<Guid, IReadOnlyList<InventoryEntry>>
            {
                [meta.Id] = [new InventoryEntry(MstscPath, MstscCmd, "Remote Desktop Connection")],
            },
        });
        Appears(Mstsc(0x30));
        desktops.WindowPlacements[new WindowHandle(0x30)] = site.DesktopId!.Value;

        Assert.True(manager.RememberWorkspace(new WindowHandle(0x30), true).IsSuccess);

        Assert.Empty(store.Stored.Inventory.GetValueOrDefault(meta.Id, []));
        Assert.Single(store.Stored.Inventory[site.Id]);
    }

    // Turning it off makes the app open where it is opened again, roster entry or not.
    [Fact]
    public void Opting_out_stops_the_app_being_sent_anywhere()
    {
        var manager = Started();
        Appears(Mstsc(0x40));
        desktops.WindowPlacements[new WindowHandle(0x40)] = meta.DesktopId!.Value;
        Assert.True(manager.RememberWorkspace(new WindowHandle(0x40), true).IsSuccess);

        Assert.True(manager.RememberWorkspace(new WindowHandle(0x40), false).IsSuccess);
        Closes(Mstsc(0x40));
        desktops.WindowPlacements.Clear();
        Appears(Mstsc(0x41));

        Assert.DoesNotContain("mstsc", store.Stored.RememberedApps);
        Assert.DoesNotContain(new WindowHandle(0x41), desktops.WindowPlacements.Keys);
    }

    // A browser window stays where it was opened whatever the list says (RosterIdentity.StaysWhereOpened),
    // so the command refuses rather than storing an opt-in that would do nothing.
    [Fact]
    public void A_browser_cannot_be_opted_in()
    {
        var manager = Started();
        var edge = new WindowInfo(new WindowHandle(0x50), 900, "msedge", EdgePath, "New tab", $"\"{EdgePath}\" --profile-directory=Default");
        Appears(edge);

        Assert.True(manager.RememberWorkspace(edge.Handle, true).IsFailure);
        Assert.Empty(store.Stored.RememberedApps);
    }

    [Fact]
    public void A_window_that_is_gone_is_refused() =>
        Assert.True(Started().RememberWorkspace(new WindowHandle(0x999), true).IsFailure);
}
