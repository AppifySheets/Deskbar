# Subtasks: a task's session knows its parent and its siblings

Task 7bf23d9a, 2026-11-24. Petre picked the approach in the session; the calls marked "Claude's call" are open to him.

## Why

Petre, asked what this is for: "i won't have to explain the context, i can just add a task and the context will be
taken from the parent and siblings".

Today a task stands alone. A session that joins one learns its title, its status line and Petre's description
(SessionBriefing, the `task join` reply) and nothing else. So a follow-up piece of a bigger piece of work starts cold:
Petre has to retell what the earlier session found, which branch it is on, what is merged and what is not. For a task
Claude worked on, most of that context is not in any field Deskbar keeps. It is in the earlier session's conversation.

## Goals

- A task can be the subtask of another task. Petre adds one with only a title.
- A session on a subtask learns, without being told: the parent (title, description, status line, unsettled comments),
  every sibling (the same, shorter), and every session on the parent and on the siblings (id, folder, live or not,
  and where its transcript is). It is told to read the end of the parent session's transcript before starting.
- A session on a parent learns its subtasks the same way.
- A session can look again later (`task family`), since siblings move on after the session started.
- A session can split its own task into subtasks, which then carry the same context to whoever picks them up.

## Non-goals

- Forking the parent's conversation (`claude --resume <id> --fork-session`). Option 2 in the conversation, kept as a
  possible later menu item if the briefing proves too thin. Unmeasured whether `--bg` accepts `--fork-session`.
- Deskbar asking the parent's live session to write a handoff note. Dropped: it interrupts a busy session.
- A parent that finishes or closes itself when its subtasks are done, or progress counts that drive status. Status
  stays per task, set as it is today.
- Deeper trees. One level: a subtask cannot have subtasks (Claude's call, see below).
- Drag-to-nest on the bar or in the window.

## Anti-goals

- A briefing so long that every session pays for it. Family text is bounded exactly as the offered list is (titles
  cut, a cap on siblings with a pointer at `task family`), and the one-line prompt reminder does not grow.
- Moving lines. The bar's order (waiting on you on top, then quadrants by rank) stays exactly as it is; a subtask is not
  pulled under its parent, because that would break the pinned Waiting on you block and Petre's manual order.
- A session acting on its family. Sessions still change only their own task. Family is read-only to them, apart from
  making new subtasks of their own task.

## Constraints

- TaskItem is an immutable record with init properties for everything added after the first build, stored as optional
  fields that JsonTaskStore reads tolerantly. The parent link follows that pattern, as `GoalId` did.
- Free text from a session goes on stdin (`-`), never on the command line.
- The `task join` reply is what a session reads when it joins (SessionStart runs before the join for a session started
  by `task start`), so the family has to be in that reply, not only in the SessionStart briefing.
- Every `task` command has a remote twin and `ControlUsage` lists it; a test fails otherwise.
- No real people's names, no em dashes in text a session reads.

## Approaches

1. Family briefing, transcripts read on demand. **Picked by Petre.** Cheap, covers siblings as well as the parent, and
   the session reads the conversation only as far as it needs.
2. Fork the parent's conversation. Most faithful, but a long parent hands every child a nearly full context, it covers
   one parent session and no siblings, and `--bg` with `--fork-session` is unmeasured.
3. A handoff note written by the parent's session on request. Dropped: it interrupts that session.

## The design

### The link

- `TaskItem.ParentId: Maybe<Guid>`, an init property, stored as an optional `parent` field.
- `TaskBoard.SetParent(id, Maybe<Guid> parent, now)`. Refused: a parent that does not exist, the task itself, a parent
  that is itself a subtask, and a task that has subtasks of its own (one level). Setting the parent it already has
  returns this same board. A closed parent is allowed: its context is still worth reading.
- `TaskBoard.AddNote` and `Create` take an optional parent. A subtask added with a parent takes the parent's workspace
  when none is given, and lands at the top of its quadrant as any new note does.
- Deleting a parent frees its subtasks (their `ParentId` goes to none, stamped), as `DetachGoal` does for goals.
  Closing a parent changes nothing about its subtasks.
- `TaskBoard.Family(id)`: the parent (if any), the siblings (the parent's other subtasks, open ones first in pile order,
  then closed ones latest first) and the task's own subtasks. Pure, tested.

### What a session reads

One function, `SessionBriefing.Family(task, family, transcripts)`, builds the text, and three places print it:

- The `task join` reply. Today it is the task's JSON; it gains a `family` field holding this text, present only when
  the task has a parent or subtasks.
- The SessionStart briefing, after "Your task is ...", when a resumed or compacted session already has its task.
- `task family --session <sid>`: the same text, on demand. Also `task family <id>` for Petre or a script.

The text, bounded (titles cut at 80, descriptions at 200, comments: the last three unsettled, each cut at 200; at most
10 siblings, then "and N more: task family"):

```
This task is a subtask of 1a2b3c4d "Subtasks support on deskbar" (in progress, status line: "PR 512 open").
The user's description of the parent: "..."
Sessions on the parent:
  e6b349eb (live, folder E:\repos\TaskSpaces\.claude\worktrees\subtasks), transcript C:\Users\...\e6b349eb-....jsonl
Before you start, read the end of the parent session's transcript to learn what was done and decided, and look at
the siblings below so you do not redo or collide with their work.
Its other subtasks:
  5e6f7a8b "Bar breadcrumb for subtasks" (backlog)
  9c0d1e2f "Family in the join reply" (in progress, status line: "tests green"), session 3f4a5b6c (live, ...), transcript ...
A live session on this machine can be reached with Claude Code's own session messaging (ListAgents, SendMessage).
```

For a parent the same block reads "This task has subtasks:" with each subtask's line and its sessions. Bounds, Claude's
call after the builder check: at most 3 sessions per task (live ones first, then the latest joined), and the whole
family block cut at 4,000 characters with a pointer at `task family`, so a SessionStart briefing stays well under
Claude Code's 10,000 characters for a hook's context.

Transcript paths come from `ClaudeTranscript.Candidates`, the first file that exists; a session whose transcript is
not found is listed without one. The lookup lives with the app's other disk reads (ClaudeSessions already does it);
SessionBriefing stays pure and takes the resolved paths.

The prompt reminder (one line) does not change, except that a subtask's line appends "(subtask of 1a2b3c4d; task
family for its context)". Claude's call: cheap, and it reminds a long-running session the family exists.

