using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using TaskSpaces.App;
using TaskSpaces.Core;
using TaskSpaces.Core.Abstractions;
using TaskSpaces.Core.Persistence;

namespace TaskSpaces.Windows.Tests;

// The strip's tray chevron points down while the hidden icons are open, as Windows' own does. It follows the tray's
// overflow window and nothing else: not the bare taskbar, which the bar also counts as "the tray" for closing a flyout
// (ShellKeys.Recognise) and which is in front, with nothing open, after the chevron's own Win+B and after Esc closes
// the overflow. Driven through the entry points the app uses (NoteForeground, RememberTrayOverflow, NoteBarPressed),
// with no real key; the shell's windows are stood in for by hidden windows of the same class names (ClassedWindow).
//
// The chevron TURNS: a test that reads where it points waits for the turn to finish (Chevron), and a test of the turn
// slows it down (Slow) to catch it half way whatever the speed of this thread's frames. The turn ignores Windows'
// "animation effects" setting, so these run the same on any machine.
public class FloatingBarTrayArrowTests
{
    const string Overflow = "NotifyIconOverflowWindow";
    const string XamlOverflow = "TopLevelWindowForOverflowXamlIsland";
    const string Taskbar = "Shell_TrayWnd";
    const string SecondaryTaskbar = "Shell_SecondaryTrayWnd";
    const string StartMenu = "DV2ControlHost";

    // The arrow and the words together (State): Windows' own says "hide" while the hidden icons are open.
    const string Up = "up: Show the tray icons";
    const string Down = "down: Hide the tray icons";

    // Slow enough to catch the arrow half way: it is past 60 degrees after about 0.4 s of it.
    static readonly Duration Slow = new(TimeSpan.FromSeconds(10));

    // `sent` collects the shortcuts the bar would have pressed, for the tests that click the chevron.
    static (FloatingBar Bar, WorkspaceManager Manager) Bar(Duration? turnTime = null, List<FloatingBar.ShellFlyout>? sent = null)
    {
        var desktops = new PulsingDesktops { CurrentId = Guid.NewGuid() };
        desktops.Desktops.Add(new DesktopInfo(desktops.CurrentId, "Work"));
        var manager = new WorkspaceManager(desktops, new StubMonitor(), new StubTitles(), new StubStore());
        Assert.True(manager.Start().IsSuccess);
        // Parked off the virtual screen like every other bar test, with the keys swapped out so nothing is pressed.
        var bar = turnTime is { } time
            ? new FloatingBar(manager) { Left = -32000, Top = -32000, SendShellKeys = flyout => sent?.Add(flyout), PressSpace = () => { }, TrayChevronTurnTime = time }
            : new FloatingBar(manager) { Left = -32000, Top = -32000, SendShellKeys = flyout => sent?.Add(flyout), PressSpace = () => { } };
        bar.ShowBar();
        bar.UpdateLayout();
        return (bar, manager);
    }

    // A bar and its chevron on a thread of their own, closed afterwards.
    static void Run(Action<FloatingBar, Button> test, Duration? turnTime = null, List<FloatingBar.ShellFlyout>? sent = null) => StaThread.Run(() =>
    {
        var (bar, _) = Bar(turnTime, sent);
        try { test(bar, TrayButton(bar)); }
        finally { bar.Close(); }
    });

    // Found by its words, which are its name too, and which change with the arrow.
    static Button TrayButton(FloatingBar bar) =>
        ((Panel)bar.FindName("ShellStrip")!).Children.OfType<Button>()
            .Single(b => AutomationProperties.GetName(b) is "Show the tray icons" or "Hide the tray icons");

    // Once any turn has finished. The words are the ToolTip's text, which must be the accessible name too.
    static string State(Button button)
    {
        var words = Assert.IsType<string>(Assert.IsType<ToolTip>(button.ToolTip).Content);
        Assert.Equal(words, AutomationProperties.GetName(button));
        return $"{Direction(button)}: {words}";
    }

