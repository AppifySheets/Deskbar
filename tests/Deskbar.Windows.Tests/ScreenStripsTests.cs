using System.Reactive;
using System.Windows.Interop;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core.Abstractions;
using Deskbar.Core.Domain;
using Deskbar.Core.Overview;
using Deskbar.Windows.Monitoring;

namespace Deskbar.Windows.Tests;

// The strip set (ScreenStrips): exactly one ScreenStrip per connected screen while the window list is on the screens,
// none otherwise (spec docs/superpowers/specs/2026-11-19-screen-strips-design.md, "The parts", the strip set).
//
// No test here registers a real app bar, moves a real window or shows one on a real screen. Every strip comes from
// the test's own `create`, which fakes the dock (DockWith) and the scale, on made-up screens far off every real
// monitor, so a strip that is shown is shown nowhere anyone can see. The one test that reads the real screens grants
// its strips no room, so they are never placed or shown.
public class ScreenStripsTests
{
    // Three side-by-side made-up screens, 1200 x 800 px each, nowhere near a real one (see ScreenStripTests for why
    // they are kept small).
    static readonly MonitorBounds Left = new(-30000, -30000, -28800, -29200);
    static readonly MonitorBounds Middle = new(-28800, -30000, -27600, -29200);
    static readonly MonitorBounds Right = new(-27600, -30000, -26400, -29200);

    // What the strip set did, in order, as the fakes saw it: "ignore", "pin", "closing" lines naming the strip's screen.
    sealed class Rig
    {
        public bool Wanted { get; set; } = true;
        public IReadOnlyList<MonitorBounds> Screens { get; set; } = [Left, Middle];
        public List<string> Log { get; } = [];
        public List<ScreenStrip> Made { get; } = [];
        public ScreenStrips Set { get; }

        // Whether a strip's own screen can be read; false makes its dock refuse, as on a screen that dropped out.
        public bool Readable { get; set; } = true;

        // What the next pins answer, first to last; once used up, every pin succeeds. A failure is logged as "pin failed".
        public Queue<bool> PinAnswers { get; } = new();

        // The strip set's clock, stepped by the test to cross the pin's retry interval without waiting for it.
        public DateTime Now { get; set; } = new(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);

        // The windows the bar knows about (made up, never real handles), where each one is, and which the shell has
        // arranged (maximized or snapped). The strip set's quiet move is logged as "nudge <name> to <left>,<top> to
        // <right>,<bottom>"; a MoveTo, which raises the window to the front, as "move ...", and none should ever appear.
        public List<WindowHandle> Windows { get; } = [];
        public HashSet<WindowHandle> Arranged { get; } = [];
        public FakeLayout Layout { get; }

        // Throws, the way Win32 and WPF throw at a display change or an unlock (the final review's first finding):
        // `create` for these screens, the dock of a strip on these screens, the list of windows a new strip clears
        // (which runs only after a dock that worked), and the clock of a strip on these screens (the guard's first
        // read, so its KeepStrip throws).
        public HashSet<MonitorBounds> CreateThrows { get; } = [];
        public HashSet<MonitorBounds> DockThrows { get; } = [];
        public bool WindowsThrow { get; set; }
        public HashSet<MonitorBounds> ClockThrows { get; } = [];

        // Run inside a pin, after its line is logged: what a message pumped by the pin's COM call could do.
        public Action? OnPin { get; set; }

        // How often the strip set asked for the connected screens.
        public int ScreenCalls { get; private set; }

        public Rig()
        {
            Layout = new FakeLayout(Log);
            Set = new ScreenStrips(
                wanted: () => Wanted,
                screens: () =>
                {
                    ScreenCalls++;
                    return Screens;
                },
                create: Make,
                ignore: hwnd => Log.Add($"ignore {Name(hwnd)} visible={ByHandle(hwnd).IsVisible}"),
                pinToAllDesktops: Pin,
                windows: () => WindowsThrow ? throw new InvalidOperationException("the window list threw") : Windows,
                layout: Layout,
                shellArranged: Arranged.Contains)
            {
                Clock = () => Now,
            };
        }

