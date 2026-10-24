#Requires -Version 5.1
<#
.SYNOPSIS
    The Store package spike, carried to a Windows 11 machine as a bundle: installs a test-signed
    Deskbar 0.0.1 package, measures what can be measured from a script, simulates a Store update
    to 0.0.2 while it runs, asks the person five questions about what they see, removes everything
    and writes spike-report.md plus an evidence\ folder to bring back.
.DESCRIPTION
    WRITTEN FOR A MACHINE THAT IS NOT THE DEVELOPER'S: run it elevated, interactively, from the
    bundle folder that holds the two packages, the certificate and StorePackaging.psm1. It changes
    the machine while it runs and undoes each change at the end, however the run ended:
      - trusts deskbar-test.cer in Cert:\LocalMachine\TrustedPeople (removed again when this run
        added it);
      - installs Petre.Deskbar 0.0.1, then 0.0.2 over it, for the signed-in user (both removed);
      - creates one virtual desktop, SpikeWs (deleted through the app at the end);
      - starts Notepad and Character Map through the app to see whether they inherit its package
        identity (both stopped again).
    Every start of the app runs with DESKBAR_APPDATA pointing at %TEMP%\deskbar-spike\appdata, so
    the app reads and writes a scratch profile, never the real %APPDATA%\Deskbar. One start is not
    the script's: if Windows brings the app back by itself after the update, it decides that
    process's environment. So the real profile (the folder, the Run values and the Start menu
    shortcut) is snapshotted first, compared at the end, and anything the run deleted is put back.

    Why it exists: the spike in "Testing, and the spike that proves full trust" in
    docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md has to run on a machine
    with the Store, and the developer's machine runs the direct build all day. So everything a
    script can check is checked here, with its evidence, and the rest is a short list of
    questions; the person brings back one report instead of a description from memory.

    Why Windows PowerShell 5.1: under PowerShell 7 the Appx cmdlets load through a Windows
    PowerShell compatibility process started at import, so Invoke-CommandInDesktopPackage would
    start the app from THAT process, which never sees the DESKBAR_APPDATA this script sets. The
    script relaunches itself under powershell.exe when started from pwsh. The release workflow's
    smoke step runs under Windows PowerShell for the same reason.

    Why two launchers. The first start uses Invoke-CommandInDesktopPackage from this elevated
    process, as scripts/store-smoke.ps1 does in CI, so the spike also proves the assumption the
    smoke test rests on (that the started app inherits DESKBAR_APPDATA). That app runs elevated,
    though, and a real Store user's never does. So the start the person looks at, before the
    questions, is made as the signed-in user without elevation: a one-off scheduled task with a
    limited token runs the same cmdlet. If that start does not answer, the script says so and falls
    back to the elevated launcher. The report names which one each check saw, and the elevation
    of every process it describes.

    A failed check never stops the run: each is recorded PASS, FAIL, SKIP or INFO with its
    evidence, and the cleanup and the report are in the finally.
.PARAMETER Bundle
    The folder holding Deskbar-0.0.1-store.msix, Deskbar-0.0.2-store.msix and deskbar-test.cer.
    Defaults to this script's folder. evidence\ is created in it.
.PARAMETER Report
    Where the Markdown report goes. Defaults to spike-report.md in the bundle.
.PARAMETER DryRun
    Changes nothing: prints the machine facts, whether the preconditions hold and what the bundle
    holds (sizes, package versions, signer against deskbar-test.cer), then stops.
.EXAMPLE
    Set-ExecutionPolicy -Scope Process Bypass; .\store-spike.ps1
#>
[CmdletBinding()]
param(
    [string] $Bundle,
    [string] $Report,
    [switch] $DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Defaulted here, not in param(): measured, Windows PowerShell 5.1 started with -File leaves
# $PSScriptRoot empty while it evaluates parameter defaults.
if (-not $Bundle) { $Bundle = $PSScriptRoot }

# Under pwsh, start again under Windows PowerShell (the header says why) and pass the exit code on.
if ($PSVersionTable.PSEdition -eq 'Core') {
    $forward = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath, '-Bundle', $Bundle)
    if ($Report) { $forward += @('-Report', $Report) }
    if ($DryRun) { $forward += '-DryRun' }
    & (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') @forward
    exit $LASTEXITCODE
}

# Windows PowerShell started from a PowerShell 7 environment (a terminal or an editor) can inherit
# PSModulePath entries for PowerShell 7's modules ahead of its own, and then fails to load even
# built-in cmdlets (measured: Get-AuthenticodeSignature, CouldNotAutoloadMatchingModule). Only
# Windows PowerShell's own folders are kept, for this process and the shells it starts. Its folders
# are named WindowsPowerShell, so "\PowerShell\" matches PowerShell 7's alone.
$env:PSModulePath = (@($env:PSModulePath -split ';') | Where-Object { $_ -and $_ -notmatch '\\PowerShell\\' }) -join ';'

Import-Module (Join-Path $PSScriptRoot 'StorePackaging.psm1') -Force

# The few Win32 questions PowerShell cannot ask: which package a process runs in (the most direct
# identity check there is, beside tasklist /apps), whether it is elevated, the long form of a path
# (%TEMP% is often 8.3, the app's own paths are not), and per-monitor DPI so a screenshot is the
# real pixels and its file is named after the real scale. CharSet.Unicode, as the briefing says.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class SpikeNative {
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int GetPackageFullName(IntPtr process, ref uint length, StringBuilder name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern uint GetLongPathName(string shortPath, StringBuilder longPath, uint length);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    [StructLayout(LayoutKind.Sequential)] struct Point { public int X; public int Y; }
    const uint QueryLimited = 0x1000;

    // The package full name, "(none)" when the process has no package identity.
    public static string PackageOf(int pid) {
        IntPtr process = OpenProcess(QueryLimited, false, pid);
        if (process == IntPtr.Zero) return "(cannot open process, error " + Marshal.GetLastWin32Error() + ")";
        try {
            uint length = 0;
            int code = GetPackageFullName(process, ref length, null);
            if (code == 15700) return "(none)";
            StringBuilder name = new StringBuilder((int)length + 1);
            code = GetPackageFullName(process, ref length, name);
            return code == 0 ? name.ToString() : "(error " + code + ")";
        } finally { CloseHandle(process); }
    }

    // TokenElevation is information class 20; TOKEN_QUERY is 8.
    public static string ElevationOf(int pid) {
        IntPtr process = OpenProcess(QueryLimited, false, pid);
        if (process == IntPtr.Zero) return "unknown";
        try {
            IntPtr token;
            if (!OpenProcessToken(process, 8, out token)) return "unknown";
            try {
                int elevated, returned;
                if (!GetTokenInformation(token, 20, out elevated, 4, out returned)) return "unknown";
                return elevated != 0 ? "elevated" : "not elevated";
            } finally { CloseHandle(token); }
        } finally { CloseHandle(process); }
    }

    public static string LongPath(string path) {
        StringBuilder buffer = new StringBuilder(1024);
        return GetLongPathName(path, buffer, 1024) > 0 ? buffer.ToString() : path;
    }

    // -4 is DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2. Without it Windows scales the numbers and
    // a screenshot of a 150% screen comes out as a cropped, blurred 100% one.
    public static bool MakeDpiAware() { return SetProcessDpiAwarenessContext(new IntPtr(-4)); }

    // The primary monitor's effective scale in percent, read live (a change in Settings shows).
    public static int PrimaryScalePercent() {
        Point origin = new Point();
        uint x, y;
        return GetDpiForMonitor(MonitorFromPoint(origin, 1), 0, out x, out y) == 0 ? (int)(x * 100 / 96) : 0;
    }
}
'@
$dpiAware = [SpikeNative]::MakeDpiAware()

# Every path this script hands the app or compares with the app's own paths is in long form.
$Bundle = [SpikeNative]::LongPath((Resolve-Path -LiteralPath $Bundle).ProviderPath)
if (-not $Report) { $Report = Join-Path $Bundle 'spike-report.md' }
$Report = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Report)
$evidence = Join-Path $Bundle 'evidence'
$oldPackage = Join-Path $Bundle 'Deskbar-0.0.1-store.msix'
$newPackage = Join-Path $Bundle 'Deskbar-0.0.2-store.msix'
$certificateFile = Join-Path $Bundle 'deskbar-test.cer'
$localAppData = [SpikeNative]::LongPath($env:LOCALAPPDATA)
$roamingAppData = [SpikeNative]::LongPath($env:APPDATA)
$tempDir = [SpikeNative]::LongPath($env:TEMP)
$scratchRoot = Join-Path $tempDir 'deskbar-spike\appdata'
$scratchState = Join-Path $scratchRoot 'Deskbar'
$traceLog = Join-Path $tempDir 'deskbar-trace.log'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$startShortcut = Join-Path $roamingAppData 'Microsoft\Windows\Start Menu\Programs\Deskbar.lnk'
$realState = Join-Path $roamingAppData 'Deskbar'
$sessionId = (Get-Process -Id $PID).SessionId
$taskName = 'Deskbar spike launch (removed by store-spike.ps1)'

