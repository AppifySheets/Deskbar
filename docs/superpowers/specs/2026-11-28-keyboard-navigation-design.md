# Keyboard-only navigation in the tasks panel

[Deskbar task b61f79de](https://deskbar.app/task#b61f79de-1d04-4d99-ab58-2d1ddba4190c), 2026-11-28. Short design (a small
change).

## Why

Petre wants to work the tasks panel without reaching for the mouse: walk the Inbox, open a task's page, act on it and
come back, all from the keyboard. He also asked for a panel that tells him which keys there are.

Today the panel knows three keys (Esc, F2, Ctrl+F). Every button is built `Focusable = false` (`TasksWindow.Flat`) so a
click never takes the keyboard from a box he is typing in, and every redraw rebuilds the buttons, so Tab reaches none of
them.

## Scope

- In: the tasks panel (Inbox, List, Matrix, Stats, an Inbox page) once it has the keyboard. The task window
  (`TasksWindow.InboxWindow.cs`) shares the page keys because it is the same class.
- Out: bringing the panel up from elsewhere in Windows (Petre picked "inside the panel only"; no new global hotkey).
  The floating bar's task lines. A Tab ring over every button (approach 2 below, kept for later if this misses
  something).

## Approaches

1. **Menus and letter keys** (picked by Petre, "your pick is good"). The arrows move the existing selection, which
   already survives redraws; the line's "⋯" menu, a WPF ContextMenu, already carries the line's actions and WPF walks it
   with the arrows once it is open; a few letters cover what the menu does not.
2. **A Tab ring over every button.** Make the buttons focusable with a visible ring, remember the focused one by name
   across redraws and put it back. Reaches everything, about three times the work, and fights the deliberate
   `Focusable = false`. Not now.

## The keys

None of these fire while a text box has the keyboard (search, add, comment, rename, link, subtask boxes): there the box
keeps its arrows, letters and Enter, and Esc still does what it does today (leaves the box, the keyboard back to the
board).

| Key | Where | Does |
|-----|-------|------|
| Up / Down | Inbox list, List, Matrix | select the previous / next line, in the order drawn (`Keys`), scrolled into view |
| Home / End | same | first / last line |
| Enter | Inbox list | open the selected line's page (as its click) |
| Enter | List, Matrix | open or fold the selected line's thread (as its click) |
| Esc | everywhere | as today: back from a page, fold a thread, close a box |
| Left / Right | Inbox list | previous / next Inbox tab (Needs you, Claude, Notes, All), when tabbed |
| Ctrl+1..4 | everywhere | Inbox, List, Matrix, Stats (Matrix only where it is allowed) |
| Menu key, Shift+F10 | a selected line or a page | open that task's "⋯" menu under its button (or under the line when it has none) |
| C | a page, a selected line with its thread open | the keyboard to the comment box |
| P | a page or selected line | open its pull request; with several, a small menu of them |
| W | a page or selected line | the workspace move menu (as the workspace chip) |
| R | a page or selected line | recheck (as the recheck tile) |
| N | anywhere | the add box (as "+ Add") |
| F2, Ctrl+F | as today | rename, search |
| ? or F1 | anywhere | the keys panel |

**Found while building:** a page opens with the keyboard in its comment box (task 64744611, `TasksWindow.PageFocus.cs`),
so plain letters on a page would be typed into the message. The letters also answer to Alt+letter everywhere (Alt+P,
Alt+W, Alt+R, Alt+N, Alt+C), which no text box uses, and the menu key works from the box too. On a page, Up/Down scroll
it when no box has the keyboard; Page Up/Page Down scroll it even from the box.

## The keys panel

A small card over the panel, drawn by the panel itself (not a new window), listing the keys above for the view that is
showing, in two columns: key and what it does. `?`, F1 or Esc closes it, and so does a click outside it. A quiet
"? keys" hint sits at the foot of the panel, next to the view switch, so the panel's existence is visible; clicking it
opens the card too.

## Left to the builder

- Where exactly the selection lands after a tab switch or view switch (first line unless the selected task is shown).
- Selection without a visible line (filtered out, folded): the first Up/Down picks the first line.
- The keys go through `Press(key, modifiers)` so the tests can drive them, as F2 and Ctrl+F already do.
- Matrix: walk its lines in the drawn order of `Keys`; no 2D movement between quadrants.
