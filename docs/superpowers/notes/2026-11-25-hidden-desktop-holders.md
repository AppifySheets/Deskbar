# What keeps a hidden test desktop alive

Measured 2026-11-25 between 02:07 and 02:14 on Petre's machine, for Deskbar task 68dc39e0. The question: after the
desktop heap ran out on the night of 2026-11-24, while `scripts/test-hidden.ps1` runs were going, which processes
keep a hidden test desktop (and its heap) alive after the run that created it has ended?

The probe is `scripts/desktop-holders.ps1`. It walks the system handle list, duplicates every desktop handle of
every process the user can open, groups them by object with `CompareObjectHandles` and reads each desktop's name
and heap size. It also reads each process's startup desktop from its process parameters (`DesktopInfo`), so a
process started on a hidden desktop shows up even if it holds no handle.

## The incident, from the event log

- 01:38:38: Deskbar 2.5.414 fails with `Win32Exception (8): Not enough memory resources are available` and
  restarts its successor. From then until 01:54 every Deskbar start dies the same way in `HwndWrapper..ctor` (or
  in a XAML parse that wraps the same failure, or `H.NotifyIcon.DpiUtilities`).
- 01:39:26: a `testhost.exe` dies with the same error 8 while creating a window. Its windows were on the hidden
  desktop, Deskbar's on Default. Two different desktop heaps failed within a minute of each other.
- 01:55:53: system shutdown. The machine rebooted at 01:56:31. The Deskbar start at 01:58 that stayed up came
  after the reboot, so it does not show that `dotnet build-server shutdown` cleared anything.
- No Win32k or Resource-Exhaustion-Detector event was logged, and nothing points at commit: 64 GB of RAM.

Settings on this machine: `SharedSection=1024,40960,768` (each desktop in WinSta0 gets a 40 MB heap unless its
creator asks for less) and `SessionViewSize` 48. A desktop created by `CreateDesktopW`, as test-hidden.ps1 did,
gets the full 40 MB.

What ran out at 01:38 is still not measured. Default and the test desktop failing together points at something
the whole session shares rather than one desktop's heap. That part is not settled here.

## What holds a desktop after a run

1. A `dotnet test --no-build` run's desktop disappeared as soon as the run ended (`DeskbarTests`, 02:08:54).
   vstest and testhost exit with the run, and nothing else held it.
2. A run that builds starts the C# compiler server on the hidden desktop if none is running. Probe: `dotnet
   build` of a throwaway console project on a hidden desktop named `DeskProbeBuild` (2 MB heap). After the build
   ended, `VBCSCompiler` and its `conhost` still held `DeskProbeBuild`.
3. Builds from the visible desktop then use that same server. A `dotnet build -v:d` from a Claude shell on
   Default logged `CompilerServer: server processed compilation`, and no second VBCSCompiler started. Over the next
   four minutes the server on `DeskProbeBuild` used 230 s of CPU compiling other sessions' builds. The compiler
   server is shared by every build of one SDK on the machine, and each compile resets its idle timer. So a test run
   that happens to start it pins its hidden desktop for as long as anyone on the machine keeps building. (This one
   ended at about 02:13:50, 30 s after it was last seen working, so it did not idle out; what stopped it was not
   seen.)
4. MSBuild worker nodes hold their startup desktop too. Six idle nodes held Default; during runs, nodes held
   `DeskbarTests`. Even `dotnet test --no-build Deskbar.sln` starts six nodes, because `dotnet test` evaluates the
   solution through MSBuild with `-m`.
5. Those nodes exited with the run only because every Claude shell here has `MSBUILDDISABLENODEREUSE=1` (it is not
   in the user or machine environment), so they start with `/nodeReuse:false`. The same script started from a
   plain terminal or from VS Code would leave reusable nodes on the hidden desktop for MSBuild's idle timeout, and
   other builds would reuse them.
6. With the old fixed desktop name `DeskbarTests`, a held desktop keeps its name, so the next run opens it again.
   A holder therefore cost one 40 MB desktop, not one per run. A per-run name turns any holder into one leaked
   desktop per run.

## What follows for test-hidden.ps1

Build on the visible desktop, where the compiler server and MSBuild nodes belong. Run only `dotnet test
--no-build` on the hidden one, with node reuse off (`-nodeReuse:false` or `MSBUILDDISABLENODEREUSE=1` for that
process) so the result does not depend on who launched the script. A job object with kill-on-close catches
anything that still slips through. The session on task 87ae7ca5 is rewriting the script along these lines (build
first, `--no-build` on the hidden desktop, a capped heap and the job object); turning node reuse off was suggested
to it but was not in its draft when this note was written.

## Other things seen

- The Claude tool shells run in `WinSta0\Default`. EnumDesktops works there if its callback is declared
  `CharSet.Unicode`. With the default ANSI marshalling the names come back as single letters ("W", "S").
- Chromium-based programs (Chrome, Edge, WebView2, Beeper, Docker Desktop) each keep a
  `sbox_alternate_desktop_0x<pid>` desktop in a separate `Service-0x0-...$` window station, 768 KB each. Nine of
  them were alive.
