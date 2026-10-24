#Requires -Version 5.1
<#
.SYNOPSIS
    Installs Deskbar's Store package, starts it, asks it `deskbar version` through the execution
    alias and checks it answers "packaged": true with the expected version. Then removes it, and
    runs the Windows App Certification Kit when the SDK has it.
.DESCRIPTION
    WRITTEN FOR AN ELEVATED, THROWAWAY MACHINE: the release workflow's store-smoke job on a
    GitHub runner, and the Store packaging spike. It changes the machine while it runs:
      - trusts a self-signed test certificate machine-wide, in Cert:\LocalMachine\TrustedPeople,
        and, when none was there, creates that certificate in Cert:\CurrentUser\My. Both are
        removed at the end, but only when this run added them;
      - installs the package Petre.Deskbar for this user, replacing any installed copy, and
        once it has tried to install, removes every copy of the package family at the end,
        however the run ended;
      - starts the installed Deskbar and stops it at the end. It sets DESKBAR_APPDATA to
        -AppData for the start, but the variable does NOT reach the app (measured by the Store
        package spike on 2026-10-24, see below), so the app reads and writes the profile of
        the user running this script: %APPDATA%\Deskbar, its Run value and its Start shortcut.
        On a throwaway runner that profile is empty and dies with the machine, which is fine.
    Do not run it on a machine where an installed Store copy of Deskbar should survive, nor on
    one whose real Deskbar profile matters: scripts/store-spike.ps1 is the one that backs a real
    profile up and puts it back.

    Why each step is there:
      Sign a COPY with a certificate whose subject is the Store publisher, because Windows
      installs only a signed package whose signer matches the manifest's Publisher, and the
      package as built stays unsigned for Partner Center (the Store signs it).
      Start the app with Invoke-CommandInDesktopPackage, the Appx cmdlet made for starting a
      process inside an installed package. Two other launchers were ruled out. Starting the
      installed exe by its path from this unpackaged process is a plain CreateProcess, which
      does not reliably give the new process the package's identity, so "packaged" could come
      back false for a reason that has nothing to do with the package. Activating it through
      explorer.exe shell:AppsFolder\... starts it from the shell's environment. The cmdlet was
      expected to start the process from this one, so the variable is set in this process's
      environment just for the call and restored after it. The spike measured otherwise: the
      cmdlet's launch is brokered, the app is not a child of this process, and it never sees
      the variable (findings in docs/superpowers/notes/2026-10-24-store-package-spike.md). The
      variable is still set, because it costs nothing and a later Windows may pass it on, but
      nothing here depends on it: the check below asks only for the version and "packaged".
      Ask through the ALIAS (deskbar.exe -> deskbar-cli.exe), because that is the command line
      a Store user has, so one answer proves the alias, the pipe and the app at once.
      Check "packaged": true, because that is what proves the installed copy runs with package
      identity, which everything the package changes depends on.
    The reasoning is in "Testing, and the spike that proves full trust" in
    docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md. The pure parts (the
    install paths, the reply check, the WACK lookup) live in StorePackaging.psm1, tested by
    StorePackaging.Tests.ps1.

    WACK runs only when appcert.exe is installed, and its verdict is REPORTED, never fatal: it
    is new on the runner, can take many minutes, and has not yet been seen to run stably there.
    The install-and-ask check is what this script fails on. The report goes to -WackReport;
    uploading it is the workflow's job, not this script's.
.PARAMETER Package
    The .msix from scripts/pack-store.ps1, unsigned. It is not modified: a copy is signed.
.PARAMETER AppData
    The folder set as DESKBAR_APPDATA for the start. Created if absent. The spike measured that
    the variable does not reach the packaged app, so the app uses the runner's own profile.
.PARAMETER Version
    The version the installed copy must report, three parts. Defaults to the version in the
    package's own manifest.
.PARAMETER WackReport
    Where WACK writes its XML report. Defaults to wack-report.xml beside the package.
.PARAMETER WaitSeconds
    How long the app has to start answering `deskbar version`. 30 by default.
.PARAMETER WackTimeoutMinutes
    How long WACK's test run may take before it is stopped and reported as timed out. 30 by
    default. Its reset, run first, gets 5 minutes.
.EXAMPLE
    scripts/store-smoke.ps1 -Package artifacts\Deskbar-2.1.43-store.msix -AppData $env:TEMP\deskbar-smoke
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Package,
    [Parameter(Mandatory)] [string] $AppData,
    [string] $Version,
    [string] $WackReport,
    [int] $WaitSeconds = 30,
    [int] $WackTimeoutMinutes = 30
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$InformationPreference = 'Continue'

