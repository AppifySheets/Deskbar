using Deskbar.Core.Domain;

namespace Deskbar.Core.Tests;

// WHEN THE ASKING WINDOW'S BORDER BLINKS, AND WHEN IT STOPS (WorkspaceManager.SettleAsks). Petre: "when i activate
// the deskbar where an app is blinking; can you have a blinking border for that window that's asking for
// attention?", then "yes on both" (stop on a click into it, or after a few seconds) and "a" from the mockup.
//
// A marked window starts blinking once it is on the desktop in front while someone is at the machine, and its mark
// ends five seconds later, or sooner on input while it is the window in front. Time is a clock the tests move; how
// long ago the last input was is the argument the bar's timer passes in.
public class AskingBorderTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();
    readonly FakeAttention attention = new();
    DateTimeOffset now = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);

    static WindowInfo Window(nint handle, string process) =>
        new(new WindowHandle(handle), (int)handle, process, $@"C:\{process}.exe", $"{process} window", $@"""C:\{process}.exe""");

    readonly WindowInfo chat = Window(0x1, "Chat");
    readonly WindowInfo editor = Window(0x2, "Editor");

    static readonly TimeSpan Here = TimeSpan.FromSeconds(1);     // input a second ago: someone is at the machine
    static readonly TimeSpan Away = TimeSpan.FromMinutes(10);   // nobody has touched it for ten minutes

    WorkspaceManager Started()
    {
        var desktop = desktops.Create("Main").Value;
        desktops.CurrentDesktopId = desktop.Id;
        monitor.InitialWindows.AddRange([chat, editor]);
        desktops.WindowPlacements[chat.Handle] = desktop.Id;
        desktops.WindowPlacements[editor.Handle] = desktop.Id;

        var manager = new WorkspaceManager(desktops, monitor, titles, store, clock: () => now, attention: attention);
        Assert.True(manager.Start().IsSuccess);
        // The editor has focus, so a flash from the chat window marks it (a flash from the focused one never does).
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Activated, editor));
        return manager;
    }

    bool Marked(WorkspaceManager manager, WindowInfo window) =>
        manager.WindowsByWorkspace().Value.OtherDesktops.Single().Windows.Single(r => r.Window.Handle == window.Handle).WantsAttention;

    [Fact]
    public void A_marked_window_in_front_blinks_for_five_seconds_and_is_then_settled()
    {
        var manager = Started();
        attention.Subject.OnNext(chat.Handle);

        manager.SettleAsks(Here);
        Assert.Equal([chat.Handle], manager.BlinkingWindows);
        Assert.True(Marked(manager, chat));

        now += TimeSpan.FromSeconds(4);
        manager.SettleAsks(Here);
        Assert.Equal([chat.Handle], manager.BlinkingWindows);

        now += TimeSpan.FromSeconds(1);
        manager.SettleAsks(Here);
        Assert.Empty(manager.BlinkingWindows);
        Assert.False(Marked(manager, chat));
        Assert.False(manager.HasWindowMarks);
    }

    // A flash while nobody is at the machine waits for them: its five seconds start when they come back.
    [Fact]
    public void Nobody_at_the_machine_means_no_blink_yet()
    {
        var manager = Started();
        attention.Subject.OnNext(chat.Handle);

        manager.SettleAsks(Away);
        now += TimeSpan.FromMinutes(10);
        manager.SettleAsks(Away);
        Assert.Empty(manager.BlinkingWindows);
        Assert.True(Marked(manager, chat));

        manager.SettleAsks(Here);
        Assert.Equal([chat.Handle], manager.BlinkingWindows);
    }

    // Clicking or typing in the asking window answers it at once...
    [Fact]
    public void Input_while_the_asking_window_is_in_front_settles_it()
    {
        var manager = Started();
        attention.Subject.OnNext(chat.Handle);
        manager.SettleAsks(Here);
        Assert.True(Marked(manager, chat));

        // Focused in the arrival's own moment (ArrivalGrace), which does not clear it...
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Activated, chat));
        now += TimeSpan.FromSeconds(2);
        // ...then a keystroke a moment ago, well after it started blinking.
        manager.SettleAsks(TimeSpan.FromMilliseconds(100));

        Assert.Empty(manager.BlinkingWindows);
        Assert.False(Marked(manager, chat));
    }

    // ...but input in ANOTHER window does not: it keeps blinking until its five seconds are up.
    [Fact]
    public void Input_in_another_window_leaves_it_blinking()
    {
        var manager = Started();
        attention.Subject.OnNext(chat.Handle);
        manager.SettleAsks(Here);

        now += TimeSpan.FromSeconds(2);
        manager.SettleAsks(TimeSpan.FromMilliseconds(100));   // typing in the editor, which has focus

        Assert.Equal([chat.Handle], manager.BlinkingWindows);
    }

    // A window that closed while marked loses its mark, or the bar's timer would run for it forever.
    [Fact]
    public void A_closed_window_loses_its_mark()
    {
        var manager = Started();
        attention.Subject.OnNext(chat.Handle);
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Disappeared, chat));

        manager.SettleAsks(Here);

        Assert.False(manager.HasWindowMarks);
    }

    // A window on a desktop you are not on is not seen, so it does not blink, however present you are.
    [Fact]
    public void A_marked_window_out_of_sight_does_not_blink()
    {
        var manager = Started();
        var elsewhere = desktops.Create("Other").Value;
        desktops.WindowPlacements[chat.Handle] = elsewhere.Id;
        attention.Subject.OnNext(chat.Handle);

        manager.SettleAsks(Here);

        Assert.Empty(manager.BlinkingWindows);
        Assert.True(manager.HasWindowMarks);
    }

    // Blinks start and end with one pulse each; a tick that changes nothing pulses nothing.
    [Fact]
    public void Only_a_change_pulses()
    {
        var manager = Started();
        attention.Subject.OnNext(chat.Handle);
        var pulses = 0;
        using var subscription = manager.StateChanged.Subscribe(_ => pulses++);

        manager.SettleAsks(Here);            // starts
        manager.SettleAsks(Here);            // nothing new
        now += TimeSpan.FromSeconds(5);
        manager.SettleAsks(Here);            // ends

        Assert.Equal(2, pulses);
    }
}
