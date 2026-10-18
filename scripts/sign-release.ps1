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
    each, stop before any upload unless the verdict is ok, upload them one at a time with
    --clobber (any name containing Setup first, then the rest by name), then publish unless
    -NoPublish is given, and print the published release's URL.

    From the upload onward a failure keeps the folder of signed files and prints its path
    with a way to finish by hand, so a dropped connection does not cost a second round of
    signing with the token.

    Exit codes:
      0  success.
      1  the verdict failed, or something before the upload failed (downloading, signing).
         Nothing was uploaded; the draft still has its unsigned originals.
      2  refused: the release is not a draft, or has no executable attached.
      3  an operation failed after signing (an upload, or publishing). The signed files are
         kept and their folder is printed.
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

# Set once the signed files exist and the first upload starts. From then on a failure keeps
# the work folder (exit 3) instead of deleting it; before then nothing has left this machine
# and the folder only holds files the draft still has, so it goes (exit 1).
$workDir = $null
$uploading = $false
$keepWorkDir = $false

try {
    # Step 1: only a draft may be signed. A published release is already live, and replacing
    # its assets would change files people have downloaded.
    $release = (Invoke-Gh release view $Tag --json 'isDraft,assets' | Out-String) | ConvertFrom-Json
    if (-not $release.isDraft) {
        # -ErrorAction Continue: under the Stop preference Write-Error would throw and skip the exit code.
        Write-Error 'only drafts are signed here; a published release is already live' -ErrorAction Continue
        exit 2
    }

    # Step 2: the executables attached to the draft. Other assets (checksums and so on) are
    # left alone. The order is the upload order, and the upload order is the order the assets
    # end up in on the release: the 1.x update checker takes the FIRST .exe it finds, and the
    # installer is meant to be that one, so any name containing Setup goes first and the rest
    # follow by name. Fixed here so it never depends on what order GitHub listed them in.
    $names = @($release.assets |
        Where-Object { $_.name -like '*.exe' } |
        ForEach-Object { $_.name } |
        Sort-Object @{ Expression = { $_ -notlike '*Setup*' } }, @{ Expression = { $_ } })
    if ($names.Count -eq 0) {
        Write-Error 'no executable attached' -ErrorAction Continue
        exit 2
    }

    # Step 3: download each executable into a fresh temp folder, removed again at the end
    # unless an upload or the publish failed.
    $workDir = Join-Path ([System.IO.Path]::GetTempPath()) "sign-release-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $workDir | Out-Null

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

    # Step 6: replace the draft's unsigned assets with the signed ones, one file per call and
    # in the order fixed in step 2, so the asset order on the release is deterministic.
    $uploading = $true
    $names | ForEach-Object {
        Write-Information "Uploading $_"
        Invoke-Gh release upload $Tag (Join-Path $workDir $_) --clobber | Out-Null
    }
    Write-Information "Uploaded: $($names -join ', ')"

    # Step 7: publish unless asked not to.
    if ($NoPublish) {
        Write-Information 'NoPublish given, the release is still a draft.'
    }
    else {
        Invoke-Gh release edit $Tag --draft=false | Out-Null
        $uploading = $false

        # A draft's URL stops resolving once it is published (it carries an untagged-... slug),
        # so ask for the URL again rather than printing the one read in step 1. The release is
        # live by now, so a failure here is only a warning: re-running the script would refuse
        # a published release, and there is nothing left to recover.
        $url = $null
        try {
            $read = & gh release view $Tag --json url --jq '.url'
            if ($LASTEXITCODE -eq 0) { $url = $read }
        }
        catch { $url = $null }
        if ($url) {
            Write-Information "Published: $url"
        }
        else {
            Write-Warning "Published $Tag, but could not read its URL back. Check it with: gh release view $Tag --web"
        }
    }
}
catch {
    Write-Error "$_" -ErrorAction Continue
    if ($uploading) {
        # Signed with the token, then lost on the way up. Keep the signed files so finishing
        # the job needs gh and a connection, not another round on the token.
        $keepWorkDir = $true
        $files = ($names | ForEach-Object { '"{0}"' -f (Join-Path $workDir $_) }) -join ' '
        Write-Information "The signed files are in $workDir; re-run ``gh release upload $Tag $files --clobber`` or re-run this script."
        exit 3
    }
    Write-Information 'Nothing was uploaded.'
    exit 1
}
finally {
    if ($workDir -and -not $keepWorkDir) {
        Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