        // A known window at `rect`, named for the move log.
        public WindowHandle Window(string name, WindowRect rect)
        {
            var window = new WindowHandle(0x1000 + Windows.Count);
            Windows.Add(window);
            Layout.Names[window] = name;
            Layout.Rects[window] = rect;
            return window;
        }

        public IReadOnlyList<string> Moves => Log.Where(line => line.StartsWith("nudge ") || line.StartsWith("move ")).ToList();

        Result Pin(nint hwnd)
        {
            var ok = PinAnswers.Count == 0 || PinAnswers.Dequeue();
            Log.Add($"pin{(ok ? "" : " failed")} {Name(hwnd)} visible={ByHandle(hwnd).IsVisible}");
            OnPin?.Invoke();
            return ok ? Result.Success() : Result.Failure("the desktop service is not ready");
        }

        ScreenStrip Make(MonitorBounds screen)
        {
            if (CreateThrows.Contains(screen)) throw new InvalidOperationException("create threw");
            var strip = new ScreenStrip(screen)
            {
                Scale = () => Readable ? 1.5 : Maybe<double>.None,
                Clock = () => ClockThrows.Contains(screen) ? throw new InvalidOperationException("the clock threw") : DateTime.UtcNow,
                DockWith = (_, s, px) => DockThrows.Contains(screen) ? throw new InvalidOperationException("the dock threw") : new(s.L, s.T, s.R, s.T + px),
            };
            // At Closing the strip is still alive and Describe still answers: "docked no" there means Undock ran first,
            // and IsVisible false means the window was already off the screen when the close began.
            strip.Closing += (_, _) =>
                Log.Add($"closing {NameOf(strip.Screen)} docked={(strip.Describe().Contains("docked no") ? "no" : "yes")} visible={strip.IsVisible}");
            Made.Add(strip);
            return strip;
        }

        ScreenStrip ByHandle(nint hwnd) => Made.Single(s => new WindowInteropHelper(s).Handle == hwnd);

        string Name(nint hwnd) => NameOf(ByHandle(hwnd).Screen);

        public static string NameOf(MonitorBounds screen) =>
            screen == Left ? "left" : screen == Middle ? "middle" : screen == Right ? "right" : "other";
    }

    // The screen layout, faked: each window's rectangle, which are minimized, which are no longer in the snapshot (a
    // window the bar still knows that has stopped being listed, so the snapshot cannot say whether it is minimized), and
    // which refuse to move. Windows on other virtual desktops ARE in the real snapshot (TopLevelWindows keeps
    // shell-cloaked windows on purpose), so the fake has no separate notion of them. A move that works updates the
    // rectangle, as the real one would.
    sealed class FakeLayout(List<string> log) : IScreenLayout
    {
        public Dictionary<WindowHandle, string> Names { get; } = [];
        public Dictionary<WindowHandle, WindowRect> Rects { get; } = [];
        public HashSet<WindowHandle> Minimized { get; } = [];
        public HashSet<WindowHandle> Unlisted { get; } = [];
        public HashSet<WindowHandle> Refusing { get; } = [];

        public ScreenFacts Snapshot() =>
            new(new Dictionary<WindowHandle, int>(), Minimized.ToHashSet(),
                Rects.Keys.Where(w => !Unlisted.Contains(w)).Select((w, i) => (w, i)).ToDictionary(x => x.w, x => x.i));

        public Maybe<WindowRect> RectOf(WindowHandle window) => Rects.TryGetValue(window, out var rect) ? rect : Maybe<WindowRect>.None;

        public Result MoveTo(WindowHandle window, WindowRect rect, bool mayChangeShowState)
        {
            log.Add($"move {Names[window]} to {rect.Left},{rect.Top} to {rect.Right},{rect.Bottom} showState={mayChangeShowState}");
            if (Refusing.Contains(window)) return Result.Failure("Windows would not move that window.");
            Rects[window] = rect;
            return Result.Success();
        }

