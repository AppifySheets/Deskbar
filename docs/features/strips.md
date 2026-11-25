---
title: Strips and pinned workspaces
summary: A thin strip along the top of every screen lists the current workspace's windows, with room for pinned workspaces and the way back.
order: 6
clip: strip-and-pin
---

The current workspace's windows, with their titles, sit in a thin strip along the top of every screen, and a workspace you keep jumping into can be pinned to the end of it.

## How to use it

Each screen's strip holds that screen's windows of the workspace you are in, and maximized windows stop below it. The active window's title is white and the rest are dimmer. Pinned windows carry a pin glyph.

A full-screen window, such as a maximized Remote Desktop session, a video or a browser in F11, covers the whole screen, strip included. The strip goes behind it while it is in front and comes back when you switch to another window on that screen or the full-screen window closes.

The strip is one of three places the window list can go. The **Window list** submenu, on any row's menu and on the bar's background menu, picks the place:

- At the top of each screen, which is the default.
- In the workspace's row, where the current workspace's row expands into one window per line.
- At the bottom of the bar, in a strip under the rows, so the rows stay uniform.

In the bar, titles are cut to fit. Widen the bar to see more of them.

Pin a workspace you keep jumping into, a Messengers workspace say, with **Pin to strip** on its row menu. It gets a small item at the end of every screen's strip and a button on the Windows taskbar, marked with a dot in its colour. Click it to go there. Once you are in it, the same item reads **↩** and the name of the workspace you came from, and clicking it takes you back. Pinned workspaces are drawn at the top of the bar with a pin beside their name, and unpinning puts the row back where it was. A workspace inside a group cannot be pinned, and moving a pinned one into a group unpins it.

Every screen's strip, the current workspace's titled list and the Windows taskbar can also carry an item reading **↩** and the name of the workspace you were on before. Clicking it takes you back. This item does not change pinned workspaces' own items, which flip to **↩** inside their workspace as described above.

## Settings

The **Window list** submenu sets where the list goes. The fewest lines the window list keeps is a slider on Manage's Settings tab.

The previous workspace's item follows **Show the previous workspace on the strips and the taskbar** in Manage. Until you choose, it is on while the window list is on the screens and Windows' taskbar auto-hides, and off otherwise. Once you tick or untick the box, your choice holds from then on, and it takes effect at once.

## From a script

```powershell
$ts = "$env:APPDATA\Deskbar\deskbar.cmd"
& $ts jump Messengers               # into Messengers, or back out if you are already there
& $ts previous-workspace on         # or off
```

`jump` works from a shortcut as well as a script.

## Related

- [The bar](the-bar.md) covers the rows and what each icon tells you.
- [Switching workspaces](switching.md) is the keyboard way back to the previous workspace.
- [Docking and taskbar pieces](docking.md) covers leaving Windows' taskbar on auto-hide.
