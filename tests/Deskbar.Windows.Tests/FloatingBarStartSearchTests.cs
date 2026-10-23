using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Deskbar.App;
using Deskbar.Core;
using Deskbar.Core.Abstractions;
using Deskbar.Core.Persistence;
using ClassedWindow = Deskbar.Windows.Tests.FloatingBarTrayArrowTests.ClassedWindow;

namespace Deskbar.Windows.Tests;

// Start and search where the shell may show both in ONE window. Measured on Windows 11 build 26300: the Windows key and
// Win+S bring the same Windows.UI.Core.CoreWindow, titled "Search", of SearchHost.exe, so what is in front does not say
// which button opened it. Before the fix the bar's confirming check read that window as Search, sent Win again and shut
// Start 0.7 s after it opened (or opened it again after the user had dismissed it), and the buttons did not light.
//
// Driven through the entry points the app uses (a click, NoteForeground, the bar's activation, the 1 s tick) with the
// confirming check run for real and no real key pressed. The shell's windows are stood in for by hidden windows of the same
// class and title (ClassedWindow), since a test cannot make the real ones take the foreground.
public class FloatingBarStartSearchTests
{
    const string CoreWindow = "Windows.UI.Core.CoreWindow";
    const string StartMenu = "DV2ControlHost";

    // One bar on a thread of its own, parked off the virtual screen like every other bar test, with the confirming check after
    // 20 ms instead of 700. The keys it would have pressed go to `Sent`, by button name, and `onKeys` hears each one with how
    // many there are so far, so a test can have the window come up as a key goes out. `Shared` is the window both buttons open
    // here. `Front` is what Windows says is in front, which the bar asks on activation and on the tick: nothing until a test
    // sets it, because a test must not read the real foreground.
    sealed class Scene : IDisposable
    {
        public readonly List<string> Sent = [];
        public readonly ClassedWindow Shared = new(CoreWindow, "Search");
        public readonly WorkspaceManager Manager;
        public readonly FloatingBar Bar;
        public nint Front;

        public Scene(Action<Scene, int>? onKeys)
        {
            var desktops = new PulsingDesktops { CurrentId = Guid.NewGuid() };
            desktops.Desktops.Add(new DesktopInfo(desktops.CurrentId, "Work"));
            Manager = new WorkspaceManager(desktops, new StubMonitor(), new StubTitles(), new StubStore());
            Assert.True(Manager.Start().IsSuccess);
            Bar = new FloatingBar(Manager)
            {
                Left = -32000, Top = -32000, PressSpace = () => { }, ShellCheckDelay = TimeSpan.FromMilliseconds(20),
                SendShellKeys = flyout => { Sent.Add(flyout.ToString()); onKeys?.Invoke(this, Sent.Count); },
                ForegroundWindow = () => Front,
            };
            Bar.ShowBar();
            Bar.UpdateLayout();
        }

        public void Dispose()
        {
            Bar.Close();
            Shared.Dispose();
        }

        // Both pairs of buttons (the second sits hidden until the bar is docked across two screens), and the first pair's.
        public List<Button> Buttons(string name) =>
            Descendants(Bar).OfType<Button>().Where(b => AutomationProperties.GetName(b) == name).ToList();

        public Button First(string name) =>
            ((Panel)Bar.FindName("ShellStart")!).Children.OfType<Button>().Single(b => AutomationProperties.GetName(b) == name);

        // Whether the button looks as it does on hover. Both pairs must agree, so one that was left behind fails here.
        public bool Lit(string name) => Assert.Single(Buttons(name).Select(b => Paint(b) == Hover(b)).Distinct());

        // A click as the user makes it: the press (the tunnelling half, which the app notes before anything else because it takes
        // the foreground from an open flyout), then the click.
        public void Click(string name) { Press(name); Release(name); }

