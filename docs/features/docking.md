---
title: Docking and taskbar pieces
summary: Dock the bar to a screen edge or across two screens, and let it carry enough of the taskbar that Windows' own can stay on auto-hide.
order: 9
---

By default the bar floats wherever you drop it. It can also dock to a screen edge or across two screens, and docked or not, it carries enough of the taskbar that you can leave Windows' own on auto-hide.

## How to use it

Both docking modes are off by default. Turn them on from any bar menu, from the box in Manage, or from the command line.

**Dock to screen edge** turns the bar into an app bar on the left or right edge of its screen, full height with square corners. Windows reserves that strip, so maximised windows, Snap and new windows keep clear of it. The bar cannot be moved while docked, because moving the strip would leave the reservation behind, but you can still drag its width. A full-screen window on the bar's screen, such as a video, a game or a maximized Remote Desktop session, has the bar step just behind it while it is in front. Minimising the bar gives the strip back.

The floating bar does the same for a full-screen window, a video or a game, on its own screen: it steps just below the window and comes back when the window leaves full screen, closes or is minimised. Only the bar's own screen counts. A video on the other screen leaves the bar on top, and a bar dragged onto the video's screen goes below it too. To keep the floating bar on top of full-screen windows instead, pick **Stay on top of full-screen windows** in the bar's right-click menu. **Step aside for full-screen windows** turns it back.

**Dock across both screens** straddles the seam between two side-by-side screens. The first screen's lane sits on the left screen and the second's on the right, each half reserving half the bar's width, and each half repeats the row's caption and group line so either side reads on its own. Screens that are not level are handled: the bar spans both strips and its content is inset to the height the two screens share. Screens with different scaling are not.

The taskbar pieces:

- A line of pins shows the taskbar's pinned apps in the taskbar's order. Click one to jump to its window in this workspace, or to start it here if it has none. A short bar under a pin means it has a window here, and a dot means it has one only elsewhere. Middle-click or `Shift`+click opens a new window. Browser pins always open a new window, and installed web apps jump to theirs. Right-click offers **New window**, **Open file location** and **Unpin from taskbar**. Pinning itself stays on the taskbar, because Windows gives other programs no way to pin.
- The top line of the footer shows CPU, memory, disk and GPU use, each with a graph of the last minute. Click it to open Task Manager. Right-click it and choose **Detach stats into a window** to move it into a small always-on-top window, which you drag with the middle mouse button and click for Task Manager. The bar's own line hides while it is detached, and the window comes back where you left it after a restart. **Put stats back on the bar**, on the window's right-click menu, undoes it. The window sits above ordinary windows, borderless-windowed games and full-screen videos included. Another always-on-top window raised after it, the taskbar for one, can sit in front of it, and no window can show over a game in exclusive full-screen mode.
- While something is playing, a now playing line above the pins shows the player's icon, the title, the artist and the app, with previous, play/pause and next. Clicking the title brings the player forward.
- The strip along the bottom has a tray chevron that opens the hidden icons in one click, quick settings (drawn as wifi, plus a battery on a laptop), a volume dial, the notification bell, the clock (time over date, in your regional format, with the long date as a tooltip) and a show desktop sliver at the very edge. The buttons send Windows' own shortcuts, so each opens the real flyout.
- The volume dial is a 270° arc. Hover and it grows a knob and shows the percentage. The mouse wheel moves it 2% a notch and unmutes. Click it to list your playback devices with the default ticked, and **Mute** at the end. Choosing a device makes it Windows' default.

Tray icons are not copied onto the bar, because apps hand them only to Explorer.

An auto-hidden taskbar comes up, and stays up, whenever an app flashes its button, until you click that app. So while your taskbar is on auto-hide, Deskbar turns off Windows' "Show flashing on taskbar apps" for as long as it runs, and puts it back to what it was when you exit. The amber dot on the bar still shows which app wants you.

## Settings

**Dock the bar to the edge of its screen** is in The bar section of Manage's Settings tab.

**Stop apps flashing on Windows' taskbar while Deskbar runs**, in the Windows on the desktop section, shows the flashing switch described above. It starts ticked on an auto-hidden taskbar and unticked on one that is always shown. Once you tick or untick it yourself, your choice holds whatever auto-hide does.

Staying on top of full-screen windows, or stepping aside, is chosen on the bar's right-click menu.

## From a script

```powershell
$ts = "$env:APPDATA\Deskbar\deskbar.cmd"
& $ts bar across                          # dock across both screens
& $ts full-screen top                     # stay on top of full-screen windows
& $ts stats detach                        # or attach
```

`bar` also takes `minimize`, `restore`, `dock`, `undock` and `edge`.

## Related

- [The bar](the-bar.md) covers the rows and the bar's own settings.
- [Strips and pinned workspaces](strips.md) covers the strip along the top of each screen.
- [Tasks](tasks.md) covers the task list in the bottom strip.
- The README on [minimising the bar](../../README.md#minimising-the-bar).