### Making subtasks

- Petre, on the bar: the task line's menu gains "Add subtask…" after "Start Claude on this". It opens the BAR's own add
  box (taskAddBox), not the panel's, with a chip "Subtask of <parent title>" in front of the text; Enter adds it, Esc
  or the chip's × drops the parent. The tasks window's "..." menu does the same with the window's own add box. While
  the parent chip is on, the box's workspace chip is set to the PARENT's workspace (still changeable), so a subtask
  added from another desktop lands with its parent. `task add --parent` without `--workspace` takes the parent's
  workspace, likewise. Claude's calls, after the builder check.
- Petre, by command: `task add <title>|- --parent <id>` and `task parent <id> <parent-id>|none` (move a task under
  another, or free it), mirroring `goal parent`.
- A session: `task subtask - --session <sid>` adds a Backlog subtask of the session's own task, assigned to Claude so
  it is offered to the next session on that workspace. The title on stdin. A session whose own task is already a
  subtask gets a SIBLING (a new subtask of the same parent), since one level is the limit and splitting your own piece
  of work is exactly when this is wanted; the reply says so. Claude's call, after the builder check.
- Offered: `TaskBoard.Offered` today offers a task on no workspace only once it has had sessions. A Claude-assigned
  subtask on no workspace is offered too when its parent has had sessions, so a subtask a session made on a free task
  still reaches the next session. Claude's call, after the builder check.
- "Start Claude on this" on a subtask needs nothing new: the session joins, and the join reply carries the family. The
  folder it is offered first is the parent's latest session's repo when the subtask has had no session of its own
  (ClaudeStart.Folders gains that step).

### How it looks

Lines keep their place. A subtask's line gets a muted breadcrumb in front of the title, and a parent's line a muted
count after it. Sketch (bar, full-width line):

```
  ● Subtasks support on deskbar  · 2 subtasks          PR 512 open
  ○ ↳ Subtasks support on deskbar › Bar breadcrumb     backlog
  ● ↳ Subtasks support on deskbar › Family in join     tests green
```

Both surfaces draw it: the tasks panel as sketched, and on its slim line (under 692 DIP) the breadcrumb shrinks to
"↳" alone; the bar's own lines, already trimmed to fit, always use the "↳" alone. The tooltip names the parent. The
breadcrumb is not clickable (the parent may be behind "+N more" or closed); Claude's call, after the builder check. Per the "mockup before visual changes" rule, the builder shows Petre a mockup of
current against proposed before writing the drawing code.

### `task list` and the JSON

Each task's JSON gains `parent` (id or null) and `subtasks` (ids). `ControlUsage` gains `task family`, `task parent`,
`task subtask` and `--parent` on `task add`.

### Petre's pick on the look (2026-11-24)

Option A of the mockup (https://claude.ai/artifact/Srgn1B2ysrFy8Kopsettk4): on the tasks panel's full line a subtask
reads "↳ <parent title, cut> › <title>", on its slim line and on the bar "↳ <title>"; a parent's line reads
"<title> · N subtasks" before its status line. The arrow, the parent's name and the count are in the faint ink, below
the title and the status line. The add box shows the parent as a chip "↳ <parent title> ×" next to the workspace chip,
with the placeholder "A subtask: Enter adds it, Esc drops the parent".

## Calls Claude made (for Petre to check)

- One level only, as goals have. A deeper tree makes the breadcrumb and the briefing grow with depth.
- Lines keep their order; no nesting under the parent on the bar.
- A subtask inherits the parent's workspace when none is given; flags are not inherited (a new note's default).
- A closed parent stays a valid parent and its context is still briefed.
- A parent's "· N subtasks" counts its open subtasks only; closed ones are history.
- Session messaging between siblings is mentioned to the session, not relied on: whether a session id is the name
  ListAgents and SendMessage take is unmeasured.

## Left to the builder

- The exact JSON shape of the `family` field (text alone is enough for a session; a structured twin is optional).
- Where in the app the transcript lookup is wired so RemoteControl can reach it (an interface the App implements, as
  IClaudeCli is).
- The add box chip's look, after the mockup.
- Docs: README's tasks section and the manual test script get subtasks.
