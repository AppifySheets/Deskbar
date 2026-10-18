using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Domain;

namespace TaskSpaces.Core.Tests;

// When each window was last used, for the bar's aging icons (Petre: "let's age icons in workspaces", then "a":
// an icon fades to grey the longer its window goes unused). Stamped when a window becomes known and whenever
// it comes to the front; forgotten when it closes.
public class LastUsedTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    DateTimeOffset clock = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);

    static WindowInfo Window(nint hwnd) =>
        new(new WindowHandle(hwnd), 500, "Code", @"C:\Apps\Code.exe", $"window {hwnd}", null);

    WorkspaceManager Started()
    {
        var manager = new WorkspaceManager(desktops, monitor, new FakeTitles(), new FakeStore(), clock: () => clock, ownProcessId: 4242);
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    [Fact]
    public void A_new_window_starts_fresh()
    {
        var manager = Started();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Window(0x10)));
        Assert.Equal(clock, manager.LastUsed(Window(0x10).Handle));
    }

    // Left alone, a window's stamp stays where it was while time goes on; that gap is its age.
    [Fact]
    public void An_unused_window_keeps_its_old_stamp()
    {
        var manager = Started();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Window(0x10)));
        var opened = clock;

        clock = clock.AddHours(3);
        Assert.Equal(opened, manager.LastUsed(Window(0x10).Handle));
        Assert.Equal(TimeSpan.FromHours(3), manager.Now - manager.LastUsed(Window(0x10).Handle)!.Value);
    }

    // Bringing it to the front is using it.
    [Fact]
    public void Activating_a_window_makes_it_fresh_again()
    {
        var manager = Started();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Window(0x10)));

        clock = clock.AddHours(3);
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Activated, Window(0x10)));
        Assert.Equal(clock, manager.LastUsed(Window(0x10).Handle));
    }

    // Across a restart of the app: the same window (handle and process) takes back its older stamp.
    [Fact]
    public void A_restart_gives_the_same_window_its_old_stamp_back()
    {
        var manager = Started();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Window(0x10)));
        var beforeRestart = clock.AddHours(-5);

        manager.RestoreLastUsed(Window(0x10).Handle, Window(0x10).ProcessId, beforeRestart);
        Assert.Equal(beforeRestart, manager.LastUsed(Window(0x10).Handle));
    }

    // A recycled handle now belongs to another process, so it is a different window and stays fresh.
    [Fact]
    public void A_recycled_handle_does_not_inherit_an_old_stamp()
    {
        var manager = Started();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Window(0x10)));

        manager.RestoreLastUsed(Window(0x10).Handle, processId: 999, clock.AddHours(-5));
        Assert.Equal(clock, manager.LastUsed(Window(0x10).Handle));
    }

    // Used since the restart: the newer stamp is already right.
    [Fact]
    public void A_restored_stamp_never_makes_a_window_older_than_its_last_use()
    {
        var manager = Started();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Window(0x10)));

        manager.RestoreLastUsed(Window(0x10).Handle, Window(0x10).ProcessId, clock.AddMinutes(5));
        Assert.Equal(clock, manager.LastUsed(Window(0x10).Handle));
    }

    // A closed window's handle can be reused by Windows for another, which must not inherit the old age.
    [Fact]
    public void A_closed_window_is_forgotten()
    {
        var manager = Started();
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, Window(0x10)));
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Disappeared, Window(0x10)));
        Assert.Null(manager.LastUsed(Window(0x10).Handle));
    }
}
