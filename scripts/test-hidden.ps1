<#
.SYNOPSIS
  Runs `dotnet test` on a hidden Win32 desktop, so the tests' real windows never reach the screen you are
  working on and the Deskbar you are running never sees them.

.DESCRIPTION
  Deskbar.Windows.Tests opens hundreds of real top-level windows. On the desktop you work on, a running Deskbar
  treats them like any other window: in a tiled workspace it tiles each one and re-lays out your own windows
  around it, and the moved test windows then fail their own assertions at random (measured 2026-11-22, see the
  comment in tests/Deskbar.Windows.Tests/StaThread.cs).

  A Win32 desktop (CreateDesktop, the same mechanism as the secure logon screen and Sysinternals Desktops) is a
  separate set of windows inside your session. A process started on it creates its windows there, and a
  program on your desktop cannot enumerate them or hear their WinEvents, so Deskbar does not react. Nothing is
  drawn on your screen, no virtual desktop is added, and the bar does not change.

  The one thing a hidden desktop cannot do is be composited: only the desktop on screen is drawn, so the few
  tests that read real screen pixels are excluded here (Category=Screen) and run on CI, where they pass.

  A desktop is not free, and three rules below keep a run from costing the rest of the session anything
  (2026-11-25: while two sessions ran this script at once, the visible desktop could no longer create windows
  and Deskbar crashed at every start with "Not enough memory resources are available"):

  - Its heap is capped (-HeapKB). Every desktop has its own desktop heap, the memory its windows, menus and
    window classes live in, and CreateDesktop without a size gives it the size the visible desktop gets: the
    middle number of SharedSection in HKLM\...\Session Manager\SubSystems\Windows, 40 MB on Petre's machine
    since that night. The cap is cheap insurance, not the cure it was first taken for: the session's desktop
    heaps share one range of address space, but on this 64-bit machine that range is not the 48 MB that
    SessionViewSize says (it is set to 48 in the registry). Measured 2026-11-25 (Deskbar task 4888c75e): with
    Default's 40 MB heap in place, CreateDesktopEx gave a single new desktop every heap asked of it up to 3,900 MB.
    So an 8 MB test desktop does not crowd Default out. When window tests refuse windows "at random", with error 8
    from Dispatcher..ctor or HwndWrapper, look at the session's USER atom table first (the check below).
  - Each run gets a desktop of its own, named after this script's process. The old fixed name made a second
    session's run join the first one's desktop, and the two runs then shared, and filled, one heap.
  - Nothing outlives the run on its desktop. A desktop lives as long as any process on it holds it, and a
    `dotnet test` that builds starts the compiler server (VBCSCompiler) there if none is running yet. That
    server then compiles every session's builds and never goes idle, so it kept the hidden desktop, heap and
    all, alive for as long as anyone on the machine kept building (measured 2026-11-25 by the session on Deskbar
    task 68dc39e0). So the build runs first, here on the visible desktop, where the compiler server and MSBuild
    nodes belong, and the hidden desktop gets only `dotnet test --no-build`: vstest and testhost, which both exit
    with the run. On top of that, everything started on the hidden desktop is in a job object that kills it when
    the run ends, as a safety net for anything that would still be left.

  The test output is written to a log file (a hidden desktop has no console you could see) and printed when the
  run ends. The exit code is dotnet test's.

.EXAMPLE
  pwsh scripts/test-hidden.ps1
  pwsh scripts/test-hidden.ps1 -Arguments 'tests/Deskbar.Windows.Tests --filter FullyQualifiedName~Tiling'
#>
param(
    # What `dotnet test` gets after the word "test". The default is the routine run minus the screen tests.
    [string]$Arguments = 'Deskbar.sln --filter "Category!=Integration&Category!=Screen"',

    # The hidden desktop's heap, in KB. Measured 2026-11-25 with the whole Windows suite (1,512 tests): at
    # 1024 KB 92 tests failed with "Not enough memory resources", at 2048 KB and 4096 KB all passed. 8192 KB is
    # four times what the suite needs, and a fifth of the 40 MB an uncapped desktop took.
    [int]$HeapKB = 8192
)

$ErrorActionPreference = 'Stop'

