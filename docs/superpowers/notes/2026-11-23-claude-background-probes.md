# Claude background sessions: probes

Date: 2026-11-23. Claude Code CLI 2.1.330, Windows Terminal, Petre's machine. Task 1 of
`docs/superpowers/plans/2026-11-23-tasks-open-claude-sessions.md`; earlier measurements are comments on Deskbar task
8f70d8ea. Probe sessions ran in `E:\repos\TaskSpaces\.claude\worktrees\probe-bg` (trusted through its parent) and were
stopped and removed afterwards. Windows opened for the probes went into the Deskbar workspace and were closed within
seconds.

## What a background session's hooks see

A session started with `claude --bg` has `CLAUDE_CODE_SESSION_ATTENDED=0` and `CLAUDE_CODE_ENTRYPOINT=cli`, plus
`CLAUDE_JOB_DIR=C:\Users\<user>\.claude\jobs\<short id>` and `CLAUDE_CODE_CHILD_SESSION=1`. Its hooks run (the trace
log shows SessionStart, UserPromptSubmit, Stop and SessionEnd for each probe), but `ClaudeHookEvent.IsHeadless` reads
ATTENDED=0 as headless, so Deskbar ignores every task event from a background session today. `CLAUDE_JOB_DIR` is the
variable that tells a background session from a `claude -p` one.

## Windows Terminal window on `claude attach`

- `wt -w new --title "ProbeOne · Claude" -p "Claude - Dark Modern" -d <folder> claude attach <short id>` opened one
  window whose title stayed "ProbeOne · Claude" after attaching (the profile has `suppressApplicationTitle: true`).
  `deskbar.cmd windows` listed it under process `WindowsTerminal`, and `deskbar.cmd move` put it in a workspace.
- Attaching a second window to the same session took the session over: the first window's `claude attach` exited
  and that window closed by itself (Windows Terminal's default close-on-exit). One attached window per session, and
  the newest wins.
- WM_CLOSE to the window, which is what the title-bar X sends, closed it and ended its `claude attach`. The session
  stayed in `claude agents --json` with the same pid.

## Resume and the copy

`claude --bg --resume <session id> "<message>"` on a session that `claude agents` still lists (here idle, state
`done`) did NOT wake it: it started a copy with a new session id (`63445b81`), as `--help` warns. Deskbar must never
resume a session that is still listed; to show it, attach.

## A crash mid-reply

- Killing the session `claude.exe` of an idle, finished session (the original `901b2dcd`): it dropped out of
  `claude agents --json` and was not restarted.
- Killing the session `claude.exe` of a busy session in the middle of a long reply (`63445b81`): the daemon started it
  again within 5 seconds under a new pid, still `busy`, and sent it a user message by itself: "Continue from where you
  left off. Note: this session was automatically restarted ...". The reply then completed (`end_turn`).
- The cut-off transcript, before the restart: the last `user` or `assistant` entry was the user's prompt, with no
  assistant entry after it. A streamed reply that never finished writes no assistant entry at all. This matches the
  plan's rule (the last message is not an assistant `end_turn`).
- So Claude Code restarts a session that crashed while working, and continues it, as long as its daemon lives. What
  it cannot cover is the daemon itself going away: a reboot, a sign-out, or the daemon being killed.

## An untrusted folder

`claude --bg "x"` in a folder never trusted exits with code 1, prints nothing on stdout, and writes to stderr:
"Workspace not trusted. Run `claude` in <folder> once and accept the trust prompt, then retry."

## Not measured

- Font fallback in a Windows Terminal profile (`"face": "JetBrains Mono, Cascadia Mono"`): not checked. The plan
  writes JetBrains Mono only when it is installed and Cascadia Mono otherwise.
- What happens at a reboot: whether the daemon comes back at sign-in and restarts its sessions. Cannot be measured
  without rebooting Petre's machine; the keeper covers it either way, because it only acts on sessions that are not
  listed.
- `claude.exe` started from the packaged (Store) build.
