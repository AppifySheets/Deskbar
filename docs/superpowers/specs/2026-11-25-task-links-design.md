# Task links: duplicate of, related to

Deskbar task 64cb52ab, 2026-11-25. Petre asked for an Open Claude button; [Deskbar #514](https://github.com/AppifySheets/Deskbar/pull/514)
had shipped it that morning for task 7f61af2e. His follow-ups, in order: "see if something like this has already been
done", "if it has, introduce a duplicate status in tasks and mark this as duplicate of", "also introduce related to",
"when working on a task, claude should examine what's related to it".

## What Petre decided (2026-11-25, three questions)

1. **Duplicate closes the task.** Stored as Closed plus a `duplicateOf` link, so every rule about closed tasks keeps
   working unchanged; everywhere a status is shown, a closed task with the link says "Duplicate" (and names the task it
   duplicates) instead of "Closed". Like GitHub's "closed as duplicate". Not a sixth status: every switch over
   `TaskStatus` would need a case, and an open "duplicate" sitting in the lists is what the close is for.
2. **A session marks its own task a duplicate itself** (`task duplicate-of <id> --session`). It found the other task, so
   it knows; Petre reopens in one click when it is wrong. This is the one way a session closes a task, and it is
   narrow: only its own task, only as a duplicate of a task that exists.
3. **A menu item with a search picker** adds a link by hand in the tasks panel: "Duplicate of…" and "Related to…" open
   a small box, type part of a title, pick the task. The links show in the open task's thread, each clickable, each
   with a cross to remove it.

## The links

- `TaskItem.DuplicateOf: Maybe<Guid>`, set only together with Closed. Any change that opens the task again (reopen,
  undo, a status by hand, a session joining) drops it: a reopened duplicate was not one.
- `TaskItem.Related: IReadOnlyList<Guid>`, stored on the task the link was made from and read both ways
  (`TaskBoard.RelatedOf`), so one link never has two copies to disagree. Making a link that exists either way is the
  same board; removing one removes it from both sides.
- A duplicate of a duplicate points at the original: `MarkDuplicate` follows the target's own link (bounded, so a loop
  in a hand-edited file cannot hang it).
- Deleting a task removes it from every `Related` list and clears every `DuplicateOf` naming it, as Delete already does
  for parents.
- Both are optional fields of tasks.json version 2 (`duplicateOf`, `related`), as `parentId` is: an older build drops
  them on its next save, which costs the links and never a task.

## The rules of marking a duplicate

`TaskBoard.MarkDuplicate(id, of, now, stoppingClaude)` refuses: an unknown task or target, the task itself, a target
that resolves back to the task, and everything `Close` refuses (open subtasks; Claude still working, unless
`stoppingClaude`). The session's own form passes `stoppingClaude`: the session asking is the one working, and it is
done. Petre's menu and `task duplicate <id> <of-id>` go through the same rules as his close.

Marking a duplicate also relates nothing: the original lists its duplicates from the links pointing at it
(`TaskBoard.DuplicatesOf`), so there is nothing to keep in step. No comment is written on either task; the link is
the record, and Petre asked for lean tasks.

## What a session reads ("claude should examine what's related to it")

- The `task join` reply gains a `related` field, and the SessionStart briefing a block after the family: the task it
  duplicates, its duplicates and its related tasks, each as a line with the user's description and its sessions with
  their transcripts, exactly as the family block lists siblings. Bounded the same way.
- The commands list tells every session, before it starts work, to look through `task list` and `task list --closed`
  for the same or related work; to link what it finds with `task relate <id> --session`; and, when its task is work
  already done or under way elsewhere, to mark it with `task duplicate-of <id> --session` instead of doing it again.
- `task family` prints the links too, so a long-running session can read them again.

## Commands

    task duplicate <id> <of-id>          Petre's: close <id> as a duplicate of <of-id>
    task relate <id> <other-id>          Petre's: link two tasks
    task unrelate <id> <other-id>        Petre's: remove the link
    task duplicate-of <of-id> --session  a session's own task closes as a duplicate
    task relate <other-id> --session     a session links its own task to another
    task unrelate <other-id> --session

`task list` prints `duplicateOf` ({id, title} or null), `duplicates` (ids) and `related` (ids, both directions).

## The tasks panel

- The thread of an open task lists "Duplicate of", "Duplicate" and "Related" lines above the comments, each title
  clickable (shows that task, switching to the Closed filter when it is closed). Only a related line has a cross that
  removes the link: a duplicate link goes by reopening the duplicate, which is that task's own Reopen.
- A closed duplicate's line says "Duplicate of <title>" ahead of its detail and in its tooltip.
- The line menu gets "Duplicate of…" (not on a task already a duplicate) and "Related to…", opening the picker in the
  task's thread: a text box over the first eight tasks matching every typed word (open first, then closed; the task
  itself, and duplicates or already related tasks, left out). Enter picks the first, a click any; Escape closes. A
  refused pick keeps the picker open and the problem line says why. The picker is drawn inside the thread rather than
  as a popup, because the panel never activates itself, and a box inside it takes the keyboard the way the comment box
  does.

## Left out

- The bar: closed tasks are not on it, and its lines lost their extra signs on purpose (Ruling 47). Its menu gets no
  link items; the panel is where tasks are read.
- Kinds beyond the two ("blocks", "follows"). Nobody asked.