# Win32 calls PowerShell has no cmdlet for: create a desktop, start a process on it inside a job, wait for it.
Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class HiddenDesktop
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb; public string lpReserved; public string lpDesktop; public string lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    // JOBOBJECT_EXTENDED_LIMIT_INFORMATION, laid out for a 64-bit process (pwsh on this machine is x64).
    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS { public ulong a, b, c, d, e, f; }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    // CreateDesktopExW rather than CreateDesktopW: only the Ex form takes a heap size. Without one, the desktop
    // gets the full interactive size from SharedSection, the same as the desktop on screen.
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateDesktopExW(string name, IntPtr device, IntPtr devmode, int flags, uint access, IntPtr security,
        uint heapSizeKB, IntPtr reserved);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcessW(string application, string commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint flags, IntPtr environment, string currentDirectory, ref STARTUPINFO startup, out PROCESS_INFORMATION info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateJobObjectW(IntPtr security, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll")] static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr process, out int code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

    // CREATE_NO_WINDOW, never CREATE_NEW_CONSOLE: a new console is handed to the default terminal, and on
    // Windows 11 that is Windows Terminal, which opened its window on the VISIBLE desktop for every run
    // (measured 2026-11-22: a CASCADIA_HOSTING_WINDOW_CLASS window appeared on Petre's screen, "i'm still
    // seeing cmd windows come up"). With no window, cmd gets a console nobody hosts, and the output still
    // reaches the log through cmd's own redirection.
    // CREATE_SUSPENDED: the process is put in the job before it runs a single instruction, so nothing it starts
    // can escape the job by being quick.
    const uint GENERIC_ALL = 0x10000000, CREATE_NO_WINDOW = 0x08000000, CREATE_SUSPENDED = 0x00000004, INFINITE = 0xFFFFFFFF;
    const int JobObjectExtendedLimitInformation = 9;
    const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    // Creates a desktop called `desktop` with a heap of heapKB, starts commandLine on it inside a job that kills
    // every process in it once the job's handle closes, waits for commandLine, returns its exit code. Closing the
    // job (here, or by Windows if this PowerShell dies first) ends whatever the run left behind, and with no
    // process left on it the desktop and its heap go away.
    public static int Run(string desktop, string commandLine, string directory, uint heapKB)
    {
        var handle = CreateDesktopExW(desktop, IntPtr.Zero, IntPtr.Zero, 0, GENERIC_ALL, IntPtr.Zero, heapKB, IntPtr.Zero);
        if (handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        var job = IntPtr.Zero;
        try
        {
            job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf(limits)))
                throw new System.ComponentModel.Win32Exception();

            var startup = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = desktop };
            PROCESS_INFORMATION info;
            if (!CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, CREATE_NO_WINDOW | CREATE_SUSPENDED,
                    IntPtr.Zero, directory, ref startup, out info))
                throw new System.ComponentModel.Win32Exception();
            if (!AssignProcessToJobObject(job, info.hProcess))
                throw new System.ComponentModel.Win32Exception();
            ResumeThread(info.hThread);
            WaitForSingleObject(info.hProcess, INFINITE);
            int code;
            GetExitCodeProcess(info.hProcess, out code);
            CloseHandle(info.hThread);
            CloseHandle(info.hProcess);
            return code;
        }
        finally
        {
            // The job first: it ends every leftover process, which is what lets the desktop go.
            if (job != IntPtr.Zero) CloseHandle(job);
            CloseDesktop(handle);
        }
    }
}
'@

# Run from the repository root, whatever the caller's directory.
$root = Split-Path -Parent $PSScriptRoot
$log = Join-Path ([IO.Path]::GetTempPath()) "deskbar-test-hidden-$PID.log"

# Stop at once if the session's USER atom table is full. Every window class name in the session comes from that one
# table, on every desktop, so a hidden desktop does not escape it, and when it is full RegisterClassEx fails with
# error 8, "Not enough memory resources", inside WPF's HwndWrapper. On 2026-11-25 that made every run fail 197 of
# 219 tests with a message that pointed at the desktop heap, and an hour went into measuring the wrong thing
# (Deskbar task 4d419051, docs/superpowers/notes/2026-11-25-user-atom-table-full.md). The check registers 100
# classes for a moment and releases them; only a full table stops the run.
$atoms = & (Join-Path $PSScriptRoot 'user-atoms.ps1') -Probe 100 -Top 0
if ($atoms -match '^FULL') {
    $atoms | Select-Object -First 4 | ForEach-Object { Write-Host $_ }
    Write-Host 'Not running the tests: they would fail creating windows. Sign out or reboot, then run again.'
    exit 1
}

# Build on the visible desktop first, so the compiler server and MSBuild nodes start here and not on the hidden
# desktop (see the description). `dotnet build` opens no windows. It gets what it shares with `dotnet test` out
# of $Arguments: the project or solution (the first word that is not an option) and the configuration (-c or
# --configuration). Skipped when the caller already passes --no-build.
$tokens = [regex]::Matches($Arguments, '"[^"]*"|\S+') | ForEach-Object Value
if ($tokens -notcontains '--no-build') {
    $build = @()
    $target = $tokens | Where-Object { -not $_.StartsWith('-') } | Select-Object -First 1
    # The first bare word counts as the target only when it comes before any option: in
    # `--filter X`, X is the filter's value, not a project.
    if ($target -and $tokens[0] -eq $target) { $build += $target.Trim('"') }
    $c = [Array]::FindIndex([string[]]$tokens, [Predicate[string]] { param($t) $t -in '-c', '--configuration' })
    if ($c -ge 0 -and $c + 1 -lt $tokens.Count) { $build += '-c', $tokens[$c + 1].Trim('"') }
    Write-Host "Building on this desktop: dotnet build $($build -join ' ')"
    Push-Location $root
    try { dotnet build @build -nologo -v q } finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

# cmd does the redirection, because the process on the hidden desktop has no console window at all.
# --no-build: the build above already ran, and a build on the hidden desktop is what used to leave the compiler
# server there. Added only when the caller did not pass it already.
# MSBUILDDISABLENODEREUSE=1: even with --no-build, dotnet test evaluates the solution through MSBuild with -m and
# starts worker nodes on the hidden desktop (measured 2026-11-25, six of them, by the session on task 68dc39e0).
# A Claude shell happens to have this variable set already; a terminal or VS Code does not, and there the nodes
# would stay on the hidden desktop for MSBuild's idle timeout, open to other builds, and the job would kill them
# under whichever build had attached. Set for the hidden run only, so the build above keeps reusing nodes.
$noBuild = if ($tokens -contains '--no-build') { '' } else { '--no-build ' }
$cmd = Join-Path $env:SystemRoot 'System32\cmd.exe'
$line = "`"$cmd`" /d /s /c `"set MSBUILDDISABLENODEREUSE=1&& dotnet test $noBuild$Arguments > `"$log`" 2>&1`""

# A desktop per run, never a shared name, so two sessions' runs never fill one heap together.
$desktop = "DeskbarTests-$PID"
Write-Host "Running on hidden desktop $desktop ($HeapKB KB heap): dotnet test $Arguments"
$code = [HiddenDesktop]::Run($desktop, $line, $root, [uint32]$HeapKB)
if (Test-Path $log) { Get-Content $log; Remove-Item $log -ErrorAction SilentlyContinue }
exit $code