        public Result Nudge(WindowHandle window, WindowRect rect)
        {
            log.Add($"nudge {Names[window]} to {rect.Left},{rect.Top} to {rect.Right},{rect.Bottom}");
            if (Refusing.Contains(window)) return Result.Failure("Windows would not move that window.");
            Rects[window] = rect;
            return Result.Success();
        }
    }

    // The setting comes on: one strip per connected screen, each on its own screen, each docked and shown.
    [Fact]
    public void Turning_the_setting_on_creates_one_strip_per_screen() => StaThread.Run(() =>
    {
        var rig = new Rig { Wanted = false };
        rig.Set.Reconcile();
        Assert.Empty(rig.Set.Current);

        rig.Wanted = true;
        rig.Set.Reconcile();

        Assert.Equal(new[] { Left, Middle }, rig.Set.Current.Select(s => s.Screen).OrderBy(s => s.Left));
        Assert.All(rig.Set.Current, strip => Assert.True(strip.IsVisible));
        Assert.All(rig.Set.Current, strip => Assert.Contains("docked yes", strip.Describe()));

        // A second reconcile on the same screens changes nothing: the same two windows, no third.
        var before = rig.Set.Current.ToList();
        rig.Set.Reconcile();
        Assert.Equal(before, rig.Set.Current);
        Assert.Equal(2, rig.Made.Count);
        rig.Set.RemoveAll();
    });

    // The setting goes off: every strip gives its band back (Undock) BEFORE its window closes, so no screen is ever
    // left with a reserved band and nothing in it.
    [Fact]
    public void Turning_it_off_undocks_then_closes_each() => StaThread.Run(() =>
    {
        var rig = new Rig();
        rig.Set.Reconcile();
        rig.Log.Clear();

        rig.Wanted = false;
        rig.Set.Reconcile();

        Assert.Empty(rig.Set.Current);
        Assert.Equal(new[] { "closing left docked=no visible=False", "closing middle docked=no visible=False" }, rig.Log.Order());
    });

    // The bar's own order (App, around the bar's construction): the handle goes to WindowMonitor.Ignore before the
    // first show, or the strip's first EVENT_OBJECT_SHOW lists it in the bar; the pin to every desktop comes after
    // the show, because a window never shown is on no desktop and the pin fails (PinFloatingBar).
    [Fact]
    public void A_new_strip_is_ignored_before_it_is_shown_and_pinned_after() => StaThread.Run(() =>
    {
        var rig = new Rig { Screens = [Left] };

        rig.Set.Reconcile();

        Assert.Equal(new[] { "ignore left visible=False", "pin left visible=True" }, rig.Log);
        rig.Set.RemoveAll();
    });

    // A strip whose first dock could not show it (its screen could not be read at that moment) is pinned once it is
    // up, on the tick after, rather than never: an unpinned strip would vanish on a desktop switch while its band
    // stayed reserved.
    [Fact]
    public void A_strip_shown_later_is_pinned_on_the_next_tick() => StaThread.Run(() =>
    {
        var rig = new Rig { Screens = [Left], Readable = false };

        rig.Set.Reconcile();
        Assert.Equal(new[] { "ignore left visible=False" }, rig.Log);

        rig.Readable = true;
        Assert.True(rig.Set.Current.Single().Dock().IsSuccess);
        rig.Set.Tick();
        rig.Set.Tick();

        Assert.Equal(new[] { "ignore left visible=False", "pin left visible=True" }, rig.Log);
        rig.Set.RemoveAll();
    });

