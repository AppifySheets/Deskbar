---
title: Naming windows
summary: Give a window a short name so the taskbar shows RDP instead of a long title, or name editor windows after the folder they hold.
order: 11
---

Give a window a short name, so the taskbar shows `RDP` instead of `Remote Desktop Manager [server-01 - fhd]`.

## How to use it

Right-click a window's icon on the bar. The menu offers five ways to name it:

- Rename this window… keys the name to the exact title the window had at the time. It lapses the moment the app rewrites its own title, so for browsers, chat apps and editors use the next one.
- Rename all *app* windows… keys the name to the process, and never lapses.
- Rename by title pattern… matches with `*`: `beeper *` names every Beeper window `beeper`.
- Name *app* windows by folder is for apps whose titles say what they hold. One name for every window of an editor is the wrong shape, since seven VS Code windows all reading `VSC` tell you nothing. This names each window after the folder, project or session it currently holds, and follows the window when you open something else. It is offered for VS Code and its forks, Visual Studio, the JetBrains IDEs and Remote Desktop Manager. Browsers are left out, because a tab title is the page rather than a container.
- Restore title takes a name back. On a window named by a title pattern it removes the pattern too, so every window the pattern named gets its title back.

A titleless window that still wants a taskbar button is shown under its process name rather than "(untitled)".

## Settings

Names persist across restarts. They are kept in `%APPDATA%\Deskbar\state.json`. Manage does not list them: the title-pattern renames are made with **Rename by title pattern…** and removed with **Restore title**, both on the icon's menu.

## From a script

The commands match the menu items: `rename-window`, `rename-app`, `rename-pattern`, `name-by-folder <hwnd> on|off` and `restore-title`. `move --title <text>` also searches the title a window had before Deskbar shortened it.

## With Claude sessions

A Claude Code session open in VS Code is a tab of a VS Code window. **Name *app* windows by folder** on a VS Code icon names each of those windows after the folder, project or session it holds, so the taskbar tells them apart. See [Claude Code sessions](claude-sessions.md).

## Related

- [Where windows go](where-windows-go.md) covers which workspace a window belongs to.
- [Driving it from scripts](scripts.md) lists every command.
