---
title: Where windows go
summary: Which desktop a window opens on, what Deskbar remembers about it, and the apps it leaves alone.
order: 10
---

Deskbar never launches anything, and it moves or resizes a window on screen only when you ask. The one thing it arranges on its own is which desktop a window belongs to, and that it remembers.

## How to use it

An app opens where you open it, unless you have told Deskbar it lives somewhere. Right-click one of its windows on the bar and tick **Remember *app*'s workspace**. From then on its windows open in the workspace that window is in, and they follow it when you move one. This suits apps that have a home, such as a messenger or a recording tool. Everything else stays where it opened, so the Remote Desktop dialog, or a tool you open once in a while, never jumps to a workspace you dragged it to weeks ago.

Identity is the executable plus its arguments, so `rider64 A.sln` and `rider64 B.sln` are different things, and an installed web app is a different thing from the browser it runs in.

Dragging teaches a remembered app. Move a window of an app you ticked and that is where the app goes next time. A window that merely opened somewhere teaches nothing until you move it once, because where a new window opens is just the desktop you happened to be on. Pinning a window to every desktop is remembered for every app, without the tick, since a pin never takes a window away from where you are.

Some apps stay where they were opened and are never learned from: browsers, Windows Terminal and console windows, Open and Save dialogs, the Windows Security sign-in and Task Manager. A browser window says nothing about the work. Explorer is never remembered either, so `Win+R` opens on the current desktop. Installed web apps, the launched-by rule and hand-written rules still apply to them.

Folders get their own memory. Several windows of one editor are a single app as far as the roster is concerned, so where a window's folder lives is remembered separately: open that folder again, anywhere, and the window goes to its own workspace. Deskbar learns this from where your windows sit once a position has held still, including moves made with Task View or `Win+Ctrl+←/→`, and from anything you drag. Deleting a workspace forgets the folders that lived in it.

A window opened by another app joins that app's workspace. Click a link in your editor and the browser window it opens lands beside the editor rather than wherever you are standing. Deskbar follows the process chain to whoever really started it, and where that app has windows in several workspaces, the one you were last in wins. Explorer and the shell never count as the launcher, so things started from the taskbar are unaffected. This is an inference about one window: it moves the window and teaches the roster nothing.

A window a background Claude session opens goes to its task's workspace. A session started with `claude --bg`, as "Start Claude on this" and the resume of a task's session do, has no window of its own anywhere up its process chain, so there is no app for the window to join. Deskbar recognises the session from the chain instead, looks up the task it works on, and moves the window to that task's workspace with the attention dot, because it arrives out of sight. A free task, a session on no task, or a task on the workspace in front leaves the window where it opened. Like the rule above, it only covers a program the session has just started: a browser that was already running opens its window from its own process, so for that a session uses `deskbar.cmd open` or `start`.

When several windows of one app are already open, a new one is left where you opened it. Four browser windows on one profile are one identity, so "put it back" has no single answer.

A few more cases:

- Remote Desktop Manager session windows go to their own workspace.
- Tabbed apps get one placement check per window, so a dashboard does not jump between workspaces.
- An automated browser, one a tool started with its own `--user-data-dir`, is placed by the launched-by rule, so it lands beside the tool that started it.
- Rules are no longer written by hand. Placement rules written by an earlier version still apply, but nothing creates new ones.

## Settings

The only switch is per app: **Remember *app*'s workspace** on a window icon's menu decides whether that app goes back to its workspace when it opens. Manage does not list these. Placement memory is kept in `%APPDATA%\Deskbar\state.json`.

## From a script

Three commands put windows in a workspace on purpose:

- `move <ws> (--title <text> | --hwnd <h>)` moves windows that are already open.
- `start <ws> -- <program> [arguments...]` starts a program and puts its window in that workspace as it opens.
- `open <link>` opens a link in the workspace of the VS Code window it was called from.

See [Driving it from scripts](scripts.md) for the details.

## With Claude sessions

A link you click in a session's editor opens its browser window beside the editor, through the launched-by rule. Folder memory covers the editor itself: open a session's folder in VS Code again, anywhere, and the window goes back to the workspace that folder lived in. Deskbar's skill also tells every Claude Code session to open links with `open`, start programs with `start` and move the windows it opens with `move --no-follow`, so they land in the session's workspace. See [Claude Code sessions](claude-sessions.md).

## Related

- [Tiling](tiling.md) is the way to have Deskbar lay windows out on screen.
- [Naming windows](naming-windows.md) covers the title-pattern renames that replaced hand-written rules.
- The README on [how identity works](../../README.md#how-it-works).
