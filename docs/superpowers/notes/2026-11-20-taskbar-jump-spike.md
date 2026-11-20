# Taskbar jump spike (2026-11-20)

Plan Task 4 of `docs/superpowers/plans/2026-11-20-pinned-workspaces.md`: can a pinned workspace have a real
Windows taskbar button whose click Deskbar hears, reliably?

## The probe

A throwaway WinForms exe (kept out of the repo) with two windows, "Probe Messengers" and "Probe Notes":
1x1 px, borderless, at (-31900, -31900), `WS_EX_APPWINDOW`, each with its own AppUserModelID
(`SHGetPropertyStoreForWindow`, `PKEY_AppUserModel_ID` = `Deskbar.Probe.A` / `.B`), and both pinned to every
desktop through the running Deskbar (`deskbar.cmd pin <hwnd>`). The process called
`SetProcessDpiAwarenessContext(-4)`. It logged `WM_ACTIVATE`, `WM_ACTIVATEAPP`, `WM_SYSCOMMAND` and `WM_SIZE`,
and swallowed `SC_MINIMIZE`. Petre clicked the buttons on his main screen's taskbar.

## What it measured

1. **Two separate buttons.** Each window got its own button: alternating clicks activated "Messengers" and
   "Notes" in turn (13:15:36 to 13:15:54, `fg` the clicked window each time). A per-window AppUserModelID is
   enough to stop the two grouping.
2. **A click on an inactive window's button activates it**, every time: `WM_ACTIVATE` with wParam 1 and the
   window in the foreground, about 1 ms after the click lands. That is the signal to act on.
3. **A click on the ACTIVE window's button minimizes it, without any `WM_SYSCOMMAND`.** From 13:21:19 the log
   shows `WM_SIZE type=1` (SIZE_MINIMIZED) with no `SC_MINIMIZE` before it, so swallowing the syscommand does
   not stop it: the shell minimizes the window directly. The next click restores it: `WM_ACTIVATE` 1 and
   `WM_SIZE type=0`.
4. A window 1x1 px far off screen keeps its taskbar button, minimized or not, and shows nothing on any screen.
5. Not measured: changing the title and icon while shown. WPF's `Title` and `Icon` set the window's text and
   `WM_SETICON`, which the shell reads on change; checked live in Task 5.

## What it decides

- The button is built. Activation is the click.
- **The window lives minimized.** Shown with `SW_SHOWMINNOACTIVE`, and minimized again (without activating)
  as soon as it has acted on a click. Every click is then a click on a minimized window, which Windows answers
  with a restore and an activation, never with a minimize. Without this, the click right after a switch could
  land on a still-active jump window (a window pinned to every desktop can keep the foreground across a desktop
  switch) and be lost to finding 3.
- Each window gets its own AppUserModelID, `Deskbar.Jump.<workspace id>`.
- `WS_EX_APPWINDOW` also puts the window in Alt+Tab. Accepted for now; noted in the PR.
- Deskbar's own window monitor listed the probe windows as pinned windows, so the real ones must be passed to
  `WindowMonitor.Ignore` as the bar, the switcher and quick find are.
