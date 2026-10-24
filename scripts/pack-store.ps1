#Requires -Version 5.1
<#
.SYNOPSIS
    Builds Deskbar's Microsoft Store package (.msix) from a hand-written manifest and the Windows
    SDK tools, with no Visual Studio and no packaging project.
.DESCRIPTION
    Sequence: publish the app and the command-line client (unless -PublishDir names a folder
    that already holds both), fill the manifest's version token, check the identity against the
    Store's values, stage the published files with the manifest and the images, build the
    resource index with MakePri and pack with MakeAppx. The result is unsigned, which is what
    Partner Center takes: the Store signs it.

    With -TestSign the package is also signed with a self-signed certificate whose subject is
    the Store publisher, so it can be sideloaded on this machine, and the script prints the
    elevated command that trusts that certificate. It does not run that command: trusting a
    certificate machine-wide is the person's decision, made in an elevated prompt.

    The reasoning is in "Package shape and how it is built" and "Images and the resource index"
    in docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md. The pure parts live
    in StorePackaging.psm1, tested by StorePackaging.Tests.ps1.
.PARAMETER Version
    Three-part version, normally VersionPrefix from Directory.Build.props. The package version
    is this plus ".0", and the publish stamps the exe with the same version.
.PARAMETER PublishDir
    A folder that already holds a Store publish: Deskbar.exe and deskbar-cli.exe side by side,
    self-contained, not single-file. When absent, the script publishes both projects itself.
.PARAMETER Output
    Where the .msix goes. Defaults to artifacts\Deskbar-<Version>-store.msix in the repo.
.PARAMETER TestSign
    Sign the package for sideloading with a self-signed certificate in Cert:\CurrentUser\My.
.EXAMPLE
    scripts/pack-store.ps1 -Version 2.1.43 -TestSign
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    [string] $PublishDir,
    [string] $Output,
    [switch] $TestSign
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$InformationPreference = 'Continue'

Import-Module (Join-Path $PSScriptRoot 'StorePackaging.psm1') -Force

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).ProviderPath
$packageSource = Join-Path $repoRoot 'src\Deskbar.App\Package'
if (-not $Output) { $Output = Join-Path $repoRoot "artifacts\Deskbar-$Version-store.msix" }
# Absolute, because MakeAppx and signtool resolve relative paths against their own working
# directory, which need not be PowerShell's current location.
$Output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)

# Fails the script when dotnet exits non-zero. A native command's exit code does not trip
# $ErrorActionPreference on its own. The arguments come as one array, not as remaining
# arguments: dotnet's short flags (-c, -o, -r) would otherwise bind to PowerShell's own common
# parameters (-OutVariable, -OutBuffer) before ever reaching dotnet.
function Invoke-DotNet {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string[]] $Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE" }
}

# Step 1: the manifest for this version, checked before anything slow runs. A wrong version
# or a mistyped identity fails here in a second rather than after a publish.
$manifestText = Get-StoreManifest -TemplatePath (Join-Path $packageSource 'AppxManifest.xml') -Version $Version
Test-StoreIdentity -ManifestText $manifestText

$workDir = Join-Path ([System.IO.Path]::GetTempPath()) "pack-store-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $workDir | Out-Null
$succeeded = $false

try {
    # Step 2: the published files, unless the caller brought them.
    if (-not $PublishDir) {
        $PublishDir = Join-Path $workDir 'publish'
        $cliDir = Join-Path $workDir 'cli'

        # The Store publish, per the spec: self-contained so the package needs no runtime
        # installed, NOT single-file because a package installs a folder anyway and the Store
        # scans its files, no PDBs, and never trimmed (WPF is not trim-safe). VersionPrefix is
        # passed so the exe's file version and the package version cannot disagree.
        $publishFlags = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
            '-p:DebugType=none', '-p:PublishSingleFile=false', "-p:VersionPrefix=$Version")

        Write-Information 'Publishing Deskbar.App'
        Invoke-DotNet -Arguments (@('publish', (Join-Path $repoRoot 'src\Deskbar.App\Deskbar.App.csproj')) + $publishFlags + @('-o', $PublishDir))
        Write-Information 'Publishing Deskbar.Cli'
        Invoke-DotNet -Arguments (@('publish', (Join-Path $repoRoot 'src\Deskbar.Cli\Deskbar.Cli.csproj')) + $publishFlags + @('-o', $cliDir))

        # deskbar-cli.exe goes next to Deskbar.exe, because the manifest's alias names it by a
        # path relative to the package root. Its own files (deskbar-cli.dll, its deps.json and
        # runtimeconfig.json) are new; the rest overlap the app's publish: the shared Deskbar
        # assemblies and the .NET runtime. Those come from the same build of the same sources
        # against the same runtime pack, so they are the same files, and overwriting is safe.
        Copy-Item -Path (Join-Path $cliDir '*') -Destination $PublishDir -Recurse -Force
    }

    # Both executables must be there: the Application and the startup task run Deskbar.exe,
    # the alias runs deskbar-cli.exe. MakeAppx would pack a folder missing either without a
    # word, and the package would install and then fail at launch.
    @('Deskbar.exe', 'deskbar-cli.exe') | ForEach-Object {
        if (-not (Test-Path -LiteralPath (Join-Path $PublishDir $_) -PathType Leaf)) {
            throw "$_ is not in $PublishDir."
        }
    }

    # Step 3: stage, index the images, pack.
    Invoke-StorePack -PublishDir $PublishDir -StageDir (Join-Path $workDir 'stage') -ManifestText $manifestText `
        -ImagesDir (Join-Path $packageSource 'Images') -Output $Output
    Write-Information "Packed $Output"

    # Step 4, optional: a test signature for sideloading on this machine.
    if ($TestSign) {
        $certificate = Invoke-StoreTestSign -Path $Output
        # The public half, next to the package, so the trust command has a file to import.
        $cerPath = [System.IO.Path]::ChangeExtension($Output, '.cer')
        Export-Certificate -Cert $certificate -FilePath $cerPath | Out-Null
        Write-Information "Signed $Output with $($certificate.Thumbprint)"
        Write-Information ''
        Write-Information 'To install it, Windows must trust that certificate machine-wide. From an ELEVATED PowerShell, run:'
        Write-Information "  Import-Certificate -FilePath '$cerPath' -CertStoreLocation Cert:\LocalMachine\TrustedPeople"
        Write-Information 'This script does not run it.'
    }

    $succeeded = $true
}
finally {
    # The temp folder goes on success. On failure it stays, with the staged package and the
    # publish in it, so what MakePri or MakeAppx saw can be inspected.
    if ($succeeded) { Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue }
    else { Write-Warning "Kept the work folder for inspection: $workDir" }
}
