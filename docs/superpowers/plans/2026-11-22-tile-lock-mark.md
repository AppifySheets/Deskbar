# Lock mark on a tile Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Draw a padlock just left of each tile's minimize button: closed and filled while the tile is locked, an
open outline while the mouse is over an unlocked tile's title bar. A click toggles the lock that PR 1 added.

**Architecture:** The geometry and the "which marks show" rule are pure functions in Core (`LockMarks`), tested
against the probe's numbers. A small WPF window (`LockMarkWindow`) draws one mark and sits directly above its tile in
z-order, never owned by it. `TileLockMarks` in the App runs a timer while the desktop being looked at is tiled. On
each tick it reads the Tiler's board and locks, each tile's caption geometry and the pointer, then shows, moves or
hides the marks. That follows the `WindowRings`/`RingFrame` pattern already in the repo.

**Tech Stack:** C# / .NET 10, WPF, Win32 + DWM P/Invoke, xUnit, CSharpFunctionalExtensions, System.Reactive.

**Spec:** `docs/superpowers/specs/2026-11-22-tile-lock-and-other-screens-design.md`, section "The pin on the window".
Probe: `docs/superpowers/notes/2026-11-22-tile-pin-probes.md`.

## Global Constraints

- Code calls it a "lock mark", never a "pin": `FloatingBar.Pins.cs` and "pinned to every desktop" already use the word.
- The glyph is a padlock in a rounded square of 24 DIP. Locked: a closed padlock, white on a #2F6FD6 fill, always shown. Unlocked: an open padlock and a 2 px outline in #8A8F98, shown only while the pointer is over that tile's title bar.
- The gap between the mark and the caption buttons is 4 DIP. The VS Code fallback is three 46 DIP buttons from the visible frame's right edge.
- No owner, parent or `SetParent` across processes, and no call that sends a message to the tile's thread (`SendMessage`, `GetWindowText` on another process). Only `Get*` reads and `DwmGetWindowAttribute`, so a hung app can never freeze the bar.
- The mark window has `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`, `ShowActivated = false` and `ShowInTaskbar = false`, and it is NOT `WS_EX_TRANSPARENT`, because it takes clicks. It is never `SW_HIDE`-ed by Win32 code; WPF `Hide()` on our own window is fine, as `WindowRings` does.
- Physical pixels go through `SetWindowPos`, never `Left`/`Top`/`Width`/`Height` (see `RingFrame.Place`).
- Marks appear only for windows on `tiler.Board`, the tiled workspace being looked at. Never for a minimized, maximized or cloaked window, or one that is settling.
- Every mark window goes through `WindowMonitor.Ignore` and is pinned to every desktop, as `WindowRings.TakeFrame` does.
- Trace lines go through `ClickTrace` behind its `On` switch, written only when a mark's state changes.
- C# style per CLAUDE.md: expression bodies, `var`, readonly collections, Rx over events, no `private` keyword, long intent comments.
- Run `Deskbar.Windows.Tests` only through `pwsh scripts/test-hidden.ps1`, never on Petre's desktop.
- Build with `dotnet build Deskbar.sln -c Release -warnaserror` before the PR.

## Review Focus

1. **A tile on a screen at a different scale from the one the mark was last on.** The mark should land on the requested physical rectangle anyway. Task 2 re-sets the rectangle when Windows rescales it, and tests that the rectangle read back equals the one asked for.
2. **A tile cloaked mid-tick by a desktop switch, or minimized or maximized.** Its mark should go at once, with no mark left over the desktop just arrived on. Task 3 tests it.
3. **A tile closed between the board read and the geometry read.** No exception, and its mark is hidden. Task 3 tests it.
4. **A tile too narrow for the mark.** No mark, rather than one drawn over the app's title or off the tile's left edge. Task 1 tests it.
5. **Tiling turned off, or the desktop changed to an untiled one.** Every mark goes, and the timer drops to its slow interval. Task 3 tests it.

---

### Task 1: Where the mark goes and which marks show (Core)

**Files:**
- Create: `src/Deskbar.Core/Tiling/LockMarks.cs`
- Test: `tests/Deskbar.Core.Tests/Tiling/LockMarksTests.cs`

