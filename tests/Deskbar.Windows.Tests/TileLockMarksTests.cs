using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Reactive;
using System.Reactive.Subjects;
using System.Windows.Interop;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core.Domain;
using Deskbar.Core.Tiling;

namespace Deskbar.Windows.Tests;

// TileLockMarks keeps one lock mark on each tile of the tiled workspace being looked at (spec
// 2026-11-22-tile-lock-and-other-screens-design.md, "The pin on the window"). The Tiler, the pointer, the windows'
// geometry and whether a tile can be drawn on are fakes; the marks are real windows over real stand-in tiles, so the
// tests see what would be on the screen. Run on a hidden desktop (scripts/test-hidden.ps1).
public class TileLockMarksTests
{
    // The world one test controls: which windows are tiles, which are locked, where the pointer is, and which tiles are
    // settling, closed (no geometry) or not drawable.
    sealed class World
    {
        public List<WindowHandle> Tiles { get; } = [];
        public HashSet<WindowHandle> Locked { get; } = [];
        public HashSet<WindowHandle> Settling { get; } = [];
        public HashSet<WindowHandle> Closed { get; } = [];
        public HashSet<WindowHandle> Hidden { get; } = [];
        public Maybe<(int X, int Y)> Pointer { get; set; } = Maybe<(int X, int Y)>.None;
        public List<(WindowHandle Window, bool On)> SetCalls { get; } = [];
        public int Ignored { get; set; }
        public Subject<Unit> DesktopChanging { get; } = new();
        public Subject<Unit> Activated { get; } = new();

        public TileLockMarks Marks() => new(
            tiles: () => Tiles.ToList(),
            lockOf: w => Tiles.Contains(w) ? Locked.Contains(w) : Maybe<bool>.None,
            setLocked: (w, on) =>
            {
                SetCalls.Add((w, on));
                if (on) Locked.Add(w); else Locked.Remove(w);
                return Result.Success();
            },
            settling: Settling.Contains,
            ignore: _ => Ignored++,
            pinToAllDesktops: _ => Result.Success(),
            geometryOf: w => Closed.Contains(w) ? Maybe<LockMarkGeometry>.None : GeometryOf(w),
            pointer: () => Pointer,
            drawable: w => !Hidden.Contains(w),
            hideNow: DesktopChanging,
            recheckNow: Activated);
    }

    // The geometry of a stand-in tile as DWM would give it with no caption buttons reported: at 96 DPI the mark is 24 px,
    // 4 px left of three 46 px buttons, in a 32 px band from the top (LockMarks.Place).
    static Maybe<LockMarkGeometry> GeometryOf(WindowHandle w)
    {
        GetWindowRect(w.Value, out var r);
        var rect = new WindowRect(r.Left, r.Top, r.Right, r.Bottom);
        return new LockMarkGeometry(rect, rect, Maybe<WindowRect>.None, 96);
    }

    static WindowRect ExpectedMark(WindowHandle w) => LockMarks.Place(GeometryOf(w).Value).Value.Mark;

    static (Window Window, WindowHandle Handle) Tile(int left = 200)
    {
        var window = new Window { Title = "lock marks tile", Width = 500, Height = 300, Left = left, Top = 200, ShowActivated = false };
        window.Show();
        return (window, new WindowHandle(new WindowInteropHelper(window).Handle));
    }

    static WindowRect RectOf(nint hwnd)
    {
        GetWindowRect(hwnd, out var r);
        return new WindowRect(r.Left, r.Top, r.Right, r.Bottom);
    }

    [Fact]
    public void A_locked_tile_gets_a_locked_mark() => StaThread.Run(() =>
    {
        var (window, tile) = Tile();
        var world = new World { Tiles = { tile }, Locked = { tile } };
        using var marks = world.Marks();
        marks.Tick();

        var mark = Assert.Single(marks.Shown).Value;
        Assert.True(mark.LockedLook);
        Assert.Equal(ExpectedMark(tile), RectOf(mark.Handle));
        window.Close();
    });

    [Fact]
    public void An_unlocked_tile_gets_no_mark_until_the_pointer_is_on_its_title() => StaThread.Run(() =>
    {
        var (window, tile) = Tile();
        var world = new World { Tiles = { tile } };
        using var marks = world.Marks();
        marks.Tick();
        Assert.Empty(marks.Shown);

        var r = RectOf(tile.Value);
        world.Pointer = (r.Left + 50, r.Top + 10);
        marks.Tick();
        Assert.False(Assert.Single(marks.Shown).Value.LockedLook);
        window.Close();
    });

    [Fact]
    public void A_click_toggles_the_lock_and_redraws() => StaThread.Run(() =>
    {
        var (window, tile) = Tile();
        var r = RectOf(tile.Value);
        var world = new World { Tiles = { tile }, Pointer = (r.Left + 50, r.Top + 10) };
        using var marks = world.Marks();
        marks.Tick();
        var mark = Assert.Single(marks.Shown).Value;

        ((UIElement)mark.Content).RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonUpEvent,
        });

