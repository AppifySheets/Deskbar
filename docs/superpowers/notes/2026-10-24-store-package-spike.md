# Store package spike, findings

Date: 2026-10-24
Machine: a second Windows 11 Pro, 25H2 (OS build 26200.7623), one screen at 150%, Microsoft Store
present. It already had the direct build 2.1.48 installed, with a real profile in
`%APPDATA%\Deskbar`, a Run value and a Start menu shortcut.
Script: `scripts/store-spike.ps1` as of commit 1f34887, run elevated under Windows PowerShell 5.1.
pwsh was not installed.
Packages: `Deskbar-0.0.1-store.msix` and `Deskbar-0.0.2-store.msix`, signed with the test
certificate whose subject is the Store publisher, installed as `Petre.Deskbar` (signature kind
Developer).

The letters are the check ids in the script's report. The report stays out of the repository: it
lists that machine's workspaces and account.

## Verdict

**The package works on a real Windows 11 machine.** Every claim about the app that the script
could check passed, and so did every manual question. Windows brings the app back by itself after
a Store-style update, so `RestartProven` is now true. The four failures in the report (d, e, f, h)
are the script's own: three come from one wrong assumption about `DESKBAR_APPDATA`, one is a
PowerShell bug. The wrong assumption had a useful side effect, because the packaged app ran
against the real profile and so measured the virtualisation exclusions and the takeover for real.

## Package identity (L)