# ---------------------------------------------------------------------------------------------
# Recording. Each check is one object; the console gets it as it happens, the report at the end.
# ---------------------------------------------------------------------------------------------
$checks = New-Object System.Collections.Generic.List[object]
$manual = New-Object System.Collections.Generic.List[object]
$update = [ordered]@{}

function Add-Check([string] $Id, [string] $Title, [string] $Result, [string[]] $Evidence) {
    $checks.Add([pscustomobject]@{ Id = $Id; Title = $Title; Result = $Result; Evidence = @($Evidence) })
    $colour = @{ PASS = 'Green'; FAIL = 'Red'; SKIP = 'Yellow'; INFO = 'Cyan' }[$Result]
    Write-Host "[$Result] $Id. $Title" -ForegroundColor $colour
    @($Evidence) | ForEach-Object { Write-Host "    $_" }
}

# Runs one check. The body returns @{ Result; Evidence }; a throw is recorded as FAIL with its
# message, so one broken check never stops the ones after it.
function Invoke-Check([string] $Id, [string] $Title, [scriptblock] $Body) {
    try {
        $outcome = & $Body
        Add-Check $Id $Title $outcome.Result $outcome.Evidence
    }
    catch { Add-Check $Id $Title 'FAIL' @("threw: $_") }
}

function Write-Step([string] $Text) { Write-Host ''; Write-Host "== $Text" -ForegroundColor White }

# ---------------------------------------------------------------------------------------------
# Running things and looking at processes.
# ---------------------------------------------------------------------------------------------

# A native command's exit code, output lines and duration. Continue is scoped here because under
# Windows PowerShell with a Stop preference a native command's stderr becomes a terminating error
# before its exit code can be read, and the CLI's "not running yet" arrives on stderr.
function Invoke-Native([string] $File, [string[]] $Arguments) {
    $ErrorActionPreference = 'Continue'
    # Cleared: $LASTEXITCODE is global, and a command that never started would inherit an old 0.
    $global:LASTEXITCODE = $null
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $all = @(& $File @Arguments 2>&1)
    [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Stdout   = [string[]]@($all | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] } | ForEach-Object { "$_" })
        Text     = ($all | ForEach-Object { "$_" }) -join "`n"
        Ms       = $watch.ElapsedMilliseconds
    }
}

# `deskbar <arguments>` through the execution alias, the command line a Store user has.
function Invoke-Deskbar([string[]] $Arguments) { Invoke-Native $paths.Alias $Arguments }

# A Windows PowerShell script run as a child process, encoded so no quoting survives to bite.
function Invoke-EncodedShell([string] $Shell, [string] $Script) {
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($Script))
    Invoke-Native $Shell @('-NoProfile', '-NonInteractive', '-EncodedCommand', $encoded)
}

function ConvertTo-PsLiteral([string] $Text) { "'" + $Text.Replace("'", "''") + "'" }

# A field of a parsed reply, or $null when the reply lacks it: strict mode would throw instead, and
# the check would lose the evidence that says what the reply did contain.
function Get-Field($Object, [string] $Name) {
    if ($null -ne $Object -and $Object.PSObject.Properties[$Name]) { $Object.PSObject.Properties[$Name].Value } else { $null }
}

# A reply's JSON, or $null when it is not JSON. Through a variable, so a JSON array comes out as
# its elements: Windows PowerShell's ConvertFrom-Json writes an array as ONE pipeline object.
function ConvertFrom-Reply($Reply) {
    # A bare return, not "return $null": @() around this call would count a $null as one element.
    try { $parsed = ConvertFrom-Json (($Reply.Stdout -join "`n").Trim()) } catch { return }
    $parsed
}

# tasklist /apps lists every process that runs inside a package, with the package's full name.
# No header (/nh), because the header row is localised.
function Get-PackagedProcessList {
    $ErrorActionPreference = 'Continue'
    @(& tasklist.exe /apps /fo csv /nh 2>$null | ConvertFrom-Csv -Header Image, Pid, Memory, Package)
}

