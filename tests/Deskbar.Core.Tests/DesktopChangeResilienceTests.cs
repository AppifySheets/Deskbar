using System.Reactive.Linq;
using Deskbar.Core.Abstractions;
using Deskbar.Core.Control;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Tests;

// #399: "deskbar.cmd switch does not clear a Claude session's ask". Going to an asking workspace with
// `deskbar.cmd switch Research` left its row flashing, and the stale ask then stopped the next take's flash from
// showing.
//
// Nothing in the code tells a switch from the pipe apart from a row click: both call WorkspaceManager.Switch, and
// what clears the ask is the arrival (OnDesktopChanged), driven by Windows' own CurrentChanged whoever switched. The
// one way for every later arrival to stop clearing asks is for the manager to stop HEARING arrivals, and Rx does
// exactly that: an exception thrown out of a Subscribe(action) handler disposes the subscription for good (measured
// for #399 with a dotnet-script probe against System.Reactive 7.0.0: a FromEventPattern subscriber that threw on its
// first event ran once for three events, and left no handler attached). The window-event subscription went deaf the
// same way in 4 of 15 runs (WindowEventResilienceTests), and the desktop-change handler reaches more than that one
// did: window activation, window moves, a state.json write, and every StateChanged subscriber inline, the bar's
// synchronous Dispatcher.Invoke rebuild among them.
//
// A deaf manager also keeps the desktop it last heard as the current one, so RequestAttention refuses to mark the
// workspace you have since left ("is the current workspace"): the second half of the report.
//
// These tests hold the manager to hearing the NEXT desktop change after one it failed to handle.
public class DesktopChangeResilienceTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeStore store = new();
    readonly List<string> traced = [];

    WorkspaceManager Started()
    {
        var manager = new WorkspaceManager(desktops, monitor, new FakeTitles(), store, ownProcessId: 4242, trace: traced.Add);
        Assert.True(manager.Start().IsSuccess);
        manager.AddWorkspace("Research");
        manager.AddWorkspace("Notes");
        return manager;
    }

    Guid DesktopOf(WorkspaceManager manager, string workspace) => manager.State.Workspaces.Single(w => w.Name == workspace).DesktopId!.Value;

    Guid IdOf(WorkspaceManager manager, string workspace) => manager.State.Workspaces.Single(w => w.Name == workspace).Id;

    // One surface whose handler throws once, on the first pulse it sees after this is called. The arrival pulses
    // StateChanged from inside the manager's own desktop-change handler, so the throw travels back up through it.
    static void GivenOneHandlerThatThrowsOnce(WorkspaceManager manager)
    {
        var thrown = false;
        manager.StateChanged.Subscribe(_ =>
        {
            if (thrown) return;
            thrown = true;
            throw new InvalidOperationException("a surface failed once");
        });
    }

    // Windows' CurrentChanged, as the virtual-desktop library raises it: a rethrow out of the handler is the event
    // raiser's problem, and the test plays the raiser.
    void Arrive(WorkspaceManager manager, string workspace)
    {
        desktops.CurrentDesktopId = DesktopOf(manager, workspace);
        try { desktops.CurrentChangedSubject.OnNext(desktops.CurrentDesktopId); }
        catch (Exception) { }
    }

    [Fact]
    public void An_arrival_after_a_failed_one_still_stops_the_blink()
    {
        var manager = Started();
        Arrive(manager, "Notes");
        GivenOneHandlerThatThrowsOnce(manager);
        Arrive(manager, "Notes");

        Assert.True(manager.RequestAttention(IdOf(manager, "Research"), default).Value);
        Arrive(manager, "Research");

        Assert.DoesNotContain(IdOf(manager, "Research"), manager.AskingWorkspaces);
    }

    // The take after: the manager has to know you left Research, or it refuses to mark it as "the current workspace".
    [Fact]
    public void After_a_failed_arrival_the_workspace_you_left_can_still_ask()
    {
        var manager = Started();
        Arrive(manager, "Research");
        GivenOneHandlerThatThrowsOnce(manager);
        Arrive(manager, "Research");
        Arrive(manager, "Notes");

        Assert.Equal(IdOf(manager, "Notes"), manager.CurrentWorkspaceId.GetValueOrDefault());
        Assert.True(manager.RequestAttention(IdOf(manager, "Research"), default).Value);
    }

    // The pipe's `switch` end to end, the way the report did it: the ask is gone once Windows says you arrived.
    [Fact]
    public void Switch_from_the_pipe_after_a_failed_arrival_clears_the_ask()
    {
        var manager = Started();
        Arrive(manager, "Notes");
        GivenOneHandlerThatThrowsOnce(manager);
        Arrive(manager, "Notes");
        manager.RequestAttention(IdOf(manager, "Research"), default);

        Assert.Equal(ControlReply.Ok, new RemoteControl(manager).Execute(["switch", "Research"]).ExitCode);
        Arrive(manager, "Research");

        Assert.DoesNotContain(IdOf(manager, "Research"), manager.AskingWorkspaces);
    }

    // Named in the trace with what threw, as a failed window event is, so the next occurrence names its cause.
    [Fact]
    public void A_failed_arrival_is_written_to_the_trace_with_what_threw()
    {
        var manager = Started();
        GivenOneHandlerThatThrowsOnce(manager);

        Arrive(manager, "Research");

        Assert.Contains(traced, line => line.Contains("desktop change") && line.Contains("Research")
                                        && line.Contains(nameof(InvalidOperationException)));
    }

    // The same exposure on the taskbar flash: OnFlashed pulses StateChanged inline too, so one failed flash used to
    // end the attention dot until a restart. (The other desktop event, Destroyed, was already guarded in
    // OnDesktopDestroyed.)
    [Fact]
    public void A_window_that_flashes_after_a_failed_flash_still_gets_its_dot()
    {
        var attention = new FakeAttention();
        var desktop = desktops.Create("Main").Value;
        desktops.CurrentDesktopId = desktop.Id;
        var chat = new WindowInfo(new WindowHandle(0x1), 1, "Chat", @"C:\apps\Chat.exe", "Chat", null);
        var mail = new WindowInfo(new WindowHandle(0x2), 2, "Mail", @"C:\apps\Mail.exe", "Mail", null);
        monitor.InitialWindows.AddRange([chat, mail]);
        desktops.WindowPlacements[chat.Handle] = desktop.Id;
        desktops.WindowPlacements[mail.Handle] = desktop.Id;
        var manager = new WorkspaceManager(desktops, monitor, new FakeTitles(), store, attention: attention, trace: traced.Add);
        Assert.True(manager.Start().IsSuccess);
        GivenOneHandlerThatThrowsOnce(manager);

        try { attention.Subject.OnNext(chat.Handle); }
        catch (Exception) { }
        attention.Subject.OnNext(mail.Handle);

        Assert.True(manager.WindowsByWorkspace().Value.OtherDesktops.Single().Windows.Single(r => r.Window.Handle == mail.Handle).WantsAttention);
    }
}
