# Task stats: a burn-up in the tasks panel

Task 521d2b07, 2026-11-25. Petre: "build stats for tasks, like a burndown or similar, with links to each task".
Asked what the stats should tell him, he answered "okay" to the proposal of a burn-up in the tasks panel where clicking
a day lists that day's tasks, so this is that proposal, with the calls below made for him.

## Why a burn-up and not a burndown

A burndown draws a fixed amount of work shrinking to zero. Petre's list has no fixed amount: 138 tasks from 19 to 25
November, about 23 new ones a day. So the view shows the two things a burndown mixes together:

* the open tasks at the end of each day (the line a burndown would draw, which here goes down only on days more got
  finished than added), and
* the tasks added and the tasks finished each day, side by side.

## What it shows

A Stats switch on the tasks panel's title line, beside List and Matrix (always shown, the Matrix's width rule does not
apply). Stats is a view of its own, not a filter: while it shows, the search row and the filter chips are hidden and
the numbers count every task, closed ones included. It is not remembered across restarts; the panel opens on the List
or the Matrix as before.

Top to bottom:

1. Four numbers: open now, added in the last 7 days, finished in the last 7 days, and the median time from added to
   finished for the tasks finished in the last 14 days.
2. "Open at the end of the day": one line, today at the right. The charts start on the day the oldest task was added,
   and cover at least 7 days and at most 14 (TaskStats.DaysToShow): a fixed 14 days, rendered from Petre's own list,
   left half of each chart a flat line, since his tasks began on 19 November.
3. "Added and finished per day": two bars a day, blue for added and green for finished, with a legend.
4. The picked day's tasks, today unless a day was clicked: "Finished" and "Added", each line the task's status mark
   and title. Clicking a line is the link: the panel leaves Stats and shows that task, picked, scrolled to and opened
   to its thread (TasksBoard.ShowTask). A closed task switches the status filter to Closed first, or it would not be
   on the list.

Clicking a day's column in either chart picks that day. A day's column shows its numbers in a tooltip.

## Where the history comes from

Nothing recorded when a status changed until now. From this build on, every task carries a status log
(`TaskItem.StatusLog`, a list of from, to and when, stored as `statusLog` and left out of the file while empty),
written in ONE place: TaskService.Commit compares the old board with the new one, so no path that changes a status can
forget to log it.

"Finished" means Done or Closed, as GoalProgress counts it. A task's finish time is when its current finished run
began: the last change from an open status into Done or Closed. For history from before the log, it falls back on what
the file already holds: a Done task's `raised` (set when it became Done), a Closed task's `updatedAt` (moved when it was
closed, and by an edit after, which is rare). So the days before this build are close, not exact, and the stats say so
in a line under the charts while any counted task leans on the fallback.

A task reopened and finished again counts once, at its last finish. A day's open count treats a task as open from its
creation to its current finish, so a task closed, reopened and still open counts as open on the days it was closed.
Both are deliberate simplifications; the log would allow exactness later.

## Pipe twins

* `task stats [--days <n>]` prints the same numbers as JSON, per day the ids and titles added and finished, for a
  session that wants them.
* `tasks-window stats` opens the panel on Stats, the twin of the switch.

## Not in it

No goal, workspace or assignee breakdown, no export, no cycle-time chart. Each is a small step on the same data if he
asks for it.
