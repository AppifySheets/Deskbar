---
title: The bar
summary: An always-on-top strip with one row per workspace that shows the windows of every desktop at once.
order: 2
clip: hover-card
---

The bar is an always-on-top strip with one row per workspace. It shows the windows of every desktop at once, not only the one you are on.

## How to use it

Each row carries the workspace's name on a line of its own above the icons, cut with an ellipsis when it does not fit. Every row has a colour rail. The current workspace's row is tinted with its colour and ringed in a solid outline. The row you came from is ringed in dashes and the one before that in dots, so you can see where one tap of `Win+Ctrl+Tab` will take you without pressing anything. Hovering a row rings it in amber, the same ring the chord uses for the row it would land on.

Workspaces you have not used lately fade back. The longer a workspace goes unused, the more its rail, name and icons dim, until after two hours it sits well behind the rest. Behind the workspace rows and the bar's top the bar is slightly see-through (the default), and a workspace you used in the last few minutes sits on a solid patch, faintly tinted with its colour; the patch wears away as the row fades, until the desktop shows faintly through the row. What shows through is your wallpaper, blurred so its colours come through without its detail. Docked, it is sharp at the bar's inner edge so the bar runs into the desktop with no hard line. Floating, the bar shows the wallpaper too, even over a window, the way Windows 11 tints a window from the desktop picture, and the frost slides with the bar as you drag it. How blurred is a slider in Manage. Only focusing a window counts as using its workspace: windows that merely opened, say after Windows restarts, leave it faded. Nothing moves or hides, and every row can still be clicked where it is.

What each icon tells you:

- The focused window is outlined and filled. A fainter amber outline on another row marks the window you would land on if you switched there.
- Icons fade as they recede: full strength in front, dimmer when covered, dimmest when minimised.
- An amber dot means the app flashed its taskbar button at you. Apps that only badge their icon (WhatsApp, for one) do not trigger it. Looking at the window clears it.
- A pulsing green ring means the window is playing sound. The ring holds still when Windows' animations are off.
- A small number at the top right is a tab count, on browser windows with two or more tabs and on VS Code windows for their editor tabs.
- Several windows with the same icon each get a short coloured bar along the bottom, so you can tell three Chrome windows apart. The key is the icon's pixels, not the process, so an installed web app next to its browser has its own icon and gets no bar.
- An icon you have not touched for fifteen minutes starts to lose its colour and is fully grey after eight hours. The window you are in never ages.
- A window with no readable icon, an elevated process for example, gets a lettered placeholder.

Rest the pointer on an icon and a card appears with the window's title, its app and workspace, and when it was last used. For a window on a workspace you are not looking at, the card adds a picture of the window at a quarter size, so you can see what is waiting elsewhere without going there. A window on your current workspace, or a minimised one, gets a card with text alone. The card waits for the pointer to settle first, with the same delay Windows' own taskbar thumbnails use, so crossing the bar on the way somewhere else shows nothing.

Within a row, icons keep the order their windows were first seen, so nothing jumps out from under the pointer. Drag an icon along its row to put it where you want. A thin marker shows where it will land, and the order is saved by app, so it comes back after a restart.

Click an icon to jump to that window wherever it lives, and click it again once you are in it to minimise it. Click anywhere else on a row to switch to that workspace. Drag an icon to another row to move the window there. Right-click an icon, a row or the empty bar for the menus.

The bar does not appear in `Alt+Tab` or on the taskbar. It stays bright while you hold the chord, drag an icon or have one of its menus open. Otherwise it holds full strength for ten seconds after the pointer leaves, then dims over four seconds to 35%.

## Settings

Manage's Settings tab has a section for the bar, and each setting in it applies as you change it:

- the see-through band behind the workspace rows, on unless you untick it (it shows your wallpaper blurred under half the bar's colour, docked or floating; across two screens, or over a tiled or solid-colour wallpaper, it keeps 85% of the bar's colour; the top of the bar, with Start, search and the pinned apps, wears the same band, the strips along the top of each screen show their own slice of the wallpaper the same way, and only the footer stays solid);
- what the bottom strip shows;
- sliders for how faint the bar goes once the pointer is elsewhere, how long it waits before dimming and how long the dimming takes (a fully opaque setting means it never dims);
- how long the pointer rests on an icon before its card appears, which follows Windows' own setting unless you untick its box;
- the text size, the icon size and the fewest lines the window list keeps;
- how blurred the wallpaper is behind the rows of a docked bar, 8 screen pixels unless you move it, down to 0 for a sharp picture.

You choose a slider's value by watching the bar while you drag. **Reset the bar's look to defaults** puts the sliders and the bottom strip back.

The bar draws at 90% by default. To make it bigger or smaller, set `"BarScale"` in `%APPDATA%\Deskbar\state.json` to anything between `0.5` and `2.0` and restart Deskbar. Manage has no setting for it yet.

## From a script

```powershell
$ts = "$env:APPDATA\Deskbar\deskbar.cmd"
& $ts workspaces          # JSON: name, group, row, colour, minimized, current
& $ts bar minimize        # also restore, dock, undock, across, edge
& $ts see-through off     # or on
```

`fold` and `unfold` fold a workspace's row to its name, as the row menu does.

## With Claude sessions

When a Claude Code session finishes a reply or waits for a permission while you are on another workspace, that workspace's name and colour rail flash orange on the bar and the session's VS Code icon glows. When you go there, the window's own border blinks orange for five seconds, or until you click or type in it, so you can tell which window asked. A window whose taskbar button flashed blinks the same way. See [Claude Code sessions](claude-sessions.md).

## Related

- [Strips and pinned workspaces](strips.md): the window list along the top of every screen.
- [Switching workspaces](switching.md) and [Arranging the bar](arranging.md).
- [Docking and taskbar pieces](docking.md): docking the bar to a screen edge, and the taskbar parts it carries.
- The README on [screens](../../README.md#screens), [clicking, dragging and moving](../../README.md#clicking-dragging-moving), [the row menu](../../README.md#the-workspace-row-menu), [groups](../../README.md#groups) and [minimising the bar](../../README.md#minimising-the-bar).
