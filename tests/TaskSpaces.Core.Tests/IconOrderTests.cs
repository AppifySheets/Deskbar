using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;

namespace TaskSpaces.Core.Tests;

// Petre: "rearranging icons when i activate a program makes no sense at this point; ring / square the
// active program without changing its position", then "let's make it possible to rearrange icons in the
// same workspace" and "go with the first-seen order, with the possibility of dragging those icons".
//
// So a row's icons are in the order their windows were first seen, activation moves nothing, a drag
// (MoveWindowInOrder) moves one window next to another, and the arrangement is saved by app identity so it
// survives a restart. These tests drive the manager rather than OverviewBuilder, because the order now
// lives in the manager's window list.
public class IconOrderTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();

    // Each window a different app, so each has its own identity for the saved order.
    static WindowInfo App(nint handle, string name) =>
        new(new WindowHandle(handle), (int)handle, name, $@"C:\{name}.exe", name, $@"""C:\{name}.exe""");

    static readonly WindowInfo A = App(0x1, "Alpha");
    static readonly WindowInfo B = App(0x2, "Bravo");
    static readonly WindowInfo C = App(0x3, "Charlie");

    WorkspaceManager Started(params WindowInfo[] windows)
    {
        var desktop = desktops.Desktops.FirstOrDefault() ?? desktops.Create("Main").Value;
        desktops.CurrentDesktopId = desktop.Id;
        windows.ToList().ForEach(w =>
        {
            monitor.InitialWindows.Add(w);
            desktops.WindowPlacements[w.Handle] = desktop.Id;
        });

        var manager = new WorkspaceManager(desktops, monitor, titles, store);
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    static IReadOnlyList<nint> Order(WorkspaceManager manager) =>
        manager.WindowsByWorkspace().Value.OtherDesktops.Single().Windows.Select(r => r.Window.Handle.Value).ToList();

    [Fact]
    public void Activating_a_window_does_not_move_its_icon()
    {
        var manager = Started(A, B, C);

        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Activated, C));

        Assert.Equal([0x1, 0x2, 0x3], Order(manager));
    }

    [Fact]
    public void A_window_opened_later_goes_at_the_end()
    {
        var manager = Started(A, B);
        var later = App(0x9, "Late");
        desktops.WindowPlacements[later.Handle] = desktops.CurrentDesktopId;

        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, later));

        Assert.Equal([0x1, 0x2, 0x9], Order(manager));
    }

    [Fact]
    public void Dragging_puts_a_window_before_or_after_another()
    {
        var manager = Started(A, B, C);

        Assert.True(manager.MoveWindowInOrder(C.Handle, A.Handle, after: false).IsSuccess);
        Assert.Equal([0x3, 0x1, 0x2], Order(manager));

        Assert.True(manager.MoveWindowInOrder(C.Handle, B.Handle, after: true).IsSuccess);
        Assert.Equal([0x1, 0x2, 0x3], Order(manager));
    }

    // Saved as apps, so a fresh start whose windows arrive in a different order still draws them the way
    // they were arranged.
    [Fact]
    public void The_arrangement_survives_a_restart()
    {
        var first = Started(A, B, C);
        first.MoveWindowInOrder(C.Handle, A.Handle, after: false);

        // The same apps, new handles, and the OS lists them in yet another order.
        var monitorAfterRestart = new FakeMonitor();
        var a = App(0x11, "Alpha");
        var b = App(0x12, "Bravo");
        var c = App(0x13, "Charlie");
        new[] { b, a, c }.ToList().ForEach(w =>
        {
            monitorAfterRestart.InitialWindows.Add(w);
            desktops.WindowPlacements[w.Handle] = desktops.CurrentDesktopId;
        });
        var second = new WorkspaceManager(desktops, monitorAfterRestart, titles, store);
        Assert.True(second.Start().IsSuccess);

        Assert.Equal([0x13, 0x11, 0x12], Order(second));
    }

    // An app with a saved place that opens later goes back to that place, not to the end.
    [Fact]
    public void An_arranged_app_opened_later_returns_to_its_place()
    {
        store.Stored = AppState.Empty with { IconOrder = [Identity(C), Identity(A), Identity(B)] };
        var manager = Started(A, B);

        var c = App(0x3, "Charlie");
        desktops.WindowPlacements[c.Handle] = desktops.CurrentDesktopId;
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, c));

        Assert.Equal([0x3, 0x1, 0x2], Order(manager));
    }

    // Arranging the bar while an app is closed must not forget where that app goes.
    [Fact]
    public void A_closed_app_keeps_its_saved_place_when_the_others_are_rearranged()
    {
        store.Stored = AppState.Empty with { IconOrder = [Identity(A), Identity(C), Identity(B)] };
        var manager = Started(A, B);

        manager.MoveWindowInOrder(B.Handle, A.Handle, after: false);

        // B before A now, and Charlie still right after Alpha, the app it followed.
        Assert.Equal([Identity(B), Identity(A), Identity(C)], store.Stored.IconOrder);
    }

    [Fact]
    public void Moving_a_window_that_has_closed_is_refused()
    {
        var manager = Started(A, B);

        Assert.True(manager.MoveWindowInOrder(new WindowHandle(0x77), A.Handle, after: false).IsFailure);
        Assert.Equal([0x1, 0x2], Order(manager));
    }

    static string Identity(WindowInfo window) =>
        Rehydration.RosterIdentity.Of(window.ProcessPath!, window.CommandLine);
}
