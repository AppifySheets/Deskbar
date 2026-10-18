# Release signing helpers: sign an executable, verify one, and decide a release's verdict.
#
# Why signtool for signing: it is the one tool here that writes an RFC 3161 timestamp.
# Set-AuthenticodeSignature was measured on this machine (Windows PowerShell 5.1 and pwsh
# 7.4) to write a SHA-256 digest but only a legacy Authenticode countersignature
# (unsigned attribute 1.2.840.113549.1.9.6), never an RFC 3161 token
# (1.3.6.1.4.1.311.3.3.1). The signing machine therefore needs the Windows SDK, or
# $env:SIGNTOOL pointing at a signtool.exe. signtool also drives a token-backed private key
# through its CSP.
#
# Verification needs no signtool, so the workflow guard job can run on a bare runner:
# Test-ExecutableSignature uses Get-AuthenticodeSignature plus a PKCS#7 parse of the PE's
# own security directory, because the cmdlet cannot tell a legacy countersignature from an
# RFC 3161 timestamp.
#
# Why the verdict is a separate pure function: the release driver and the workflow guard
# both call Get-ReleaseVerdict, and it is the one piece here with branching worth testing.
# It takes plain result objects and touches no files, certificates or GitHub.

Set-StrictMode -Version Latest

# OIDs read from the signer's unsigned attributes and digest algorithm.
$script:Rfc3161TimestampOid = '1.3.6.1.4.1.311.3.3.1'
$script:Sha256Oid = '2.16.840.1.101.3.4.2.1'

function ConvertTo-NormalizedThumbprint {
    # Thumbprints get pasted from the certificate dialog with spaces and invisible marks in
    # them. Keep hex digits only and upper-case, so comparisons are exact.
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Thumbprint)

    ($Thumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
}

function Find-SignTool {
    # $env:SIGNTOOL wins, then the newest SDK install, then whatever is on PATH.
    [CmdletBinding()]
    [OutputType([string])]
    param()

    if ($env:SIGNTOOL -and (Test-Path -LiteralPath $env:SIGNTOOL)) { return $env:SIGNTOOL }

    $kitsBin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $sdkCopy = Get-ChildItem -Path (Join-Path $kitsBin '*\x64\signtool.exe') -ErrorAction SilentlyContinue |
        Sort-Object { try { [version]$_.Directory.Parent.Name } catch { [version]'0.0' } } -Descending |
        Select-Object -First 1
    if ($sdkCopy) { return $sdkCopy.FullName }

    $onPath = Get-Command -Name signtool.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($onPath) { return $onPath.Source }

    throw 'signtool.exe not found. Install the Windows SDK (signing tools) or set $env:SIGNTOOL to its path.'
}

function Get-SignatureAttributeInfo {
    # Reads the first signer of the PE's embedded PKCS#7 signature. Returns
    # @{ Timestamped; DigestSha256 }, both false when there is no signature to read.
    [CmdletBinding()]
    [OutputType([hashtable])]
    param([Parameter(Mandatory)] [string] $Path)

    $none = @{ Timestamped = $false; DigestSha256 = $false }
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 0x40 -or $bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) { return $none }

    # Optional header sits after the 4-byte PE signature and 20-byte file header (pe + 24).
    # Its data directories start at +112 for PE32+ (magic 0x20B) and +96 for PE32.
    $pe = [BitConverter]::ToInt32($bytes, 0x3C)
    if ($pe -lt 0 -or $pe + 24 + 2 -gt $bytes.Length) { return $none }
    $optional = $pe + 24
    $directories = $optional + $(if ([BitConverter]::ToUInt16($bytes, $optional) -eq 0x20B) { 112 } else { 96 })

    # Directory entry 4 is the security directory: a FILE offset (not an RVA) and a size.
    $entry = $directories + 4 * 8
    if ($entry + 8 -gt $bytes.Length) { return $none }
    $offset = [long][BitConverter]::ToUInt32($bytes, $entry)
    $size = [long][BitConverter]::ToUInt32($bytes, $entry + 4)
    if ($offset -eq 0 -or $size -le 8 -or ($offset + 8) -gt $bytes.Length) { return $none }

    # WIN_CERTIFICATE: dwLength (4), wRevision (2), wCertificateType (2), then the PKCS#7.
    # dwLength includes the 8-byte header; the directory itself may carry padding after it.
    $length = [long][BitConverter]::ToUInt32($bytes, [int]$offset)
    if ($length -le 8 -or ($offset + $length) -gt $bytes.Length) { return $none }
    $blob = New-Object byte[] ($length - 8)
    [Array]::Copy($bytes, [int]($offset + 8), $blob, 0, $blob.Length)

    Add-Type -AssemblyName System.Security
    $cms = New-Object System.Security.Cryptography.Pkcs.SignedCms
    try { $cms.Decode($blob) } catch { return $none }
    if ($cms.SignerInfos.Count -eq 0) { return $none }

    $signer = $cms.SignerInfos[0]
    $stamped = @($signer.UnsignedAttributes | Where-Object { $_.Oid.Value -eq $script:Rfc3161TimestampOid }).Count -gt 0
    @{ Timestamped = $stamped; DigestSha256 = $signer.DigestAlgorithm.Value -eq $script:Sha256Oid }
}