# One line saying everything that matters about a process for the identity questions.
function Format-Process([int] $Id) {
    $process = Get-Process -Id $Id -ErrorAction SilentlyContinue
    $cim = Get-CimInstance Win32_Process -Filter "ProcessId=$Id" -ErrorAction SilentlyContinue
    $name = if ($process) { $process.ProcessName } else { '(gone)' }
    $path = if ($process -and $process.Path) { $process.Path } else { '?' }
    $parent = '?'
    if ($cim) {
        $parentProcess = Get-Process -Id $cim.ParentProcessId -ErrorAction SilentlyContinue
        $parent = "$($cim.ParentProcessId) $(if ($parentProcess) { $parentProcess.ProcessName } else { '(gone)' })"
    }
    "pid $Id $name | path $path | package $([SpikeNative]::PackageOf($Id)) | $([SpikeNative]::ElevationOf($Id)) | parent $parent"
}

# Every Deskbar or deskbar-cli running from an installed copy of the package, any version.
function Get-PackagedDeskbar([string] $InstallDir = (Join-Path $env:ProgramFiles 'WindowsApps\Petre.Deskbar_')) {
    @(Get-Process -Name Deskbar, deskbar-cli -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($InstallDir, [StringComparison]::OrdinalIgnoreCase) })
}

function Stop-PackagedDeskbar {
    Get-PackagedDeskbar | ForEach-Object {
        Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
        $_.WaitForExit(10000) | Out-Null
    }
}

# Anything Deskbar-like already running in this login session: a direct build, a 1.x TaskSpaces,
# an installed copy. It would own the control pipe and the Win+Ctrl+Tab hotkey, and answer instead.
function Get-SessionDeskbar {
    @(Get-Process | Where-Object { $_.SessionId -eq $sessionId -and $_.ProcessName -match '^(Deskbar|deskbar-cli|TaskSpaces)' })
}

# Asks `deskbar version` until it answers or the time is up. Returns the parsed reply or $null.
function Wait-Answer([int] $Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $reply = Invoke-Deskbar @('version')
        if ($reply.ExitCode -eq 0) { return [pscustomobject]@{ Raw = $reply; Json = ConvertFrom-Reply $reply } }
        Start-Sleep -Seconds 1
    }
    $null
}