        public void Press(string name) =>
            First(name).RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });

        public void Release(string name) => First(name).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        // The shared window comes to the front, or some other window does.
        public void Up() => Bar.NoteForeground(Shared.Handle);
        public void Away() => Bar.NoteForeground(0x0BAD);

        // The confirming check (and a retry's) is a timer, so the dispatcher runs until it is over. Bounded.
        public void AwaitCheck()
        {
            var clock = Stopwatch.StartNew();
            while (Bar.ShellCheckPending)
            {
                Assert.True(clock.ElapsedMilliseconds < 5000, "the confirming check never finished");
                Bar.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                Thread.Sleep(2);
            }
        }

        // The flyout up and its check over: opened by a click, or from the keyboard with nobody asking.
        public void Open(string name, bool keyboard = false)
        {
            if (!keyboard) Click(name);
            Up();
            AwaitCheck();
        }

        // What an Esc on the open flyout does: Windows gives the foreground to the bar and its window is activated. Raised through
        // the window's own OnActivated, as WM_ACTIVATE does (WPF raises Activated from it, synchronously).
        public void Activate()
        {
            Front = new WindowInteropHelper(Bar).Handle;
            typeof(Window).GetMethod("OnActivated", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Bar, [EventArgs.Empty]);
        }

        // Everything queued at Background priority or above has run, which is where an activation's re-sync waits. Bounded.
        public void Drain() =>
            Bar.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle, CancellationToken.None, TimeSpan.FromSeconds(5));
    }

    static void Run(Action<Scene> test, Action<Scene, int>? onKeys = null) => StaThread.Run(() =>
    {
        using var scene = new Scene(onKeys);
        test(scene);
    });

    static IEnumerable<DependencyObject> Descendants(DependencyObject node) =>
        LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()
            .SelectMany(child => new[] { child }.Concat(Descendants(child)));

    // What the button is painted with (the template's border, which shows the button's Background unless the pointer is on it),
    // and what the template paints on hover.
    static Color Paint(Button button)
    {
        button.ApplyTemplate();
        return ((SolidColorBrush)((Border)button.Template.FindName("Chrome", button)).Background).Color;
    }

    static Color Hover(Button button) =>
        ((SolidColorBrush)button.Template.Triggers.OfType<Trigger>()
            .Single(t => t.Property == UIElement.IsMouseOverProperty).Setters.OfType<Setter>().Single().Value).Color;

    // The decision on its own, with no process to look at: the class and the process, or failing that the English title.
    [Theory]
    [InlineData(CoreWindow, "Search", "SearchHost", true)]
    [InlineData(CoreWindow, "Zoeken", "SearchHost", true)]      // another language: the process says it
    [InlineData(CoreWindow, "Search", "", true)]                // a process that cannot be read: the title still does
    [InlineData(CoreWindow, "Notification Center", "ShellExperienceHost", false)]
    [InlineData(StartMenu, "Start menu", "explorer", false)]    // a Start of its own is not the shared window
    [InlineData("Notepad", "Search", "SearchHost", false)]
    public void The_shared_window_is_told_by_class_and_process_or_title(string windowClass, string title, string process, bool shared) =>
        Assert.Equal(shared, ShellKeys.IsStartSearchHost(windowClass, title, process));

    // The window the button opened stays (the check sends nothing more) and the button looks as on hover for as long as it is in
    // front, and only that button, though both open the same window. One the user dismisses before the check comes (a click on
    // the desktop) is not opened again by it.
    [Theory]
    [InlineData("Start", "Search")]
    [InlineData("Search", "Start")]
    public void The_flyout_a_button_opened_stays_and_only_its_button_is_lit_until_it_closes(string name, string other) => Run(s =>
    {
        s.Click(name);
        Assert.Equal([name], s.Sent);
        Assert.False(s.Lit(name));

        s.Up();
        Assert.True(s.Lit(name));
        Assert.False(s.Lit(other));

        // The check comes and goes with the window up. A second Win would shut it, which is what used to happen.
        s.AwaitCheck();
        Assert.Equal([name], s.Sent);
        Assert.True(s.Lit(name));

        // However it closes, away from the bar, the light goes out.
        s.Away();
        Assert.False(s.Lit(name));
        Assert.Equal([name], s.Sent);

        s.Click(name);
        s.Up();
        s.Away();
        s.AwaitCheck();
        Assert.Equal([name, name], s.Sent);
        Assert.False(s.Lit(name));
    });

    // A click on the button while its flyout is open closes it and sends nothing that would open it again, from the press on and
    // still when the first click's check is yet to come. The next click opens it as normal.
    [Theory]
    [InlineData("Start")]
    [InlineData("Search")]
    public void A_click_on_the_button_while_its_flyout_is_open_only_closes_it(string name) => Run(s =>
    {
        s.Click(name);
        s.Up();
        s.Click(name);
        Assert.Equal([name], s.Sent);
        Assert.False(s.Lit(name));

        s.AwaitCheck();
        Assert.Equal([name], s.Sent);

        s.Click(name);
        Assert.Equal([name, name], s.Sent);
    });

    // The press shuts Start, the click on search opens search in the same window, and the new opener is search: a window still
    // counted as Start's would light the wrong button.
    [Fact]
    public void Opening_search_over_an_open_start_names_search_as_the_opener() => Run(s =>
    {
        s.Open("Start");
        s.Click("Search");
        Assert.Equal(["Start", "Search"], s.Sent);

        s.Up();
        Assert.True(s.Lit("Search"));
        Assert.False(s.Lit("Start"));
    });

    // A keystroke that was lost, so nothing ever came to the front, is sent once more and no more. And when the retry is what
    // opens the window, it is still the button's: it comes up just after the retry goes out (queued behind the check that sent
    // it, as on the real machine, where it takes tens of milliseconds).
    [Theory]
    [InlineData("Start", "Search", false)]
    [InlineData("Search", "Start", false)]
    [InlineData("Start", "Search", true)]
    public void A_lost_keystroke_is_sent_once_more_and_no_more(string name, string other, bool retryOpens) => Run(s =>
    {
        s.Click(name);
        s.AwaitCheck();

        Assert.Equal([name, name], s.Sent);
        Assert.Equal(retryOpens, s.Lit(name));
        Assert.False(s.Lit(other));
    }, onKeys: retryOpens ? (s, count) => { if (count == 2) s.Bar.Dispatcher.InvokeAsync(s.Up); } : null);

    // A window the keyboard opened has no known opener. Neither button lights, since either could be right, and a click on EITHER
    // only closes it: a key would open it again, since the press has already closed it.
    [Fact]
    public void A_window_the_keyboard_opened_lights_nothing_and_a_click_on_either_button_only_closes_it() => Run(s =>
    {
        foreach (var name in new[] { "Start", "Search" })
        {
            s.Up();
            Assert.False(s.Lit("Start"));
            Assert.False(s.Lit("Search"));

            s.Click(name);
            Assert.Empty(s.Sent);
        }

        // Closed now, so the next click opens it as normal.
        s.Click("Start");
        Assert.Equal(["Start"], s.Sent);
    });

    // Where Start has a window of its own, DV2ControlHost (the maintainer's machine), everything works as before and lights up.
    // The "Search" window there is only search, so it is lit for search whoever opened it, and a click on Start still opens Start.
    [Fact]
    public void Where_start_has_a_window_of_its_own_everything_works_as_before_and_lights_up() => Run(s =>
    {
        using var start = new ClassedWindow(StartMenu, "Start menu");

        s.Click("Start");
        s.Bar.NoteForeground(start.Handle);
        Assert.True(s.Lit("Start"));
        Assert.False(s.Lit("Search"));
        s.AwaitCheck();
        Assert.Equal(["Start"], s.Sent);

        s.Click("Start");
        Assert.Equal(["Start"], s.Sent);
        Assert.False(s.Lit("Start"));

        s.Up();
        Assert.True(s.Lit("Search"));
        Assert.False(s.Lit("Start"));
        s.Click("Start");
        Assert.Equal(["Start", "Start"], s.Sent);

        s.Up();
        s.Click("Search");
        Assert.Equal(2, s.Sent.Count);
    });

    // The light is what the template paints on hover, and the buttons keep it through a new layout: the second pair shown across
    // the seam and hidden again, and the rebuild a setting change brings.
    [Fact]
    public void The_open_look_is_the_hover_look_and_survives_a_new_layout() => Run(s =>
    {
        s.Click("Start");
        s.Up();
        var start = s.First("Start");
        Assert.True(s.Lit("Start"));
        Assert.NotEqual(Colors.Transparent, Paint(start));

        s.Bar.LayOutHeader(acrossSeam: true);
        s.Bar.UpdateLayout();
        Assert.Equal(2, s.Buttons("Start").Count(b => b.IsVisible));
        Assert.True(s.Lit("Start"));
        Assert.False(s.Lit("Search"));

        Assert.True(s.Manager.SetHiddenStripItems([StripItem.Notifications]).IsSuccess);
        s.Bar.LayOutHeader(acrossSeam: false);
        s.Bar.UpdateLayout();
        Assert.Same(start, s.First("Start"));
        Assert.True(s.Lit("Start"));
    });

    // A newer click takes over from an older click's confirming check: its shortcut now would undo the newer one. Nothing comes
    // up here, so a check that did not stand aside would send its own retry.
    [Fact]
    public void A_newer_click_supersedes_an_older_confirming_check() => Run(s =>
    {
        s.Click("Start");
        s.Click("Search");
        s.AwaitCheck();

        Assert.Equal(["Start", "Search", "Search"], s.Sent);
    });

    // THE FLYOUT THAT CLOSES WITHOUT THE BAR HEARING IT. Esc, the Windows key or Win+S shuts it and Windows gives the foreground to
    // the bar, whose own windows the monitor never reports, so NoteForeground is not called. What the bar does hear is its own
    // window being activated and the 1 s tick; `Front` is set as the close does, with no NoteForeground after.
    //
    // The light goes out at once and the next click opens the flyout, once (the bar already has the foreground, so that click
    // does not activate it again). Also for a window the keyboard opened and shut: that click must open Start, not be read as
    // the click that closes a window no longer there.
    [Theory]
    [InlineData("Start", false)]
    [InlineData("Search", false)]
    [InlineData("Start", true)]
    public void The_light_goes_out_when_the_flyout_closes_unheard_and_the_next_click_opens_it(string name, bool keyboard) => Run(s =>
    {
        s.Open(name, keyboard);
        s.Activate();
        s.Drain();
        Assert.False(s.Lit("Start"));
        Assert.False(s.Lit("Search"));

        s.Click(name);
        Assert.Equal(Enumerable.Repeat(name, keyboard ? 1 : 2), s.Sent);
    });

    // The tray's arrow follows the overflow window the same way. The window here is known only by the handle the bar remembered
    // after its own Space (its class is not one a foreground change would name); the arrow's words are its name.
    [Fact]
    public void The_trays_arrow_turns_up_when_the_bar_gets_the_foreground_back_with_no_press() => Run(s =>
    {
        using var overflow = new ClassedWindow("RememberedOverflow", "");
        s.Bar.RememberTrayOverflow(overflow.Handle);
        Assert.Single(s.Buttons("Hide the tray icons"));

        s.Activate();
        s.Drain();
        Assert.Single(s.Buttons("Show the tray icons"));
    });

    // The click that closes it, in the order Windows delivers it: the click ACTIVATES the bar first, and only then comes the press
    // that says the flyout was open. The re-sync the activation queued runs between the press and the release. A re-sync that
    // ran before the press would make this click read "not open" and send Win, which opens Start again.
    [Theory]
    [InlineData("Start", false)]
    [InlineData("Search", false)]
    [InlineData("Start", true)]     // opened from the keyboard: no known opener, and a click on either button only closes it
    public void A_click_while_the_flyout_is_open_only_closes_it_though_the_click_activates_the_bar(string name, bool keyboard) => Run(s =>
    {
        s.Open(name, keyboard);
        var keys = s.Sent.Count;

        s.Activate();
        s.Press(name);
        s.Drain();
        s.Release(name);

        Assert.Equal(keys, s.Sent.Count);
        Assert.False(s.Lit(name));
    });

    // A press after the activation settles the state itself, so the re-sync the activation queued stands down: what the click
    // brought up since is still shown once it has run, though the foreground it would read (stale here, on purpose) says the bar.
    [Fact]
    public void The_resync_an_activation_queued_does_nothing_once_a_press_has_come_after_it() => Run(s =>
    {
        s.Open("Start");
        s.Activate();
        s.Click("Search");
        s.Up();
        Assert.True(s.Lit("Search"));

        s.Drain();
        Assert.True(s.Lit("Search"));
        Assert.Equal(["Start", "Search"], s.Sent);
    });

    // THE TICK, for a close the activation did not tell the bar about: it asks Windows what is in front and lets that decide.
    [Theory]
    [InlineData("Start")]
    [InlineData("Search")]
    public void The_tick_puts_a_stale_light_out_when_no_event_said_the_flyout_closed(string name) => Run(s =>
    {
        // With nothing shown open it does nothing, whatever is in front.
        s.Front = s.Shared.Handle;
        s.Bar.ResyncShellFlyouts();
        Assert.False(s.Lit(name));

        s.Open(name);

        // Still in front: nothing to correct.
        s.Front = s.Shared.Handle;
        s.Bar.ResyncShellFlyouts();
        Assert.True(s.Lit(name));

        // Gone, to some other window, and no event arrived.
        s.Front = 0x0BAD;
        s.Bar.ResyncShellFlyouts();
        Assert.False(s.Lit(name));

        s.Click(name);
        Assert.Equal([name, name], s.Sent);
    });

    // ...and it never settles between the activation of a click and its press: the activation queued its own re-sync, which is
    // behind the press, and the tick leaves the settling to that one.
    [Theory]
    [InlineData("Start")]
    [InlineData("Search")]
    public void The_tick_cannot_land_between_the_activation_of_a_click_and_its_press(string name) => Run(s =>
    {
        s.Open(name);
        s.Activate();
        s.Bar.ResyncShellFlyouts();
        Assert.True(s.Lit(name));

        s.Press(name);
        s.Drain();
        s.Release(name);
        Assert.Equal([name], s.Sent);
    });
}