    // A pin that fails (the desktop COM object gone stale after idle, #148, or "not ready") must not count as pinned: an
    // unpinned strip is cloaked on every other desktop while its band stays reserved there, an empty band nothing would
    // ever repair. So Tick tries again, at most once every FloatingBar.RetryEvery as the docked bar's guard does, and
    // once a pin has worked it is never made again.
    [Fact]
    public void A_failed_pin_is_retried_on_a_throttle_until_it_works() => StaThread.Run(() =>
    {
        var rig = new Rig { Screens = [Left] };
        rig.PinAnswers.Enqueue(false);

        rig.Set.Reconcile();
        Assert.Equal(new[] { "ignore left visible=False", "pin failed left visible=True" }, rig.Log);

        // Within the retry interval: no second attempt.
        rig.Now += FloatingBar.RetryEvery - TimeSpan.FromSeconds(1);
        rig.Set.Tick();
        Assert.Equal(2, rig.Log.Count);

        // Past it: tried again, and this time it works.
        rig.Now += TimeSpan.FromSeconds(2);
        rig.Set.Tick();
        Assert.Equal(new[] { "ignore left visible=False", "pin failed left visible=True", "pin left visible=True" }, rig.Log);

        // Pinned now: never again, however long it runs.
        rig.Now += FloatingBar.RetryEvery * 3;
        rig.Set.Tick();
        rig.Set.Tick();
        Assert.Equal(3, rig.Log.Count);
        rig.Set.RemoveAll();
    });

    // A settle can land on the moment Windows reports no usable screen at all (spec "Constraints"). That is not every
    // screen leaving: the strips stay, the same windows, until a real arrangement arrives.
    [Fact]
    public void A_settle_with_no_screens_keeps_the_strips() => StaThread.Run(() =>
    {
        var rig = new Rig();
        rig.Set.Reconcile();
        var before = rig.Set.Current.ToList();
        rig.Log.Clear();

        rig.Screens = [];
        rig.Set.Reconcile();

        Assert.Equal(before, rig.Set.Current);
        Assert.Empty(rig.Log);
        Assert.All(before, strip => Assert.True(strip.IsVisible));
        rig.Set.RemoveAll();
    });

    // A screen unplugged: its strip is undocked and closed, the others are left exactly as they were (the same
    // windows, not rebuilt), and a screen that arrives gets a strip of its own.
    [Fact]
    public void A_screen_that_left_takes_its_strip_with_it() => StaThread.Run(() =>
    {
        var rig = new Rig();
        rig.Set.Reconcile();
        var left = rig.Set.Current.Single(s => s.Screen == Left);
        rig.Log.Clear();

        rig.Screens = [Left, Right];
        rig.Set.Reconcile();

        Assert.Contains("closing middle docked=no visible=False", rig.Log);
        Assert.DoesNotContain(rig.Log, line => line.StartsWith("closing left"));
        Assert.Equal(new[] { Left, Right }, rig.Set.Current.Select(s => s.Screen).OrderBy(s => s.Left));
        Assert.Same(left, rig.Set.Current.Single(s => s.Screen == Left));
        rig.Set.RemoveAll();
    });

    // Exit and Restart: every strip undocked, then closed, and the set empty.
    [Fact]
    public void RemoveAll_undocks_every_strip() => StaThread.Run(() =>
    {
        var rig = new Rig { Screens = [Left, Middle, Right] };
        rig.Set.Reconcile();
        rig.Log.Clear();

        rig.Set.RemoveAll();

        Assert.Empty(rig.Set.Current);
        Assert.Equal(new[] { "closing left docked=no visible=False", "closing middle docked=no visible=False", "closing right docked=no visible=False" },
            rig.Log.Order());
    });

    // ...and for good: a settle or a state pulse that arrives while the app is going away must not dock a new strip
    // that nothing would ever undock.
    [Fact]
    public void Nothing_comes_back_after_RemoveAll() => StaThread.Run(() =>
    {
        var rig = new Rig();
        rig.Set.Reconcile();
        rig.Set.RemoveAll();

        rig.Set.Reconcile();

        Assert.Empty(rig.Set.Current);
        Assert.Equal(2, rig.Made.Count);
    });

    // The bar is asked to rebuild after a reconcile that changed something (so new strips get their tabs), and only
    // then: a reconcile that changed nothing must not cost a rebuild.
    [Fact]
    public void Changed_fires_only_when_a_strip_came_or_went() => StaThread.Run(() =>
    {
        var rig = new Rig();
        var changes = new List<Unit>();
        using var _ = rig.Set.Changed.Subscribe(changes.Add);

        rig.Set.Reconcile();
        rig.Set.Reconcile();
        Assert.Single(changes);

        rig.Screens = [Left];
        rig.Set.Reconcile();
        Assert.Equal(2, changes.Count);
        rig.Set.RemoveAll();
    });

