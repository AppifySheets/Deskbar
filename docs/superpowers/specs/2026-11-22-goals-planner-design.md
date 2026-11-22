# Goals: a weekly and monthly planner the tasks count toward

Date: 2026-11-22. Issue: [Deskbar #417](https://github.com/AppifySheets/Deskbar/issues/417). Builds on
`2026-11-22-tasks-board-design.md` (the one list of tasks) and on its later rulings: the important and urgent flags,
the drag order, and the List/Matrix view named after Covey's quadrants (ledger rulings 25 and 28 in
`.superpowers/sdd/2026-11-22-tasks-board/progress.md`). Read that spec first; this one adds a layer above it.

## Why

The tasks list covers the next few hours or days well. Nothing in it says what the week or the month is for. Petre
asked for "monthly and weekly planner" along the lines of Stephen Covey's *The 7 Habits of Highly Effective People*,
and for "daily tasks contribute to weekly and monthly". Covey's weekly planning: once a week, decide the few things
that matter most (his "big rocks", quadrant II, important but not urgent), then let the daily work serve them. A goal
works like an epic: it groups tasks and outlasts any one of them.

## Goals

- Weekly and monthly goals. A weekly goal can belong to a monthly goal.
- A task can belong to a goal, and its progress counts toward that goal and up the chain to the month.
- A planner window of its own, for setting the week's and the month's goals and for the weekly review.
- A quiet prompt at the start of a week and of a month: one line on the bar's list, "Plan this week".
- Pipe commands for all of it, as for tasks.

## Non-goals

- Flags on goals. Petre (2026-11-22): goals are "all important". They are quadrant II by definition.
- A workspace on a goal. Petre: "tasks relate to workspaces, i don't think goals need that". A goal's tasks keep
  their own workspaces, so a goal can span several without the goal saying so.
- Covey's roles (a weekly goal per role in life). Left out; a goal is just a title. Can come later if the list gets
  long.
- Scheduling goals into days or hours, a calendar, recurring goals, reminders by sound or toast.
- Goals for Claude sessions now. They come with the Claude-session work (PR B of the tasks board), below.
- The phone sync itself. Goals are built to go with it (Petre: a later sync includes goals), see Constraints.

## Anti-goals

- A chore. The weekly review must take a minute: carry over, done or drop per goal with one click each, and the new
  goals typed in one box. If planning takes effort, Petre stops doing it and the feature is dead weight.
- A busier bar. The bar stays a compact list of tasks: no goal chip on its lines, no progress bars. The only thing the
  bar gains is the "Plan this week" line, and only while it is due.
- Nagging. The prompt is one line on the list, dismissable, and it does not come back for that week once dismissed.

## Constraints

- Everything in the tasks spec's Constraints holds: the pipe is the only way in for other programs, every action has a
  twin in `ControlUsage`, no new global hotkey, no sound, no Windows toast.
- Sync-ready, like tasks: every goal has a Guid made on the device, an `UpdatedAt`, and UTC times. The period a goal
  belongs to is a calendar date in Petre's local time (the Monday of its week, or the first of its month), stored as a
  date, not an instant, because "this week" is his week wherever the clock says UTC midnight falls.
- A week runs Monday to Sunday. A month is the calendar month.
- Code style per CLAUDE.md. Release notes in the pull request, no version edits.

## What Petre decided

### The goal

A goal has a title, a period (one week or one month), a status (open, done, dropped) and an order within its period.
A weekly goal may name a monthly goal as its parent; it may only name a goal of a month its week touches. No flags, no
workspace.

### Tasks contribute

A task may belong to one goal, weekly or monthly, or to none, as today. Progress counts up the chain:

- A weekly goal shows "3 of 5 done": its tasks, counting Done and Closed as done.
- A monthly goal shows its weekly goals ("2 of 4 weeks' goals done") and the tasks attached to it directly.
- Marking a goal done is Petre's call. Finishing every task does not close a goal by itself, and an open task does not
  stop him marking it done.