function Invoke-ExecutableSignature {
    <#
    .SYNOPSIS
        Authenticode-signs a file with signtool, using a certificate from Cert:\CurrentUser\My.
    .DESCRIPTION
        Returns the Get-AuthenticodeSignature result for the file afterwards. Signing an
        already signed file replaces its signature. Without -TimestampUrl the signature is
        not timestamped; that is for tests only, the release driver always passes one.
    #>
    [CmdletBinding()]
    [OutputType([System.Management.Automation.Signature])]
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Thumbprint,
        [string] $TimestampUrl
    )

    $wanted = ConvertTo-NormalizedThumbprint $Thumbprint

    # Check the store first so a wrong thumbprint gives a clear error, not a signtool one.
    $certificate = Get-ChildItem -Path Cert:\CurrentUser\My |
        Where-Object { $_.Thumbprint -eq $wanted } |
        Select-Object -First 1
    if ($null -eq $certificate) {
        throw "No certificate with thumbprint $wanted in Cert:\CurrentUser\My."
    }

    $signtool = Find-SignTool
    $arguments = @('sign', '/fd', 'SHA256', '/sha1', $wanted, '/s', 'My')
    # /tr is an RFC 3161 server; /td sets the timestamp's own digest algorithm. The
    # timestamp keeps the signature valid after the certificate itself expires.
    if ($TimestampUrl) { $arguments += @('/tr', $TimestampUrl, '/td', 'SHA256') }
    $arguments += $Path

    $output = & $signtool @arguments 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        throw "signtool failed with exit code ${LASTEXITCODE}: $output"
    }

    Get-AuthenticodeSignature -FilePath $Path
}

function Test-ExecutableSignature {
    <#
    .SYNOPSIS
        Reports whether a file carries a valid signature from the expected certificate, with
        a SHA-256 digest and an RFC 3161 timestamp.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Thumbprint
    )

    $wanted = ConvertTo-NormalizedThumbprint $Thumbprint
    $newResult = {
        param($status, $signed, $valid, $thumbprintMatches, $timestamped, $sha256)
        [pscustomobject]@{
            Path              = $Path
            Status            = $status
            Signed            = $signed
            Valid             = $valid
            ThumbprintMatches = $thumbprintMatches
            Timestamped       = $timestamped
            DigestSha256      = $sha256
            Ok                = $signed -and $valid -and $thumbprintMatches -and $timestamped -and $sha256
        }
    }

    # A missing file is a failed check, not a crash.
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return & $newResult 'FileNotFound' $false $false $false $false $false
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    $signed = $signature.Status -ne 'NotSigned'
    $thumbprintMatches = $signed -and ($null -ne $signature.SignerCertificate) -and ($signature.SignerCertificate.Thumbprint -eq $wanted)
    # An unsigned file has no security directory to parse, so those fields stay false.
    $attributes = if ($signed) { Get-SignatureAttributeInfo -Path $Path } else { @{ Timestamped = $false; DigestSha256 = $false } }

    & $newResult ([string]$signature.Status) $signed ($signature.Status -eq 'Valid') $thumbprintMatches $attributes.Timestamped $attributes.DigestSha256
}

function Get-ReleaseVerdict {
    <#
    .SYNOPSIS
        Decides whether a release passes, from the Test-ExecutableSignature results of its executables.
    .DESCRIPTION
        Not ok when there are no results (no executable attached) or any result is not ok.
        Each failure adds a reason naming the file and the first failing field, with the
        signature status when the result carries one.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)] [AllowEmptyCollection()] [object[]] $Results
    )

    $reasons = @()
    if ($Results.Count -eq 0) {
        $reasons += 'no executable attached'
    }
    foreach ($result in $Results) {
        if ($result.Ok) { continue }
        $field = @('Signed', 'Valid', 'ThumbprintMatches', 'Timestamped', 'DigestSha256') |
            Where-Object { $null -eq $result.PSObject.Properties[$_] -or -not $result.$_ } |
            Select-Object -First 1
        if (-not $field) { $field = 'Ok' }
        $status = $result.PSObject.Properties['Status']
        $suffix = if ($status -and $status.Value) { " (status $($status.Value))" } else { '' }
        $reasons += "$($result.Path): $field is false$suffix"
    }

    [pscustomobject]@{
        Ok      = $reasons.Count -eq 0
        Reasons = [string[]]$reasons
    }
}

Export-ModuleMember -Function Invoke-ExecutableSignature, Test-ExecutableSignature, Get-ReleaseVerdict