    // The debug dump's lines: one per strip, from the strip's own Describe.
    [Fact]
    public void Describe_has_a_line_per_strip() => StaThread.Run(() =>
    {
        var rig = new Rig();
        Assert.Single(rig.Set.Describe());

        rig.Set.Reconcile();

        Assert.Equal(2, rig.Set.Describe().Count());
        Assert.All(rig.Set.Describe(), line => Assert.StartsWith("strip on ", line));
        rig.Set.RemoveAll();
    });

    // The made-up screens' tops are at -30000, and a strip there is 36 px (24 DIP at the rig's 150%), so its bottom edge
    // is -29952. A window whose top is above that has its title bar under the strip.
    static WindowRect Under(MonitorBounds screen) => new(screen.Left + 100, screen.Top + 6, screen.Left + 500, screen.Top + 306);

    // The shell does not move a restored window whose title bar a new top bar covers (measured, probe notes section 1),
    // so the set does, once, when it creates the strip: moved down to the strip's bottom edge, size kept, with the quiet
    // move (Nudge: no raise to the front, no show-state change), never MoveTo. A window on another virtual desktop is in
    // the snapshot like any other and is moved too: the band is reserved on every desktop. Left alone: a window already
    // below the strip, a minimized one (its rectangle is -32000 nonsense), a maximized or snapped one (the shell's to
    // size), one the snapshot no longer lists, and one on a screen with no new strip. And once means once: a tick and a
    // reconcile that changes nothing move nothing more.
    [Fact]
    public void A_new_strip_moves_restored_windows_under_it_once() => StaThread.Run(() =>
    {
        var rig = new Rig { Screens = [Left] };
        rig.Window("under", Under(Left));
        rig.Window("below", new WindowRect(Left.Left + 100, Left.Top + 100, Left.Left + 500, Left.Top + 400));
        rig.Layout.Minimized.Add(rig.Window("minimized", Under(Left)));
        rig.Arranged.Add(rig.Window("maximized", new WindowRect(Left.Left - 8, Left.Top - 8, Left.Right + 8, Left.Bottom + 8)));
        // Snapped to the left half: not zoomed, its top at the screen's top, its bottom at the work area's.
        rig.Arranged.Add(rig.Window("snapped", new WindowRect(Left.Left, Left.Top, Left.Left + 600, Left.Bottom - 48)));
        rig.Layout.Unlisted.Add(rig.Window("unlisted", Under(Left)));
        rig.Window("on another desktop", new WindowRect(Left.Left + 700, Left.Top + 20, Left.Left + 1100, Left.Top + 320));
        rig.Window("on middle", Under(Middle));

        rig.Set.Reconcile();

        Assert.Equal(new[]
        {
            $"nudge under to {Left.Left + 100},{Left.Top + 36} to {Left.Left + 500},{Left.Top + 336}",
            $"nudge on another desktop to {Left.Left + 700},{Left.Top + 36} to {Left.Left + 1100},{Left.Top + 336}",
        }, rig.Moves);

        // Put back under the strip by hand: the strip set has had its one go at it.
        rig.Layout.Rects[rig.Windows[0]] = Under(Left);
        rig.Set.Tick();
        rig.Set.Reconcile();
        Assert.Equal(2, rig.Moves.Count);
        rig.Set.RemoveAll();
    });

    // A screen arriving creates one strip, and only that strip clears its screen: the strip that was already there
    // moves nothing, even with a window back under it.
    [Fact]
    public void A_strip_that_already_existed_moves_nothing() => StaThread.Run(() =>
    {
        var rig = new Rig { Screens = [Left] };
        rig.Set.Reconcile();
        rig.Window("under left", Under(Left));
        rig.Window("under middle", Under(Middle));

        rig.Screens = [Left, Middle];
        rig.Set.Reconcile();

        Assert.Equal(new[] { $"nudge under middle to {Middle.Left + 100},{Middle.Top + 36} to {Middle.Left + 500},{Middle.Top + 336}" }, rig.Moves);
        rig.Set.RemoveAll();
    });

