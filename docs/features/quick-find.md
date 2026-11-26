---
title: Quick find
summary: Win+Alt+Space searches every window, browser tab and VS Code editor tab on every workspace and takes you there.
order: 4
clip: quick-find
---

Quick find opens a search box over everything open on every workspace, and Enter takes you to what you picked.

## How to use it

Press `Win+Alt+Space`, or click the **Search** button on the bar. The box searches window titles, every browser tab and every VS Code editor tab. Type a few letters of anything in the title, in any order: `inv 10` finds "Invoice 2026-10".

Keep typing while it searches: every letter shows at once, and the count beside the box reads `searching...` until the results catch up with what you typed. Enter always takes the top row for what is in the box.

Results sit under the workspace they are in, in its colour, with the workspaces you used most recently first. Enter takes you there and `Esc` closes the box.

A browser window is listed by its tabs, so you find the page, not the window that happens to show it. A tab is found by its own title or by its workspace, so `code` does not list every tab of every VS Code window.

A program that is already running is offered once more as a new window row, which opens another window of it on the desktop you are on, as a middle click on its taskbar button does. Type the program's name (`code`, `edge`) while it has several windows open and that row comes first, so Enter opens the new window. With one window open, Enter still goes to that window and the new window row sits below it.

## Settings

Change the chord on Manage's **Shortcuts** tab. It can never be the switcher's. These two chords are the only ones Deskbar registers. Windows' own search is still `Win+S`.

## From a script

`deskbar find inv 10` opens the box with the text already typed.

## With Claude sessions

Every open Claude Code session is a VS Code editor tab named after the session, so quick find lists it. Type a few letters of the session's name and press Enter to go to its window, on whatever workspace it is. See [Claude Code sessions](claude-sessions.md).

## Related

- [Switching workspaces](switching.md) moves by workspace instead of by name.
- [Manage](manage.md) has the Shortcuts tab.
