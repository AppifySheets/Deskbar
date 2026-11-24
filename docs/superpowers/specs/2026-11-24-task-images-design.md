# Images in tasks, and comments that wake the session

Task d4ea6cbe, "ability to paste images in deskbar tasks", with Petre's comment on it: "and in comments, which will be
picked up by claude sessions". Designed 2026-11-24.

## Why

Petre works with many Claude sessions at once and talks to them through Deskbar's tasks panel more and more, not
through each session's terminal. What he most often needs to hand a session is a screenshot: "this is what the bar
looks like now". Today he cannot paste a picture into a task at all, and even a text comment never reaches the session:
no hook passes a comment on, and `task list` gives a comment's id but not its text. So a comment is only for his own
eyes, and handing a session anything still means finding its window.

Success: he presses Win+Shift+S, clicks the task's comment box, presses Ctrl+V and Enter, and within a few seconds the
session that owns the task is looking at the picture and working on it, whether it was busy, idle, or not running at
all.

## Goals

- Paste (Ctrl+V) or drop an image into a task's comment box and into the description editor.
- See the image in the thread and in the description as a thumbnail; a click opens it full size.
- A comment Petre posts (text, images or both) reaches the task's Claude session and wakes it if it is idle. Petre
  chose this over "at its next turn" (2026-11-24: "it's not enough ... i want it to wake the idle session").
- A session can read a comment's text, and the image paths in it, from `task list`.

## Non-goals

- Sessions attaching images to their own comments. A session's comment may still name a file in markdown; only images
  in Deskbar's own folder render (see Safety).
- Images in a task's title or status line.
- Redelivering a comment Petre edits afterwards. The edit is visible in `task list`; the session is not woken again.
- Cleaning up image files that an edit removed from a comment. Files go when their task is deleted.

## Anti-goals

- A session woken for nothing: a turn costs money and attention, so a session is woken only by Petre's own new
  comment, never by a session's comment, an edit, a settle, or a status change.
- A comment that is lost or delivered twice. One comment is delivered once, to one session.
- Breaking every time Claude Code updates. Only documented Claude Code behaviour is used (approach A below).
- Slowing the machine with many idle sessions: Petre runs twenty or more, so whatever waits per session must be light.

## Approaches (for delivery)

Petre picked **A** on 2026-11-24.

- **A. An async Stop hook that waits, plus resume for a session that is not running.** Claude Code's hook setting
  `asyncRewake` (documented in the hook schema: "runs in background and wakes the model on exit code 2") lets a Stop
  hook keep running after the turn ends; when it exits 2, its stderr is handed to the session as a new message and the
  session takes a turn. Measured 2026-11-24 on two throwaway `claude --bg` sessions: a hook that waited 30 s and one
  that waited 700 s with `"timeout": 3600` both woke their idle session, which answered within 2 s. The message
  arrives as `Stop hook blocking error from command "Stop": <stderr>` inside a system reminder. A session that is not
  running gets `claude --bg --resume <id> <message>`, which keeps its id and conversation (measured 2026-11-23).
- **B. Claude Code's internal session pipe** (`messagingSocketPath` in `~/.claude/sessions/<pid>.json`, what sessions
  use to message each other). Instant, nothing waits, and it reaches VS Code sessions, but the protocol is
  undocumented, minified and keyed, and any update could break it silently. Rejected for that.
- **C. A `claude -p` messenger per comment** that relays through B. Slow (10 s or more), costs a model call per
  comment, and still rests on B. Rejected.

## The design

Two pull requests, in this order, both under this task.

### PR 1: pasting and showing images

**Where images live.** `<app data>\task-images\<task id>\<yyyyMMdd-HHmmss-fff>.png` (`.jpg` for a JPEG file dropped
as is), under `%APPDATA%\Deskbar`, or the same folder under `DESKBAR_APPDATA` (AppPaths). Deleting a task deletes
its folder; closing keeps it. Each file is written when the comment or description is SAVED, not when it is pasted,
so a draft thrown away leaves nothing.

**Encoding.** Reuse `ReportImages`: a clipboard picture becomes a PNG; a dropped or copied PNG or JPEG file is copied
byte for byte; another format (BMP, GIF, TIFF, WebP) is re-encoded as PNG. No size cap is needed locally, but a
picture wider than 4096 px is scaled down to 4096 so a session reading it is not handed a huge image (builder may
tune the number; Claude Code downsizes images itself, this only keeps files sane).

