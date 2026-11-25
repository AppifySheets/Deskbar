---
title: Manage
summary: Manage holds every setting, the shortcuts, the time spent per workspace and Deskbar Pro.
order: 13
---

Manage is Deskbar's settings window. Left-click the tray icon to open it.

## How to use it

Manage has four tabs and opens on the first:

- Settings holds every setting, in three sections, and each applies as you change it. The version you are running is at the bottom right.
- Shortcuts rebinds the switcher's chord and quick find's.
- Time reports how long you have spent in each workspace today, this week (since Monday) and over the last thirty days, by group and by workspace, sortable. Only input counts, keyboard or mouse. Two minutes without either is idle, so a workspace left on screen overnight accrues nothing, and a machine that slept is not credited for the gap. Time is counted in fifteen-second slices.
- Deskbar Pro says whether this copy has Deskbar Pro. Without it, the tab has **Get Pro** with the price in your currency (in the GitHub copy, **Open Deskbar in the Store**) and **Check again**, which asks the Store at once, for Pro bought on another PC. With or without Pro, **Report a problem…** is there too, because priority support is what Pro adds.

Workspaces and rules are not managed here. The bar adds, renames, deletes and reorders workspaces. A window icon's menu has **Remember *app*'s workspace**, which decides whether that app goes back to its workspace when it opens, and the title-pattern renames, made with **Rename by title pattern…** and removed with **Restore title**.

## Settings

The Settings tab has three sections, and each change applies at once.

The Deskbar on this PC section has the boxes **Start Deskbar with Windows**, **Check GitHub for new versions** and **Teach Claude Code about Deskbar**. The GitHub copy's autostart records the path of the exe you ran it from, so pick a permanent home for the exe before you tick it. The Microsoft Store copy starts through a startup task instead, and its update box checks the Store rather than GitHub.

The section for the bar has **Dock the bar to the edge of its screen**, the see-through band behind the workspace rows (on unless you untick it; it shows your wallpaper blurred, docked or floating, and keeps 85% of the bar's colour where there is no single picture to show), what the bottom strip shows, and the sliders:

- how faint the bar goes once the pointer is elsewhere;
- how long it waits before dimming, and how long the dimming takes;
- how long the pointer rests on an icon before its card appears, which follows Windows' own setting unless you untick its box;
- the text size and the icon size;
- the fewest lines the window list keeps;
- how blurred your wallpaper is behind the rows while the bar is docked (**Wallpaper blur**, 8 screen pixels unless you move it).

You choose a slider's value by watching the bar while you drag. **Reset the bar's look to defaults** puts the sliders and the bottom strip back.

The Windows on the desktop section has the outline around the window you are in, which also outlines the window you were in before it in dashes and the one before that in dots, the way the bar rings the workspaces you came from. It also has **Stop apps flashing on Windows' taskbar while Deskbar runs**, and the gap between tiled windows, which takes effect when you press Apply.

**Show the previous workspace on the strips and the taskbar** is in Manage too.

## From a script

Some settings have a command: `see-through on|off`, `previous-workspace on|off`, `claude-skill off`, `claude-tasks on|off` and `claude-resume on|off`. Manage itself deliberately has no command.

## With Claude sessions

**Teach Claude Code about Deskbar** installs the skill and hooks that make the bar flash when a session needs you and send a session's links and windows to its own workspace. **Claude Code sessions keep their tasks in the task list**, under it, adds the hooks that tie each session to a task, and **Resume the sessions of open tasks after a restart or a crash**, under that, wakes the session of an open task that is no longer running. See [Claude Code sessions](claude-sessions.md).

## Related

- [The bar](the-bar.md) and [Docking and taskbar pieces](docking.md) explain the bar's settings.
- [Switching workspaces](switching.md) and [Quick find](quick-find.md) explain the shortcuts.
- [Updates](updates.md) explains the update check.
- The README on [installing and starting with Windows](../../README.md#install) and [Deskbar Pro](../../README.md#deskbar-pro).
