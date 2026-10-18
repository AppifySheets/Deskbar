#Requires -Version 5.1
<#
.SYNOPSIS
    Signs the executables of a draft GitHub release, checks the result, uploads it and publishes.
.DESCRIPTION
    This runs on Petre's machine because the code signing certificate lives on a hardware
    token, and the token holds the private key. The key never leaves it, so signing cannot
    happen in CI.

    The repository's workflow does not trust this script. It re-verifies the signature of
    every executable when the release is published (the guard job), using the same
    Get-ReleaseVerdict, so a release signed wrongly, or not at all, is caught there even if
    this script was skipped or edited.

    Sequence: confirm the release is a draft, download its .exe assets, sign each, verify
    each, stop before any upload unless the verdict is ok, upload with --clobber, then
    publish unless -NoPublish is given.

    Exit codes: 0 on success, 1 when the verdict fails (nothing uploaded), 2 for a release
    that cannot be signed (not a draft, or no executable attached).
.PARAMETER Tag
    The tag of the draft release, for example v1.28.0.
.PARAMETER Thumbprint
    SHA-1 thumbprint of the signing certificate in Cert:\CurrentUser\My.
.PARAMETER TimestampUrl
    RFC 3161 timestamp server. The timestamp keeps the signature valid after the certificate
    expires. The default is a free server any certificate may use.
.PARAMETER NoPublish
    Upload the signed files but leave the release a draft.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Tag,
    [Parameter(Mandatory)] [string] $Thumbprint,
    [string] $TimestampUrl = 'http://timestamp.digicert.com',
    [switch] $NoPublish
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$InformationPreference = 'Continue'

Import-Module (Join-Path $PSScriptRoot 'ReleaseSigning.psm1') -Force

# Fails the script when gh exits non-zero. A native command's exit code does not trip
# $ErrorActionPreference on its own.
function Invoke-Gh {
    [CmdletBinding()]
    param([Parameter(ValueFromRemainingArguments)] [string[]] $Arguments)

    $output = & gh @Arguments
    if ($LASTEXITCODE -ne 0) { throw "gh $($Arguments -join ' ') failed with exit code $LASTEXITCODE" }
    $output
}

# Step 1: only a draft may be signed. A published release is already live, and replacing
# its assets would change files people have downloaded.
$release = (Invoke-Gh release view $Tag --json 'isDraft,assets,url' | Out-String) | ConvertFrom-Json
if (-not $release.isDraft) {
    # -ErrorAction Continue: under the Stop preference Write-Error would throw and skip the exit code.
    Write-Error 'only drafts are signed here; a published release is already live' -ErrorAction Continue
    exit 2
}

# Step 2: the executables attached to the draft. Other assets (checksums and so on) are
# left alone.
$names = @($release.assets | Where-Object { $_.name -like '*.exe' } | ForEach-Object { $_.name })
if ($names.Count -eq 0) {
    Write-Error 'no executable attached' -ErrorAction Continue
    exit 2
}

# Step 3: download each executable into a fresh temp folder, removed again at the end.
$workDir = Join-Path ([System.IO.Path]::GetTempPath()) "sign-release-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $workDir | Out-Null

try {
    $names | ForEach-Object {
        Write-Information "Downloading $_"
        Invoke-Gh release download $Tag --pattern $_ --dir $workDir | Out-Null
    }

    # Step 4: sign each file with a timestamp, then verify it independently of signtool and
    # keep the result for the verdict.
    $results = @($names | ForEach-Object {
        $path = Join-Path $workDir $_
        Write-Information "Signing $_"
        Invoke-ExecutableSignature -Path $path -Thumbprint $Thumbprint -TimestampUrl $TimestampUrl | Out-Null
        Test-ExecutableSignature -Path $path -Thumbprint $Thumbprint
    })

    # Step 5: the same verdict the workflow guard uses. On failure, stop before any upload so
    # the draft keeps its unsigned originals.
    $verdict = Get-ReleaseVerdict -Results $results
    if (-not $verdict.Ok) {
        $verdict.Reasons | ForEach-Object { Write-Error $_ -ErrorAction Continue }
        Write-Information 'Verdict failed, nothing uploaded.'
        exit 1
    }

    # Step 6: replace the draft's unsigned assets with the signed ones.
    $files = @($names | ForEach-Object { Join-Path $workDir $_ })
    Invoke-Gh release upload $Tag @files --clobber | Out-Null
    Write-Information "Uploaded: $($names -join ', ')"

    # Step 7: publish unless asked not to, then print where the release lives.
    if ($NoPublish) {
        Write-Information 'NoPublish given, the release is still a draft.'
    }
    else {
        Invoke-Gh release edit $Tag --draft=false | Out-Null
        Write-Information "Published: $($release.url)"
    }
}
finally {
    Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue
}