**In the markdown.** An image is a line of its own, `![image](task-images/<task id>/<file>.png)`: plain markdown with
a path RELATIVE to the app data folder, never an absolute one. An absolute path would be wrong under
`DESKBAR_APPDATA`, on another profile, and in the cloud task list the tasks-in-the-bar design plans for, and changing
the stored form later would mean migrating comments. Everything a session reads expands it to the absolute path
(`task list`, SessionBriefing, the delivered message), because a session passes that straight to its Read tool.
Images go after the comment's text, one per line, in the order they were added. The description works the same way.

**Comment box.** Ctrl+V with a picture or image files on the clipboard adds them as pending images; Ctrl+V with only
text is left to the text box, exactly as `ReportWindow.PasteImages` does. Dropping image files or a picture on the
box does the same. Pending images show as a row of small thumbnails (about 48 DIP high) under the box, each with an
x that removes it. Enter posts text plus images; a comment of images alone is allowed. A pending image survives a
redraw the way the text draft does (`drafts`). The hint line under the box gains "Paste or drop images." The
placeholder stays short.

**Description editor.** The same paste and drop, with the same thumbnail row under the description box. Images
already in the description show there too (parsed from its markdown), with the x to remove one; the description box
itself shows the text without the image lines, so Petre never edits a path by hand. Enter saves the text plus the
image lines.

