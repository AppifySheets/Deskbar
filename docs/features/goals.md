---
title: Goals
summary: Weekly and monthly goals that your tasks count toward, set from a script for now, with a planner window to come.
order: 16
# Clips to film: none new for this release. The tasks-window clip should be filmed again once a goal exists, since its
# toolbar then shows the goal filter. The planner and plan-this-week clips come with the planner.
---

A goal is something you want done this week or this month. Tasks count toward it, and `goal list` shows how much of the work behind each goal is finished. The idea comes from Stephen Covey's weekly planning in *The 7 Habits of Highly Effective People*: once a week, pick the few things that matter most, then let the daily work serve them.

## How to use it

A goal has a title, a period and a status. The period is one week, Monday to Sunday, or one calendar month. The status is open, done or dropped. Goals have no flags and no workspace. They are all important by definition, and their tasks keep their own workspaces, so one goal can span several.

A weekly goal can belong to a monthly goal, as long as its week falls at least partly in that month. The week of Monday 30 November 2026 runs into December, so a goal for that week can belong to a November goal or a December one.

For now you add and change goals with `deskbar.cmd goal` (see [From a script](#from-a-script)). A planner window, where you set the week's and the month's goals and review the last ones, is coming.

Once at least one goal exists, the [tasks window](tasks.md#the-tasks-window) offers two more things:

- **...** on a line has a **Goal** menu. It lists **None**, then this month's and this week's open goals, with the task's own goal ticked. Picking one links the task to it. The task keeps its flags, its place and its status.
- **Goal: all** in the toolbar, next to the workspace filter, narrows the list and the Matrix to one goal, or to tasks with no goal. The counts on the status chips follow it. Filtering by a monthly goal also shows the tasks of the weekly goals that belong to it.

With no goals at all, neither shows, and the window looks as it did before goals.

On the bar nothing changes except the tooltip. Point at a task that belongs to a goal and the tooltip's last line names it: "Goal: Ship the goals planner". The bar shows no goal chip and no progress.

## How tasks count

A task belongs to at most one goal, weekly or monthly. A task counts as done when it is Done or Closed.

- A weekly goal counts its tasks: "3 of 5 done", or "no tasks yet".
- A monthly goal counts its weekly goals, "2 of 4 weeks' goals done", plus the tasks that belong to it directly. With only tasks it reads "1 of 2 tasks done", and with neither "nothing attached yet".

A dropped weekly goal counts in neither number. Only the weeks that fall in the monthly goal's own month count, so a monthly goal moved from November to December does not count its November weeks toward December. Those weeks still name it, as a record of what they were for.

Finishing every task never marks a goal done, and an open task never stops you marking it done. That is your call.

## Changing goals

- **Done** and **drop** end a goal. Both stay in their period, so a past week reads as what happened. **Reopen** brings one back.
- **Carry** moves a goal to another period of its kind: a weekly goal to another week, a monthly goal to another month. It keeps its tasks. Without a period it goes to the current week or month. A carried weekly goal keeps its monthly goal only if the new week still falls in that month.
- **Delete** is for mistakes. It frees the goal's tasks, and a monthly goal's weekly goals lose their link to it.

## Settings

Goals are kept in `goals.json`, beside `tasks.json` in `%APPDATA%\Deskbar`. Nothing is written there until you add a goal or dismiss the review with `goal prompt dismiss`. The link from a task to its goal is kept on the task, in `tasks.json`. A task whose goal no longer exists reads as having no goal and can be linked again.

If `goals.json` cannot be read, it is kept aside as a `.bak` and your tasks keep their links. Until it reads again, `goal list` and `goal add` answer with that problem instead of an empty list. A goal in the file whose id or period is missing or cannot be read is skipped, and the file it came from is copied aside as a `.bak` first, so nothing is lost.

## From a script

```powershell
$ts = "$env:APPDATA\Deskbar\deskbar.cmd"
& $ts goal add "Ship the goals planner"            # this week
& $ts goal add "Deskbar Pro in the Store" --month  # this month
& $ts goal add "Plan the trip" --week 2026-12-07   # the week holding that date
& $ts goal list                                    # JSON: this month's goals, then this week's
& $ts goal parent 3f2a 9c1e                        # the weekly goal 3f2a belongs to the monthly goal 9c1e
& $ts task goal 51b0 3f2a                          # task 51b0 counts toward goal 3f2a; none unlinks it
& $ts goal done 3f2a
& $ts goal carry 7d44                              # to the current week or month
```

Ids are a goal's full id or a unique prefix of four or more characters. A date is `yyyy-MM-dd`, in the years 2000 to 2999, and picks the week or month that holds it; a bare `--week` or `--month` means the current one. "Current" is your local week, whatever the time is in UTC.

`goal list` gives each goal's `id`, `title`, `kind` (week or month), `period` (its first day), `status`, `order`, `parent`, `progress` (the line above, such as "3 of 5 done") with the counts behind it, and `taskIds`. `task list` gives each task's `goal` as its id and title, or null.

The full set:

- `goal list [--week [<date>] | --month [<date>] | --all]`
- `goal add <title>|- [--week [<date>] | --month [<date>]]`, with `-` reading the title from stdin
- `goal done <id>`, `goal drop <id>` and `goal reopen <id>`
- `goal rename <id> <title>|-`, with `-` reading the title from stdin
- `goal delete <id>`, which answers with the goal as it was
- `goal carry <id> [--week [<date>] | --month [<date>]]`
- `goal parent <id> <monthly-id>|none`
- `goal prompt [dismiss]`, whether a weekly or monthly review is due, as `{due: week, month or null}`; `dismiss` marks this week and month as reviewed. The first time, only the week is due.
- `task goal <id> <goal-id>|none`, which needs an open goal

## Related

- [Tasks](tasks.md) covers the task list and the tasks window.
- [Driving it from scripts](scripts.md) lists every other command.
