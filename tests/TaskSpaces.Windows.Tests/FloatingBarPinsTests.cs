using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using CSharpFunctionalExtensions;
using TaskSpaces.App;
using TaskSpaces.Core;
using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Domain;
using TaskSpaces.Windows.Shell;

namespace TaskSpaces.Windows.Tests;

// The taskbar's pins as a dock line above the strip (Petre: "can you also show pinned icons somewhere?",
// "taskbar pinned", then "a"). Every bar here gets a FakePins: the real ShellTaskbarPins would put the pins of
// whoever runs the suite on the bar, and a click would start their apps.
public class FloatingBarPinsTests
{
    sealed class FakePins(params TaskbarPin[] start) : ITaskbarPins
    {
        public List<TaskbarPin> Current { get; } = [.. start];
        public List<string> Launched { get; } = [];
        public int Version { get; set; } = 1;
        public IReadOnlyList<TaskbarPin> Read() => [.. Current];
        public Maybe<PinImage> ImageOf(TaskbarPin pin, int size) => Maybe<PinImage>.None;
        public string? AppIdOf(WindowHandle window) => null;

        public Result Launch(TaskbarPin pin)
        {
            Launched.Add(pin.Name);
            return Result.Success();
        }

        public Result Reveal(TaskbarPin pin) => Result.Success();
    }

    static TaskbarPin Shortcut(string name, string target, string? appId = null) =>
        new(name, $@"C:\Pins\{name}.lnk", appId, target, []);

    static TaskbarPin InstalledApp(string name, string appId) => new(name, appId, appId, null, []);

    static FloatingBar Bar(ITaskbarPins? pins)
    {
        var desktops = new PulsingDesktops { CurrentId = Guid.NewGuid() };
        desktops.Desktops.Add(new DesktopInfo(desktops.CurrentId, "Work"));
        var manager = new WorkspaceManager(desktops, new StubMonitor(), new StubTitles(), new StubStore());
        Assert.True(manager.Start().IsSuccess);
        var bar = new FloatingBar(manager) { Left = -32000, Top = -32000, SendShellKeys = _ => { }, Pins = pins };
        bar.ShowBar();
        bar.UpdateLayout();
        return bar;
    }

    static WrapPanel Line(FloatingBar bar) => (WrapPanel)bar.FindName("PinLine")!;

    static List<string> Names(FloatingBar bar) =>
        Line(bar).Children.OfType<Button>().Select(AutomationProperties.GetName).ToList();

    static Button Pin(FloatingBar bar, string name) =>
        Line(bar).Children.OfType<Button>().Single(b => AutomationProperties.GetName(b) == name);

    static WindowInfo Window(string exe, int handle = 1) =>
        new(new WindowHandle(handle), 100, System.IO.Path.GetFileNameWithoutExtension(exe), exe, "a window", null);

    [Fact]
    public void Without_a_pin_source_there_is_no_dock_line() => StaThread.Run(() =>
    {
        var bar = Bar(null);
        Assert.Equal(Visibility.Collapsed, Line(bar).Visibility);
        bar.Close();
    });

    // In the taskbar's order, not alphabetical: the order is the point of reading the pin list rather than
    // the pinned shortcuts folder.
    [Fact]
    public void The_pins_show_in_the_taskbars_order() => StaThread.Run(() =>
    {
        var bar = Bar(new FakePins(
            Shortcut("Microsoft Edge", @"C:\Edge\msedge.exe", "MSEdge"),
            InstalledApp("YouTube Music", "music.youtube.com!App"),
            Shortcut("Google Chrome", @"C:\Chrome\chrome.exe", "Chrome")));

        Assert.Equal(Visibility.Visible, Line(bar).Visibility);
        Assert.Equal(["Microsoft Edge", "YouTube Music", "Google Chrome"], Names(bar));
        bar.Close();
    });

    // A pin with no window in this workspace starts its app.
    [Fact]
    public void Clicking_a_pin_with_nothing_open_here_starts_it() => StaThread.Run(() =>
    {
        var pins = new FakePins(Shortcut("Obsidian", @"C:\Obsidian\Obsidian.exe", "md.obsidian"));
        var bar = Bar(pins);

        Pin(bar, "Obsidian").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal(["Obsidian"], pins.Launched);
        bar.Close();
    });