**Rendering.** `MarkdownBlocks` learns one more construct: a line that is only `![alt](task-images/...)`, which
resolves to a file inside `<app data>\task-images\` with a picture extension (`..` segments refused). It becomes a thumbnail, at most
the column's width and 160 DIP high, aspect kept, with a thin border; a click opens the file with the default image
viewer (shell open). The image is decoded off the UI thread at thumbnail size (`DecodePixelWidth`) and cached by path;
a missing or unreadable file shows the line as text, never an exception. Anything else (a URL, a path elsewhere, an
image mid-line) stays the text it was written as.

**On the bar.** The bar shows a description's first line; image lines are skipped when choosing it, and a description
that is only images shows "image" (dim), so a path never appears on the bar.

**Can a session read the file?** Sessions read inside their own folder, and `task-images` is outside it. Petre's
sessions run in auto mode, where a Read outside the folder is normally allowed, but this is not measured. PR 1's first
step measures it on a throwaway background session: if the Read goes through, nothing more is needed; if it stops at a
permission prompt, Petre decides whether Deskbar may add the folder to `permissions.additionalDirectories` in
`~/.claude/settings.json` (wider than the hook entries ClaudeHooks writes today), and the plan waits for that answer.

**`task list`.** Each thread entry gains `markdown`, the comment's full text, with image paths expanded. The design note in
RemoteControl.Tasks.cs that "the thread itself is the window's" changes accordingly. `SessionBriefing` keeps cutting a
description at 200 characters, but appends the description's image paths whole after the cut ("images in the
description: <path>, <path>; read them with your Read tool"), since a cut path is useless.

**Safety.** Only files inside `task-images` are rendered or opened, and only with a picture extension, because a
comment may come from a session: a click must never run a program, and a crafted path must not make Deskbar open
something elsewhere. Same rule as links (only web links open).

### PR 2: a comment wakes its session

**Which comments.** Petre's own new comments: posted in the panel, or `task comment <id>` without `--session` run
outside a Claude session (the client sees no `CLAUDECODE` variable). Not a session's comment, a comment another
session posts by id (session-inventory and the like), an edit, a settle, or Deskbar's own failure lines (which are
also stored with no session id, so the wake fires where Petre's comment is POSTED, never on "a new comment with no
session" in the store).

**Which session.** The task's newest session that has a transcript (the one double-click would open). One comment goes
to one session.

**The waiter.** A new entry on Stop in `~/.claude/settings.json`, written by `ClaudeHooks.Sync` beside the existing
ones and only while "Claude Code sessions keep their tasks" is on: `{"type":"command","command":"\".../claude-wait-hook.cmd\"","asyncRewake":true,"timeout":86400}`.
A third script name, so an older Deskbar neither claims nor rewrites it (Ruling 35's reasoning). The script:

1. Hands the hook's JSON to `deskbar.cmd claude-wait`, which returns at once with the path of this session's inbox
   file and a generation number, and exits. The control pipe serves one client at a time, so it is never held while
   waiting.
2. Loops cheaply: is there an inbox file? is my generation still the newest? Sleeping between checks without a
   console (a cmd `timeout` refuses redirected input; `ping -n 2 127.0.0.1` or similar). The builder measures the
   cost with twenty waiters and picks the interval (one to two seconds is the target for "within a few seconds").
3. When the inbox file exists, claims it by renaming it (only one waiter can win a rename), prints its text to stderr
   and exits 2: the session wakes with it.
4. When a newer generation exists (a later Stop started a newer waiter), exits 0 quietly.
5. When its timeout passes, exits 0 quietly. The timeout is as long as Claude Code accepts (the build measures the
   cap; the target is 30 days), so a lapse is rare. A lapse is recorded: `claude-wait` notes when each waiter expires,
   so Deskbar knows a running session has no waiter. A once-a-day "no new comments" turn was considered and dropped:
   with twenty idle sessions it is twenty uncached turns, twenty workspace blinks and twenty rings of Petre's Stop bell
   a day, all for nothing, which is this design's own anti-goal.

**Delivery.** When Petre posts a comment, Deskbar picks the session (above) and checks `claude agents --json`:

- **Running** (idle or busy): write the comment to the session's inbox file (appending if one is already waiting,
  so two quick comments arrive together). An idle session's waiter wakes it; a busy one's next Stop starts a waiter
  that finds the file at once, so the comment arrives as its turn ends. A busy session may still have the waiter its
  previous Stop armed; whether that waiter's exit 2 during a turn is injected, queued or dropped is NOT measured, and
  PR 2 measures it first. If it could be dropped, the generation is bumped when a turn starts (UserPromptSubmit), so
  only a waiter armed after the turn ends claims the inbox.
- **Running, waiter lapsed** (idle past the timeout): a background session is stopped (`claude stop`) and resumed with
  the comment (`claude --bg --resume <id> <message>`), which wakes it under the same id without starting a copy. A
  terminal or VS Code session cannot be restarted that way, so its comment waits in the inbox for its next turn.
- **Not running** (closed, rebooted, stopped because its task was Done): `claude --bg --resume <id> <message>` from
  the session's folder, through the keeper's existing resume path, without writing the inbox.
- **No session with a transcript**: nothing is delivered; the comment stays in the thread as today.

A delivery that fails (resume refused, folder untrusted) is traced and noted in the thread as Deskbar's line, as
resume failures already are.

**The message.** Written for Claude, like SessionBriefing: "Deskbar: the user commented on your task <id8> "<title>":"
then the comment's markdown whole, then, when it has images, "Read each image path above with your Read tool." For a
Done task it adds the existing Done rule (more work: `task join`; a question about finished work: leave it done).

**VS Code sessions.** Their hooks run the same Claude Code, so the waiter should work there, but this is not yet
measured. The first step of PR 2 measures it on a throwaway session; if a VS Code session does not wake, PR 2 still
ships for background and terminal sessions and VS Code ones get their comment at their next turn (the inbox file
waits), and that limit is said in the docs.

**Cleanup.** SessionEnd deletes the session's inbox and generation files. A stopped session's leftover waiter exits
when its generation is superseded or its timeout passes; a stray inbox file for a session that is not running is never
written, because delivery checks first.

## Constraints

- No new NuGet package; no WebView (MarkdownBlocks' rule).
- Every user-facing change updates `docs/features/tasks.md` (and `claude-sessions.md` for PR 2); each PR has a
  `## Release notes` section.
- Windows tests run on the hidden desktop (`scripts/test-hidden.ps1`), never on Petre's desktop.
- `ClaudeHooks.Sync` keeps every rule in its header: only Deskbar's entries, backup once, leave unparseable files.

## Left to the builder

- The thumbnail sizes and the 4096 px cap, within what is said above.
- The waiter's polling interval and sleep mechanism, by measurement.
- Whether `claude-wait` returns the inbox path on stdout or the script derives it.
- How the description editor keeps text and image lines apart internally.

## Calls I made (Petre to check)

- Images stored as files, not embedded in tasks.json, with app-data-relative paths in the markdown, expanded to
  absolute ones wherever a session reads them.
- A comment another Claude session posts by id does not wake the target session.
- No once-a-day turn; a month-long waiter instead, and stop-then-resume for a background session whose waiter lapsed.
- Image lines go after the text, not at the caret.
- Files are written on save, and deleted only with their task.
- The waiter lives under the existing "Claude Code sessions keep their tasks" setting; no new setting.
- One comment goes to the task's newest session, not every session it ever had.