Import-Module (Join-Path $PSScriptRoot 'StorePackaging.psm1') -Force

# Trusting a certificate machine-wide needs an elevated process. Say so before anything is
# signed or copied, rather than failing halfway with an access-denied error.
$principal = New-Object Security.Principal.WindowsPrincipal ([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'store-smoke.ps1 must run elevated: it trusts a test certificate in Cert:\LocalMachine\TrustedPeople.'
}

$Package = (Resolve-Path -LiteralPath $Package).ProviderPath
$AppData = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($AppData)
if (-not $WackReport) { $WackReport = Join-Path (Split-Path -Parent $Package) 'wack-report.xml' }
$WackReport = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($WackReport)

# The expected version, from the package itself when the caller did not name one. CI names it,
# from the build, so the check does not just compare the package with itself.
if (-not $Version) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($Package)
    try {
        $entry = $zip.GetEntry('AppxManifest.xml')
        if ($null -eq $entry) { throw "$Package has no AppxManifest.xml; is it a package from pack-store.ps1?" }
        $reader = New-Object System.IO.StreamReader ($entry.Open())
        try { $Version = Get-StoreManifestVersion -ManifestText $reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    finally { $zip.Dispose() }
}
$paths = Get-StoreInstallPaths -Version $Version
Write-Information "Smoke-testing $($paths.FullName) from $Package"

# Another Deskbar in this login session owns the session's control pipe, so it, not the
# installed copy, would answer the alias, and the installed copy would stop at "already
# running". Refuse rather than test the wrong process.
$sessionId = (Get-Process -Id $PID).SessionId
$others = @(Get-Process -Name Deskbar -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $sessionId })
if ($others.Count -gt 0) {
    throw "A Deskbar is already running in this session (process $(($others | ForEach-Object Id) -join ', ')). It would answer instead of the installed copy; stop it first."
}

# Runs `deskbar version` through the alias. Returns the exit code and stdout lines.
# Continue is scoped to this function for the same reason as Invoke-SdkTool's: under Windows
# PowerShell with a Stop preference, a native command's stderr becomes a terminating error
# before its exit code can be read, and "not running yet" arrives on stderr.
function Invoke-VersionQuery([string] $Alias) {
    $ErrorActionPreference = 'Continue'
    # Cleared first: $LASTEXITCODE is global and keeps the last native command's code, so an
    # alias that failed to start at all would otherwise inherit signtool's 0 and read as an answer.
    $global:LASTEXITCODE = $null
    $all = @(& $Alias 'version' 2>&1)
    [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Stdout   = [string[]]@($all | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] } | ForEach-Object { "$_" })
        Text     = ($all | ForEach-Object { "$_" }) -join "`n"
    }
}

# The processes running from the package's install folder: the Deskbar the cmdlet started (it
# hands back no process object, so this is how the script finds it) and any deskbar-cli left
# over. Matched by path, so nothing else on the machine named Deskbar is touched.
function Get-InstalledProcess {
    @(Get-Process -Name Deskbar, deskbar-cli -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($paths.InstallDir, [System.StringComparison]::OrdinalIgnoreCase) })
}

# Stops whatever runs from the package and removes every installed copy of the package family.
# Asks Windows what is installed rather than assuming Add-AppxPackage finished, so a package
# that was registered before Add-AppxPackage threw is removed too. Idempotent: it runs once
# before WACK and again in the cleanup.
function Remove-SmokeInstall {
    Get-InstalledProcess | ForEach-Object {
        Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
        $_.WaitForExit(10000) | Out-Null
    }
    Get-AppxPackage -Name ($paths.FamilyName.Split('_')[0]) |
        Where-Object { $_.PackageFamilyName -eq $paths.FamilyName } |
        ForEach-Object {
            Remove-AppxPackage -Package $_.PackageFullName
            Write-Information "Removed $($_.PackageFullName)"
        }
}

# Runs a WACK command and waits for it, at most $Minutes. Start-Process joins the arguments with
# spaces and does not quote them, so paths arrive quoted by the caller.
function Invoke-AppCert([string] $AppCert, [string[]] $Arguments, [int] $Minutes) {
    $run = Start-Process -FilePath $AppCert -ArgumentList $Arguments -PassThru -NoNewWindow
    if (-not $run.WaitForExit($Minutes * 60 * 1000)) {
        Stop-Process -Id $run.Id -Force -ErrorAction SilentlyContinue
        throw "appcert $($Arguments[0]) did not finish within $Minutes minutes and was stopped."
    }
    $run
}

