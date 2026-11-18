# Taskbar flashing switch, off while Deskbar runs

Short design for a small change. Agreed with Petre on 2026-11-18.

## Why

Petre keeps Windows' taskbar on auto-hide because the bar replaces it. It still "keeps popping up when i
don't need it". Measured on his machine that day with a throwaway probe:

- With auto-hide on and the pointer on another screen, the taskbar was fully up and stayed up for 6.5
  minutes. Two buttons (Edge, VS Code) were in the "wants attention" state. Windows keeps an auto-hidden
  taskbar up for as long as any button asks for attention, until that window is clicked. VS Code flashes
  whenever a Claude session finishes in a window that is not in front.
- With Windows' own setting "Show flashing on taskbar apps" off (Settings, Personalization, Taskbar,
  Taskbar behaviors; `TaskbarFlashing` under `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced`),
  four Teams flashes in the next 13 minutes raised nothing.
- The shell still sends the flash event with the setting off, so the bar's amber dot is unaffected.
- Explorer reads the value on every flash. Writing 1 with no broadcast made the next flash raise the
  taskbar, and writing 0 with no broadcast kept it down. So a registry write is all it takes.

His rule for the fix: "if i stop deskbar, it should bring back the taskbar", then "set the flashing off, so
that without deskbar taskbar still does its thing". Turning the switch off for good would leave him with no
flash and no amber dot whenever Deskbar is not running.

## What it does

- Manage gets a checkbox: "Stop apps flashing on Windows' taskbar while Deskbar runs (the bar's amber dot
  shows them instead)". Off by default (`AppState.QuietTaskbar`).
- Deskbar suggests it instead of switching it on unasked. While the box is unticked, the taskbar is on
  auto-hide and flashing is on, a note under the box says a flashing app brings the taskbar up and keeps it
  there until that app is clicked.
- While the box is ticked and Deskbar runs, flashing is off. On start (and when the box is ticked) Deskbar
  remembers the value it found, then writes 0.
- On Exit, Restart, sign-out and when the box is unticked, Deskbar writes the remembered value back and
  forgets it. A user who had flashing off before Deskbar keeps it off.
- After a crash the switch stays off until the next start. That start finds the remembered value and keeps
  it as the original instead of remembering the 0 it wrote itself, so the next clean exit still restores.

## Goals, non-goals, anti-goals

- Goal: a flashing app no longer holds an auto-hidden taskbar up while Deskbar runs, and Windows behaves
  exactly as before once Deskbar stops.
- Non-goal: auto-hide itself. It stays the user's own Windows setting; Deskbar only reads it for the note.
- Non-goal: the pointer reveal at the bottom edge. Five of those were logged in the same 13 minutes. No
  Windows setting stops them; hiding the taskbar window would, at the cost of the tray chevron (Win+B) and
  of a taskbar that stays hidden after a crash. Parked until Petre says the edge reveals bother him.
- Anti-goal: leaving a Windows setting changed after Deskbar is gone, or changing one nobody ticked a box for.

## Approaches considered

1. The Windows setting alone, set once by hand. No code, but no flash and no dot without Deskbar. Petre
   tried it first; it is what proved the cause.
2. Deskbar cancels each flash on the taskbar (`FlashWindowEx` with `FLASHW_STOP` on the other app's
   window). Unmeasured, per flash, and it fights the other app. Not needed once 1 was shown to work.
3. Hide `Shell_TrayWnd` while Deskbar runs. Stops every reveal, risks the tray and crash recovery.
4. Chosen: Deskbar owns the Windows setting from approach 1 for as long as it runs.

## How

- `AppState.QuietTaskbar` and `WorkspaceManager.SetQuietTaskbar`, the same shape as `DockBar`.
- `TaskbarFlashing` in `Deskbar.App`: a pure plan (what to write, what to remember) plus the applier. `App`
  calls it from the state pulse, like `ClaudeSkill.Sync`, and from `ExitApp`, `RestartApp` and
  `SessionEnding`.
- The remembered value is a one-line marker file, `taskbar-flashing.before`, in the state folder, beside
  the takeover marker. Not in state.json: it is a fact about this machine's registry at this moment, and
  writing it must not depend on the manager still being alive during shutdown.
- A missing registry value reads as on, Windows' default.
- Only the app itself writes the registry (the entry-assembly guard `StartupRegistration` uses), so a test
  that builds a ManageWindow or runs the plan cannot touch the real setting.
- Store copy: a packaged process's HKCU write is virtualised, so the write goes through `reg.exe` with
  `DetachedLauncher.RunOutsidePackage`, as the Run value delete does
  (`docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md`). Not testable on Petre's machine.
- No `deskbar.cmd` command. It is a setting about how the app sits on the machine, and nothing a session
  would script.

## Left to the builder

The wording of the note, and where the box sits among Manage's bottom checkboxes.
