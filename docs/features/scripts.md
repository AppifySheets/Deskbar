---
title: Driving it from scripts
summary: Every bar menu command has a command-line twin, through the deskbar.cmd shim that hands it to the running copy.
order: 12
clip: session-start
---

While Deskbar runs it keeps a small batch file at `%APPDATA%\Deskbar\deskbar.cmd` that hands a command to the running copy, so a script, a shortcut or a coding agent can do anything the bar's menus do.

## How to use it

A coding agent that has just set up a git worktree can give it a workspace of its own and move the editor there:

```powershell
& "$env:APPDATA\Deskbar\deskbar.cmd" move wt-login --create --group EC --title wt-login --wait 20
```

Everything the bar's menus do has a command, and each one runs the same code as its menu item. `help` prints the list from the running copy, so it always matches the version you have:

| Group | Commands |
|---|---|
| Listing (JSON) | `workspaces`, `windows`, `desktops` |
| Workspaces | `create <name> [placement] [--folder <path>]`, `rename`, `reorder <ws> up\|down\|top\|end\|<n>`, `fold`, `unfold`, `color <ws> <colour>`, `delete <ws> [--close-windows]`, `switch` |
| Groups | `group-create`, `group-join`, `group-leave`, `group-rename`, `group-color`, `group-fold`, `group-unfold`, `group-reorder <group> up\|down\|top\|end\|<n>`, `ungroup` |
| Desktops | `name-desktop <id or number> <name>` |
| Starting | `start <ws> [--create [placement]] [--cwd <folder>] -- <program> [arguments...]`, `open <link> [--from <folder>]` |
| Windows | `move <ws> [--create [placement]] [--no-follow] (--title <text> \| --hwnd <h>)...`, `rename-window`, `rename-app`, `rename-pattern`, `name-by-folder <hwnd> on\|off`, `restore-title`, `pin`, `unpin` |
| Bookmarks | `bookmark list [<workspace>]`, `bookmark add <workspace> --url <url> \| --program <path> [--cwd <folder>] [-- <args>] \| --app <id> [--name <name>]`, `bookmark rename <workspace> <name\|id> --name <name>`, `bookmark remove <workspace> <name\|id>`, `bookmark open <workspace> <name\|id>`, `bookmark open-all <workspace>` |
| Bar | `bar minimize\|restore\|dock\|undock\|across\|edge`, `arrange on\|off`, `stats detach\|attach`, `see-through on\|off` |
| Tasks | `task list [--closed]`, `task overview`, `task show <id>`, `task manager`, `task add <title>\|- [--workspace <name>\|none] [--important] [--urgent] [--parent <id>] [--session <id>]`, `task close [--stop-claude\|--anyway]`, `task reopen`, `task status`, `task rename <id> <title>\|-`, `task move`, `task comment`, `task delete`, `task flag <id> important\|urgent on\|off`, `task order <id> top\|end\|<n>`, `task comment-settle <comment-id> --session <id>`, `task assign <id> me\|claude`, `task describe <id> <markdown>\|-`, `task comment-edit <comment-id> <markdown>\|-`, `task goal <id> <goal-id>\|none`, `task go <id>`, `task open <id>`, `task link <id>`, `task start <id> [--folder <path>]`, `task open-session <id> <session>`, `tasks-window show\|hide\|list\|matrix`; a Claude session's `task join`, `task new`, `task status-line`, `task ask`, `task answered`, `task comment`, `task done` and `task later`, each with `--session <id>`; the manager session's own `task ask -` and `task answered`, without it |
| Goals | `goal list [--week [<date>] \| --month [<date>] \| --all]`, `goal add <title>\|- [--week [<date>] \| --month [<date>]]`, `goal done`, `goal drop`, `goal reopen`, `goal rename <id> <title>\|-`, `goal delete`, `goal carry <id> [--week [<date>] \| --month [<date>]]`, `goal parent <id> <monthly-id>\|none`, `goal prompt [dismiss]` |

A few more, with `$ts` standing for the batch file:

```powershell
$ts = "$env:APPDATA\Deskbar\deskbar.cmd"
& $ts workspaces                          # JSON: name, group, row, colour, minimized, current
& $ts create Review --after Services      # beside Services, inside its group
& $ts reorder Review end                  # to the bottom of its group
& $ts group-leave Review                  # out of the group
& $ts color Review teal                   # a palette name, #RRGGBB, none or default
& $ts move Review --hwnd 0x51107A --no-follow
& $ts bar across                          # dock across both screens
& $ts delete Review                       # refused while it still has windows
```

A new workspace goes at the end of the bar unless you say where. `--group <group>` puts it at the bottom of that group, `--position <n>` at row n counting a whole group as one row, and `--before` or `--after <workspace>` work like the menu's Insert items. `create` is not an error when the workspace already exists.

`move <workspace>` picks windows with `--title <text>`, which also searches the title a window had before Deskbar shortened it, or with `--hwnd <handle>` taken from `windows`. A move is all or nothing: when any selector finds no window, nothing moves and the exit code is 3. `--wait <s>` keeps retrying that case, which covers an editor that is still opening. Moving the window you are in takes you along, as a drag on the bar does. Add `--no-follow` to stay put, which you want for a window a script has just opened, because that window has focus.

`start <workspace> -- <program> [arguments...]` starts a program and puts its window in that workspace as it opens, without taking you along. Windows opens every new window on the desktop in front, so this saves you a move afterwards, but it is still a move: Deskbar claims the window the moment it appears, which on a measured run was half a second after the command, with the window on your current workspace for part of that. It claims every window of the started process and the processes it starts for 30 seconds, plus the first new window with the same exe name, which covers a browser or editor that hands the request to a copy already running. Everything after `--` is the program's own command line. `--cwd <folder>` sets the folder it starts in, since the running app cannot see yours. Deskbar never starts anything on its own.

```powershell
& $ts start wt-login --create --group EC --cwd C:\src\login -- code .
& $ts start Research -- chrome --new-window https://example.com
```

`open <link>` is for a session or a terminal inside VS Code. It finds the VS Code window whose folder holds the caller's folder. When that window is in a workspace you are not looking at, the link gets a new window of your default browser there, and you stay where you are. Otherwise it opens as usual.

`delete` closes nothing unless you add `--close-windows`. The menu asks before closing, and a command cannot ask, so you have to say it. Manage, Check for updates and Exit deliberately have no command.

Exit codes: 0 done, 1 refused, 2 Deskbar is not running, 3 no window matched.

## Settings

There is nothing to set up. Deskbar rewrites `deskbar.cmd` at each start.

The batch file exists because PowerShell does not wait for a windowed program it calls directly, so it would get no output and no exit code. From Git Bash or cmd you can also call the exe itself with `ctl <command>`. The client passes arguments through verbatim, so it does not have to match the app's version.

The Microsoft Store copy also puts `deskbar` on your PATH, so `deskbar windows` and `deskbar help` work from PowerShell, cmd and Git Bash. Its `deskbar.cmd` is still written and points at that command, so scripts that call it keep working. `deskbar version` prints the running build's version and whether it is the packaged Store copy.

## With Claude sessions

Deskbar's Claude Code skill teaches every session on the machine to open links with `open`, start programs with `start` and move the windows it opens with `move --no-follow`, so they land in the session's own workspace. Its hooks run `%APPDATA%\Deskbar\claude-hook.cmd` when a session finishes a reply or waits for a permission, and `claude-task-hook.cmd` when a session starts, gets a message or ends; both hand the hook's details to `deskbar.cmd claude-hook`, which is for the hooks rather than for scripts. `claude-skill off`, `claude-tasks on|off` and `claude-resume on|off` set the three settings. `task go <id>` does what a double-click on a task does (its workspace, then its session's window), `task start <id> [--folder <path>]` starts a background session on the task and opens a window on it, refusing and naming the folders when several could be meant, and `task open-session <id> <session>` opens one of the task's sessions by its id or short id. See [Claude Code sessions](claude-sessions.md).

## Related

- [Tasks](tasks.md) has the `task` commands with examples.
- [Goals](goals.md) has the `goal` commands with examples.
- [Where windows go](where-windows-go.md) covers what happens to windows nobody moved.
- [Arranging the bar](arranging.md) covers `arrange on` and `arrange off`.