Attaching a task to a goal does not change its flags. The flags stay Petre's (and Claude's for its own tasks).

### The planner window

A window of its own (Petre: the weekly review happens "of its own", not in the tasks window), a regular window like
the tasks window: it opens where he left it and comes to the desktop he is on before it activates.

- This month's goals at the top, each with its progress. This week's goals below them, each with its tasks under it.
- Arrows step to the previous or next week; the month follows the week shown.
- One text box adds a goal to the week shown (a chip beside it switches it to the month, as the bar's add box does
  with workspaces). A weekly goal's menu sets its monthly parent.
- A column of open tasks that belong to no goal, newest first, to drag onto a goal. Dragging a task off a goal back to
  that column detaches it.
- Done and dropped goals stay in their period, struck through and dim, so a past week reads as what happened.

### The weekly and monthly review

On the first day the app runs in a new week, the bar's list shows one line at its top, "Plan this week", with a close
sign. Clicking it opens the planner on the review; the close sign dismisses it for that week. The same happens on a new
month ("Plan this month"; when both are due, one line, "Plan this month").

The review is a strip at the top of the planner listing the previous period's goals that are still open. Each gets
three one-click actions: carry over (the goal moves to the new period, its tasks with it), done, drop. Once the strip is
empty the planner is the normal view. Opening the planner from anywhere also counts as having seen the prompt.

### Linking a task to a goal elsewhere

- The tasks window: a task's More menu gains "Goal", listing this week's and this month's open goals and "None".
- The bar: no chip, no new control. A task's tooltip names its goal.
- The tasks window's List and Matrix views can filter by goal.

### Pipe commands

`goal add`, `goal list` (JSON), `goal done`, `goal drop`, `goal reopen`, `goal rename`, `goal delete`, `goal carry`
(move to another period), `goal parent`, and `task goal <id> <goal-id>|none`, plus `planner-window show|hide`. Exact
argument names are the builder's, within the `task` commands' conventions (ids by prefix, `--week`/`--month` for a
period, defaulting to the current one).

### Claude sessions

When the Claude-session work lands, a session's start context lists this week's open goals by title and id, and the
skill tells it it may attach its own task to one with `task goal`. It never adds, drops or marks goals done; those are
Petre's.

## Calls made in writing this up (Claude's, for Petre to check)

- Goals live in a file of their own, `goals.json`, beside `tasks.json`, written the same durable way. The earlier short
  design said `tasks.json`. The reason for the change: a build from before goals reads a newer `tasks.json` as the
  version it knows and drops what it does not understand when it saves. If the updater ever puts an older build back
  (it has replaced branch builds before), goals inside `tasks.json` would be lost on its first save; in their own file
  the older build never touches them. The task's link to its goal still lives on the task in `tasks.json`.
- Delete exists for a goal (pipe and menu) for mistakes; it detaches the goal's tasks and its weekly children. Drop is
  the normal end of a goal that did not happen.
- A goal carried over keeps its id, so its tasks stay attached and its history is one goal, not a chain of copies.
- The review is due on the first day the app runs in the new period, judged by the last period Petre reviewed or
  dismissed, stored with the goals. A machine off all of Monday still gets the prompt on Tuesday.

## Left to the builder

- The goal store's shape, behind an interface like `ITaskStore`, and how goals reach the tasks window and the bar.
- The planner's layout within the rules above, the bar's gutter and lane rules for the "Plan this week" line, and DPI.
- Drag in the planner under the bar's drag rules (the #48 click lessons, no rebuild under an open menu).
- Splitting the work into pull requests. A suggestion: first the goals model, store, pipe and the task link (the More
  menu and the tooltip); then the planner window and the review line. Each merge releases, so each must leave a
  working app.

## Order

After the flags, drag order and Matrix view are in. Before or after the Claude-session work, whichever is ready
first; the goals part of the session context is added by whichever lands second.
