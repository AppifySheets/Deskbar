# Tasks panel probes (2026-11-23)

Plan: `docs/superpowers/plans/2026-11-23-tasks-panel.md`, Task 0.

## The shell's order of two app bars on one edge

Not measured. Windows Sandbox is not installed on Petre's machine (`%windir%\System32\WindowsSandbox.exe` is
missing, and the feature query needs elevation), and registering app bars on his own screens would shrink his work
area while he works, which the project's rules forbid.

What the design does about it: it does not rely on the answer. Every time the bar's app bar registers (ABM_NEW), or
the bar lands on another screen or edge, the panel removes its own app bar and registers again after the bar's
(`TasksPanel`, `FloatingBar.Strip`). If the shell keeps an older registration outside a newer one, which is how
AppBar.cs's earlier measurement read (a new app bar was stacked beside the vertical taskbar already on the edge),
this keeps the bar outside. If the shell kept a re-registered bar outside regardless, the extra re-dock costs one
work-area change and nothing else.

To measure later, on a machine that is not his: register A then B on the right edge, print both granted rects,
remove and re-register A, print again. The manual test script's Explorer-restart step checks the outcome live.
