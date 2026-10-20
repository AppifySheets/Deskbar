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

        // ...and the arguments each launch was given, in the same order.
        public List<string?> LaunchedWith { get; } = [];

        public Result Launch(TaskbarPin pin, string? arguments = null)
        {
            Launched.Add(pin.Name);
            LaunchedWith.Add(arguments);
            return Result.Success();
        }

        public Result Reveal(TaskbarPin pin) => Result.Success();
        public string? NameOfApp(string appId) => null;
        public Maybe<PinImage> ImageOfApp(string appId, int size) => Maybe<PinImage>.None;

        // As the taskbar would: the pin goes and the change counter moves.
        public Result Unpin(TaskbarPin pin)
        {
            Current.Remove(pin);
            Version++;
            return Result.Success();
        }
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

    static Panel Line(FloatingBar bar) => (Panel)bar.FindName("PinLine")!;

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

    // Petre: "i want it to always open a new window". The VS Code pin starts VS Code with --new-window, so a new
    // window opens whatever VS Code's own setting says; any other pin starts as its shortcut stands.
    [Fact]
    public void The_vs_code_pin_always_asks_for_a_new_window() => StaThread.Run(() =>
    {
        var pins = new FakePins(
            Shortcut("Visual Studio Code", @"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\Code.exe"),
            Shortcut("Obsidian", @"C:\Obsidian\Obsidian.exe", "md.obsidian"));
        var bar = Bar(pins);

        Pin(bar, "Visual Studio Code").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pin(bar, "Obsidian").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        Assert.Equal(["Visual Studio Code", "Obsidian"], pins.Launched);
        Assert.Equal(["--new-window", null], pins.LaunchedWith);
        bar.Close();
    });

    // Petre: "clicking vscode in quick launch doesn't open it". A Deskbar started from a VS Code terminal carried
    // ELECTRON_RUN_AS_NODE=1 (and VSCODE_*), so every Electron app it started ran as bare Node and exited.
    // Those are dropped at startup; anything else stays.
    [Theory]
    [InlineData("ELECTRON_RUN_AS_NODE", true)]
    [InlineData("VSCODE_IPC_HOOK", true)]
    [InlineData("VSCODE_PID", true)]
    [InlineData("PATH", false)]
    [InlineData("ELECTRON_ENABLE_LOGGING", false)]
    [InlineData("DESKBAR_APPDATA", false)]
    public void Only_the_editor_hosts_variables_are_dropped(string name, bool dropped) =>
        Assert.Equal(dropped, LaunchEnvironment.IsHostOnly(name));

    [Fact]
    public void Cleaning_removes_them_from_the_process_environment()
    {
        Environment.SetEnvironmentVariable("VSCODE_DESKBAR_TEST_PROBE", "1");
        Environment.SetEnvironmentVariable("DESKBAR_TEST_KEEP", "1");

        LaunchEnvironment.Clean();

        Assert.Null(Environment.GetEnvironmentVariable("VSCODE_DESKBAR_TEST_PROBE"));
        Assert.Null(Environment.GetEnvironmentVariable("ELECTRON_RUN_AS_NODE"));
        Assert.Equal("1", Environment.GetEnvironmentVariable("DESKBAR_TEST_KEEP"));
        Environment.SetEnvironmentVariable("DESKBAR_TEST_KEEP", null);
    }

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

    // Petre: "i don't really like this distribution", then "b": the pins packed from the left in 26 DIP cells.
    // His ten fit one line of his 273 DIP bar, each cell straight after the last, no gaps.
    [Fact]
    public void Pins_pack_from_the_left_on_one_line_when_they_fit()
    {
        var points = PackedPinPanel.Layout(10, 273, 26, 26, seam: null);
        Assert.All(points, p => Assert.Equal(0, p.Y));
        Assert.Equal(Enumerable.Range(0, 10).Select(i => i * 26.0), points.Select(p => p.X));
    }

    // "will b move to the next line if it's more icons?" Yes: the eleventh starts a second line at the left
    // edge, still packed, rather than the pins spreading out to share the width.
    [Fact]
    public void More_pins_than_fit_wrap_onto_a_new_line_still_packed()
    {
        var points = PackedPinPanel.Layout(12, 273, 26, 26, seam: null);
        Assert.Equal(new System.Windows.Point(0, 26), points[10]);
        Assert.Equal(new System.Windows.Point(26, 26), points[11]);
        // However narrow, the first cell of a line always goes, so a pin is never lost.
        Assert.Equal(new System.Windows.Point(0, 26), PackedPinPanel.Layout(2, 10, 26, 26, seam: null)[1]);
    }

    // Docked across two screens: no cell may straddle the line between them ("icons at the bottom shouldn't be
    // broken into halves"). A pin that would start before the seam and end after it starts AT the seam, so the
    // left screen's half fills and the line carries on in the right half.
    [Fact]
    public void Across_the_seam_no_pin_sits_on_the_line_between_the_screens()
    {
        const double seam = 273;
        var points = PackedPinPanel.Layout(20, 546, 26, 26, seam);
        Assert.DoesNotContain(points, p => p.X < seam && p.X + 26 > seam);
        // Ten fit before the seam (260), the eleventh starts on it, and the right half takes ten more.
        Assert.Equal(seam, points[10].X);
        Assert.All(points, p => Assert.Equal(0, p.Y));
    }

    // Petre: "rightclick should have unpin option". It is on the pin's menu, and the pin leaves the line at once
    // rather than at the next poll.
    [Fact]
    public void Unpinning_from_the_menu_takes_the_pin_off_the_line() => StaThread.Run(() =>
    {
        var pins = new FakePins(Shortcut("Obsidian", @"C:\Obsidian\Obsidian.exe"), Shortcut("RustDesk", @"C:\RustDesk\RustDesk.exe"));
        var bar = Bar(pins);

        var unpin = Pin(bar, "Obsidian").ContextMenu!.Items.OfType<MenuItem>().Single(i => (string)i.Header == "Unpin from taskbar");
        unpin.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        Assert.Equal(["RustDesk"], Names(bar));
        bar.Close();
    });

    // Petre: "when clicking on quicklaunch icons, don't activate existing browser windows, open a new one
    // immediately". A browser pin is told by the exe it starts; an installed web app is not one, even though
    // it runs as the browser.
    [Theory]
    [InlineData(@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", true)]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe", true)]
    [InlineData(@"C:\Program Files\Mozilla Firefox\firefox.exe", true)]
    [InlineData(@"C:\Program Files\Obsidian\Obsidian.exe", false)]
    [InlineData(@"C:\Tools\Something\launcher.exe", false)]
    [InlineData(@"C:\Users\me\AppData\Local\Programs\Opera\launcher.exe", true)]
    public void A_browser_pin_is_told_by_its_exe(string target, bool browser) =>
        Assert.Equal(browser, FloatingBar.IsBrowser(Shortcut("Pin", target)));

    // VS Code's pin opens a new window like a browser's (Petre: "clicking vscode doesn't open a new window, it
    // minimizes the current vscode window").
    [Theory]
    [InlineData(@"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\Code.exe", true)]
    [InlineData(@"C:\Users\me\AppData\Local\Programs\Microsoft VS Code Insiders\Code - Insiders.exe", true)]
    [InlineData(@"C:\Windows\explorer.exe", false)]
    public void A_vs_code_pin_is_told_by_its_exe(string target, bool editor) =>
        Assert.Equal(editor, FloatingBar.IsEditor(Shortcut("Pin", target)));

    [Fact]
    public void An_installed_web_app_is_not_a_browser_pin() =>
        Assert.False(FloatingBar.IsBrowser(InstalledApp("YouTube Music", "music.youtube.com!App")));

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