    // A window that will not move (an elevated process, a dead handle) is traced and skipped; the rest still move.
    [Fact]
    public void A_window_that_will_not_move_does_not_stop_the_rest() => StaThread.Run(() =>
    {
        var rig = new Rig { Screens = [Left] };
        rig.Layout.Refusing.Add(rig.Window("stubborn", Under(Left)));
        rig.Window("willing", new WindowRect(Left.Left + 600, Left.Top + 10, Left.Left + 900, Left.Top + 310));

        rig.Set.Reconcile();

        Assert.Equal(2, rig.Moves.Count);
        Assert.Equal(Left.Top + 36, rig.Layout.Rects[rig.Windows[1]].Top);
        rig.Set.RemoveAll();
    });

    // A strip whose first dock was refused (its screen could not be read) has no granted band, so there is no height to
    // clear to and nothing is moved.
    [Fact]
    public void A_strip_whose_dock_was_refused_moves_nothing() => StaThread.Run(() =>
    {
        var rig = new Rig { Screens = [Left], Readable = false };
        rig.Window("under", Under(Left));

        rig.Set.Reconcile();

        Assert.Empty(rig.Moves);
        rig.Set.RemoveAll();
    });

    // THROWS AT THE WORST MOMENT (final review, first finding). A reconcile runs at a display change, an unlock and a
    // replug, which is exactly when Win32 and WPF throw. Anything that escaped reached DispatcherUnhandledException,
    // which FailFasts and relaunches the app. So a reconcile catches and traces, and one screen's failure does not
    // keep the other screens from their strips.
    [Fact]
    public void A_create_that_throws_does_not_escape_Reconcile_and_the_other_screens_still_get_strips() => StaThread.Run(() =>
    {
        var rig = new Rig();
        rig.CreateThrows.Add(Left);

        Assert.Null(Record.Exception(rig.Set.Reconcile));
        Assert.Equal(new[] { Middle }, rig.Set.Current.Select(s => s.Screen));

        // The next reconcile, with create working again, makes the missing one, and only that one.
        rig.CreateThrows.Clear();
        rig.Set.Reconcile();
        Assert.Equal(new[] { Left, Middle }, rig.Set.Current.Select(s => s.Screen).OrderBy(s => s.Left));
        Assert.Equal(2, rig.Made.Count);
        rig.Set.RemoveAll();
    });

    // A dock that throws (the shell's SendMessage, the window's EnsureHandle) does not escape either, and the strip it
    // was docking is already in Current, so the next reconcile does not make a second one for the same screen.
    [Fact]
    public void A_dock_that_throws_does_not_escape_Reconcile_or_make_a_second_strip() => StaThread.Run(() =>
    {
        var rig = new Rig { Screens = [Left] };
        rig.DockThrows.Add(Left);

        Assert.Null(Record.Exception(rig.Set.Reconcile));
        Assert.Equal(Left, Assert.Single(rig.Set.Current).Screen);

        rig.DockThrows.Clear();
        rig.Set.Reconcile();
        Assert.Single(rig.Set.Current);
        Assert.Single(rig.Made);
        rig.Set.RemoveAll();
    });

    // The ordering half of the finding: a throw AFTER a dock that worked (here the clearing of the windows under the new
    // strip) must not leave a docked, shown strip outside Current, or the next reconcile opens a second strip on that
    // screen (the spec's second anti-goal). Whether the first reconcile's throw escapes is the tests above; this one
    // only looks at what is left on the screen.
    [Fact]
    public void A_throw_after_a_dock_that_worked_leaves_one_strip_for_its_screen() => StaThread.Run(() =>
    {
        var rig = new Rig { Screens = [Left] };
        rig.Window("under", Under(Left));
        rig.WindowsThrow = true;
        _ = Record.Exception(rig.Set.Reconcile);

        rig.WindowsThrow = false;
        rig.Set.Reconcile();

        Assert.Equal(Left, Assert.Single(rig.Set.Current).Screen);
        Assert.Single(rig.Made);
        Assert.Single(rig.Made, strip => strip.IsVisible);
        rig.Set.RemoveAll();
    });

