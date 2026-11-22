# Tile pin probes, 2026-11-22

For the pin on a tile (spec `docs/superpowers/specs/2026-11-22-tile-lock-and-other-screens-design.md`, "The pin on the
window"). Measured on Petre's machine: every screen at 144 DPI (150%), Windows 11. Probes ran with
`SetProcessDpiAwarenessContext(-4)` and `CharSet.Unicode` P/Invokes, and read only: `DwmGetWindowAttribute` and an
off-screen `PrintWindow(PW_RENDERFULLCONTENT)` of each title bar. Nothing on screen was moved or drawn.

## Where Windows says the caption buttons are

`DWMWA_CAPTION_BUTTON_BOUNDS` (attribute 5) answers in WINDOW coordinates (relative to `GetWindowRect`'s top-left,
invisible resize border included):

| App | Class | Bounds | Width x height |
|---|---|---|---|
| Edge, Chrome | `Chrome_WidgetWin_1` | right edge minus 8 px, top 0 | 220 x 44 |
| File Explorer | `CabinetWClass` | right edge minus 8 px, top 0 | 220 x 44 |
| Windows Terminal | `CASCADIA_HOSTING_WINDOW_CLASS` | right edge minus 8 px, top 0 | 220 x 44 |
| VS Code | `Chrome_WidgetWin_1` | a ZERO-WIDTH rect at the window's right edge | 0 x 44 |

VS Code draws its own minimize, maximize and close, so DWM has none to report. The captures show them flush right,
about 69 px each, which is 46 DIP at 150%: the same 3 x 46 DIP the other apps get from Windows. So a window whose
bounds come back empty but whose height is not falls back to three 46 DIP buttons at the right edge of the visible frame
(`DWMWA_EXTENDED_FRAME_BOUNDS`), with the reported height.

## What sits left of minimize (what the pin covers)

- VS Code: its own title-bar actions. In the captured window, right to left before minimize: a pushpin (VS Code's
  "always on top" toggle), a full-screen button, then the "Update" pill when an update waits. A Deskbar pin there sits
  on or beside VS Code's own pin.
- Edge with vertical tabs, File Explorer, Windows Terminal: empty title bar (Explorer's tab strip and its "+" are at
  the far left). Nothing covered.
- Chrome with horizontal tabs (not captured): its tab search button sits at one end of the tab strip; not measured.

## Not measured yet

How an unowned overlay keeps just above its tile in z-order across processes, and how quickly it follows a moved tile.
That is measured with real windows in `Deskbar.Windows.Tests` during the build, not on Petre's screen.

## Side note from the probe

Starting a window with `deskbar.cmd start "<workspace>"` put Windows Terminal on the workspace being looked at, not the
one named (`wt.exe` hands off to an already-running Terminal process, so the started process is not the window's
owner). Explorer landed where it was asked. Both were tiled into the tiled workspace at once and their tiles gave the
room back when they closed.
