# The manager session

Date: 2026-11-25. Deskbar task bbf9a6ed, "add a manager claude session that i can start from the bar and will act as
an overview for everything happening in my tasks".

## Why

Petre keeps about 30 open tasks, most with a Claude session on them, and works across many at once. The tasks panel
shows each task's line, but finding out what is going on across all of them, and getting something done about it,
means going through them one by one. He wants one Claude session he can open from the bar that knows the whole board
and does the coordinating for him. His words, from the task:

- "add a manager claude session that i can start from the bar and will act as an overview for everything happening in
  my tasks"
- "let's add a button to deskbar that will start the manager session; it should always be present, on best model,
  fable and should only ever be tasked with coordination"
- Asked whether it only reports or also acts: "acting as well"
- "it should be light, not consume a lot of tokens"

The user-wide `session-inventory` skill already does a version of this for sessions and pull requests (a ledger,
asking about what waits, handing checks to the owning session, never doing the work). The manager is the same idea
built into Deskbar and reading the task board, which already holds what each session is doing.

## Goals

- A Manager button on the bar, always there, opens the manager: its window if it is running, otherwise a new one.
- The manager is a Claude Code background session on Fable (`--model fable`).
- It knows the board: what waits on Petre, what is stuck, what is being worked on, what is idle.
- It acts on the board when Petre asks: start Claude on a task, pass his answer to a task's session, set flags and
  order, move a task, assign it, split off a subtask, add a task, write a comment, rename, describe.
- It is light. It costs nothing while Petre is not talking to it, and each turn reads a compact view of the board, not
  the full one.

## Non-goals (this change)

- Deskbar waking the manager on events (a task starts waiting on Petre, a session ends without done, a question sits
  unanswered). Petre picked "1 now, 2 later": this is the second step, a follow-up task, and the design below leaves
  room for it (see "Later: event wakes").
- A chat surface drawn by Deskbar. The manager is a terminal window like every other task session.
- Replacing `session-inventory`. It stays as it is; it covers pull requests and sessions outside Deskbar's tasks.

## Anti-goals

- The manager doing project work. It never reads or edits code, runs builds or tests, opens, reviews or merges pull
  requests, or reads other sessions' transcripts. When something needs checking, it asks the session that owns it.
- The manager closing or deleting a task, or marking one done. Only Petre closes a task; a session marks its own work
  done.
- The manager starting a session that Petre did not ask for. In this change it acts only inside a turn Petre started.
- The manager polling: no Monitor, no loop, no scheduled wakeup, no cron. Each of those is a Fable turn that usually
  finds nothing.
- The manager turning into a task on the board, or being nagged by the hooks to join one.
- Two managers at once.

## What was measured (2026-11-25)

- `deskbar task list` on Petre's board: 34 tasks, 29 open, about 52,500 characters, roughly 13,000 tokens. Every
  look at the board through it would cost that on Fable. Hence `task overview` below.
- `claude agents --json` reports each session's `name`, set by `claude -n <name>`.
- `claude --help` (2.1.331) has `--model` taking the alias `fable`, `-n/--name`, and `--append-system-prompt-file`.
- Neither `%APPDATA%\Deskbar` nor the user's home folder is a trusted folder in `~/.claude.json`, so the manager's
  first start in a folder of its own meets the trust prompt once.
- A comment written with `task comment <id>` is stored as Petre's ("you"); one written with `--session` is stored as
  Claude's. The pipe client appends `--from <folder>` to every `task` call (`ControlArguments.AboutTheCaller`).

Not measured yet, for the builder (see "Left to the builder"): whether `--append-system-prompt-file` and `--model`
carry over to `claude --bg --resume`, and whether `claude --bg` with `--model fable` starts a Fable session.

## Approaches

Three ways were weighed for how the manager learns what is happening:

1. **Asleep until Petre talks to it** (picked for now). The button opens the window; the manager reads the board when
   Petre writes to it. No cost while idle. It never notices anything on its own, which the panel's "waiting on you"
   already covers.
2. **Deskbar wakes it on events.** As 1, plus a short message from Deskbar when something worth noticing happens. More
   of a real manager, but every wake is a Fable turn. Petre picked this as the next step.
3. **A check-in every 30 minutes**, like `session-inventory`. Rejected: it pays for a turn whether anything changed or
   not, against "light".

