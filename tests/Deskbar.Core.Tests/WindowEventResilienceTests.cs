using System.Reactive.Linq;
using Deskbar.Core.Abstractions;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Tests;

// Petre: "i sometimes feel that the deskbar icons don't reflect the change in windows, what's open, etc
// soon enough".
//
// Measured on his machine, 2026-11-01. The window monitor traced every close the moment it happened, and
// the manager dropped each one only at the next 5s repair sweep:
//
//   00:46:31.855 destroy 1079E (Docker Desktop)
//   00:46:35.849 repair: adopted [] dropped [1079E/Docker Desktop]
//
// A Notepad started with `deskbar.cmd start "Deskbar" -- notepad.exe` reached the manager 4.2s later through
// "repair: adopted", and opened on the workspace in front instead of Deskbar's, because OnAppeared (which
// does the placing) never ran. The other subscribers to the same event stream still heard every event, so
// the hooks were fine. Only the manager had gone deaf, and it stayed deaf until the app restarted. That
// happened in 4 of the last 15 runs, 30 to 50 minutes in.
//
// The mechanism is Rx's own: an exception thrown out of a Subscribe(action) handler disposes that
// subscription for good (a dotnet-script probe against System.Reactive 7.0.0: the throwing subscriber saw
// 2 of 4 events). WindowMonitor swallows the rethrow so the native callback survives, and nothing said a
// word. These tests hold the manager to hearing the NEXT event after one it failed to handle.
public class WindowEventResilienceTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeStore store = new();
    readonly List<string> traced = [];

    static WindowInfo Window(nint hwnd, string process) =>
        new(new WindowHandle(hwnd), (int)hwnd, process, $@"C:\apps\{process}.exe", process, null);

    WorkspaceManager Started()
    {
        var manager = new WorkspaceManager(desktops, monitor, new FakeTitles(), store, ownProcessId: 4242, trace: traced.Add);
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    // One surface whose handler throws once, on the first pulse it sees. The pulse is raised from inside the
    // manager's own handling of a window event, so the throw travels back up through it. A fault in placement
    // or renaming takes the same road.
    void GivenOneHandlerThatThrowsOnce(WorkspaceManager manager)
    {
        var thrown = false;
        manager.StateChanged.Subscribe(_ =>
        {
            if (thrown) return;
            thrown = true;
            throw new InvalidOperationException("a surface failed once");
        });
    }

    // What WindowMonitor does with an exception that comes back out of OnNext: swallow it, so a managed
    // exception never unwinds through user32's callback.
    void Raise(WindowEventKind kind, WindowInfo window)
    {
        try { monitor.Subject.OnNext(new WindowEvent(kind, window)); }
        catch (Exception) { }
    }

    [Fact]
    public void A_window_that_opens_after_a_failed_event_is_known_at_once()
    {
        var manager = Started();
        GivenOneHandlerThatThrowsOnce(manager);

        Raise(WindowEventKind.Appeared, Window(0x100, "first"));
        Raise(WindowEventKind.Appeared, Window(0x200, "second"));

        Assert.Contains(manager.KnownWindows, w => w.Handle == new WindowHandle(0x200));
    }

    [Fact]
    public void A_window_that_closes_after_a_failed_event_is_dropped_at_once()
    {
        var manager = Started();
        Raise(WindowEventKind.Appeared, Window(0x300, "closing"));
        GivenOneHandlerThatThrowsOnce(manager);

        Raise(WindowEventKind.Appeared, Window(0x100, "first"));
        Raise(WindowEventKind.Disappeared, Window(0x300, "closing"));

        Assert.DoesNotContain(manager.KnownWindows, w => w.Handle == new WindowHandle(0x300));
    }

    // The failure is named in the trace, because the one that took Petre's manager down was swallowed with
    // nothing more than a Debug.WriteLine, and which handler threw is still unknown.
    [Fact]
    public void A_failed_event_is_written_to_the_trace_with_what_threw()
    {
        var manager = Started();
        GivenOneHandlerThatThrowsOnce(manager);

        Raise(WindowEventKind.Appeared, Window(0x100, "first"));

        Assert.Contains(traced, line => line.Contains("Appeared") && line.Contains("first")
                                        && line.Contains(nameof(InvalidOperationException)));
    }
}