**Interfaces:**
- Consumes: `WindowRect(int Left, int Top, int Right, int Bottom)` with `Width` and `Height` (`Deskbar.Core.Domain`), and `WindowHandle`.
- Produces:
  ```csharp
  public sealed record LockMarkGeometry(WindowRect Window, WindowRect Visible, Maybe<WindowRect> CaptionButtons, int Dpi);
  public sealed record LockMarkSpot(WindowRect Mark, WindowRect TitleBand);
  public sealed record LockMark(WindowHandle Window, WindowRect Frame, bool Locked);
  public static class LockMarks
  {
      public const int SizeDip = 24, GapDip = 4, CaptionButtonDip = 46, FallbackTitleDip = 32;
      public static Maybe<LockMarkSpot> Place(LockMarkGeometry g);
      public static IReadOnlyList<LockMark> Wanted(
          IEnumerable<(WindowHandle Window, LockMarkSpot Spot, bool Locked)> tiles, Maybe<(int X, int Y)> pointer);
  }
  ```
  `Window` is `GetWindowRect`. `Visible` is `DWMWA_EXTENDED_FRAME_BOUNDS`. `CaptionButtons` is `DWMWA_CAPTION_BUTTON_BOUNDS` in WINDOW coordinates (relative to `Window`'s top-left), or None when DWM refused.

The rules for `Place`, all at `px(d) = Math.Round(d * Dpi / 96.0, MidpointRounding.AwayFromZero)`:
- Caption buttons with width > 0: the buttons' left edge is `Window.Left + cb.Left`. The band is `Window.Top + cb.Top` and `cb.Height` tall.
- Caption buttons with width 0 and height > 0 (VS Code): the buttons' left edge is `Visible.Right - px(3 * 46)`. The band is the same as above.
- None, or height 0: the buttons' left edge is `Visible.Right - px(3 * 46)`. The band starts at `Visible.Top` and is `px(32)` tall.
- The mark is `px(24)` square, its right edge `px(4)` left of the buttons, vertically centred in the band (integer division).
- `TitleBand` is `Visible.Left .. Visible.Right` horizontally, the band vertically.
- None when the mark's left edge would fall left of `Visible.Left`.

`Wanted`: a locked tile always gets `LockMark(window, spot.Mark, true)`. An unlocked tile gets `LockMark(window, spot.Mark, false)` only when the pointer is inside `TitleBand` or inside `Mark`, with left and top inclusive and right and bottom exclusive. With no pointer, only locked tiles. Tiles keep their input order.

- [ ] **Step 1: Write the failing tests** (the probe's numbers at 144 DPI, and one at 96)

```csharp
public class LockMarksTests
{
    static readonly WindowHandle A = new(0xA), B = new(0xB);

    [Fact] // Edge, Chrome, Explorer, Terminal: DWM reports 220 x 44 at the right edge minus 8
    public void Beside_the_buttons_Windows_reports() =>
        Assert.Equal(new LockMarkSpot(new WindowRect(1730, 104, 1766, 140), new WindowRect(1011, 100, 1989, 144)),
            LockMarks.Place(new LockMarkGeometry(new WindowRect(1000, 100, 2000, 900), new WindowRect(1011, 100, 1989, 889),
                new WindowRect(772, 0, 992, 44), 144)).Value);

    [Fact] // VS Code: a zero-width rectangle at the right edge, so three 46 DIP buttons from the visible right
    public void Beside_three_standard_buttons_when_the_app_draws_its_own() =>
        Assert.Equal(new WindowRect(1351, 4, 1387, 40),
            LockMarks.Place(new LockMarkGeometry(new WindowRect(0, 0, 1600, 1000), new WindowRect(0, 0, 1600, 1000),
                new WindowRect(1600, 0, 1600, 44), 144)).Value.Mark);

    [Fact]
    public void A_32_DIP_title_band_when_DWM_says_nothing() =>
        // 96 DPI: buttons from 1600 - 138 = 1462, mark 1434..1458, top 0 + (32 - 24) / 2 = 4
        Assert.Equal(new LockMarkSpot(new WindowRect(1434, 4, 1458, 28), new WindowRect(0, 0, 1600, 32)),
            LockMarks.Place(new LockMarkGeometry(new WindowRect(0, 0, 1600, 1000), new WindowRect(0, 0, 1600, 1000),
                Maybe<WindowRect>.None, 96)).Value);

    [Fact]
    public void No_mark_on_a_tile_too_narrow_for_it() =>
        Assert.True(LockMarks.Place(new LockMarkGeometry(new WindowRect(0, 0, 220, 600), new WindowRect(0, 0, 220, 600),
            new WindowRect(0, 0, 220, 44), 144)).HasNoValue);

    [Fact]
    public void A_locked_tile_shows_wherever_the_pointer_is() { /* A locked, B unlocked, pointer None => [A locked] */ }

    [Fact]
    public void An_unlocked_tile_shows_while_the_pointer_is_on_its_title_band() { /* pointer inside B's band => [B unlocked] */ }

    [Fact]
    public void The_band_excludes_its_bottom_edge() { /* pointer at band.Bottom => nothing for an unlocked tile */ }
}
```

Write the three `Wanted` tests out with `Assert.Equal` on the full list.

- [ ] **Step 2: Run them and confirm they fail**

Run: `dotnet test tests/Deskbar.Core.Tests --filter "FullyQualifiedName~LockMarksTests"`
Expected: build error, `LockMarks` not defined.

- [ ] **Step 3: Implement `LockMarks` in `src/Deskbar.Core/Tiling/LockMarks.cs`**

Write a header comment that quotes the spec and the probe, and explains why the fallback exists.

- [ ] **Step 4: Run them and confirm they pass**

Same command. Expected: PASS, 7 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Deskbar.Core/Tiling/LockMarks.cs tests/Deskbar.Core.Tests/Tiling/LockMarksTests.cs docs/superpowers/notes/2026-11-22-tile-pin-probes.md docs/superpowers/specs/2026-11-22-tile-lock-and-other-screens-design.md docs/superpowers/plans/2026-11-22-tile-lock-mark.md
git commit -m "feat: where a tile's lock mark goes and when it shows"
```

### Task 2: The mark's window, directly above its tile

**Files:**
- Create: `src/Deskbar.App/ZOrder.cs`
- Create: `src/Deskbar.App/LockMarkWindow.cs`
- Modify: `src/Deskbar.App/RingFrame.cs` (`Place` uses `ZOrder.PlaceDirectlyAbove`)
- Test: `tests/Deskbar.Windows.Tests/LockMarkWindowTests.cs`

**Interfaces:**
- Consumes: `LockMark` (Task 1), `NativeMethods` (`Deskbar.Windows.Monitoring`).
- Produces:
  ```csharp
  static class ZOrder
  {
      // Puts `hwnd` on `rect` (physical pixels), directly above `target`, shown and not activated. Leaves the z-order
      // alone when `hwnd` already is the window above `target`. Re-sets the rectangle once if Windows rescaled it.
      public static void PlaceDirectlyAbove(nint hwnd, nint target, int x, int y, int width, int height);
  }
  sealed class LockMarkWindow : Window
  {
      public nint Handle { get; }                  // EnsureHandle
      public IObservable<Unit> Clicked { get; }    // left button released over the mark
      public void Put(LockMark mark, nint target); // look + ZOrder.PlaceDirectlyAbove
      internal bool LockedLook { get; }            // for tests
  }
  ```
  `ZOrder.PlaceDirectlyAbove` is `RingFrame.Place`'s z-order and rectangle half, moved verbatim (`GW_HWNDPREV`, the `keepOrder` check, the `HWND_TOP` fallback, the re-set when the rectangle came out different). `RingFrame` keeps its own `Topmost` line before calling it. The mark never sets `Topmost`, because a tile is never topmost.

`LockMarkWindow` copies `RingFrame`'s constructor settings, except that it keeps `IsHitTestVisible` true, uses a hand cursor and skips `WS_EX_TRANSPARENT`. Its content is a `Viewbox` holding a 24 x 24 `Border`: corner radius 5, and the padlock as two `Path`s, the shackle (closed `M8 11V8a4 4 0 0 1 8 0v3`, open `M8 11V8a4 4 0 0 1 7.6-1.8`) and the body (`Rect 5,11 14x10`, radius 2). The tooltip reads "New windows won't split this tile. Click to allow." when locked and "Don't split this tile" when unlocked. Brushes are frozen statics.

- [ ] **Step 1: Write the failing tests**, on the test STA thread the other window tests use (`StaThread`), against a plain WPF target window this test opens:
  - `Put_lands_directly_above_its_target`: after `Put`, `GetWindow(target, GW_HWNDPREV) == mark.Handle`.
  - `Put_lands_on_the_rectangle_asked_for`: `GetWindowRect(mark.Handle)` equals `mark.Frame`.
  - `A_second_put_keeps_a_window_raised_in_between_in_front`: open a third window over the target after the first `Put`, `Put` again with the same frame, and the third window is still above the mark.
  - `The_mark_is_a_tool_window_that_never_activates_and_takes_clicks`: the ex-style has `WS_EX_TOOLWINDOW` and `WS_EX_NOACTIVATE` and not `WS_EX_TRANSPARENT`.
  - `The_look_follows_the_lock`: `Put` with `Locked: true`, then `false`, and `LockedLook` follows.
  - `A_click_is_reported`: raise `MouseLeftButtonUp` on the window's content and `Clicked` emits once.
  - `RingFrame` still passes its existing tests after the refactor.

- [ ] **Step 2: Run them and confirm they fail**

Run: `pwsh scripts/test-hidden.ps1 -Arguments 'tests/Deskbar.Windows.Tests --filter FullyQualifiedName~LockMarkWindowTests'`
Expected: build error, `LockMarkWindow` not defined.

- [ ] **Step 3: Write `ZOrder.cs`, switch `RingFrame.Place` to it, then write `LockMarkWindow.cs`**

- [ ] **Step 4: Run them and confirm they pass, ring tests included**

Run: `pwsh scripts/test-hidden.ps1 -Arguments 'tests/Deskbar.Windows.Tests --filter "FullyQualifiedName~LockMarkWindowTests|FullyQualifiedName~Ring"'`
Expected: PASS.

- [ ] **Step 5: Commit** `feat: the lock mark's window, directly above its tile`

### Task 3: Keeping the marks on the tiles

**Files:**
- Create: `src/Deskbar.App/TileLockMarks.cs`
- Create: `src/Deskbar.App/LockMarkNatives.cs`
- Modify: `src/Deskbar.Windows/Monitoring/NativeMethods.cs` (add `DWMWA_CAPTION_BUTTON_BOUNDS = 5`)
- Test: `tests/Deskbar.Windows.Tests/TileLockMarksTests.cs`

**Interfaces:**
- Consumes: `LockMarks`, `LockMarkGeometry`, `LockMark` (Task 1); `LockMarkWindow` (Task 2).
- Produces:
  ```csharp
  static class LockMarkNatives
  {
      public static Maybe<LockMarkGeometry> GeometryOf(WindowHandle window); // None if GetWindowRect or the frame read fails
      public static bool Drawable(WindowHandle window);                      // IsWindow && !IsIconic && !IsZoomed && not cloaked
      public static Maybe<(int X, int Y)> Pointer();                         // GetCursorPos
  }
  sealed class TileLockMarks : IDisposable
  {
      public static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(100), Slow = TimeSpan.FromMilliseconds(500);
      public TileLockMarks(
          Func<IReadOnlyList<WindowHandle>> tiles,            // the Tiler's board, every screen
          Func<WindowHandle, Maybe<bool>> lockOf,             // Tiler.TileLockOf
          Func<WindowHandle, bool, Result> setLocked,         // Tiler.SetTileLocked
          Func<WindowHandle, bool> settling,                  // TileSurface.IsSettling
          Action<nint> ignore, Func<nint, Result> pinToAllDesktops,
          Func<WindowHandle, Maybe<LockMarkGeometry>>? geometryOf = null,
          Func<Maybe<(int X, int Y)>>? pointer = null,
          Func<WindowHandle, bool>? drawable = null);
      public void Start();
      internal void Tick();
      internal TimeSpan Interval { get; }
      internal IReadOnlyDictionary<WindowHandle, LockMarkWindow> Shown { get; }
      public void Dispose();
  }
  ```

One `Tick`:
1. For each tile, skip it unless it is drawable, not settling and has a geometry and a spot. The tile's lock is `lockOf(w).GetValueOrDefault(false)`.
2. `LockMarks.Wanted(...)` with the pointer.
3. Hide every shown mark whose window is not wanted any more, and return it to an idle stack.
4. Wanted windows reuse their own mark, or take one from the idle stack, or make a new one: `ignore` it, `Show()` it, and pin it, retrying on later ticks until pinning works, as `WindowRings.unpinned` does. Then `Put(mark, tile)`.
5. `Interval = tiles().Count > 0 ? Fast : Slow`.

A mark's `Clicked` toggles the lock with `setLocked(w, !lockOf(w).GetValueOrDefault(false))`, traces a failure, and calls `Tick()` at once. Each tick wraps its work in try/catch and traces a failure, so one bad window can't stop the timer. Trace lines are `lock mark 0x<hwnd>: locked|hover|gone`, written only on a change. `Dispose` stops the timer and closes every window.

- [ ] **Step 1: Write the failing tests** with fake `tiles`, `lockOf`, `geometryOf`, `pointer` and `drawable`, and a real plain WPF target window per tile so `Put` has a real hwnd:
  - `A_locked_tile_gets_a_locked_mark`: `Shown` has the tile, `LockedLook` is true and the rectangle is the spot's mark.
  - `An_unlocked_tile_gets_no_mark_until_the_pointer_is_on_its_title`: no mark, then the pointer moves into the band, and an unlocked mark appears.
  - `A_click_toggles_the_lock_and_redraws`: emit `Clicked`, `setLocked` gets `(tile, true)` and the look flips with no second `Tick`.
  - `A_minimized_maximized_or_cloaked_tile_loses_its_mark`: `drawable` returns false, and the mark is gone after the next tick (Review Focus 2).
  - `A_tile_that_closed_mid_tick_loses_its_mark_quietly`: `geometryOf` returns None, no exception and no mark (Review Focus 3).
  - `A_settling_tile_has_no_mark_until_it_settles`.
  - `No_tiles_hides_every_mark_and_slows_the_timer`: `tiles` becomes empty, `Shown` is empty and `Interval == Slow` (Review Focus 5).
  - `A_mark_is_reused_not_remade`: a tile leaves and another joins, and `ignore` was called once.
  - `GeometryOf_reads_a_real_window`: on a plain WPF window, `Window` equals `GetWindowRect` and `Dpi > 0`.

- [ ] **Step 2: Run them and confirm they fail**

Run: `pwsh scripts/test-hidden.ps1 -Arguments 'tests/Deskbar.Windows.Tests --filter FullyQualifiedName~TileLockMarksTests'`
Expected: build error.

- [ ] **Step 3: Implement `LockMarkNatives` and `TileLockMarks`**

- [ ] **Step 4: Run them and confirm they pass**. Same command, expected PASS.

- [ ] **Step 5: Commit** `feat: lock marks follow the tiles of the desktop being looked at`

### Task 4: Wiring, docs and the live check list

**Files:**
- Modify: `src/Deskbar.App/App.xaml.cs` (build next to `windowRings` after `tiler` exists, `Start()`, and dispose before the Tiler in both teardown paths, around lines 2106 and 2504)
- Modify: `README.md` (the tiling section: the padlock on each tile)
- Modify: `docs/superpowers/notes/manual-test-script.md` (L9 onwards)
- Modify: `docs/superpowers/specs/2026-11-22-tile-lock-and-other-screens-design.md` (only where the build departs from it: the poll replaces "moved on LOCATIONCHANGE", and a settling tile hides its mark)

**Interfaces:**
- Consumes: `TileLockMarks` (Task 3), `tiler.Board.Screens.Values.SelectMany(s => s).ToList()`, `tiler.TileLockOf`, `tiler.SetTileLocked`, `tileSurface.IsSettling`, and the `ignore` and `pinToAllDesktops` lambdas `WindowRings` gets.

- [ ] **Step 1: Wire it in `App.xaml.cs`.** Every lambda reads `tiler` and `tileSurface` when called, the way the bar's `TileLockOf` does, so a disposed Tiler gives no tiles.
- [ ] **Step 2: Build Release with warnings as errors**

Run: `dotnet build Deskbar.sln -c Release -warnaserror`
Expected: 0 warnings, 0 errors.

- [ ] **Step 3: Run the routine suites**

Run: `dotnet test tests/Deskbar.Core.Tests --filter "Category!=Integration"`, then `pwsh scripts/test-hidden.ps1`.
Expected: all green, apart from the known `TileSurfaceTests` timing flake, which gets a rerun.

- [ ] **Step 4: Docs.** README: the padlock, what each look means and that a click toggles it. Manual test script steps:
  - L9: a locked tile shows a closed padlock left of minimize, in VS Code, Edge, Explorer and Terminal.
  - L10: hovering an unlocked tile's title bar shows the open outline.
  - L11: a click toggles the lock, does not take focus and changes the icon menu's tick to match.
  - L12: a floating window dragged over a tile's title bar covers the mark.
  - L13: switching to an untiled desktop takes every mark away, and coming back brings the locked ones back.
  - L14: a maximized tile shows no mark, and restoring it brings the mark back.
  - L15: on a screen at another scale, the mark sits beside minimize.
- [ ] **Step 5: Commit** `feat: lock marks on every tile, wired in` and the docs commit.
