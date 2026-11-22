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

  The test output is written to a log file (a hidden desktop has no console you could see) and printed when the
  run ends. The exit code is dotnet test's.

.EXAMPLE
  pwsh scripts/test-hidden.ps1
  pwsh scripts/test-hidden.ps1 -Arguments 'tests/Deskbar.Windows.Tests --filter FullyQualifiedName~Tiling'
#>
param(
    # What `dotnet test` gets after the word "test". The default is the routine run minus the screen tests.
    [string]$Arguments = 'Deskbar.sln --filter "Category!=Integration&Category!=Screen"'
)

$ErrorActionPreference = 'Stop'

# Win32 calls PowerShell has no cmdlet for: create a desktop, start a process on it, wait for it.
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateDesktopW(string name, IntPtr device, IntPtr devmode, int flags, uint access, IntPtr security);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcessW(string application, string commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint flags, IntPtr environment, string currentDirectory, ref STARTUPINFO startup, out PROCESS_INFORMATION info);

    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr process, out int code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

    const uint GENERIC_ALL = 0x10000000, CREATE_NEW_CONSOLE = 0x10, INFINITE = 0xFFFFFFFF;

    // Starts commandLine on the desktop called `desktop` (created, or opened if it exists), waits for it, returns
    // its exit code. The desktop goes away by itself once this handle is closed and no thread uses it.
    public static int Run(string desktop, string commandLine, string directory)
    {
        var handle = CreateDesktopW(desktop, IntPtr.Zero, IntPtr.Zero, 0, GENERIC_ALL, IntPtr.Zero);
        if (handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        try
        {
            var startup = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = desktop };
            PROCESS_INFORMATION info;
            if (!CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, CREATE_NEW_CONSOLE, IntPtr.Zero, directory, ref startup, out info))
                throw new System.ComponentModel.Win32Exception();
            WaitForSingleObject(info.hProcess, INFINITE);
            int code;
            GetExitCodeProcess(info.hProcess, out code);
            CloseHandle(info.hThread);
            CloseHandle(info.hProcess);
            return code;
        }
        finally
        {
            CloseDesktop(handle);
        }
    }
}
'@

# Run from the repository root, whatever the caller's directory.
$root = Split-Path -Parent $PSScriptRoot
$log = Join-Path ([IO.Path]::GetTempPath()) "deskbar-test-hidden-$PID.log"

# cmd does the redirection, because the process on the hidden desktop has a console nobody can see.
$cmd = Join-Path $env:SystemRoot 'System32\cmd.exe'
$line = "`"$cmd`" /d /s /c `"dotnet test $Arguments > `"$log`" 2>&1`""

Write-Host "Running on a hidden desktop: dotnet test $Arguments"
$code = [HiddenDesktop]::Run('DeskbarTests', $line, $root)
if (Test-Path $log) { Get-Content $log; Remove-Item $log -ErrorAction SilentlyContinue }
exit $code