    // Middle-click is always a new window, as on the taskbar.
    [Fact]
    public void Middle_clicking_a_pin_starts_a_new_window() => StaThread.Run(() =>
    {
        var pins = new FakePins(Shortcut("Obsidian", @"C:\Obsidian\Obsidian.exe", "md.obsidian"));
        var bar = Bar(pins);

        Pin(bar, "Obsidian").RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Middle) { RoutedEvent = UIElement.MouseUpEvent });
        Assert.Equal(["Obsidian"], pins.Launched);
        bar.Close();
    });

    // The line follows the taskbar: a pin added there shows up once the change counter moves, and not before.
    [Fact]
    public void A_pin_added_on_the_taskbar_appears() => StaThread.Run(() =>
    {
        var pins = new FakePins(Shortcut("Obsidian", @"C:\Obsidian\Obsidian.exe"));
        var bar = Bar(pins);

        pins.Current.Add(Shortcut("RustDesk", @"C:\RustDesk\RustDesk.exe"));
        bar.ReloadPins();
        Assert.Equal(["Obsidian"], Names(bar));

        pins.Version++;
        bar.ReloadPins();
        Assert.Equal(["Obsidian", "RustDesk"], Names(bar));
        bar.Close();
    });

    // --- which windows are a pin's ------------------------------------------------------------------------

    static readonly IReadOnlySet<string> Claimed =
        new HashSet<string>(["MSEdge", "Chrome", "music.youtube.com!App"], StringComparer.OrdinalIgnoreCase);

    // Both say an AppUserModelID: they decide it. Edge's windows say MSEdge, as its pin does (measured).
    [Fact]
    public void A_window_with_the_pins_app_id_is_the_pins() =>
        Assert.True(FloatingBar.PinMatches(Shortcut("Edge", @"C:\Edge\msedge.exe", "MSEdge"), Window(@"C:\Edge\msedge.exe"), "MSEdge", Claimed));

    // A PWA runs as its browser's exe (measured: YouTube Music in the same process as every Edge window), so the
    // exe alone would light the browser's pin for it. Its own ID says otherwise, and wins.
    [Fact]
    public void A_pwa_is_not_its_browsers() =>
        Assert.False(FloatingBar.PinMatches(Shortcut("Edge", @"C:\Edge\msedge.exe", "MSEdge"), Window(@"C:\Edge\msedge.exe"), "music.youtube.com!App", Claimed));

    [Fact]
    public void A_pwa_is_its_own_pins() =>
        Assert.True(FloatingBar.PinMatches(InstalledApp("YouTube Music", "music.youtube.com!App"), Window(@"C:\Edge\msedge.exe"), "music.youtube.com!App", Claimed));

    // A pin with no ID of its own (Volume Mixer, RustDesk, LINQPad on Petre's taskbar) goes by the exe it starts.
    [Fact]
    public void A_pin_without_an_app_id_goes_by_its_exe() =>
        Assert.True(FloatingBar.PinMatches(Shortcut("RustDesk", @"C:\RustDesk\RustDesk.exe"), Window(@"C:\rustdesk\rustdesk.exe"), null, Claimed));

    // ...but not for a window whose ID another pin claims outright.
    [Fact]
    public void The_exe_rule_stands_down_for_a_claimed_window() =>
        Assert.False(FloatingBar.PinMatches(Shortcut("Old Edge", @"C:\Edge\msedge.exe"), Window(@"C:\Edge\msedge.exe"), "music.youtube.com!App", Claimed));

    // An installed app is only ever matched by its ID; it has no exe of its own to compare.
    [Fact]
    public void An_installed_app_needs_its_id() =>
        Assert.False(FloatingBar.PinMatches(InstalledApp("Files", "Files!App"), Window(@"C:\Files\Files.exe"), null, Claimed));

    // --- the pin list's format ------------------------------------------------------------------------------

    // [flag][uint32 size][size bytes], repeated, as measured. A size that runs off the end stops the walk and
    // keeps what came before it.
    [Fact]
    public void The_pin_list_splits_into_its_entries_and_stops_at_a_bad_size()
    {
        static byte[] Entry(params byte[] idList) => [0, .. BitConverter.GetBytes((uint)idList.Length), .. idList];
        byte[] blob = [.. Entry(1, 2, 0, 0), .. Entry(3, 4, 5, 0, 0), 0, .. BitConverter.GetBytes(999u), 7, 7];

        var lists = ShellTaskbarPins.SplitFavorites(blob);

        Assert.Equal(2, lists.Count);
        Assert.Equal(new byte[] { 1, 2, 0, 0 }, lists[0]);
        Assert.Equal(new byte[] { 3, 4, 5, 0, 0 }, lists[1]);
    }
}
