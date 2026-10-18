# Release signing helpers: sign an executable, verify one, and decide a release's verdict.
#
# Why PowerShell's own cmdlets (Set-AuthenticodeSignature, Get-AuthenticodeSignature) rather
# than signtool: signtool ships in the Windows SDK, and the machine that signs should not
# need the SDK installed. The cmdlets also drive a token-backed private key through its CSP
# the same way signtool does, so a hardware token works unchanged.
#
# Why the verdict is a separate pure function: the release driver and the workflow guard job
# both call Get-ReleaseVerdict, and it is the one piece here with branching worth testing.
# It takes plain result objects and touches no files, certificates or GitHub.

Set-StrictMode -Version Latest

function Invoke-ExecutableSignature {
    <#
    .SYNOPSIS
        Authenticode-signs a file with a certificate from Cert:\CurrentUser\My.
    .DESCRIPTION
        Returns the Signature object from Set-AuthenticodeSignature. Signing an already
        signed file replaces its signature. Without -TimestampUrl the signature is not
        timestamped; that is for tests only, the release driver always passes one.
    #>
    [CmdletBinding()]
    [OutputType([System.Management.Automation.Signature])]
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Thumbprint,
        [string] $TimestampUrl
    )

    # Look the certificate up by thumbprint so the caller never handles key material.
    $certificate = Get-ChildItem -Path Cert:\CurrentUser\My |
        Where-Object { $_.Thumbprint -eq $Thumbprint } |
        Select-Object -First 1
    if ($null -eq $certificate) {
        throw "No certificate with thumbprint $Thumbprint in Cert:\CurrentUser\My."
    }

    $arguments = @{
        FilePath      = $Path
        Certificate   = $certificate
        HashAlgorithm = 'SHA256'
        IncludeChain  = 'All'
    }
    # The timestamp keeps the signature valid after the certificate itself expires.
    if ($TimestampUrl) { $arguments['TimestampServer'] = $TimestampUrl }

    Set-AuthenticodeSignature @arguments
}

function Test-ExecutableSignature {
    <#
    .SYNOPSIS
        Reports whether a file carries a valid, timestamped signature from the expected certificate.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Thumbprint
    )

    $signature = Get-AuthenticodeSignature -FilePath $Path
    $signed = $signature.Status -ne 'NotSigned'
    $valid = $signature.Status -eq 'Valid'
    $thumbprintMatches = ($null -ne $signature.SignerCertificate) -and ($signature.SignerCertificate.Thumbprint -eq $Thumbprint)
    $timestamped = $null -ne $signature.TimeStamperCertificate

    [pscustomobject]@{
        Path              = $Path
        Signed            = $signed
        Valid             = $valid
        ThumbprintMatches = $thumbprintMatches
        Timestamped       = $timestamped
        Ok                = $signed -and $valid -and $thumbprintMatches -and $timestamped
    }
}

function Get-ReleaseVerdict {
    <#
    .SYNOPSIS
        Decides whether a release passes, from the Test-ExecutableSignature results of its executables.
    .DESCRIPTION
        Not ok when there are no results (no executable attached) or any result is not ok.
        Each failure adds a reason naming the file and the first failing field.
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
        $field = @('Signed', 'Valid', 'ThumbprintMatches', 'Timestamped') |
            Where-Object { -not $result.$_ } |
            Select-Object -First 1
        $reasons += "$($result.Path): $field is false"
    }

    [pscustomobject]@{
        Ok      = $reasons.Count -eq 0
        Reasons = [string[]]$reasons
    }
}

Export-ModuleMember -Function Invoke-ExecutableSignature, Test-ExecutableSignature, Get-ReleaseVerdict
