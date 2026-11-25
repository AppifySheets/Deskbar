# A model for each task session

Date: 2026-11-25. Deskbar task e52e33cb, "give each claude session best model for the job, never default. maybe start
with one model and delegate to a cheaper one".

## Why

Every session Deskbar starts on a task runs `claude --bg "<first message>"` with no model argument
(`ClaudeSessions.Start`), so it gets the model in `~/.claude/settings.json`: Fable at high effort on Petre's machine,
for a one-line fix as much as for a design. He runs about 30 task sessions and hits the usage limits. His words:

- "give each claude session best model for the job, never default"
- Asked what it is for: "limits, weekly or 5hr"
- Asked who picks: "deskbar picks the model; maybe through the deskbar manager agent"
- "maybe each task starts with the best model and delegates to something cheaper"
- Asked why Haiku should do the picking, he was told it should not, and agreed to a strong model doing it.

## Goals

- Every session Deskbar starts on a task gets an explicit `--model` and `--effort`, chosen for that task.
- Deskbar chooses without asking Petre. He can see the choice and overrule it.
- The manager, or anyone on the pipe, can name the model when starting a task.
- Sessions are told to hand mechanical work to cheaper subagents.

## Non-goals (this change)

- Moving a running session to a stronger model. Measured below: it means a copy with a new session id, so it is a
  follow-up with its own design.
- Reading how much of the usage limit is left. Deskbar has no source for it; the choice goes by the task alone.
- Changing the manager session's own model (its design fixes it at Fable) or sessions Petre starts by hand.

## Anti-goals

- A session started on the settings default because the sizing failed or timed out.
- A start that waits noticeably longer, or fails, because of the sizing.
- The sizing costing enough to matter against the limit it protects.

## Measured (2026-11-25, Claude Code 2.1.332)

- A stripped one-shot call, `claude -p --model fable --output-format json --no-session-persistence --tools ""
  --strict-mcp-config --setting-sources "" --disable-slash-commands --system-prompt "..." "<prompt>"`, sent about 700
  input tokens and answered in about 2.5 seconds wall time (Haiku: 1.9 seconds). It needs no trusted folder and runs
  no hooks. So the model that sizes costs next to nothing, and a strong one can do it.
- `claude --bg --model haiku ...` starts a session whose turns are answered by `claude-haiku-5-5` (read from the
  transcript's `message.model`).
- `claude --bg --resume <id>` with no flags continues the same session on its saved options, the model among them
  ("woke session ... with its saved options (--tools, --setting-sources, -n, --model)"). The resume paths in
  `ClaudeSessions` therefore need no change.
- `claude --bg --resume <id> --model sonnet` does not change the session. It starts a copy under a new id, running or
  stopped ("keeps its own saved options, so the flags you passed started a copy as ..."), and the copy's next turn is
  answered by Sonnet.

## Approaches

1. **Deskbar sizes the task at start** (picked). One stripped call to a strong model with the task's text returns a
   model and an effort; Deskbar passes them to `claude --bg`. The whole session runs on the right model, which is
   where the saving is. Its weakness: the sizer reads the task's words, not the code.
2. **The manager picks.** Better judgment (it knows the board), but it acts only in a turn Petre started, a start
   from the bar or the panel never passes through it, and each pick would be a Fable turn with the board in context.
   Kept as an override: `task start` takes `--model`.
3. **Start every session on the best model and let it delegate down.** The main session still carries the whole
   conversation on Fable, a handoff drops detail, and delegating is the session's own call each time. Kept as a line
   in the briefing, not as the mechanism.

Considered and left for later: start cheap and move up when the session finds the work is harder than it read. It
suits the limit best, but a move is a copy with a new id (measured), which touches joining, the ledger and the
terminal window.

## Design

**Sizing.** `ClaudeStart` (Core, pure) builds the sizing prompt from the task's title, description, parent's title
and the thread's unsettled comments, cut to about 4,000 characters, and parses the answer. The answer is JSON held to
a schema (`--json-schema`): `model` (`sonnet`, `opus` or `fable`), `effort` (`medium` or `high`) and a `reason` of
one short sentence. The system prompt says what each rung is for: Sonnet for a change whose place and shape the task
already names; Opus for a feature or a bug whose cause is not known; Fable for design, for work across several
subsystems and for a bug that earlier attempts on the task did not cure. Haiku is not a rung: a task session writes
code. The call runs on Fable through `ClaudeCli` off the UI thread, with a 20 second limit.

**Never the default.** If the call fails, times out or answers outside the schema, the session starts on Sonnet at
high effort and the reason says the sizing failed. `--model` and `--effort` are on every `claude --bg` Deskbar issues
for a task.

**Overrule.** `deskbar task start <id> [--folder <path>] [--model <m>] [--effort <e>]`: a named model skips the
sizing. `deskbar task model <id> sonnet|opus|fable|auto` stores a standing choice on the task (`TaskItem.Model`,
`auto` clears it) that every later start of that task uses; the task's menu gets the same four entries under "Model".
Both are listed in `ControlUsage`. The manager's briefing learns the `--model` argument once its branch is on main.

**Where it shows.** `TaskSession` gains the model, effort and reason it was started with (old sessions have none).
They are in the task's JSON on the pipe, and the session's line in the task's thread and window reads, for example,
"Sonnet, medium: a one-file text change". No thread comment is written for it.

**Briefing.** `SessionBriefing` gains one line for every task session: give test runs, searches, rebases and other
mechanical work to a subagent on a cheaper model (the Agent tool's `model`), and keep design and review for itself.

## Left to the builder

- The exact wording of the sizing prompt and of the briefing line. Try the prompt against a dozen real tasks from
  the board (small fixes and the known hard ones) before settling it, and put the cases in the tests.
- Where the session line is drawn in the panel, the task window and the bar's tooltip.
- Whether a subtask's sizing should also see its parent's description (the family briefing already has it).
- Docs: the README's tasks section and `docs/superpowers/notes/manual-test-script.md` get the new steps.

## Follow-up (own task)

A session that finds the work harder than sized asks Deskbar to move it up: Deskbar stops it, starts the copy with
`--bg --resume <id> --model fable`, joins the copy to the task and moves the terminal window to it.