    // The app going away in the middle of a reconcile (RemoveAll run by a message the pin's COM call pumped): the strip
    // being opened is in Current already, so RemoveAll undocks it, and the rest of that pass opens nothing more. A strip
    // docked after RemoveAll would hold its band with nothing left to give it back.
    [Fact]
    public void RemoveAll_pumped_mid_reconcile_stops_the_pass_and_leaves_nothing_docked() => StaThread.Run(() =>
    {
        var rig = new Rig();
        rig.OnPin = () =>
        {
            rig.OnPin = null;
            rig.Set.RemoveAll();
        };

        rig.Set.Reconcile();

        Assert.Empty(rig.Set.Current);
        Assert.Single(rig.Made);
        Assert.All(rig.Made, strip => Assert.False(strip.IsVisible));
    });

    // The 1 s beat: one strip whose guard throws does not keep the beat from the next strip. Here the next one has a
    // pin to retry, and the retry is what shows the beat reached it.
    [Fact]
    public void A_strip_whose_guard_throws_does_not_stop_Tick_reaching_the_next() => StaThread.Run(() =>
    {
        var rig = new Rig();
        rig.PinAnswers.Enqueue(true);
        rig.PinAnswers.Enqueue(false);
        rig.Set.Reconcile();
        var first = rig.Set.Current[0];
        var second = rig.Set.Current[1];
        Assert.Equal($"pin failed {Rig.NameOf(second.Screen)} visible=True", rig.Log.Last());

        rig.ClockThrows.Add(first.Screen);
        rig.Now += FloatingBar.RetryEvery + TimeSpan.FromSeconds(1);

        Assert.Null(Record.Exception(rig.Set.Tick));
        Assert.Equal($"pin {Rig.NameOf(second.Screen)} visible=True", rig.Log.Last());
        rig.ClockThrows.Clear();
        rig.Set.RemoveAll();
    });

    // Most people never put the window list on the screens, and the spec says the merge changes nothing for them. The
    // 1 s beat runs for everyone, so with no strip it must not even ask for the screens.
    [Fact]
    public void Tick_with_no_strips_does_not_enumerate_the_screens() => StaThread.Run(() =>
    {
        var rig = new Rig { Wanted = false };
        rig.Set.Reconcile();
        var before = rig.ScreenCalls;

        rig.Set.Tick();
        rig.Set.Tick();

        Assert.Equal(before, rig.ScreenCalls);
    });

    // A strip's Screen is matched with == against the overview's screens (FloatingBar.ScreenNumberOf, for the drop and
    // for which windows a strip lists), so the screens the app reconciles on must be those very rectangles:
    // ScreenFacts.MonitorPlacement, which is what Overview.Screens is. Read-only on the real machine: the strips made
    // here are granted no room, so none is ever placed or shown.
    [Fact]
    public void A_strip_made_from_the_real_screens_maps_to_a_screen_number() => StaThread.Run(() =>
    {
        var placement = new ScreenLayout().Snapshot().MonitorPlacement ?? new Dictionary<int, MonitorBounds>();
        var overview = new Overview([], [], [], placement);
        var shown = new List<nint>();
        var set = new ScreenStrips(
            wanted: () => true,
            screens: ScreenLayout.Screens,
            create: screen => new ScreenStrip(screen) { Scale = () => 1.0, DockWith = (_, s, _) => new(s.L, s.T, s.L, s.T) },
            ignore: _ => { },
            pinToAllDesktops: hwnd =>
            {
                shown.Add(hwnd);
                return Result.Success();
            },
            windows: () => [],
            layout: new FakeLayout([]),
            shellArranged: _ => false);

        set.Reconcile();

        Assert.Equal(placement.Count, set.Current.Count);
        Assert.All(set.Current, strip => Assert.True(FloatingBar.ScreenNumberOf(overview, strip.Screen).HasValue));
        Assert.All(set.Current, strip => Assert.False(strip.IsVisible));
        Assert.Empty(shown);
        set.RemoveAll();
    });
}
