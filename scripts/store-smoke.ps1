#Requires -Version 5.1
<#
.SYNOPSIS
    Installs Deskbar's Store package, starts it, asks it `deskbar version` through the execution
    alias and checks it answers "packaged": true with the expected version. Then removes it, and
    runs the Windows App Certification Kit when the SDK has it.
.DESCRIPTION
    WRITTEN FOR AN ELEVATED, THROWAWAY MACHINE: the release workflow's store-smoke job on a
    GitHub runner, and the Store packaging spike. It changes the machine while it runs:
      - trusts a self-signed test certificate machine-wide, in Cert:\LocalMachine\TrustedPeople
        (removed again at the end, unless it was already trusted before the script started);
      - installs the package Petre.Deskbar for this user, replacing any installed copy, and
        removes it at the end;
      - starts the installed Deskbar.exe with DESKBAR_APPDATA pointing at -AppData, so it never
        reads or writes a real profile, and stops it at the end.
    Do not run it on a machine where an installed Store copy of Deskbar should survive.

    Why each step is there:
      Sign a COPY with a certificate whose subject is the Store publisher, because Windows
      installs only a signed package whose signer matches the manifest's Publisher, and the
      package as built stays unsigned for Partner Center (the Store signs it).
      Start the installed exe DIRECTLY from this process, rather than through
      explorer.exe shell:AppsFolder\..., because a process started here gets the variables set
      for it here; an activation through the shell starts from the shell's environment, and
      DESKBAR_APPDATA would be lost.
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
    The folder the started Deskbar uses as its profile (DESKBAR_APPDATA). Created if absent.
.PARAMETER Version
    The version the installed copy must report, three parts. Defaults to the version in the
    package's own manifest.
.PARAMETER WackReport
    Where WACK writes its XML report. Defaults to wack-report.xml beside the package.
.PARAMETER WaitSeconds
    How long the app has to start answering `deskbar version`. 30 by default.
.PARAMETER WackTimeoutMinutes
    How long WACK may run before it is stopped and reported as timed out. 30 by default.
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

$workDir = Join-Path ([System.IO.Path]::GetTempPath()) "store-smoke-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $workDir | Out-Null
$signed = Join-Path $workDir (Split-Path -Leaf $Package)
$certificate = $null
$trustAdded = $false
$installed = $false
$process = $null
$failure = $null

try {
    # Step 1: a signed copy, and Windows' trust in its signer.
    Copy-Item -LiteralPath $Package -Destination $signed
    $certificate = Invoke-StoreTestSign -Path $signed
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
    Add-AppxPackage -Path $signed
    $installed = $true
    if (-not (Test-Path -LiteralPath $paths.Exe -PathType Leaf)) {
        $actual = @(Get-AppxPackage -Name 'Petre.Deskbar' | ForEach-Object InstallLocation) -join ', '
        throw "Installed, but $($paths.Exe) does not exist. Windows reports the install location as: $actual"
    }
    if (-not (Test-Path -LiteralPath $paths.Alias)) {
        throw "Installed, but the execution alias $($paths.Alias) was not created."
    }
    Write-Information "Installed $($paths.FullName)"

    # Step 3: start the installed exe with a throwaway profile. ProcessStartInfo with
    # UseShellExecute off is a plain CreateProcess, so the variable is set for the child only
    # and this process's environment is left alone.
    New-Item -ItemType Directory -Path $AppData -Force | Out-Null
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo $paths.Exe
    $startInfo.UseShellExecute = $false
    $startInfo.WorkingDirectory = $AppData
    $startInfo.EnvironmentVariables['DESKBAR_APPDATA'] = $AppData
    $process = [System.Diagnostics.Process]::Start($startInfo)
    Write-Information "Started $($paths.Exe) as process $($process.Id) with DESKBAR_APPDATA=$AppData"

    # Step 4: ask until it answers or the time is up. Each failed ask already waits for the
    # pipe (the client's own connect timeout), so the pause between asks is short.
    $deadline = (Get-Date).AddSeconds($WaitSeconds)
    $answer = $null
    $last = $null
    while ($null -eq $answer -and (Get-Date) -lt $deadline) {
        if ($process.HasExited) {
            throw "Deskbar exited with code $($process.ExitCode) before answering. Last reply: $(if ($last) { $last.Text } else { '(none)' })"
        }
        $last = Invoke-VersionQuery -Alias $paths.Alias
        if ($last.ExitCode -eq 0) { $answer = $last }
        else { Start-Sleep -Seconds 1 }
    }
    if ($null -eq $answer) {
        throw "deskbar version did not answer within $WaitSeconds seconds. Last reply (exit $($last.ExitCode)): $($last.Text)"
    }

    # Step 5: the verdict.
    $reply = Assert-StoreVersionReply -Reply $answer.Stdout -Version $Version
    Write-Information "deskbar version answered: version $($reply.version), packaged $($reply.packaged), exe $($reply.exe)"
    Write-Information 'Smoke test passed: the installed package runs with package identity and answers through its alias.'
}
catch {
    $failure = $_
}
finally {
    # Stop the copy that was started, then anything else running from the install folder (a
    # copy the app may have started itself), so Remove-AppxPackage does not find it in use.
    if ($process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        $process.WaitForExit(10000) | Out-Null
    }
    Get-Process -Name Deskbar, deskbar-cli -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($paths.InstallDir, [System.StringComparison]::OrdinalIgnoreCase) } |
        Stop-Process -Force -ErrorAction SilentlyContinue

    # Cleanup failures are warnings: they must not hide the verdict above.
    if ($installed) {
        try { Remove-AppxPackage -Package $paths.FullName; Write-Information "Removed $($paths.FullName)" }
        catch { Write-Warning "Could not remove $($paths.FullName): $_" }
    }
}

