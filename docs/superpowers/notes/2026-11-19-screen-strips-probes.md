# Screen strips: what was measured before building

Task 0 of `docs/superpowers/plans/2026-11-19-screen-strips.md`, on Petre's machine, 2026-11-19. Two 4K
screens at 150% (144 dpi): the left one at `[-3840,286,0,2446]` with Deskbar docked on its right edge
(`[-335,286,0,2446]`), the primary at `[0,0,3840,2160]` with Windows' taskbar on auto-hide at the bottom.

## 1. Top-edge app bars

A throwaway WinForms probe (borderless, tool window, topmost, PerMonitorV2) registered its own app bars,
never the running Deskbar's, and removed each one. 32 DIP is 48 px at 150%.

| Case | Asked | QUERYPOS / SETPOS | Work area |
|---|---|---|---|
| Top bar alone, left screen | `[-3840,286,0,334]` | same | `[-3840,334,-335,2446]`, back to `[-3840,286,…]` on remove |
| Top bar alone, primary | `[0,0,3840,48]` | same | `[0,48,3840,2160]`, back to `[0,0,…]` on remove |
| Top, then a left bar 300 px wide | left asked `[0,0,300,2160]` | left granted `[0,48,300,2160]` | `[300,48,3840,2160]` |
| Left, then top | top granted `[0,0,3840,48]`; left keeps `[0,0,…]` until it asks again, then `[0,48,300,2160]` | | `[300,48,3840,2160]` in both states |

What follows from it:

- The top edge works on both screens with the same calls as the left and right edges.
- **The shell itself puts a side bar below a top bar**, in either order: a side bar's QUERYPOS for the
  full screen height comes back starting at the top bar's bottom edge. The top bar is always granted the
  full width. So "strip spans the whole top, the docked bar below" needs no registration order and no
  extra code. It was seen on the real bar too: while the probe held the top of the left screen, the
  running Deskbar moved itself from `[-335,286,0,2446]` to `[-335,334,0,2446]` (it re-docks on
  `ABN_POSCHANGED`, and `AppBar.Dock` keeps the top and bottom QUERYPOS gives), and went back to 286
  when the probe removed its bar.
- Each change by one bar sends `ABN_POSCHANGED` (wParam 1) to the others, about ten in a burst. A
  SETPOS that grants the rectangle the bar already holds sends none, so bars re-docking on that
  notification settle instead of looping.
- **The shell does not move a restored window** whose title bar ends up under a new top bar: a window at
  `[450,6,1350,456]` stayed there with the work area at `[0,48,…]`. The strips have to move such windows
  down themselves.

## 2. A bar-built button in another window

Answered from the code rather than a live spike (a ruling, see the plan's ledger): each behaviour of
`IconButton` was traced to the bar state it reads.

- Click, and click on the active window to minimize: `manager.ToggleWindow`, independent of the window.
  Works.
- Right-click menu (`LazyMenu`), drag out (`WindowDragSource`, OLE), the drop it lands through: window
  independent. Work.
- **Press tracking breaks.** `pressSeen`, `pressConsumedByChild` and `orphanRelease` are set by the
  bar's own window-level preview handlers (`OnPreviewMouseLeftButtonDown` and its release twin). A press
  on a strip never reaches them, so `orphanRelease` keeps whatever the bar's last release left, and a
  stale `true` makes the icon's orphan-release handler toggle the window a second time. The strip must
  report its presses and releases to the bar, and only the press-state half of those handlers: the rest
  of them start moving or resizing the bar.
- **The hover card breaks.** `ShowHoverCard` places the popup against the bar and computes its offset
  with `anchor.TranslatePoint(..., this)`. Read from the code, that looked like it would throw for an
  anchor in another window; when task 7 measured it, it did not throw, and the card opened beside the
  bar instead of beside the tab. Either way a tab's card needs the tab as its placement target, below it. The watch that closes the card must also count the
  strips as places the pointer may be.
- The candidate ring (`ApplyCandidate`) and the fade read only the bar's own rows and state. A hovered
  tab rings nothing on the bar, which is fine.

## 3. A screen dropping out

Not run: locking or unplugging needs Petre at the desk, and the answer does not change what gets built.
The 2026-11-01 measurement in `FloatingBar.DockGuard.cs` already says what matters: a waking screen drops
out for a moment and `MONITOR_DEFAULTTONEAREST` then answers with another screen. So the strip's guard
does not ask Windows which screen it is on. It compares its window with its OWN screen's rectangle, and
re-docks there only while that screen is connected; a screen that is gone is the reconcile's business,
on the settled layout. Lock, unlock and unplug are in the manual script (task 10) and are tried live with
Petre before the PR.
