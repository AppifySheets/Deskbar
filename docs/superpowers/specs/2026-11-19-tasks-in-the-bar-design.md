# Tasks in the bar: design

Date: 2026-11-19. Status: for Petre's approval. Mockups: layout A of the brainstorm page (`.superpowers/brainstorm/`,
gitignored).

## Intent

Petre runs many threads of work at once, most of them a workspace each: a repo, its VS Code window, a Claude
session, a browser. He wants Deskbar to support attention-focus multitasking: give one thread his attention,
park the others with "come back to this in 10 minutes", and have the bar hand each parked thread back when its
time is up. Alongside the timers he wants a plain list of tasks, some tied to a workspace and some not ("call the
accountant").

In his words: "i want the ability to mark windows as active work so that they're identified in the bar", "when i
have a window active, i want to have a timer in the bar which i will set to one of the predefined values", "each
timer should create a task management item that i can change", "timer should reset when i come back to the
window. if i still want to set a timer, i should do so explicitly", "essentially, i want to build attention-focus
multitasking", "i should also have a list of tasks, that may or may not include windows", and finally that a
workspace has the task, not individual windows.

## Goals

- Start a task with a timer of 1, 5, 10 or 30 minutes in two clicks from the bar, without leaving what he is doing.
- See at a glance which workspaces are parked and how long until each comes back.
- Be pulled back when a timer runs out: the row blinks, and a toast appears if the bar is out of sight.
- Keep one list that answers "what needs me next": due tasks, Claude sessions asking for him, running timers,
  then open tasks.
- Edit tasks: rename, set or clear a timer, change or remove the workspace, mark done, delete.
- Survive restarts and reboots.

## Non-goals

- Time tracking. No totals per task or per day are shown. The records keep start and end times so totals stay
  cheap to add later.
- Tasks tied to individual windows. A task points at a workspace or at nothing. (Window targets were considered
  and dropped: windows lose identity across reboots, and Petre's workspaces already are his topics.)
- Taskbar flashes in the list. Teams flashes for every message and would bury everything else; the bar already
  keeps the full blink for sessions for this reason (`FloatingBar.Asking.cs`).
- Renaming "workspace". Petre would like a shorter word ("desk" was proposed); that is a separate, later job.
  This feature says "workspace" in code and UI.

## Anti-goals

- Anything that restarts a timer on its own. Once a timer ends, a new one starts only when Petre sets it.
- A third global hotkey. The project rule stands: two hotkeys, each earned (CLAUDE.md).
- A second way of grouping windows next to workspaces.
- A surface that steals focus from the window he is working in when he only glances at the bar.

## The model

A task:

| Field       | Meaning |
|-------------|---------|
| Id          | Guid |
| Title       | free text, editable |
| WorkspaceId | `Maybe<Guid>`: the workspace it belongs to, or none |
| Created     | when it was made |
| Timer       | `Maybe<(DateTimeOffset Started, TimeSpan Length)>`: the running or expired timer, if any |
| DoneAt      | `Maybe<DateTimeOffset>` |
| History     | the timers it has had, each as start, length and how it ended (came back, done, cancelled), for later totals |

One live task per workspace (Petre agreed, 2026-11-19). A workspace has at most one task that is not done. A
minutes button pressed for a workspace that already has an Open, Running or Due task puts the new timer on that
task instead of creating another, and the title box starts with that task's title rather than the window's. So
parking the same workspace ten times a day leaves one row, not ten. Free tasks (no workspace) are not limited.
Changing a task's workspace to one that already has a live task is refused with a short reason in the window.

States, derived from the fields and the clock, never stored:

1. Running: a timer whose end is in the future.
2. Due: a timer whose end has passed and that has not been answered.
3. Open: no timer, not done.
4. Done: `DoneAt` set.

A Claude session asking for Petre (`WorkspaceManager.RequestAttention`, the live `askingWorkspaces` set) appears in
the list as a Due row for its workspace ("Claude in Store asks for you"). It is not a stored task: it is drawn
from the live set, leaves when the set loses it, and is gone after a restart, exactly as today. Its row has no
"..." menu of task actions: a click goes there, and a small dismiss button clears the ask without going there,
through a new `WorkspaceManager.ClearAttention(workspaceId)` (my call). `task list` includes asks, each with
`"kind": "ask"` and no id.

A timer that ran out while Deskbar was not running (overnight, a reboot) is Due on the next start, all at once.
That is honest about what was missed, and the list sorts them oldest first (my call).

## Coming back ends the timer

A timer on a workspace task ends, Running or Due alike, when Petre arrives on that workspace and stays there for
two seconds. Arriving means a desktop switch into it (the same arrival `OnDesktopChanged` uses to clear an ask);
the two-second dwell keeps a pass through it (Alt+Tab, the switcher, a mis-click) from killing a 30 minute timer.
The task then becomes Open; the history records "came back". Every running or due timer on that workspace ends
together.

Being on the workspace already is not coming back. A timer set while on the workspace (the default) keeps
running while he stays there. If it runs out while he is still there, it turns Due and stays Due until he
leaves and comes back, or answers it in the list (my call: a reminder he set on purpose should not be swallowed
because he happened to be in the right place).

A task with no workspace has nothing to come back to. When its timer runs out it is Due until he answers it in
the list: marks it done, sets a new timer, or clears the timer.

## The bar

The stopwatch button sits in the strip (`FloatingBar.Shell.cs`, `BuildShellStrip`), just left of the language
code. It cannot be a clock glyph: the strip already shows Windows' clock.

- At rest: the glyph only.
- Running: the glyph, a swatch of the soonest timer's workspace colour, and its time left (`4:32`).
- Due: orange, pulsing with the shared `AskingPulse` brush, the word "due".

When the strip runs out of room (`FitClock`), the stopwatch's countdown text goes right after the date, leaving
the glyph and swatch; the glyph itself is never dropped before the clock, because it is the only way into the
feature. The language code keeps its first place. The stopwatch also gets an entry in `HiddenStripItems`, so it
can be hidden in Settings like the strip's other items (my call).

A workspace row with a running timer gets a small pie in its name gutter that empties as the time runs out, blue
because nothing wants him yet. With several running timers on one workspace the pie shows the soonest.

A workspace row with a due task blinks exactly as an asking row does today: the name and the colour rail flash
orange (`WantsAttention`). The overview gets the due workspaces stamped on the same way `Asking` stamps asks, so
the drawing code is shared.

### When the bar cannot be seen

The blink and the orange stopwatch are the only signals while the bar is on screen; no sound (Petre agreed). When
a timer comes due while the bar cannot be seen (minimized to its taskbar button, hidden, or stepped aside for a
full-screen window), Deskbar also shows one Windows toast notification: the task's title and its workspace, and a
click on it switches there. One toast per task coming due, never repeated. Deskbar has no notification code yet,
so this is new infrastructure and belongs to the first pull request. An unpackaged app needs an AppUserModelID
registered for its toasts to show (the direct copy's Start menu shortcut can carry it); the Store copy has one
from its package. The builder measures this on the real machine before relying on it.

## The task window (layout A, picked from the mockups)

A click on the stopwatch opens a small window under it. It works like Quick find's window
(`QuickFindWindow.xaml.cs`): its own top-level window, takes the keyboard, closes on Escape or a click elsewhere,
only hides on close so it can be shown again. Display only: a controller decides what is listed and what each
action does, so the tests can drive it.

```
 +------------------------------------------------------+
 | [ title___________________________________ ]         |
 | [#] Deskbar   other workspace...   none              |
 | [1 min] [5 min] [10 min] [30 min] [no timer]         |
 |------------------------------------------------------|
 | DUE                                                  |
 |  #  Claude in Store asks for you          now   ...  |
 |  #  Answer the Store question  Store  3 min ago ...  |
 | RUNNING                                              |
 |  #  Wait for CI  Deskbar                 4:32   ...  |
 | OPEN                                                 |
 |     Call the accountant                         ...  |
 | > Done today (3)                                     |
 +------------------------------------------------------+
```

- The title box has focus on open. Its default is the title of the window that was in front before the bar was
  clicked (the bar keeps it, as the language switcher's `NoteForeground` does), selected so typing replaces it.
- The workspace chips default to the workspace that window is on. "other workspace..." lists the named
  workspaces; "none" makes a free task.
- A minutes button or "no timer" creates the task and closes the window. Enter with a title and no button is "no
  timer".
- The list sorts by what needs him next: Due (oldest first, asks before timers when tied), Running (soonest
  first), Open (newest first), then "Done today", collapsed.
- Each row: workspace swatch, title, the workspace name in grey, time left or how long ago it came due, and a
  "..." menu with Rename, Set timer (1, 5, 10, 30), Clear timer, Change workspace, Mark done, Delete. A click on
  the row switches to its workspace. A double click on the title edits it in place.
- The time column counts down live, redrawing only what changed (Quick find's rule, so a scrolled list stays put).

Placement: under the stopwatch, on the stopwatch's screen, in that screen's DPI, kept inside the work area.

## Persistence

Tasks live in `tasks.json` beside `state.json` in `AppPaths.NewStateDir`, written the way `time.json` is
(atomic write with a `.previous` copy), on every change. A separate file because tasks change all day and
`state.json` should not be rewritten for each timer. A task whose workspace was deleted keeps its title and
becomes a free task. Done tasks stay in the file (the history is what later totals would read); the list shows
only today's.

## Remote control

Every bar action gets a command, listed in `ControlUsage` under a new "Tasks" section, so the test that every
dispatched command is listed covers them:

```
task list                                   every task as JSON, with its state and time left
task add <title> [--workspace <name>] [--in 1m|5m|10m|30m]
task timer <id> (--in <minutes> | --clear)
task done <id>
task rename <id> <title>
task delete <id>
```

Without `--workspace`, `task add` puts the task on the caller's workspace, found from the caller's folder the way
`attention` finds it (`RemoteControl.Open.cs`, `--from <folder>`); `--workspace none` makes a free task (my call,
because the main use is a Claude session parking its own workspace).

`--in` accepts the four preset values only, to keep the bar and the command line telling the same story. A
Claude session can then park its own workspace for Petre ("check back in 10 minutes when CI is done").

## Approaches considered

1. A small task window that drops down under the stopwatch (picked). Quick find already solved focus, keyboard
   and click-away dismissal; rows can count down live and be edited in place.
2. A right-click menu on the stopwatch, with renames through `PromptDialog`. Cheapest and matches the bar's other
   menus, but a menu cannot count down live, every edit is a dialog, and ten tasks in a menu is clumsy.
3. A Tasks tab in Manage. Room to edit, but Manage is a settings window opened on purpose, which breaks the
   two-click flow.

## Constraints

- Core logic (states, sorting, coming back, persistence model) lives in `Deskbar.Core` with tests; the window and
  the strip button in `Deskbar.App`. The clock is injected so tests can move time.
- Static WPF brushes frozen; the pulse reuses the bar's shared animated brushes.
- The bar's rebuild is re-entrancy guarded; the per-second countdown must not trigger full rebuilds. Only the
  stopwatch label and the pies update on the tick, through the existing 1 s timer.
- Nothing gated to Pro: the Store build gates nothing yet.

## Left to the builder

- The exact glyph for the stopwatch (Segoe Fluent Icons has one) and the pie's size inside the 58 DIP gutter.
- Whether a docked-across-screens bar shows the stopwatch on both halves, following what the strip already does
  for its other buttons.
- How a nested workspace in a group counts for "coming back" (arrival on its own desktop is the natural answer).
- Delivery as two pull requests: first the model, persistence, timers, the bar's stopwatch, pies and blink, and the
  `task` commands; then the task window. Each with a `## Release notes` section.