Started through `Invoke-CommandInDesktopPackage`, the app answered `deskbar version` with
`"packaged": true`, version 0.0.1 and its exe under `C:\Program Files\WindowsApps\`.
`tasklist /apps` listed it inside `Petre.Deskbar_0.0.1.0_x64__n8ch8v53xw03y`, and
`GetPackageFullName` on the process said the same. **Pass.**

## The alias is a console command (i)

`where deskbar` found `%LOCALAPPDATA%\Microsoft\WindowsApps\deskbar.exe`. From cmd,
`deskbar version` printed the JSON and `!ERRORLEVEL!` was 0, in 218 ms. From Windows PowerShell
the same, `$LASTEXITCODE` 0, in 407 ms. pwsh was not installed, so it was not tried. **Pass:** the
shells waited for the alias and kept its output and exit code, so the `uap3:AppExecutionAlias` form
in the manifest needs no `Subsystem="console"`.

## Children run outside the package (c, c2)

`deskbar start SpikeWs -- notepad.exe` started Notepad in its own package
(`Microsoft.WindowsNotepad_...`), not Deskbar's. `deskbar start SpikeWs -- charmap.exe` started
Character Map with no package at all, and `tasklist /apps` listed neither under Deskbar's
package. Character Map is the unambiguous one, since it is a plain Win32 exe. **Pass:** the
breakaway works, so Windows will not end what Deskbar starts when it services the package.

## Start entry and startup task (g, g2, M4)

`Get-StartApps` lists `Deskbar` as `Petre.Deskbar_n8ch8v53xw03y!Deskbar`. The startup task's
registry state was `State = 2` (enabled) with `UserEnabledStartupOnce = 0`, and Settings, Apps,
Startup showed Deskbar listed and on. Nothing had enabled it by launching: the manifest's
`Enabled="true"` is enough. **Pass.**

## The app as the person saw it (M1, M2, M2b, M3, M5)

Win+Ctrl+Tab opened the switcher. Manage's "Start Deskbar with Windows" box was ticked. Asked for
the note under that box, the person typed the next checkbox's text ("Check the Microsoft Store for
new versions"), so there was no note, which is right for a packaged copy whose task is available
(`StartupNoteText` returns nothing then). The version line showed 0.0.2, the version installed by
then. The taskbar icon looked like Deskbar's own icon, not a small icon on a coloured plate.
**Pass.** The screenshot (check j) shows the bar docked and the taskbar icon present; it stays out
of the repository because it shows that machine's workspaces.

These answers were given against an elevated app: the as-user launch fell back (see M0 under
Script findings).

## Entitlement without a Store licence (h, dump)

The dump the app wrote (check h; the copy reached the evidence before the script threw) reads
`entitlement free, Free tier`. The sideloaded test package has no Store licence, so the free tier
is what the design asks for: no licence means no Pro, and no error. **Pass.**

## Windows brings the app back after a Store-style update (U)

With 0.0.1 running, `Add-AppxPackage Deskbar-0.0.2-store.msix -ForceApplicationShutdown` closed
it and returned after 37 s. Windows then started Deskbar again by itself, 38 s after the install
began: the new process ran from the 0.0.2 folder, its parent was sihost, it was not elevated, and
its command line was the bare exe. The old process was gone. Nothing in the script started it.
**Pass, `restartProven` true.** Which mechanism brought it back (the restart registration made at
startup by `CrashRestart`, or Windows' own handling of packaged apps) was not separated.

## The virtualisation exclusions and the takeover (d, e, f)

Measured by accident, since the app was meant to run against a scratch profile (Script findings).
The packaged app's writes under `%APPDATA%\Deskbar` landed in the real folder, not in the
package's private `LocalCache`: it created `dumps\`, wrote the `packaged-first-start-done`
marker, and rewrote `state.json`, `state.json.previous`, `last-used.json` and `deskbar.cmd`. It
also deleted the real HKCU Run value `Deskbar` and the real Start menu `Deskbar.lnk`, which is
the first-packaged-start takeover doing its job. **Pass for the app:** the `RoamingAppData` and
HKCU Run exclusions resolve at run time, and the takeover runs once and leaves its marker. The
script's backup and restore then put every file, the Run value and the shortcut back as they were.

## Script findings

- **`DESKBAR_APPDATA` does not reach the packaged app (d, e, f).** The script set it for every
  start it made, but `Invoke-CommandInDesktopPackage` hands the start to a broker, so the app is
  not a child of the process that set the variable and never sees it. The scratch folder stayed
  empty (d failed), and the real profile was touched (e and f failed). `scripts/store-smoke.ps1`
  rests on the same assumption; on a throwaway runner that only means the app writes to the
  runner's own profile. Both scripts now say so; the spike's backup and restore are what protect
  a real profile, and its checks e and f now judge the restore rather than expect no change.
- **Check h threw "Cannot find path 'C'".** The dump was written and copied into the evidence.
  The cause is PowerShell, not the path: `$found = if (...) { @(...) }` unrolls a one-element
  array through the pipeline, so `$found` was the path string and `$found[0]` its drive letter.
  Reproduced under 5.1 with the same message, and fixed by putting `@()` around the whole `if`.
- **The as-user launch fell back (M0).** The non-elevated start through a scheduled task ran, but
  the elevated script's `deskbar version` got no answer within 45 s, so the script started the app
  elevated instead and the manual questions were answered against that copy.

## Open

- **An elevated `deskbar` does not reach a non-elevated app.** Twice a non-elevated Deskbar ran
  (the sihost restart in U, the as-user launch in M0), and both times an elevated
  `deskbar version` got no answer within 30 to 45 s, while an elevated app and an elevated CLI
  answered in about 200 ms. Unexplained, and untested in the configuration real users have, where
  both sides are non-elevated. If it ever matters, look first at `PipeOptions.CurrentUserOnly` in
  `ControlPipeServer` and `ControlClient`, the one place where the pipe's two ends check who the
  other is.
- **An update delivered by the Store itself** has not been watched. U was `Add-AppxPackage` over
  a sideloaded package, the closest a script can get before there is a listing.
- **Not covered by this run:** pwsh through the alias; a 100% screenshot beside the 150% one
  (M6 was skipped); signing out and in to see the startup task fire at login without a prior
  launch (g2 and M4 show it enabled, not firing); media control in the packaged app, and whether
  the docked bar reserves its screen edge.

## What this changes

- `RestartProven` in `src/Deskbar.App/App.xaml.cs` is true. The Store offer is only the question,
  without "Deskbar will close to update; open it from Start afterwards.", and the README says the
  app comes back by itself.
- The alias stays declared with `uap3:AppExecutionAlias`. The Task 6 review's open item, whether
  it needs `Subsystem="console"`, is closed by check i.
- `scripts/store-spike.ps1` and `scripts/store-smoke.ps1` no longer claim `DESKBAR_APPDATA`
  reaches the packaged app, and the spike's dump check is fixed.