Petre's pick: "1 now, 2 later".

## How it works

### The button

A small "Manager" button on the bar's task list, in the chip row beside "All tasks" (`FloatingBar.TaskList.cs`, the
`task-add-row` grid: a third Auto column, not a wrapper around `taskMoreButton`, because `ApplyMoreText` measures that
button's parent). It is always shown while the task list is shown. Its pipe twin is `deskbar.cmd task manager`, listed
in `ControlUsage`, so the button and the pipe go through the same method.

A click:

1. If today's manager is listed by `claude agents --json`: bring its window forward, or attach a new window when none
   is open (the way `ClaudeTaskActions.Attach` does for a task).
2. If today's manager is not listed but its transcript is there: resume it (`claude --bg --resume <id>`, no message, no
   model call), then attach.
3. Otherwise (no manager yet, yesterday's, or its transcript gone): stop the old one if it is still listed
   (`claude stop`), start a new one, record its id and today's date, and attach.

"Today" is the local date. One manager per day keeps its context short: a session kept for days carries its whole
history into every turn, and the board, not the conversation, is where the state lives.

### Where it runs and what it is told

- Folder: `<Deskbar app data>\manager` (respecting `DESKBAR_APPDATA`), created on first use. A folder of its own keeps
  its transcripts apart and is how Deskbar recognises the manager anywhere a session's folder is known (hooks, the
  keeper, the pipe's `--from`), with no race against learning its id.
- Start: `claude --bg --model fable -n "Deskbar manager" --append-system-prompt-file <folder>\manager-briefing.md
  "<first message>"`. The first message asks for the overview: what waits on Petre, then what is stuck, in a few lines.
  Opening the manager is Petre asking "what is going on", so the first turn answers that.
- The briefing file is written by Deskbar from text in `Deskbar.Core` (so a test pins it) every time it starts or
  resumes the manager, so it follows the build. It says, briefly:
  - you coordinate Petre's Deskbar tasks and do nothing else (the anti-goals above, as rules);
  - read the board with `deskbar.cmd task overview`, at most once per message from Petre unless you changed something;
    read one task in full with `task show <id>` only when you need its thread;
  - the commands you may use (the `task` verbs for the goals above, `task start` to put Claude on a task) and the ones
    you may not (`close`, `delete`, `status ... done`);
  - to tell a task's session something, comment on its task (`task comment <id> -`): Deskbar wakes an idle session
    with it. Never message a busy session directly;
  - no Monitor, loop, scheduled wakeup, subagent or transcript reading; replies of a few lines.
- The window: title `Deskbar manager · Claude` through `ClaudeStart.AttachTitle`, opened on the workspace Petre is on,
  as a free task's window is.
- The trust prompt on the first start: the existing untrusted-folder offer (a terminal running `claude` in the folder
  so Petre accepts trust himself), shown as a message rather than written into a task's thread, since there is none.

### What it may run (my call, after the builder check)

The rules are enforced at start, not only written in the briefing, so a slip costs nothing:

- `--allowedTools` lets Bash run `deskbar.cmd task *` (and `deskbar task *` on a Store machine) without a permission
  prompt; a background session stuck at a prompt is no use.
- `--disallowedTools` refuses Read, Edit, Write, NotebookEdit, Agent, Monitor, WebFetch, WebSearch, ScheduleWakeup,
  CronCreate and every other Bash command. Skill stays, so Petre's writing rules (humanizer) still apply.
- `--strict-mcp-config` with no MCP config: the manager needs none of his MCP servers, and their tool lists are paid
  for on every turn.
- User settings stay loaded: Deskbar's own hooks live in `~/.claude/settings.json`, and so do his writing rules and the
  session-title hook.

The builder measures that these flags hold under `--bg` and `--bg --resume`.

### Who counts as the manager (my call)

Any session whose folder is the manager folder, and any `task` call whose `--from` is that folder. The folder is
Deskbar's own; nothing else runs there. The one other session there is the trust terminal on the first start, which
Petre closes once he has accepted trust; Deskbar does not retry the start by itself, the next click starts it (as a
task's start does today). The stored id is used only for the day rule and for attaching, never for identity, so there
is no gap between starting a manager and learning its id.

### When it asks a session something (my call)

In this change the manager comments and ends its turn; it does not wait for the reply, since waiting inside a turn is
polling. The reply lands on the board (the task's status line, question or thread), where Petre sees it in the panel
or the next time he asks the manager. Event wakes (the next step) are the fix for "tell me when it answers".

### The manager is not a task

- Hooks: `SessionStarted` and `Prompted` in `RemoteControl.ClaudeHook.cs` recognise a session whose folder is the
  manager folder and answer with a short manager briefing line (`SessionBriefing.Manager`) instead of "this session
  has no task yet, join or create one". It is not marked attended, so the keeper's adoption never fires.
- Keeper: `SessionKeeper.Plan` and `Stops` take the manager's folder (or a predicate) and leave its sessions alone:
  never adopted, never resumed after a reboot, never stopped by the done sweep. The button resumes it on demand.
- `task new` and `task join` with the manager's session refuse, with a line saying the manager does not take tasks.

### What it changes on the board, and in whose name

Commands from the manager folder (known by the pipe's `--from`) are the manager's, not Petre's:

- a comment it writes is shown as the manager's, not as "you", and wakes the task's idle session the way Petre's
  comment does (the asyncRewake waiter), since passing his answer on is the main reason to write one. Every manager
  comment wakes, its own requests included;
- what the woken session makes of it (Petre's pick, 2026-11-25, option 1 of two): only Petre's words quoted verbatim
  count as his. The manager writes them as a quote marked as his ("Petre says:" and his words unchanged), and the
  session takes that as his own comment: it can settle its question and go ahead with what he approved. Anything the
  manager writes in its own words is a coordinator's request: the session does what is routine, and whatever needs
  Petre's approval (a merge, anything destructive, a design choice) still goes back to him through `task ask`. The
  wake text from `SessionBriefing.Commented` says which of the two it is, and the manager's briefing tells it never
  to paraphrase into a quote. The alternative, every manager comment carrying his authority, was turned down: a
  wrong paraphrase would become an order;
- the refused verbs above are refused for it with a one-line reason, whatever form they come in.

### `task overview` and `task show`

- `task overview`: one line per open task, in the pile's order, subtasks indented under their parent. Each line: the
  first 8 characters of the id, status, `!` important and `*` urgent, workspace, title (cut to about 60 characters),
  the status line, the open question when there is one, and the sessions (none, idle, working, ended). About 30 lines
  for Petre's board, a few thousand characters. Snoozed tasks are counted on one closing line, not listed.
- `task show <id>`: one task as `task list` describes it, thread included.
- Both are ordinary `task` verbs, listed in `ControlUsage` and useful to any session, not only the manager.

## Later: event wakes

The second step, a separate task. Deskbar would send the manager's session a short line (`claude --bg --resume <id>
"<line>"` when it is idle, or a comment-style wake) on a small set of events Petre picks, batched so a burst is one
turn, and only while today's manager exists. Nothing in this change should make that harder: the manager's id and
day are stored, and its folder identifies it.

## Left to the builder

- Measure, before relying on them: `--model fable` with `--bg`; whether `--model` and `--append-system-prompt-file`
  survive `--bg --resume` (if not, pass them again on resume); whether `claude --bg` accepts the flags in this order.
- Where the manager id and day live: `AppState` (`ManagerSessionId`, `ManagerSessionDay`, in the style of
  `ProReminderSince`) with a `WorkspaceManager` setter.
- How the manager's author shows in the thread (a new author kind or Claude's with a mark), and the exact wording of
  the briefing and the first message, kept short.
- The button's look: text "Manager" or a glyph with a tooltip, in the style of the chip row.
- The overview's exact line format.

## Tests

- `ClaudeStart` (or a new `ManagerStart` in Core): the start arguments, the briefing text, the day rule (same day
  attach or resume, new day stop and start).
- `SessionKeeper`: a live session in the manager folder is never adopted, resumed or stopped.
- `SessionBriefing`: the manager folder gets the manager line on SessionStart and UserPromptSubmit, not the join nag.
- `RemoteControl`: `task overview` and `task show` output; `task manager` dispatches; the manager's refused verbs; a
  manager comment's author and its wake; `Task_is_in_the_help` with the new lines.
- Manual steps in `docs/superpowers/notes/manual-test-script.md`: first start with the trust prompt, a second click
  brings the window forward, the next day's click starts a new manager and stops the old one, the manager passes an
  answer to a task's session and that session wakes.