        Assert.Equal([(tile, true)], world.SetCalls);
        Assert.True(mark.LockedLook);
        window.Close();
    });

    // Review focus 2: a tile minimized, maximized, or cloaked by a desktop switch loses its mark at the next tick.
    [Fact]
    public void A_minimized_maximized_or_cloaked_tile_loses_its_mark() => StaThread.Run(() =>
    {
        var (window, tile) = Tile();
        var world = new World { Tiles = { tile }, Locked = { tile } };
        using var marks = world.Marks();
        marks.Tick();
        var mark = Assert.Single(marks.Shown).Value;

        world.Hidden.Add(tile);
        marks.Tick();
        Assert.Empty(marks.Shown);
        Assert.False(mark.IsVisible);
        window.Close();
    });

    // Review focus 3: a tile that closed between reading the board and reading its geometry: no exception, no mark.
    [Fact]
    public void A_tile_that_closed_mid_tick_loses_its_mark_quietly() => StaThread.Run(() =>
    {
        var (window, tile) = Tile();
        var world = new World { Tiles = { tile }, Locked = { tile } };
        using var marks = world.Marks();
        marks.Tick();
        world.Closed.Add(tile);
        marks.Tick();
        Assert.Empty(marks.Shown);
        window.Close();
    });

    // A tile being dragged or re-tiled shows no mark until it settles, rather than one trailing it.
    [Fact]
    public void A_settling_tile_has_no_mark_until_it_settles() => StaThread.Run(() =>
    {
        var (window, tile) = Tile();
        var world = new World { Tiles = { tile }, Locked = { tile }, Settling = { tile } };
        using var marks = world.Marks();
        marks.Tick();
        Assert.Empty(marks.Shown);

        world.Settling.Clear();
        marks.Tick();
        Assert.Single(marks.Shown);
        window.Close();
    });

    // Review focus 5: tiling off, or a desktop that is not tiled, takes every mark away and slows the timer.
    [Fact]
    public void No_tiles_hides_every_mark_and_slows_the_timer() => StaThread.Run(() =>
    {
        var (window, tile) = Tile();
        var world = new World { Tiles = { tile }, Locked = { tile } };
        using var marks = world.Marks();
        marks.Tick();
        Assert.Equal(TileLockMarks.Fast, marks.Interval);

        world.Tiles.Clear();
        marks.Tick();
        Assert.Empty(marks.Shown);
        Assert.Equal(TileLockMarks.Slow, marks.Interval);
        window.Close();
    });

    // A mark that is no longer needed is kept for the next tile rather than closed and made again, because each new one
    // costs an Ignore and a COM call to pin it to every desktop.
    [Fact]
    public void A_mark_is_reused_not_remade() => StaThread.Run(() =>
    {
        var (first, a) = Tile();
        var (second, b) = Tile(left: 800);
        var world = new World { Tiles = { a }, Locked = { a, b } };
        using var marks = world.Marks();
        marks.Tick();
        world.Tiles.Clear();
        world.Tiles.Add(b);
        marks.Tick();

        Assert.Equal(b, Assert.Single(marks.Shown).Key);
        Assert.Equal(1, world.Ignored);
        first.Close();
        second.Close();
    });

    // Review I1: the marks are pinned to every desktop, so on a desktop switch they would stay where they were, over the
    // desktop just arrived on, until the next tick. The switch hides them all at once.
    [Fact]
    public void A_desktop_change_hides_every_mark_at_once() => StaThread.Run(() =>
    {
        var (window, tile) = Tile();
        var world = new World { Tiles = { tile }, Locked = { tile } };
        using var marks = world.Marks();
        marks.Tick();
        var mark = Assert.Single(marks.Shown).Value;

        world.DesktopChanging.OnNext(Unit.Default);
        Assert.Empty(marks.Shown);
        Assert.False(mark.IsVisible);
        window.Close();
    });

    // Review I2: clicking into a tile brings it to the front, over its own mark; the activation puts the mark back above
    // it at once rather than on the next tick, so a locked padlock does not blink on every focus change.
    [Fact]
    public void An_activation_puts_the_marks_back_above_their_tiles_at_once() => StaThread.Run(() =>
    {
        var (window, tile) = Tile();
        var world = new World { Tiles = { tile }, Locked = { tile } };
        using var marks = world.Marks();
        marks.Tick();
        var mark = Assert.Single(marks.Shown).Value;

        ZOrderProbe.ToFront(tile.Value);
        Assert.NotEqual(mark.Handle, ZOrderProbe.VisibleAbove(tile.Value));
        world.Activated.OnNext(Unit.Default);
        Assert.Equal(mark.Handle, ZOrderProbe.VisibleAbove(tile.Value));
        window.Close();
    });

    [Fact]
    public void GeometryOf_reads_a_real_window() => StaThread.Run(() =>
    {
        var (window, tile) = Tile();
        var geometry = LockMarkNatives.GeometryOf(tile).Value;
        Assert.Equal(RectOf(tile.Value), geometry.Window);
        Assert.True(geometry.Dpi > 0);
        window.Close();
    });

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetWindowRect(nint hwnd, out RECT rect);
}