# Step 6, optional: WACK, against the same signed copy, after the smoke test's install is gone
# (WACK installs and removes the package itself, so the certificate must still be trusted).
# Skipped when the smoke test failed: the run is red anyway, and WACK can take many minutes.
# A WACK failure is reported, as a warning annotation in CI, and never fails the script: it
# has not yet been seen to run stably on the runner, and a later task makes it fatal once it has.
if ($null -eq $failure) {
    $appcert = Find-AppCert
    if (-not $appcert) {
        Write-Information 'WACK is not installed (no App Certification Kit under the Windows SDK), so it was skipped.'
    }
    else {
        try {
            $reportDir = Split-Path -Parent $WackReport
            if (-not (Test-Path -LiteralPath $reportDir)) { New-Item -ItemType Directory -Path $reportDir | Out-Null }
            if (Test-Path -LiteralPath $WackReport) { Remove-Item -LiteralPath $WackReport -Force }

            # reset clears state an earlier WACK run left behind, as Microsoft's command-line
            # steps say to do before each test. Its own failure is not a verdict.
            Start-Process -FilePath $appcert -ArgumentList 'reset' -Wait -NoNewWindow
            Write-Information "Running WACK ($appcert) against $signed"
            # Quoted by hand: Start-Process joins the arguments with spaces and does not quote.
            $wack = Start-Process -FilePath $appcert -PassThru -NoNewWindow -ArgumentList @(
                'test', '-appxpackagepath', "`"$signed`"", '-reportoutputpath', "`"$WackReport`"")
            if (-not $wack.WaitForExit($WackTimeoutMinutes * 60 * 1000)) {
                Stop-Process -Id $wack.Id -Force -ErrorAction SilentlyContinue
                throw "WACK did not finish within $WackTimeoutMinutes minutes and was stopped."
            }

            if (-not (Test-Path -LiteralPath $WackReport)) { throw "WACK exited with code $($wack.ExitCode) and wrote no report." }
            # The report's root element carries the overall verdict in OVERALL_RESULT, PASS when
            # every test passed. Anything else, a missing attribute included, is reported.
            [xml]$report = Get-Content -LiteralPath $WackReport -Raw
            $overall = $report.DocumentElement.GetAttribute('OVERALL_RESULT')
            if ($overall -eq 'PASS') { Write-Information "WACK passed. Report: $WackReport" }
            else { throw "WACK's overall result is '$overall'. Report: $WackReport" }
        }
        catch {
            Write-Warning "WACK: $_"
            if ($env:GITHUB_ACTIONS -eq 'true') { Write-Host "::warning title=WACK::$_" }
        }
    }
}

# The trust goes last, after WACK has used it, and only when this run added it.
if ($trustAdded) {
    try { Remove-Item -LiteralPath "Cert:\LocalMachine\TrustedPeople\$($certificate.Thumbprint)" -Force; Write-Information 'Removed the test certificate from Cert:\LocalMachine\TrustedPeople.' }
    catch { Write-Warning "Could not remove the test certificate from Cert:\LocalMachine\TrustedPeople: $_" }
}
Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue

if ($null -ne $failure) { throw $failure }