    static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    // The tunnelling half of a press, raised on `target` so it travels down from the bar's window as the app sees it
    // (OnPreviewMouseDown, OnPreviewMouseLeftButtonDown). Not the bubbling half, which would make a button take capture.
    static MouseButtonEventArgs Press(UIElement target, MouseButton button)
    {
        var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, button) { RoutedEvent = Mouse.PreviewMouseDownEvent };
        target.RaiseEvent(press);
        return press;
    }

    // What turns the chevron: the transform on the glyph the button holds.
    static RotateTransform Turn(Button button) => (RotateTransform)((FrameworkElement)button.Content).RenderTransform;

    // Runs the dispatcher, whose frames drive an animation, until `done` or the limit. Bounded, and false when the
    // limit came first, so a turn that never finishes fails a test instead of hanging it.
    static bool PumpUntil(Button button, Func<bool> done, int limitMilliseconds = 5000)
    {
        var clock = Stopwatch.StartNew();
        while (!done())
        {
            if (clock.ElapsedMilliseconds > limitMilliseconds) return false;
            button.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(2);
        }
        return true;
    }

    // The animation is released once it has settled on its target.
    static void Settle(Button button) =>
        Assert.True(PumpUntil(button, () => !Turn(button).HasAnimatedProperties), "the chevron never finished turning");

    static void TurnPast(Button button, double degrees) =>
        Assert.True(PumpUntil(button, () => Turned(button) > degrees), $"the chevron never turned past {degrees} degrees");

    // Where a turn is going: its one key frame.
    static double Target(DoubleAnimationUsingKeyFrames turn) => Assert.IsAssignableFrom<DoubleKeyFrame>(Assert.Single(turn.KeyFrames)).Value;

    // The chevron's left foot, tip and right foot where they are NOW, in the button's own coordinates. Read through
    // every transform between the path and the button, so the test follows what is drawn and not how it is turned.
    static Point[] Points(Button button)
    {
        var path = (Path)((Canvas)((Viewbox)button.Content).Child).Children[0];
        var toButton = path.TransformToAncestor(button);
        var figure = path.Data.GetFlattenedPathGeometry().Figures.Single();
        var points = new[] { figure.StartPoint }
            .Concat(figure.Segments.Cast<PolyLineSegment>().SelectMany(s => s.Points))
            .Select(toButton.Transform)
            .ToArray();
        Assert.Equal(3, points.Length);
        return points;
    }

    // The same, once any turn has finished, and as a chevron: both feet level.
    static Point[] Chevron(Button button)
    {
        Settle(button);
        var points = Points(button);
        Assert.Equal(points[0].Y, points[2].Y, 6);
        return points;
    }

    static string Direction(Button button)
    {
        var c = Chevron(button);
        return c[1].Y < c[0].Y ? "up" : c[1].Y > c[0].Y ? "down" : "flat";
    }

    // How far the DRAWN chevron is turned from pointing up, 0 to 180 degrees, from where its tip is NOW relative to
    // the middle of its feet. Not the transform's angle, so it is what is on screen that is measured.
    static double Turned(Button button)
    {
        var c = Points(button);
        var feet = new Point((c[0].X + c[2].X) / 2, (c[0].Y + c[2].Y) / 2);
        return Math.Abs(Math.Atan2(c[1].X - feet.X, feet.Y - c[1].Y) * 180 / Math.PI);
    }

    // The point the chevron turns about (halfway from its tip to the middle of its feet): it may not move while it turns.
    static Point Pivot(Point[] c) => new((c[1].X + (c[0].X + c[2].X) / 2) / 2, (c[1].Y + (c[0].Y + c[2].Y) / 2) / 2);

    // Either class of overflow window turns it down. The bare taskbar does not, though the bar counts it as "the tray in
    // front" (so a second press closes it): the arrow used to follow that, and turned down on a click on the empty taskbar.
    [Theory]
    [InlineData(Overflow, "down")]
    [InlineData(XamlOverflow, "down")]
    [InlineData(Taskbar, "up")]
    [InlineData(SecondaryTaskbar, "up")]
    public void The_chevron_points_down_for_an_overflow_window_and_stays_up_for_the_bare_taskbar(string windowClass, string expected) => Run((bar, chevron) =>
    {
        using var window = new ClassedWindow(windowClass);

        bar.NoteForeground(window.Handle);

        Assert.Equal(expected, Direction(chevron));
    });

    // The chevron's own press: Win+B focuses the taskbar (still up), the bar's Space opens the overflow and the bar
    // remembers that window whatever its class (#227), which turns it down. If the Space left the taskbar in front, the
    // two cannot be told apart, so it stays up.
    [Fact]
    public void The_chevron_turns_down_when_the_bars_own_space_opens_the_overflow() => Run((bar, chevron) =>
    {
        using var taskbar = new ClassedWindow(Taskbar);
        var overflow = (nint)0x7E57;

        bar.NoteBarPressed();
        bar.NoteForeground(taskbar.Handle);
        Assert.Equal(Up, State(chevron));
        bar.RememberTrayOverflow(overflow);
        Assert.Equal(Down, State(chevron));
        bar.NoteForeground(taskbar.Handle);
        Assert.Equal(Up, State(chevron));

        // The remembered handle is the overflow whenever it comes to the front again.
        bar.NoteForeground(overflow);
        Assert.Equal(Down, State(chevron));

        bar.NoteForeground(0);
        bar.RememberTrayOverflow(taskbar.Handle);
        Assert.Equal(Up, State(chevron));
        bar.NoteForeground(taskbar.Handle);
        Assert.Equal(Up, State(chevron));
    });

    // However it closes, the arrow and its words (tooltip and accessible name) turn back together. The ToolTip stays the
    // same object throughout, so one that is showing at that moment changes with them.
    [Fact]
    public void The_chevron_points_up_and_says_show_again_however_the_overflow_closes() => Run((bar, chevron) =>
    {
        var tip = chevron.ToolTip;
        using var overflow = new ClassedWindow(XamlOverflow);
        using var taskbar = new ClassedWindow(Taskbar);
        using var start = new ClassedWindow(StartMenu);
        (string How, Action Close)[] closes =
        [
            ("Esc, which leaves the bare taskbar in front", () => bar.NoteForeground(taskbar.Handle)),
            ("a click on another window", () => bar.NoteForeground(0x0BAD)),
            ("another flyout coming up", () => bar.NoteForeground(start.Handle)),
            ("the window going away", () => bar.NoteForeground(0)),
            ("a press on the bar, which takes the foreground", bar.NoteBarPressed),
        ];

        Assert.Equal(Up, State(chevron));
        foreach (var (how, close) in closes)
        {
            bar.NoteForeground(overflow.Handle);
            Assert.Equal(Down, State(chevron));
            close();
            Assert.True(State(chevron) == Up, $"the chevron did not turn back after {how}");
        }
        Assert.Same(tip, chevron.ToolTip);
    });

    // A press of any button but the left takes the foreground from the open overflow too, and the bar must hear of it: a
    // middle drag closed the tray and left the arrow down, and the next left click was read as "it was open" and only
    // turned the arrow up. Raised as the tunnelling event, where the app notes it. Not marked handled, so the middle
    // button's drag still starts from it.
    [Theory]
    [InlineData(MouseButton.Middle)]
    [InlineData(MouseButton.Right)]
    [InlineData(MouseButton.XButton1)]
    [InlineData(MouseButton.XButton2)]
    public void A_press_of_any_other_button_on_the_bar_resets_the_arrow_and_the_next_click_opens_the_tray(MouseButton button)
    {
        var sent = new List<FloatingBar.ShellFlyout>();
        Run((bar, chevron) =>
        {
            using var overflow = new ClassedWindow(Overflow);
            bar.NoteForeground(overflow.Handle);
            Assert.Equal(Down, State(chevron));

            var press = Press((UIElement)bar.FindName("Rows")!, button);

            Assert.False(press.Handled);
            Assert.Equal(Up, State(chevron));

            // The click that follows opens the tray at once. Read before anything is pumped, as the bar's own check for
            // the overflow (a timer) would send the shortcut again if it ran first.
            Press(chevron, MouseButton.Left);
            Click(chevron);
            Assert.Equal([FloatingBar.ShellFlyout.Tray], sent);
        }, sent: sent);
    }

    // The left press is noted once, in its own handler. Noted twice, the second note would read the first one's reset as
    // "nothing was open" and the click would open the tray it had just closed.
    [Fact]
    public void A_left_press_on_the_chevron_still_closes_the_open_tray_and_the_next_opens_it()
    {
        var sent = new List<FloatingBar.ShellFlyout>();
        Run((bar, chevron) =>
        {
            using var overflow = new ClassedWindow(Overflow);
            bar.NoteForeground(overflow.Handle);

            Press(chevron, MouseButton.Left);
            Click(chevron);
            Assert.Empty(sent);
            Assert.Equal(Up, State(chevron));

            Press(chevron, MouseButton.Left);
            Click(chevron);
            Assert.Equal([FloatingBar.ShellFlyout.Tray], sent);
        }, sent: sent);
    }

    // Only the orientation changes: the same three points turn half way round the chevron's own middle.
    [Fact]
    public void Turning_the_chevron_changes_neither_its_size_nor_its_place() => Run((bar, chevron) =>
    {
        var up = Chevron(chevron);
        bar.RememberTrayOverflow(0x7E57);
        var down = Chevron(chevron);

        Assert.Equal("down", Direction(chevron));
        Assert.Equal(up.Min(p => p.X), down.Min(p => p.X), 6);
        Assert.Equal(up.Max(p => p.X), down.Max(p => p.X), 6);
        Assert.Equal(up.Min(p => p.Y), down.Min(p => p.Y), 6);
        Assert.Equal(up.Max(p => p.Y), down.Max(p => p.Y), 6);
    });

    // LayOutStrip takes every button out and puts it back (here: an item switched off in Settings). The chevron is the
    // same button each time, so its direction must survive, including a change made while it was out. Needs the manager.
    [Fact]
    public void The_chevron_keeps_its_direction_through_a_relayout() => StaThread.Run(() =>
    {
        var (bar, manager) = Bar();
        var strip = (Panel)bar.FindName("ShellStrip")!;
        var chevron = TrayButton(bar);
        void Hide(params StripItem[] items)
        {
            Assert.True(manager.SetHiddenStripItems(items).IsSuccess);
            bar.UpdateLayout();
        }

        bar.RememberTrayOverflow(0x7E57);
        Hide(StripItem.QuickSettings);
        Assert.DoesNotContain(strip.Children.OfType<Button>(), b => AutomationProperties.GetName(b).StartsWith("Quick settings"));
        Assert.Same(chevron, TrayButton(bar));
        Assert.Equal("down", Direction(chevron));

        // Closed while the chevron is switched off, so it comes back pointing up...
        Hide(StripItem.Tray);
        Assert.DoesNotContain(chevron, strip.Children.OfType<Button>());
        bar.NoteForeground(0x0BAD);
        Hide();
        Assert.Same(chevron, TrayButton(bar));
        Assert.Equal("up", Direction(chevron));

        // ...and opened while it is switched off, so it comes back pointing down.
        Hide(StripItem.Tray);
        bar.RememberTrayOverflow(0x7E57);
        Hide();
        Assert.Same(chevron, TrayButton(bar));
        Assert.Equal("down", Direction(chevron));
        bar.Close();
    });

    // Slowed so the arrow can be caught part of the way round; what it measures is the DRAWN chevron.
    [Fact]
    public void The_chevron_turns_about_its_own_middle_instead_of_jumping() => Run((bar, chevron) =>
    {
        var rest = Points(chevron);
        Assert.Equal(0, Turned(chevron), 6);

        bar.RememberTrayOverflow(0x7E57);

        // Right after the change it is not already down: it is turning, towards 180, over the time it was given.
        Assert.True(Turn(chevron).HasAnimatedProperties);
        Assert.Equal(Slow, bar.TrayChevronTurning!.Duration);
        Assert.Equal(180, Target(bar.TrayChevronTurning));
        Assert.True(Turned(chevron) < 90);

        // Some frames later it is between up and down, still on the same pivot and with the same size.
        TurnPast(chevron, 30);
        var half = Points(chevron);
        Assert.InRange(Turned(chevron), 30, 180 - 1e-6);
        Assert.NotEqual(half[0].Y, half[2].Y, 3);
        Assert.Equal(Pivot(rest).X, Pivot(half).X, 6);
        Assert.Equal(Pivot(rest).Y, Pivot(half).Y, 6);
        Assert.Equal((rest[2] - rest[0]).Length, (half[2] - half[0]).Length, 6);
        Assert.Equal((rest[1] - rest[0]).Length, (half[1] - half[0]).Length, 6);
    }, Slow);

    // The shipped values: WinUI's standard fast duration and curve (assumed to match Windows' own arrow, not measured), so
    // changing them changes this test. On arrival the animation is let go, so nothing holds the angle and it is the target.
    [Fact]
    public void The_chevron_takes_the_standard_fast_turn_and_settles_on_its_target() => Run((bar, chevron) =>
    {
        var turn = Turn(chevron);

        bar.RememberTrayOverflow(0x7E57);

        var animation = bar.TrayChevronTurning!;
        Assert.True(turn.HasAnimatedProperties);
        Assert.Equal(TimeSpan.FromMilliseconds(167), animation.Duration.TimeSpan);
        var frame = Assert.IsType<SplineDoubleKeyFrame>(Assert.Single(animation.KeyFrames));
        Assert.Equal(180, frame.Value);
        Assert.Equal(new Point(0, 0), frame.KeySpline.ControlPoint1);
        Assert.Equal(new Point(0, 1), frame.KeySpline.ControlPoint2);

        Settle(chevron);
        Assert.Null(bar.TrayChevronTurning);
        Assert.False(turn.HasAnimatedProperties);
        Assert.Equal(180, turn.Angle);
        Assert.Equal("down", Direction(chevron));
    });

    // NoteForeground runs on every focus change, nearly all with the overflow nowhere near. Only a change of target may
    // start a turn: a call that changes nothing must leave a turn in progress alone (a restart on each focus change would
    // stall the arrow) and start nothing at rest.
    [Fact]
    public void A_call_that_changes_nothing_does_not_start_or_restart_a_turn() => Run((bar, chevron) =>
    {
        var turn = Turn(chevron);
        using var overflow = new ClassedWindow(Overflow);
        using var other = new ClassedWindow(StartMenu);

        // Closed and staying closed: nothing starts.
        bar.NoteForeground(other.Handle);
        bar.NoteForeground(0);
        bar.NoteForeground(0x0BAD);
        bar.NoteBarPressed();
        Assert.Null(bar.TrayChevronTurning);
        Assert.False(turn.HasAnimatedProperties);

        // Opening, and asked again and again to open: the one turn carries on, from where it has got to.
        bar.NoteForeground(overflow.Handle);
        var opening = bar.TrayChevronTurning;
        Assert.NotNull(opening);
        TurnPast(chevron, 30);
        var reached = Turned(chevron);

        bar.NoteForeground(overflow.Handle);
        bar.NoteForeground(overflow.Handle);
        bar.RememberTrayOverflow(overflow.Handle);

        Assert.Same(opening, bar.TrayChevronTurning);
        Assert.True(turn.HasAnimatedProperties);
        Assert.Equal(reached, Turned(chevron), 6);
        TurnPast(chevron, reached + 1);
    }, Slow);

    // A turn reversed half way goes back from where the arrow is, not from either end (the new turn has no From, so it
    // starts at the value the old one reached). Measured on the drawn chevron over the frames after the change: none may
    // be past the point reached (it did not carry on to 180) or back near 0 (it did not start again).
    [Fact]
    public void A_turn_reversed_half_way_goes_back_from_where_the_arrow_is() => Run((bar, chevron) =>
    {
        bar.RememberTrayOverflow(0x7E57);
        var opening = bar.TrayChevronTurning;
        TurnPast(chevron, 60);
        var before = Turned(chevron);
        Assert.InRange(before, 60, 180 - 1e-6);

        bar.NoteBarPressed();

        var closing = bar.TrayChevronTurning;
        Assert.NotNull(closing);
        Assert.NotSame(opening, closing);
        Assert.Equal(0, Target(closing));
        var samples = new List<double>();
        var movedBack = PumpUntil(chevron, () =>
        {
            samples.Add(Turned(chevron));
            return samples[^1] < before - 10;
        });
        Assert.True(movedBack, "the chevron never turned back");
        Assert.True(samples.Count >= 2, "the change should be seen on the frames after it, not only at once");
        foreach (var angle in samples) Assert.InRange(angle, before * 0.4, before + 1e-6);
    }, Slow);

    // ...and ends where the LATEST target says: the turn it replaced must not settle the angle when it ends (it would
    // leave the arrow down after the overflow closed), and nothing may be left holding it.
    [Fact]
    public void A_reversed_turn_settles_on_the_latest_target() => Run((bar, chevron) =>
    {
        var turn = Turn(chevron);
        bar.RememberTrayOverflow(0x7E57);
        TurnPast(chevron, 1);

        bar.NoteBarPressed();
        Settle(chevron);
        Assert.Equal(0, turn.Angle);
        Assert.Equal("up", Direction(chevron));

        // Frames after it settled, in case the replaced turn reports its end late.
        var until = Stopwatch.StartNew();
        PumpUntil(chevron, () => until.ElapsedMilliseconds > 300);
        Assert.Equal(0, turn.Angle);
        Assert.False(turn.HasAnimatedProperties);
        Assert.Null(bar.TrayChevronTurning);
    });

    // A bar that is not on screen has nothing to animate, and the arrow must still be right when it comes back.
    [Fact]
    public void A_bar_that_is_not_shown_turns_the_chevron_at_once() => Run((bar, chevron) =>
    {
        bar.Hide();

        bar.RememberTrayOverflow(0x7E57);

        Assert.Null(bar.TrayChevronTurning);
        Assert.False(Turn(chevron).HasAnimatedProperties);
        Assert.Equal(180, Turned(chevron), 6);
    }, Slow);

    // A real window, never shown, of a given class name: the bar reads the foreground window's class from Windows, and the
    // shell's own windows cannot be made to take the foreground from a test. The class is registered for this process only.
    sealed class ClassedWindow : IDisposable
    {
        const uint WsPopup = 0x80000000;
        readonly string windowClass;
        readonly nint instance = GetModuleHandleW(null);

        public nint Handle { get; }

        public ClassedWindow(string windowClass)
        {
            this.windowClass = windowClass;
            var definition = new WindowClass
            {
                Size = (uint)Marshal.SizeOf<WindowClass>(),
                WindowProc = GetProcAddress(GetModuleHandleW("user32.dll"), "DefWindowProcW"),
                Instance = instance,
                ClassName = windowClass,
            };
            Assert.True(RegisterClassExW(ref definition) != 0);
            Handle = CreateWindowExW(0, windowClass, null, WsPopup, 0, 0, 0, 0, 0, 0, instance, 0);
            Assert.True(Handle != 0);
        }

        public void Dispose()
        {
            DestroyWindow(Handle);
            UnregisterClassW(windowClass, instance);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WindowClass
        {
            public uint Size, Style;
            public nint WindowProc;
            public int ClassExtra, WindowExtra;
            public nint Instance, Icon, Cursor, Background;
            public string? MenuName, ClassName;
            public nint SmallIcon;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassExW(ref WindowClass definition);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool UnregisterClassW(string name, nint instance);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern nint CreateWindowExW(uint exStyle, string windowClass, string? title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
        [DllImport("user32.dll")] static extern bool DestroyWindow(nint hwnd);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern nint GetModuleHandleW(string? name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern nint GetProcAddress(nint module, string name);
    }
}