$workDir = Join-Path ([System.IO.Path]::GetTempPath()) "store-smoke-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $workDir | Out-Null
$signed = Join-Path $workDir (Split-Path -Leaf $Package)
$certificate = $null
$certificateCreated = $false
$trustAdded = $false
$installAttempted = $false
# Which test certificates the user already had, so the cleanup removes only one this run made.
$certificatesBefore = @(Get-ChildItem -Path Cert:\CurrentUser\My | ForEach-Object Thumbprint)

try {
    # Step 1: a signed copy, and Windows' trust in its signer.
    Copy-Item -LiteralPath $Package -Destination $signed
    $certificate = Invoke-StoreTestSign -Path $signed
    $certificateCreated = $certificatesBefore -notcontains $certificate.Thumbprint
    $trustedPath = "Cert:\LocalMachine\TrustedPeople\$($certificate.Thumbprint)"
    if (Test-Path -LiteralPath $trustedPath) {
        Write-Information "Certificate $($certificate.Thumbprint) is already trusted machine-wide; it stays."
    }
    else {
        $cerPath = Join-Path $workDir 'store-test.cer'
        Export-Certificate -Cert $certificate -FilePath $cerPath | Out-Null
        Import-Certificate -FilePath $cerPath -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
        $trustAdded = $true
        Write-Information "Trusted $($certificate.Thumbprint) in Cert:\LocalMachine\TrustedPeople for this run."
    }

    # Step 2: install, and check Windows put it where the derivation says. A mismatch here
    # means the family name or the architecture changed, and every later path would be wrong.
    # The flag is set BEFORE the call, so a package registered by an Add-AppxPackage that then
    # threw is still removed; a run that failed earlier never got this far, and leaves an
    # installed copy it did not touch alone.
    $installAttempted = $true
    Add-AppxPackage -Path $signed
    if (-not (Test-Path -LiteralPath $paths.Exe -PathType Leaf)) {
        $actual = @(Get-AppxPackage -Name 'Petre.Deskbar' | ForEach-Object InstallLocation) -join ', '
        throw "Installed, but $($paths.Exe) does not exist. Windows reports the install location as: $actual"
    }
    if (-not (Test-Path -LiteralPath $paths.Alias)) {
        throw "Installed, but the execution alias $($paths.Alias) was not created."
    }
    Write-Information "Installed $($paths.FullName)"

    # Step 3: start the app inside its package (the header says why this launcher). The
    # variable lives in this process's environment only for the call, and the brokered launch
    # drops it (measured by the spike), so the app writes to the runner's own profile, which is
    # thrown away with the runner. The full path names the exe unambiguously; it is the
    # manifest's Application executable.
    New-Item -ItemType Directory -Path $AppData -Force | Out-Null
    $hadVariable = Test-Path -LiteralPath Env:DESKBAR_APPDATA
    $previousVariable = $env:DESKBAR_APPDATA
    $env:DESKBAR_APPDATA = $AppData
    try {
        Invoke-CommandInDesktopPackage -PackageFamilyName $paths.FamilyName -AppId $paths.AppId -Command $paths.Exe
    }
    finally {
        if ($hadVariable) { $env:DESKBAR_APPDATA = $previousVariable }
        else { Remove-Item -LiteralPath Env:DESKBAR_APPDATA -ErrorAction SilentlyContinue }
    }
    Write-Information "Started $($paths.AppId) in $($paths.FamilyName) (DESKBAR_APPDATA=$AppData was set for the call; the packaged app does not see it)"

    # Step 4: ask until it answers or the time is up. The cmdlet returns no process to watch,
    # so the bound is the deadline alone. Each failed ask already waits for the pipe (the
    # client's own connect timeout), so the pause between asks is short.
    $deadline = (Get-Date).AddSeconds($WaitSeconds)
    $answer = $null
    $last = $null
    while ($null -eq $answer -and (Get-Date) -lt $deadline) {
        $last = Invoke-VersionQuery -Alias $paths.Alias
        if ($last.ExitCode -eq 0) { $answer = $last }
        else { Start-Sleep -Seconds 1 }
    }
    if ($null -eq $answer) {
        # Whether the app is running at all tells "never started or crashed" apart from
        # "running but not answering".
        $running = @(Get-InstalledProcess | Where-Object { $_.ProcessName -eq 'Deskbar' } | ForEach-Object Id)
        $state = if ($running.Count -gt 0) { "Deskbar is running (process $($running -join ', '))" } else { 'no Deskbar is running from the package' }
        throw "deskbar version did not answer within $WaitSeconds seconds, and $state. Last reply (exit $($last.ExitCode)): $($last.Text)"
    }

    # Step 5: the verdict. A throw here skips WACK and goes straight to the cleanup.
    $reply = Assert-StoreVersionReply -Reply $answer.Stdout -Version $Version
    Write-Information "deskbar version answered: version $($reply.version), packaged $($reply.packaged), exe $($reply.exe)"
    Write-Information 'Smoke test passed: the installed package runs with package identity and answers through its alias.'

    # Step 6: out of WACK's way. WACK installs and removes the package itself, so the smoke
    # test's install goes first; the certificate stays trusted, since WACK's install needs it.
    Remove-SmokeInstall

    # Step 7, optional: WACK, against the same signed copy. Only reached when the smoke test
    # passed: otherwise the run is red anyway, and WACK can take many minutes. Its failures,
    # timeouts included, are reported as a warning (an annotation in CI) and never fail the
    # script: it has not yet been seen to run stably on the runner, and a later task makes it
    # fatal once it has.
    try {
        $appcert = Find-AppCert
        if (-not $appcert) {
            Write-Information 'WACK is not installed (no App Certification Kit under the Windows SDK), so it was skipped.'
        }
        else {
            $reportDir = Split-Path -Parent $WackReport
            if (-not (Test-Path -LiteralPath $reportDir)) { New-Item -ItemType Directory -Path $reportDir | Out-Null }
            if (Test-Path -LiteralPath $WackReport) { Remove-Item -LiteralPath $WackReport -Force }

            # reset clears state an earlier WACK run left behind, as Microsoft's command-line
            # steps say to do before each test. Bounded like the test run, since a hung reset
            # would otherwise hold the job until its own timeout.
            Invoke-AppCert -AppCert $appcert -Arguments @('reset') -Minutes 5 | Out-Null
            Write-Information "Running WACK ($appcert) against $signed"
            $wack = Invoke-AppCert -AppCert $appcert -Minutes $WackTimeoutMinutes -Arguments @(
                'test', '-appxpackagepath', "`"$signed`"", '-reportoutputpath', "`"$WackReport`"")

            if (-not (Test-Path -LiteralPath $WackReport)) { throw "WACK exited with code $($wack.ExitCode) and wrote no report." }
            # The report's root element carries the overall verdict in OVERALL_RESULT, PASS when
            # every test passed. Anything else, a missing attribute included, is reported.
            [xml]$report = Get-Content -LiteralPath $WackReport -Raw
            $overall = $report.DocumentElement.GetAttribute('OVERALL_RESULT')
            if ($overall -eq 'PASS') { Write-Information "WACK passed. Report: $WackReport" }
            else { throw "WACK's overall result is '$overall'. Report: $WackReport" }
        }
    }
    catch {
        Write-Warning "WACK: $_"
        if ($env:GITHUB_ACTIONS -eq 'true') { Write-Host "::warning title=WACK::$_" }
    }
}
finally {
    # Everything the run changed is undone here, whether it passed, failed or threw somewhere
    # unexpected, so neither the package nor a machine-wide trust outlives it. Each piece is
    # caught on its own: a failing cleanup must not hide the verdict, or stop the next piece.
    if ($installAttempted) {
        try { Remove-SmokeInstall }
        catch { Write-Warning "Could not remove the installed package: $_" }
    }

    # The trust goes after WACK has used it, and only when this run added it.
    if ($trustAdded) {
        try {
            Remove-Item -LiteralPath "Cert:\LocalMachine\TrustedPeople\$($certificate.Thumbprint)" -Force
            Write-Information 'Removed the test certificate from Cert:\LocalMachine\TrustedPeople.'
        }
        catch { Write-Warning "Could not remove the test certificate from Cert:\LocalMachine\TrustedPeople: $_" }
    }

    # The signing certificate with its private key, only when this run created it. One the user
    # already had (pack-store.ps1 -TestSign makes the same one) stays.
    if ($certificateCreated) {
        try {
            Remove-Item -LiteralPath "Cert:\CurrentUser\My\$($certificate.Thumbprint)" -DeleteKey -Force
            Write-Information 'Removed the test certificate this run created from Cert:\CurrentUser\My.'
        }
        catch { Write-Warning "Could not remove the test certificate from Cert:\CurrentUser\My: $_" }
    }

    Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue
}
