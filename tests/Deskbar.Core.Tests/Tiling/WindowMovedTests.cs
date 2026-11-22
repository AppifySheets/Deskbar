using Deskbar.Core.Abstractions;
using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;

namespace Deskbar.Core.Tests.Tiling;

// WorkspaceManager.WindowMoved: every window the manager moves to another desktop is announced, and only when
// the move happened. The Tiler depends on it, because a desktop change raises no window event of its own.
public sealed class WindowMovedTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();
    readonly Workspace alpha = new(Guid.NewGuid(), "Alpha", Guid.NewGuid());
    readonly Workspace beta = new(Guid.NewGuid(), "Beta", Guid.NewGuid());
    static readonly WindowHandle Window = new(7);

    (WorkspaceManager Manager, List<WindowHandle> Announced) Started()
    {
        new[] { alpha, beta }.ToList().ForEach(w => desktops.Desktops.Add(new DesktopInfo(w.DesktopId!.Value, w.Name)));
        desktops.CurrentDesktopId = alpha.DesktopId!.Value;
        monitor.InitialWindows.Add(new WindowInfo(Window, 700, "app", null, "window", null));
        desktops.WindowPlacements[Window] = alpha.DesktopId!.Value;
        store.Stored = AppState.Empty with { Workspaces = [alpha, beta] };
        var manager = new WorkspaceManager(desktops, monitor, titles, store);
        Assert.True(manager.Start().IsSuccess);
        var announced = new List<WindowHandle>();
        manager.WindowMoved.Subscribe(announced.Add);
        return (manager, announced);
    }

    [Fact]
    public void A_successful_move_announces_the_window()
    {
        var (manager, announced) = Started();

        Assert.True(manager.MoveToDesktop(Window, beta.DesktopId!.Value).IsSuccess);
        Assert.True(manager.AssignWindow(Window, alpha.Id).IsSuccess);

        Assert.Equal([Window, Window], announced);
    }

    [Fact]
    public void A_refused_move_announces_nothing()
    {
        var (manager, announced) = Started();
        desktops.RejectMoveFor.Add(Window);

        Assert.True(manager.MoveToDesktop(Window, beta.DesktopId!.Value).IsFailure);
        Assert.True(manager.AssignWindow(Window, beta.Id).IsFailure);

        Assert.Empty(announced);
    }
    // (b) The tasks window brought to the desktop in front (TasksBoard.Arrive) goes through the manager's doorway, so the
    // Tiler hears it arrive on a tiled workspace and gives it a slot, and nothing else about it is remembered: no
    // membership, no placement, no "detached" mark, which MoveToDesktop would write.
    [Fact]
    public void Bringing_a_window_to_the_desktop_in_front_announces_it_and_remembers_nothing()
    {
        var (manager, announced) = Started();
        desktops.WindowPlacements[Window] = beta.DesktopId!.Value;
        var before = store.Stored;

        Assert.True(manager.MoveWindowToCurrentDesktop(Window).IsSuccess);

        Assert.Equal(alpha.DesktopId!.Value, desktops.WindowPlacements[Window]);
        Assert.Equal([Window], announced);
        Assert.Same(before, store.Stored);
    }

    [Fact]
    public void A_refused_move_to_the_desktop_in_front_announces_nothing()
    {
        var (manager, announced) = Started();
        desktops.RejectMoveFor.Add(Window);

        Assert.True(manager.MoveWindowToCurrentDesktop(Window).IsFailure);

        Assert.Empty(announced);
    }
}
