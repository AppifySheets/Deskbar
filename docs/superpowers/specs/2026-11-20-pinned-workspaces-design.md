# Pinned workspaces

## Intent

Petre keeps a Messengers workspace and jumps into it many times a day to answer something, then goes
back to whatever he was doing. Today that round trip means finding Messengers' row (or walking the
switcher), then finding the way back. Petre: "i need to jump in and out of the messengers", "have a
simple way to go back to the previous workspace".

A pinned workspace gets one item on every screen strip and in the bar. Outside the workspace the item
takes you in; inside it, the same item, in the same place, takes you back to where you came from.

## Goals

- One click into a pinned workspace from any workspace or unnamed desktop.
- One click back, on the same spot, to the workspace you were in before.
- "Before" is right however you arrived: the bar, the switcher, Win+Ctrl+arrows, Task View.

## Non-goals

- Pinning a workspace's windows to every desktop. Windows' own "show on all desktops" stays a
  per-window thing. The only window this OS-pins is Deskbar's own taskbar stand-in (below).
- A hotkey. The switcher's Win+Ctrl+Tab tap already goes back, and a third global hotkey needs its own
  case (CLAUDE.md).
- The attention dot on the item when a window in the pinned workspace flashes. Worth doing, later, in
  its own PR.

## Anti-goals

- The item moving under the pointer between "go" and "back". The point is one fixed spot.
- A back target that is wrong because the switch came from outside Deskbar.

## Approaches

- A, chosen: one item per pinned workspace that flips. Outside it reads the workspace's name; inside,
  "<- <previous workspace>". One spot to build a habit on.
- B: the pinned item stays as it is, and a separate back item appears beside it while you are inside.
  More explicit, but it adds an item and the target moves.
- C: a back item in every workspace, no pinning. Simpler, but loses the one-click way in, which is
  half of the loop.

Petre picked A ("because i need to jump in and out of the messengers").

## Design

Sketch of a strip:

```
working:        [Deskbar] [Messengers]      | Code  Terminal  Chrome
in Messengers:  [Messengers] [<- Deskbar]   | Teams  Outlook  Beeper
                 name tag     pinned item     this workspace's windows
```

- Model: `Workspace.Pinned`, a bool init property defaulting to false, stored in state.json the way
  `Folded` and `Tiled` are. No migration.
- The back target: the most-recently-used list the manager already keeps (`WorkspaceMru`), fed by
  every desktop switch whatever caused it. The target is the most recent workspace other than the
  current one. With nothing to go back to (first visit since start), the item reads the workspace's
  name and does nothing on click.
  - My call: an unnamed desktop you came from does not count, since the MRU only holds workspaces.
    Coming from an unnamed desktop, "back" goes to the last workspace instead.
- Strips (`ScreenStrip`): the pinned items sit right after the name tag, before the tabs, in the
  pinned workspace's colour, and take their width out of the tabs' budget. Every strip shows them.
- Bar: the same items on the header line next to Start and search (where `PinLine` lives), so they
  exist when the window list is not on the screens and there are no strips.
- Pinning: "Pin to strip" / "Unpin from strip" on the workspace row's right-click menu, and its remote
  twins `pin-workspace <workspace>` / `unpin-workspace <workspace>` in `ControlUsage` (every menu item
  has one). My call on the names: `pin` and `unpin` are taken by windows.
- Order of several pinned items: the workspaces' own order.
- Clicking runs the same switch the bar's row uses, so the MRU, the follow-active rules and the
  switch's trace stay one path.

## Added after approval

Petre: "can it also show up in windows taskbar?", then "yes and also do the taskbar; i want the pinned
strip icon on all screens", "so i can go to it from both strips".

- Every strip, so every screen, carries the items; that was already the design.
- `jump <workspace>` on the control pipe: into the workspace, or back if you are already in it. The
  same rule the item follows, so a hand-pinned shortcut to `deskbar.cmd jump Messengers` behaves like
  the item, with a fixed icon and name.
- A real Windows taskbar button per pinned workspace: a small Deskbar window, OS-pinned to all
  desktops so its button shows on every desktop's taskbar, titled and iconed as the item reads now
  (the workspace's name and colour outside, "↩ <previous>" inside). Activating it switches; it never
  shows anything. Petre's taskbar is on the main screen only (`MMTaskbarEnabled` = 0, measured
  2026-11-20), so the button is there and the strips cover the other screens.
  - Traps to measure in a throwaway spike first: clicking the button of the active window minimizes
    it rather than activating it; several windows of one exe group under one button unless each has
    its own AppUserModelID; and the window must be left out of the bar's own lists
    (`WindowMonitor.Ignore`) or it shows as a pinned window everywhere.
  - If the spike shows the click cannot be made reliable, the button is dropped and the `jump`
    shortcut is the taskbar answer.

Then, with the strip items live: "let's also have the pinned workspace showing that it's pinned", "maybe move it
to the top of the list", "i may also have multiple pinned workspaces, both should go to the top", "they need to be
ungrouped, no grouped workspace should be available for pinning", and, asked where an unpinned row goes: "back
where it was".

- Pinned rows are DRAWN first on the bar, each side in stored order, with the pin pinned windows wear beside the
  name. The stored order is untouched, so unpinning puts a row back where it was. Row numbers (`deskbar.cmd
  workspaces`) and Move up/down/top/end follow the drawn order: a pinned row moves among the pinned, any other
  block among the rest, each in the stored slots its side already holds.
- Only an ungrouped workspace pins: the row menu does not offer it on a grouped one, `pin-workspace` refuses it,
  and joining a group unpins (enforced where every change is persisted).
- Strip items sit at the END of the window tabs (Petre: "put it at the end of the list of windows"), in "Look 2"
  from the mockup: a dark chip with a dot and underline in the colour of where it goes.

## Left to the builder

- The item's exact look (padding, the arrow glyph, the current-workspace marking) within the strip's
  existing tab style; a mockup goes to Petre before it is final (memory: mockups before visual
  changes).
- Tests: Core for the back-target rule and the persisted flag; Windows tests for the strip and bar
  items in both states.
