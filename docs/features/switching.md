---
title: Switching workspaces
summary: Win+Ctrl+Tab walks your workspaces in most-recently-used order, the way Alt+Tab walks windows.
order: 3
clip: switch
---

`Win+Ctrl+Tab` moves between workspaces the way `Alt+Tab` moves between windows, and one tap takes you back to wherever you just were.

## How to use it

Hold `Win+Ctrl` and tap `Tab` to walk your workspaces in most-recently-used order, then release to switch. Add `Shift` to walk backwards, and press `Esc` to cancel. Because the order is most recently used, one tap returns you to the workspace you just left, however your list is arranged.

While you hold the chord, a short list appears beside the bar with the walk in order. On the bar itself, the row you would land on is ringed in amber and the window that would take focus there is outlined. Nothing switches until you let go, so walking past three workspaces does not animate three desktop switches.

You arrive in the window you left: landing on a workspace puts focus back on whichever window had it last time you were there. A workspace you have not been to yet this session gives focus to the front-most window on your main monitor.

You can also click a row on the bar to switch to that workspace, or click the **↩** in the bar's header, which goes back to the workspace you were on before, the same as one tap of the chord.

`Win+Ctrl+Tab` was chosen by testing every `*+Tab` chord against Windows. It is the only one with both its forward and reverse halves free, and it neighbours `Win+Ctrl+←/→`, which Windows already uses for desktops.

## Settings

Change the chord on Manage's **Shortcuts** tab. The new chord is checked as you type and takes effect at once. It can never be the same as quick find's.

Whatever you pick becomes exclusive while Deskbar runs, so avoid chords other apps need: `Ctrl+Tab` would stop reaching browsers, editors and Explorer. Deskbar detects the key release by polling while the list is up, not with a keyboard hook, so it never sits in the path of your keystrokes.

## From a script

`switch` changes workspace from a script, and `deskbar.cmd jump <workspace>` goes into a workspace or back out if you are already there. See [Driving it from scripts](scripts.md) and [Strips and pinned workspaces](strips.md).

## With Claude sessions

When a session's workspace flashes orange on the bar, click its row to go there, or walk to it with the chord and let go when the amber ring is on it. When you are alternating between two sessions, one tap of the chord takes you from one to the other. See [Claude Code sessions](claude-sessions.md).

## Related

- [Quick find](quick-find.md) goes to a window or tab by name instead of by workspace.
- [The bar](the-bar.md) shows the rings that tell you where a tap lands.
- [Manage](manage.md) has the Shortcuts tab.