# A path the app reported or writes to, looked up where it really landed. A packaged app's writes
# under AppData that the manifest does not exclude from virtualisation go to the package's private
# copy, %LOCALAPPDATA%\Packages\<family>\LocalCache\Local or ...\Roaming, which outside processes
# see at that path and the app sees at the original one. Which of the two holds a file is itself
# a finding, so both are returned, existing ones only.
function Find-AppFile([string] $Path) {
    $candidates = @($Path)
    $private = Join-Path $localAppData "Packages\$($paths.FamilyName)\LocalCache"
    if ($Path.StartsWith("$localAppData\", [StringComparison]::OrdinalIgnoreCase)) {
        $candidates += Join-Path $private ('Local\' + $Path.Substring($localAppData.Length + 1))
    }
    elseif ($Path.StartsWith("$roamingAppData\", [StringComparison]::OrdinalIgnoreCase)) {
        $candidates += Join-Path $private ('Roaming\' + $Path.Substring($roamingAppData.Length + 1))
    }
    @($candidates | Where-Object { Test-Path -LiteralPath $_ })
}

# Copies an app file into evidence\ under a name of our choosing; returns where it was found.
function Save-AppFile([string] $Path, [string] $Name) {
    $found = @(Find-AppFile $Path)
    if ($found.Count -gt 0) { Copy-Item -LiteralPath $found[0] -Destination (Join-Path $evidence $Name) -Force }
    $found
}

# The primary screen, at its real pixels, named after the scale it was taken at.
function Save-Screen([string] $Prefix) {
    Add-Type -AssemblyName System.Drawing, System.Windows.Forms
    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $scale = [SpikeNative]::PrimaryScalePercent()
    $file = Join-Path $evidence "$Prefix-$scale.png"
    $bitmap = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
        $bitmap.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $graphics.Dispose(); $bitmap.Dispose() }
    "$file ($($bounds.Width)x$($bounds.Height) at $scale%)"
}

# ---------------------------------------------------------------------------------------------
# Starting the app inside its package.
# ---------------------------------------------------------------------------------------------

# From this elevated process, as store-smoke.ps1 does: the variable is set in this process only for
# the call, and the cmdlet's child is expected to inherit it (check d measures that).
function Start-Elevated([string] $Exe) {
    $had = Test-Path -LiteralPath Env:DESKBAR_APPDATA
    $previous = $env:DESKBAR_APPDATA
    $env:DESKBAR_APPDATA = $scratchRoot
    try { Invoke-CommandInDesktopPackage -PackageFamilyName $paths.FamilyName -AppId $paths.AppId -Command $Exe }
    finally {
        if ($had) { $env:DESKBAR_APPDATA = $previous } else { Remove-Item -LiteralPath Env:DESKBAR_APPDATA -ErrorAction SilentlyContinue }
    }
}

# As the signed-in user with a limited token: a one-off scheduled task (run only while the user is
# signed in, so it needs no password and runs on their desktop) runs Windows PowerShell, which sets
# the variable and calls the same cmdlet. Its own output, including whether it saw itself as an
# administrator, goes to evidence\launch-as-user.log. The task is removed before this returns.
function Start-AsUser([string] $Exe) {
    $log = Join-Path $evidence 'launch-as-user.log'
    Remove-Item -LiteralPath $log -ErrorAction SilentlyContinue
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes((Get-AsUserScript $Exe $log)))
    $action = New-ScheduledTaskAction -Execute (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') `
        -Argument "-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -EncodedCommand $encoded"
    $principal = New-ScheduledTaskPrincipal -UserId $facts.ShellUser -LogonType Interactive -RunLevel Limited
    # No time limit: the task ends when its PowerShell exits, and a limit could end the app with it.
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)
    Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Settings $settings -Force | Out-Null
    try {
        Start-ScheduledTask -TaskName $taskName
        $deadline = (Get-Date).AddSeconds(60)
        while ((Get-Date) -lt $deadline -and -not ((Test-Path -LiteralPath $log) -and (Get-Content -LiteralPath $log -Raw) -match 'started|failed')) {
            Start-Sleep -Milliseconds 500
        }
    }
    finally { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $log) { (Get-Content -LiteralPath $log) -join ' / ' } else { '(the task wrote no log)' }
}

# The script the scheduled task runs, apart so its text can be checked without registering a task.
function Get-AsUserScript([string] $Exe, [string] $Log) {
    $inner = @"
`$ErrorActionPreference = 'Stop'
`$env:DESKBAR_APPDATA = $(ConvertTo-PsLiteral $scratchRoot)
'admin token: ' + ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
try {
    Invoke-CommandInDesktopPackage -PackageFamilyName $(ConvertTo-PsLiteral $paths.FamilyName) -AppId $(ConvertTo-PsLiteral $paths.AppId) -Command $(ConvertTo-PsLiteral $Exe)
    'started'
} catch { 'failed: ' + `$_ }
"@
    "& { $inner } *>&1 | Out-File -LiteralPath $(ConvertTo-PsLiteral $Log) -Encoding utf8"
}

# ---------------------------------------------------------------------------------------------
# The bundle and the machine.
# ---------------------------------------------------------------------------------------------

# The version a package's manifest carries, read from the .msix without installing it.
function Get-PackageVersion([string] $Package) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($Package)
    try {
        $reader = New-Object System.IO.StreamReader ($zip.GetEntry('AppxManifest.xml').Open())
        try { Get-StoreManifestVersion -ManifestText $reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    finally { $zip.Dispose() }
}

# What the bundle holds and whether it fits together: both packages signed by the certificate in
# deskbar-test.cer (or Windows refuses the install after trusting the wrong one), the second newer.
function Test-Bundle {
    $lines = New-Object System.Collections.Generic.List[string]
    $problems = New-Object System.Collections.Generic.List[string]
    $thumbprint = $null
    if (Test-Path -LiteralPath $certificateFile) {
        $certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 $certificateFile
        $thumbprint = $certificate.Thumbprint
        $lines.Add("deskbar-test.cer: $((Get-Item -LiteralPath $certificateFile).Length) bytes, $($certificate.Subject), thumbprint $thumbprint, valid until $($certificate.NotAfter.ToString('yyyy-MM-dd'))")
        if ($certificate.NotAfter -lt (Get-Date)) { $problems.Add('deskbar-test.cer has expired.') }
    }
    else { $problems.Add("deskbar-test.cer is missing from $Bundle.") }

    $versions = @{}
    foreach ($package in @($oldPackage, $newPackage)) {
        $leaf = Split-Path -Leaf $package
        if (-not (Test-Path -LiteralPath $package)) { $problems.Add("$leaf is missing from $Bundle."); continue }
        $version = Get-PackageVersion $package
        $versions[$leaf] = $version
        $signer = (Get-AuthenticodeSignature -LiteralPath $package).SignerCertificate
        $signedBy = if ($signer) { $signer.Thumbprint } else { '(unsigned)' }
        $lines.Add("${leaf}: $((Get-Item -LiteralPath $package).Length) bytes, package version $version, signed by $signedBy")
        if ($signedBy -ne $thumbprint) { $problems.Add("$leaf is not signed by the certificate in deskbar-test.cer.") }
    }
    if ($versions.Count -eq 2 -and [version]$versions[(Split-Path -Leaf $newPackage)] -le [version]$versions[(Split-Path -Leaf $oldPackage)]) {
        $problems.Add('The 0.0.2 package is not newer than the 0.0.1 one, so it would not install as an update.')
    }
    [pscustomobject]@{ Lines = $lines; Problems = $problems; Thumbprint = $thumbprint; Versions = $versions }
}

function Get-MachineFacts {
    $os = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $elevated = ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    # The owner of this session's Explorer is the person at the screen. Reading it needs elevation,
    # and without it -IncludeUserName throws outright, which a dry run must survive.
    $shell = try { Get-Process -Name explorer -IncludeUserName -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $sessionId } | Select-Object -First 1 } catch { $null }
    [pscustomobject]@{
        Caption    = (Get-CimInstance Win32_OperatingSystem).Caption
        Build      = [int]$os.CurrentBuild
        Winver     = "$($os.DisplayVersion) (OS build $($os.CurrentBuild).$($os.UBR))"
        User       = $identity.Name
        ShellUser  = if ($shell -and $shell.UserName) { $shell.UserName } else { '(unknown)' }
        Elevated   = $elevated
        PowerShell = "$($PSVersionTable.PSVersion) $($PSVersionTable.PSEdition)"
        Store      = [bool](Get-AppxPackage -Name Microsoft.WindowsStore -ErrorAction SilentlyContinue)
        Screens    = "$(@([System.Windows.Forms.Screen]::AllScreens).Count) screen(s), primary at $([SpikeNative]::PrimaryScalePercent())%, DPI-aware script: $dpiAware"
    }
}

# What the real profile looks like, so the end can say whether the run touched it, and undo it.
function Get-RealProfile {
    $run = Get-ItemProperty -LiteralPath $runKey -ErrorAction SilentlyContinue
    $value = { param($name) if ($run -and $run.PSObject.Properties[$name]) { [string]$run.PSObject.Properties[$name].Value } else { $null } }
    # The folder itself too: deleting a file changes its time and no remaining file's.
    $files = @(if (Test-Path -LiteralPath $realState) { Get-Item -LiteralPath $realState; Get-ChildItem -LiteralPath $realState -Recurse -Force -ErrorAction SilentlyContinue })
    [pscustomobject]@{
        StateExists   = Test-Path -LiteralPath $realState
        # In ticks, compared exactly (Measure-Object would round them through a double); 0 when
        # there is no folder.
        StateNewest   = [long](@($files | ForEach-Object { $_.LastWriteTimeUtc.Ticks }) + [long]0 | Sort-Object -Descending | Select-Object -First 1)
        StateFiles    = @($files | ForEach-Object { $_.FullName })
        RunDeskbar    = & $value 'Deskbar'
        RunTaskSpaces = & $value 'TaskSpaces'
        Shortcut      = Test-Path -LiteralPath $startShortcut
    }
}

Add-Type -AssemblyName System.Windows.Forms
$facts = Get-MachineFacts
$bundleState = Test-Bundle

# The preconditions, all listed at once rather than one per attempt.
$problems = New-Object System.Collections.Generic.List[string]
$bundleState.Problems | ForEach-Object { $problems.Add($_) }
if (-not $facts.Elevated) { $problems.Add('Run it elevated: trusting the test certificate machine-wide needs an administrator.') }
if ($facts.Build -lt 22000) { $problems.Add("This is build $($facts.Build); the package needs Windows 11 (build 22000 or later).") }
if ($facts.ShellUser -ne '(unknown)' -and $facts.ShellUser -ne $facts.User) {
    # A standard user who elevated with another account would install the package for THAT account.
    $problems.Add("This PowerShell runs as $($facts.User) but the desktop belongs to $($facts.ShellUser); run it elevated as the signed-in user.")
}
Get-SessionDeskbar | ForEach-Object {
    $problems.Add("$($_.ProcessName) (process $($_.Id), $(if ($_.Path) { $_.Path } else { 'path unreadable' })) is running in this session; exit it first.")
}
Get-AppxPackage -Name Petre.Deskbar -ErrorAction SilentlyContinue | ForEach-Object {
    $problems.Add("$($_.PackageFullName) is already installed; remove it first (Get-AppxPackage Petre.Deskbar | Remove-AppxPackage).")
}

if ($DryRun) {
    Write-Step 'Dry run: nothing is changed'
    $facts | Format-List | Out-String | Write-Host
    $bundleState.Lines | ForEach-Object { Write-Host "  $_" }
    Write-Host "  scratch profile would be: $scratchRoot"
    Write-Host "  report would be: $Report"
    if ($problems.Count -eq 0) { Write-Host 'Preconditions hold: a real run would start.' -ForegroundColor Green }
    else { Write-Host 'A real run would refuse:' -ForegroundColor Yellow; $problems | ForEach-Object { Write-Host "  - $_" -ForegroundColor Yellow } }
    exit ([int]($bundleState.Problems.Count -gt 0))
}

if ($problems.Count -gt 0) {
    $problems | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    throw 'store-spike.ps1 refused to start; nothing was changed.'
}

$oldVersion = $bundleState.Versions[(Split-Path -Leaf $oldPackage)]
$newVersion = $bundleState.Versions[(Split-Path -Leaf $newPackage)]
$paths = Get-StoreInstallPaths -Version $oldVersion
$newPaths = Get-StoreInstallPaths -Version $newVersion

# A previous run's evidence is kept beside, not overwritten.
if (Test-Path -LiteralPath $evidence) { Rename-Item -LiteralPath $evidence -NewName "evidence-$(Get-Date -Format yyyyMMdd-HHmmss)" }
New-Item -ItemType Directory -Path $evidence | Out-Null
Start-Transcript -LiteralPath (Join-Path $evidence 'console.log') | Out-Null

$trustAdded = $false
$installAttempted = $false
$answered = $false
$before = Get-RealProfile
# A copy of the direct build's Start shortcut, so the end can put it back if the run deleted it.
if ($before.Shortcut) { Copy-Item -LiteralPath $startShortcut -Destination (Join-Path $evidence 'Deskbar.lnk.backup') }
$traceOffset = if (Test-Path -LiteralPath $traceLog) { (Get-Item -LiteralPath $traceLog).Length } else { 0 }

try {
    Write-Step 'Trust and install'
    Invoke-Check 'T' 'deskbar-test.cer trusted in Cert:\LocalMachine\TrustedPeople' {
        if (Test-Path -LiteralPath "Cert:\LocalMachine\TrustedPeople\$($bundleState.Thumbprint)") {
            @{ Result = 'INFO'; Evidence = @('already trusted before this run; it stays') }
        }
        else {
            Import-Certificate -FilePath $certificateFile -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
            $script:trustAdded = $true
            @{ Result = 'PASS'; Evidence = @("imported $($bundleState.Thumbprint); removed again at the end") }
        }
    }

    $installAttempted = $true
    Invoke-Check 'I' "Add-AppxPackage $(Split-Path -Leaf $oldPackage)" {
        Add-AppxPackage -Path $oldPackage
        $installed = @(Get-AppxPackage -Name Petre.Deskbar)
        @{
            Result   = if ($installed.Count -eq 1 -and $installed[0].InstallLocation -eq $paths.InstallDir -and (Test-Path -LiteralPath $paths.Alias)) { 'PASS' } else { 'FAIL' }
            Evidence = @($installed | ForEach-Object { "Get-AppxPackage: $($_.PackageFullName), family $($_.PackageFamilyName), at $($_.InstallLocation), signature $($_.SignatureKind)" }) +
                @("expected at $($paths.InstallDir); alias $($paths.Alias) exists: $(Test-Path -LiteralPath $paths.Alias)")
        }
    }

    Write-Step 'First start, elevated, through Invoke-CommandInDesktopPackage'
    if (Test-Path -LiteralPath (Split-Path -Parent $scratchRoot)) { Remove-Item -LiteralPath (Split-Path -Parent $scratchRoot) -Recurse -Force }
    New-Item -ItemType Directory -Path $scratchState -Force | Out-Null
    New-Item -ItemType File -Path (Join-Path $scratchState 'trace.on') | Out-Null
    Invoke-Check 'L' 'the installed app starts and answers `deskbar version` with package identity' {
        Start-Elevated $paths.Exe
        $reply = Wait-Answer 60
        if ($null -eq $reply) { return @{ Result = 'FAIL'; Evidence = @('no answer within 60 s') + @(Get-PackagedDeskbar | ForEach-Object { Format-Process $_.Id }) } }
        $script:answered = $true
        $verdict = try { Assert-StoreVersionReply -Reply $reply.Raw.Stdout -Version $oldVersion | Out-Null; "packaged and $oldVersion, as expected" } catch { "verdict: $_" }
        $underWindowsApps = "$(Get-Field $reply.Json 'exe')".StartsWith((Join-Path $env:ProgramFiles 'WindowsApps'), [StringComparison]::OrdinalIgnoreCase)
        $listed = @(Get-PackagedProcessList | Where-Object { $_.Package -like 'Petre.Deskbar_*' } | ForEach-Object { "tasklist /apps: $($_.Image) pid $($_.Pid) in $($_.Package)" })
        @{
            Result   = if ((Get-Field $reply.Json 'packaged') -eq $true -and $underWindowsApps -and $listed.Count -gt 0) { 'PASS' } else { 'FAIL' }
            Evidence = @("deskbar version: $(($reply.Raw.Stdout -join ' ').Trim())", $verdict) + $listed +
                @(Get-PackagedDeskbar | ForEach-Object { Format-Process $_.Id })
        }
    }
    # Give the bar a moment to lay itself out before it is photographed.
    Start-Sleep -Seconds 5

    Write-Step 'Automated checks'
    $skip = @{ Result = 'SKIP'; Evidence = @('the app did not answer, so there is nothing to ask') }
    Invoke-Check 'a' '`deskbar workspaces` answers JSON' {
        if (-not $answered) { return $skip }
        $reply = Invoke-Deskbar @('workspaces')
        $parsed = @(ConvertFrom-Reply $reply)
        @{ Result = if ($reply.ExitCode -eq 0 -and $parsed.Count -gt 0) { 'PASS' } else { 'FAIL' }; Evidence = @("exit $($reply.ExitCode), $($reply.Ms) ms", $reply.Text) }
    }
    Invoke-Check 'b' '`deskbar create SpikeWs` and `deskbar workspaces` lists it' {
        if (-not $answered) { return $skip }
        $create = Invoke-Deskbar @('create', 'SpikeWs')
        $list = Invoke-Deskbar @('workspaces')
        $names = @(ConvertFrom-Reply $list | ForEach-Object { Get-Field $_ 'name' })
        @{ Result = if ($create.ExitCode -eq 0 -and $names -contains 'SpikeWs') { 'PASS' } else { 'FAIL' }
            Evidence = @("create: exit $($create.ExitCode) $($create.Text)", "workspaces: $($names -join ', ')") }
    }

    # A child the app starts must NOT carry the app's package identity (the breakaway starter), or
    # Windows ends it when it services or removes the package. Notepad is ambiguous on Windows 11,
    # where it is itself a Store app and so always shows SOME package; Character Map is a plain
    # Win32 exe, so for it any package at all means the breakaway failed.
    foreach ($probe in @(@{ Id = 'c'; Exe = 'notepad.exe'; Name = 'notepad' }, @{ Id = 'c2'; Exe = 'charmap.exe'; Name = 'charmap' })) {
        Invoke-Check $probe.Id "``deskbar start SpikeWs -- $($probe.Exe)`` starts it outside Deskbar's package" {
            if (-not $answered) { return $skip }
            $existing = @(Get-Process -Name $probe.Name -ErrorAction SilentlyContinue | ForEach-Object Id)
            $start = Invoke-Deskbar @('start', 'SpikeWs', '--', $probe.Exe)
            Start-Sleep -Seconds 5
            $started = @(Get-Process -Name $probe.Name -ErrorAction SilentlyContinue | Where-Object { $existing -notcontains $_.Id })
            $underDeskbar = @(Get-PackagedProcessList | Where-Object { $_.Package -like 'Petre.Deskbar_*' -and $_.Image -like "$($probe.Name)*" })
            $inherited = @($started | Where-Object { [SpikeNative]::PackageOf($_.Id) -like 'Petre.Deskbar_*' })
            $evidenceLines = @("start: exit $($start.ExitCode) $($start.Text)") + @($started | ForEach-Object { Format-Process $_.Id }) +
                @($underDeskbar | ForEach-Object { "tasklist /apps lists $($_.Image) pid $($_.Pid) under $($_.Package)" })
            $started | ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
            @{ Result = if ($start.ExitCode -eq 0 -and $started.Count -gt 0 -and $underDeskbar.Count -eq 0 -and $inherited.Count -eq 0) { 'PASS' } else { 'FAIL' }; Evidence = $evidenceLines }
        }
    }

    Invoke-Check 'd' 'the scratch state folder: state.json with WrittenBy, the shim on the alias, no takeover marker' {
        if (-not $answered) { return $skip }
        $state = @(Save-AppFile (Join-Path $scratchState 'state.json') 'state.json')
        $shim = @(Save-AppFile (Join-Path $scratchState 'deskbar.cmd') 'deskbar.cmd')
        $marker = @(Find-AppFile (Join-Path $scratchState 'packaged-first-start-done'))
        $hasWrittenBy = $state.Count -gt 0 -and (Get-Content -LiteralPath $state[0] -Raw) -match '"WrittenBy"'
        $shimText = if ($shim.Count -gt 0) { Get-Content -LiteralPath $shim[0] -Raw } else { '' }
        $shimGood = $shimText -match [regex]::Escape('Microsoft\WindowsApps\deskbar.exe') -and $shimText -notmatch ' ctl'
        @{ Result = if ($hasWrittenBy -and $shimGood -and $marker.Count -eq 0) { 'PASS' } else { 'FAIL' }
            Evidence = @("state.json found at: $(if ($state) { $state -join ', ' } else { 'nowhere' }); WrittenBy present: $hasWrittenBy",
                "deskbar.cmd found at: $(if ($shim) { $shim -join ', ' } else { 'nowhere' }); names the alias without ctl: $shimGood",
                "packaged-first-start-done found at: $(if ($marker) { $marker -join ', ' } else { 'nowhere (right: the root is overridden)' })") + @($shimText -split "`r?`n" | Where-Object { $_ }) }
    }

    Invoke-Check 'g' 'the Start menu has the Deskbar entry' {
        $entry = @(Get-StartApps | Where-Object { $_.Name -eq 'Deskbar' })
        @{ Result = if (@($entry | Where-Object { $_.AppID -eq "$($paths.FamilyName)!$($paths.AppId)" }).Count -gt 0) { 'PASS' } else { 'FAIL' }
            Evidence = @($entry | ForEach-Object { "Get-StartApps: $($_.Name) -> $($_.AppID)" }) + @('the startup task state is read by hand (manual question 4)') }
    }
    # Where Windows keeps the startup task's state, dumped as found: unverified, for comparing with
    # what Settings and Manage show, not as a verdict.
    Invoke-Check 'g2' 'the startup task registry state (raw, for comparison with manual question 4)' {
        $key = "HKCU:\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData\$($paths.FamilyName)\Deskbar"
        $values = Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue
        @{ Result = 'INFO'; Evidence = @(if ($values) { $values.PSObject.Properties | Where-Object { $_.Name -notlike 'PS*' } | ForEach-Object { "$($_.Name) = $($_.Value)" } } else { "no key at $key" }) }
    }

    Invoke-Check 'h' '`deskbar dump` writes a dump (copied to evidence\), and the trace is on' {
        if (-not $answered) { return $skip }
        $reply = Invoke-Deskbar @('dump')
        $file = Get-Field (ConvertFrom-Reply $reply) 'file'
        $found = if ($file) { @(Save-AppFile $file 'dump.txt') } else { @() }
        $header = if ($found) { @(Get-Content -LiteralPath $found[0] | Where-Object { $_ -match '^\s+(version|entitlement|exe)\s' } | ForEach-Object { $_.Trim() }) } else { @() }
        $traces = @(Find-AppFile $traceLog)
        @{ Result = if ($found.Count -gt 0 -and $header.Count -eq 3) { 'PASS' } else { 'FAIL' }
            Evidence = @("dump: exit $($reply.ExitCode), says $file, found at: $(if ($found) { $found -join ', ' } else { 'nowhere' })") + $header +
                @("trace log found at: $(if ($traces) { $traces -join ', ' } else { 'nowhere' }) (copied at the end)") }
    }

    # cmd and both PowerShells wait for a console exe and keep its output and exit code, and lose
    # both for a GUI one. The alias runs deskbar-cli.exe, a console exe, so this should pass as is;
    # if it does not, the manifest needs uap10:Subsystem="console" on the alias. The timings show
    # whether the shell waited. !ERRORLEVEL! with /v:on, because %ERRORLEVEL% on the same line is
    # expanded before deskbar runs.
    Invoke-Check 'i' 'the alias from cmd, Windows PowerShell and pwsh keeps its output and exit code' {
        if (-not $answered) { return $skip }
        $where = Invoke-Native "$env:SystemRoot\System32\where.exe" @('deskbar')
        $shells = New-Object System.Collections.Generic.List[object]
        $shells.Add(@{ Name = 'cmd'; Run = Invoke-Native "$env:SystemRoot\System32\cmd.exe" @('/d', '/v:on', '/c', 'deskbar version & echo EXIT=!ERRORLEVEL!') })
        $shells.Add(@{ Name = 'powershell'; Run = Invoke-EncodedShell "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" 'deskbar version; "EXIT=$LASTEXITCODE"' })
        $pwsh = Get-Command pwsh.exe -ErrorAction SilentlyContinue
        if ($pwsh) { $shells.Add(@{ Name = 'pwsh'; Run = Invoke-EncodedShell $pwsh.Source 'deskbar version; "EXIT=$LASTEXITCODE"' }) }
        $good = @($shells | Where-Object { $_.Run.Text -match '"packaged"' -and $_.Run.Text -match 'EXIT=0' })
        @{ Result = if ($good.Count -eq $shells.Count) { 'PASS' } else { 'FAIL' }
            Evidence = @("where deskbar: $($where.Text -replace "`n", '; ')") + @($shells | ForEach-Object { "$($_.Name) ($($_.Run.Ms) ms): $($_.Run.Text -replace "`r?`n", ' ')" }) +
                @(if (-not $pwsh) { 'pwsh is not installed here, so it was not tried' }) }
    }

    Invoke-Check 'j' 'screenshot of the primary screen with the bar and the taskbar icon' {
        @{ Result = if ($answered) { 'PASS' } else { 'INFO' }; Evidence = @(Save-Screen 'screen') }
    }

    Write-Step 'Store-style update: 0.0.2 over the running 0.0.1 with -ForceApplicationShutdown'
    Invoke-Check 'U' 'the app comes back by itself after the update (restartProven)' {
        $running = @(Get-PackagedDeskbar $paths.InstallDir | Where-Object { $_.ProcessName -eq 'Deskbar' } | ForEach-Object Id)
        $update['running before'] = if ($running) { $running -join ', ' } else { 'nothing (the update is then no test of a restart)' }
        $watch = [Diagnostics.Stopwatch]::StartNew()
        try { Add-AppxPackage -Path $newPackage -ForceApplicationShutdown; $update['Add-AppxPackage'] = "returned after $([int]$watch.Elapsed.TotalSeconds) s" }
        catch { $update['Add-AppxPackage'] = "threw after $([int]$watch.Elapsed.TotalSeconds) s: $_" }
        $update['installed now'] = (@(Get-AppxPackage -Name Petre.Deskbar | ForEach-Object PackageFullName) -join ', ')
        $back = @()
        $deadline = (Get-Date).AddSeconds(60)
        while ($back.Count -eq 0 -and (Get-Date) -lt $deadline) {
            Start-Sleep -Seconds 1
            $back = @(Get-PackagedDeskbar $newPaths.InstallDir | Where-Object { $_.ProcessName -eq 'Deskbar' })
        }
        $update['restartProven'] = $back.Count -gt 0
        $update['came back after'] = if ($back) { "$([int]$watch.Elapsed.TotalSeconds) s from the start of the install" } else { 'did not within 60 s of the install returning' }
        $update['old process still running'] = @($running | Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue }).Count -gt 0
        $back | ForEach-Object { $update["process $($_.Id)"] = Format-Process $_.Id }
        $update['command line'] = @($back | ForEach-Object { (Get-CimInstance Win32_Process -Filter "ProcessId=$($_.Id)").CommandLine }) -join ' | '
        if ($back) {
            $reply = Wait-Answer 30
            $update['deskbar version'] = if ($reply) { ($reply.Raw.Stdout -join ' ').Trim() } else { 'no answer within 30 s' }
        }
        # A restart Windows makes is not the script's, so it may not carry DESKBAR_APPDATA.
        $update['real %APPDATA%\Deskbar touched so far'] = (Get-RealProfile).StateNewest -ne $before.StateNewest
        @{ Result = 'INFO'; Evidence = @($update.Keys | ForEach-Object { "${_}: $($update[$_])" }) }
    }
    Stop-PackagedDeskbar
    $paths = $newPaths

    Write-Step 'Your turn: the app (0.0.2) starts again, as you, not elevated'
    $launch = try { Start-AsUser $paths.Exe } catch { "threw: $_" }
    $reply = Wait-Answer 45
    if ($null -eq $reply) {
        Stop-PackagedDeskbar
        $launch += ' / no answer within 45 s, so it was started elevated instead'
        Start-Elevated $paths.Exe
        $reply = Wait-Answer 60
    }
    Add-Check 'M0' 'the app for the manual questions' $(if ($reply) { 'INFO' } else { 'FAIL' }) (@("as-user launch: $launch") +
        @(if ($reply) { "deskbar version: $(($reply.Raw.Stdout -join ' ').Trim())" } else { 'no answer from either launcher' }) +
        @(Get-PackagedDeskbar | ForEach-Object { Format-Process $_.Id }))

    $questions = @(
        @{ Id = 'M1'; Text = 'Press Win+Ctrl+Tab. Did the workspace switcher appear? (y/n, plus anything you noticed)' },
        @{ Id = 'M2'; Text = 'Open Manage from the tray icon. Is "Start Deskbar with Windows" ticked? (y/n)' },
        @{ Id = 'M2b'; Text = 'Is there a note under that box? Type it word for word, or leave empty if none' },
        @{ Id = 'M3'; Text = 'In Manage, which version does the version line show (0.0.1 or 0.0.2)? Type what it says' },
        @{ Id = 'M4'; Text = 'Settings > Apps > Startup: is Deskbar listed, and is it on? (e.g. "listed, on")' },
        @{ Id = 'M5'; Text = 'Does the taskbar icon look like the Deskbar icon, not a small icon on a coloured tile? (y/n)' },
        @{ Id = 'M6'; Text = 'Optional: set Settings > System > Display > Scale to 150% now for a second screenshot, then press Enter (type s to skip)' },
        @{ Id = 'M7'; Text = 'Anything else you noticed (empty is fine)' }
    )
    # With no app to look at, the questions would only collect guesses.
    if (-not $reply) { $questions = @(); Add-Check 'M' 'the manual questions' 'SKIP' @('no running app to look at') }
    else { Write-Host 'Answer each question, then press Enter. The app stays running until the last one.' -ForegroundColor White }
    foreach ($question in $questions) {
        $answer = Read-Host "$($question.Id). $($question.Text)"
        if ($question.Id -eq 'M6') {
            if ($answer -ne 's') { $answer = "screenshot: $(Save-Screen 'screen')" } else { $answer = 'skipped' }
        }
        $manual.Add([pscustomobject]@{ Id = $question.Id; Question = $question.Text; Answer = $answer })
    }

    # The workspace the checks created is a real virtual desktop; delete it while the app runs.
    if ($reply) {
        $delete = Invoke-Deskbar @('delete', 'SpikeWs')
        Add-Check 'X' 'SpikeWs deleted again' $(if ($delete.ExitCode -eq 0) { 'INFO' } else { 'FAIL' }) @("exit $($delete.ExitCode) $($delete.Text)", 'if it failed, close that desktop by hand (Win+Tab)')
    }
}
catch { Add-Check 'Z' 'the run stopped early' 'FAIL' @("$_", $_.ScriptStackTrace) }
finally {
    Write-Step 'Cleanup'
    # Each piece on its own, so one failing does not skip the rest or hide the report.
    try { Stop-PackagedDeskbar } catch { Write-Warning "Could not stop the app: $_" }
    try {
        $traces = @(Find-AppFile $traceLog)
        $traces | ForEach-Object -Begin { $n = 0 } -Process { $n++; Copy-Item -LiteralPath $_ -Destination (Join-Path $evidence "deskbar-trace-$n.log") -Force }
        Add-Check 't' 'trace log copied' 'INFO' (@($traces | ForEach-Object { "from $_" }) + @("the real %TEMP% log was $traceOffset bytes before this run; this run's lines follow that offset"))
    }
    catch { Write-Warning "Could not copy the trace: $_" }
    if ($installAttempted) {
        try {
            Get-AppxPackage -Name Petre.Deskbar | ForEach-Object { Remove-AppxPackage -Package $_.PackageFullName; Write-Host "Removed $($_.PackageFullName)" }
        }
        catch { Write-Warning "Could not remove the package: $_ (remove it with: Get-AppxPackage Petre.Deskbar | Remove-AppxPackage)" }
    }
    if ($trustAdded) {
        try { Remove-Item -LiteralPath "Cert:\LocalMachine\TrustedPeople\$($bundleState.Thumbprint)" -Force; Write-Host 'Removed the test certificate from TrustedPeople.' }
        catch { Write-Warning "Could not remove the test certificate: $_" }
    }

    # The real profile, compared with the snapshot and put back where the run changed it.
    try {
        $after = Get-RealProfile
        $when = { param($ticks) if ($ticks) { ([datetime]$ticks).ToString('yyyy-MM-dd HH:mm:ss.fff') + ' UTC' } else { '-' } }
        $folder = @("before: exists $($before.StateExists), newest write $(& $when $before.StateNewest)", "after: exists $($after.StateExists), newest write $(& $when $after.StateNewest)")
        $touched = $after.StateExists -ne $before.StateExists -or $after.StateNewest -ne $before.StateNewest
        if ($after.StateExists -and -not $before.StateExists) {
            # Created by the run: moved into the evidence, so a later real install starts clean.
            Move-Item -LiteralPath $realState -Destination (Join-Path $evidence 'real-appdata-Deskbar-created-by-run')
            $folder += 'it did not exist before, so it was moved into evidence\real-appdata-Deskbar-created-by-run'
        }
        elseif ($after.StateExists) {
            $added = @($after.StateFiles | Where-Object { $before.StateFiles -notcontains $_ })
            $folder += @($added | ForEach-Object { "added by the run: $_" })
            # The takeover marker would stop a later real Store install from taking over autostart.
            $added | Where-Object { (Split-Path -Leaf $_) -eq 'packaged-first-start-done' } | ForEach-Object { Remove-Item -LiteralPath $_; $folder += "removed $_" }
        }
        Add-Check 'e' 'the real %APPDATA%\Deskbar was not created or modified' $(if ($touched) { 'FAIL' } else { 'PASS' }) $folder

        $registry = @()
        foreach ($name in @('Deskbar', 'TaskSpaces')) {
            $was = $before."Run$name"; $now = $after."Run$name"
            $registry += "Run value ${name}: before '$was', after '$now'"
            if ("$was" -ne "$now") {
                if ($null -eq $was) { Remove-ItemProperty -LiteralPath $runKey -Name $name; $registry += "removed the $name value the run wrote" }
                else { Set-ItemProperty -LiteralPath $runKey -Name $name -Value $was; $registry += "put the $name value back" }
            }
        }
        $registry += "Start menu Deskbar.lnk: before $($before.Shortcut), after $($after.Shortcut)"
        if ($before.Shortcut -and -not $after.Shortcut) {
            Copy-Item -LiteralPath (Join-Path $evidence 'Deskbar.lnk.backup') -Destination $startShortcut
            $registry += 'put the shortcut back from the backup'
        }
        $same = "$($before.RunDeskbar)" -eq "$($after.RunDeskbar)" -and "$($before.RunTaskSpaces)" -eq "$($after.RunTaskSpaces)" -and $before.Shortcut -eq $after.Shortcut
        Add-Check 'f' 'no Run value or Start shortcut was written or deleted by the run' $(if ($same) { 'PASS' } else { 'FAIL' }) $registry
    }
    catch { Write-Warning "Could not compare the real profile: $_" }

    # The report: everything above, in one file to bring back.
    try {
        $md = New-Object System.Collections.Generic.List[string]
        $md.Add('# Deskbar Store package spike report'); $md.Add('')
        $md.Add("Run $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz') by store-spike.ps1 from $Bundle."); $md.Add('')
        $md.Add('## Machine'); $md.Add('')
        $facts.PSObject.Properties | ForEach-Object { $md.Add("- $($_.Name): $($_.Value)") }
        $bundleState.Lines | ForEach-Object { $md.Add("- $_") }
        $md.Add("- scratch profile (DESKBAR_APPDATA): $scratchRoot"); $md.Add('')
        $md.Add('## Summary'); $md.Add(''); $md.Add('| Check | Result | What |'); $md.Add('|---|---|---|')
        $checks | ForEach-Object { $md.Add("| $($_.Id) | $($_.Result) | $($_.Title -replace '\|', '/') |") }
        $md.Add(''); $md.Add('## Checks with evidence'); $md.Add('')
        $checks | ForEach-Object {
            $md.Add("### $($_.Id). $($_.Title): $($_.Result)"); $md.Add(''); $md.Add('```')
            $_.Evidence | ForEach-Object { $md.Add("$_") }
            $md.Add('```'); $md.Add('')
        }
        $md.Add('## Store-style update'); $md.Add('')
        $update.Keys | ForEach-Object { $md.Add("- ${_}: $($update[$_])") }
        $md.Add(''); $md.Add('## Manual answers'); $md.Add('')
        $manual | ForEach-Object { $md.Add("- $($_.Id). $($_.Question)"); $md.Add("  - answer: $($_.Answer)") }
        $md.Add(''); $md.Add('## Files in evidence\'); $md.Add('')
        Get-ChildItem -LiteralPath $evidence -Recurse -File | ForEach-Object { $md.Add("- $($_.FullName.Substring($evidence.Length + 1)) ($($_.Length) bytes)") }
        [System.IO.File]::WriteAllLines($Report, $md, (New-Object System.Text.UTF8Encoding $false))
        Write-Host ''; Write-Host "Report: $Report" -ForegroundColor Green
        Write-Host "Send back the report and the evidence folder, zipped: $Report, $evidence" -ForegroundColor Green
    }
    catch { Write-Warning "Could not write the report: $_" }
    try { Stop-Transcript | Out-Null } catch { }
}
