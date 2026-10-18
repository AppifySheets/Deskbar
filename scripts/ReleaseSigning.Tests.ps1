# Pester 3.4 tests for ReleaseSigning.psm1, run under Windows PowerShell 5.1:
#   powershell.exe -NoProfile -Command "Invoke-Pester -Path scripts/ReleaseSigning.Tests.ps1"
#
# Needs signtool.exe (Windows SDK, or $env:SIGNTOOL) and, for two tests, network access to a
# timestamp server.
#
# Pester 3 has no BeforeAll/AfterAll, so setup sits at the top of the Describe block and
# teardown in a finally block around everything after the first certificate is created.
# Throwaway self-signed certificates go in Cert:\CurrentUser\My only and are always removed
# again, with their keys. Nothing is ever added to a trusted root store, so `Valid` and a
# fully `Ok` result cannot be exercised here; that is covered by a live check against the
# real certificate.

$modulePath = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'ReleaseSigning.psm1'

Describe 'ReleaseSigning' {
    $workDir = Join-Path ([System.IO.Path]::GetTempPath()) ("ReleaseSigning-" + [guid]::NewGuid().ToString('N'))
    $thumb = $null
    $thumbB = $null
    $timestampUrl = 'http://timestamp.digicert.com'

    try {
        New-Item -ItemType Directory -Path $workDir | Out-Null
        $certA = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=ReleaseSigning test' -CertStoreLocation Cert:\CurrentUser\My
        $thumb = $certA.Thumbprint
        $certB = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=ReleaseSigning test' -CertStoreLocation Cert:\CurrentUser\My
        $thumbB = $certB.Thumbprint

        # The things to sign: copies of a real executable. A bare copy of powershell.exe still
        # reads as Microsoft-signed (Windows finds it by hash in a catalog) and signing leaves
        # it alone. Appending one byte changes the hash, so the catalog no longer matches and
        # the copy is genuinely unsigned.
        $newExe = {
            param([string] $name)
            $target = Join-Path $workDir $name
            Copy-Item -Path (Join-Path $PSHOME 'powershell.exe') -Destination $target
            [System.IO.File]::AppendAllText($target, 'x')
            $target
        }
        $exe = & $newExe 'signme.exe'
        $unsignedExe = & $newExe 'unsigned.exe'

        Import-Module $modulePath -Force

        It 'signs a file so Get-AuthenticodeSignature sees the test certificate' {
            # A self-signed signature reports UnknownError on most machines and Valid on some,
            # so assert only that a signature is present and is ours.
            (Invoke-ExecutableSignature -Path $exe -Thumbprint $thumb).Status | Should Not Be 'NotSigned'
            (Get-AuthenticodeSignature $exe).SignerCertificate.Thumbprint | Should Be $thumb
        }

        It 'throws when the certificate is not in the store' {
            { Invoke-ExecutableSignature -Path $exe -Thumbprint ('0' * 40) } | Should Throw 'No certificate with thumbprint'
        }

        It 'throws with signtool output when signtool fails' {
            { Invoke-ExecutableSignature -Path (Join-Path $workDir 'missing.exe') -Thumbprint $thumb } | Should Throw 'signtool failed'
        }

        It 'accepts a thumbprint with spaces and lower case' {
            $messy = ($thumb.ToLowerInvariant() -replace '(.{4})', '$1 ')
            (Invoke-ExecutableSignature -Path $exe -Thumbprint $messy).Status | Should Not Be 'NotSigned'
            (Test-ExecutableSignature -Path $exe -Thumbprint $messy).ThumbprintMatches | Should Be $true
        }

        It 'reports an unsigned file as not signed and not ok' {
            $r = Test-ExecutableSignature -Path $unsignedExe -Thumbprint $thumb
            $r.Signed | Should Be $false
            $r.Timestamped | Should Be $false
            $r.DigestSha256 | Should Be $false
            $r.Ok | Should Be $false
        }

        It 'reports a missing file as everything false without throwing' {
            $r = Test-ExecutableSignature -Path (Join-Path $workDir 'nope.exe') -Thumbprint $thumb
            $r.Signed | Should Be $false
            $r.Ok | Should Be $false
        }

        It 'reports a signed but untimestamped file as not ok' {
            Invoke-ExecutableSignature -Path $exe -Thumbprint $thumb | Out-Null
            $r = Test-ExecutableSignature -Path $exe -Thumbprint $thumb
            $r.Signed | Should Be $true
            $r.Timestamped | Should Be $false
            $r.DigestSha256 | Should Be $true
            $r.Ok | Should Be $false
        }

        It 'reports a signature timestamped with an RFC 3161 token as timestamped' {
            # Needs the network: a failure here means the timestamp server could not be reached.
            Invoke-ExecutableSignature -Path $exe -Thumbprint $thumb -TimestampUrl $timestampUrl | Out-Null
            $r = Test-ExecutableSignature -Path $exe -Thumbprint $thumb
            $r.Timestamped | Should Be $true
            $r.DigestSha256 | Should Be $true
        }

        It 'does not accept a legacy countersignature as a timestamp' {
            # Set-AuthenticodeSignature -TimestampServer writes the legacy kind (measured), so
            # this is the regression check that the cmdlet's output is not mistaken for RFC 3161.
            # Needs the network, like the test above.
            Set-AuthenticodeSignature -FilePath $exe -Certificate (Get-Item "Cert:\CurrentUser\My\$thumb") -HashAlgorithm SHA256 -TimestampServer $timestampUrl | Out-Null
            $r = Test-ExecutableSignature -Path $exe -Thumbprint $thumb
            $r.Signed | Should Be $true
            $r.Timestamped | Should Be $false
        }

        It 'reports a thumbprint mismatch' {
            Invoke-ExecutableSignature -Path $exe -Thumbprint $thumb | Out-Null
            (Test-ExecutableSignature -Path $exe -Thumbprint ('0' * 40)).ThumbprintMatches | Should Be $false
            (Test-ExecutableSignature -Path $exe -Thumbprint $thumb).ThumbprintMatches | Should Be $true
        }

        It 're-signing replaces the signature' {
            Invoke-ExecutableSignature -Path $exe -Thumbprint $thumb | Out-Null
            Invoke-ExecutableSignature -Path $exe -Thumbprint $thumbB | Out-Null
            (Get-AuthenticodeSignature $exe).SignerCertificate.Thumbprint | Should Be $thumbB
        }

        It 'a verdict over no results is not ok and says why' {
            $v = Get-ReleaseVerdict -Results @()
            $v.Ok | Should Be $false
            $v.Reasons[0] | Should Be 'no executable attached'
        }

        It 'a verdict over one failing result names the file and the field' {
            $v = Get-ReleaseVerdict -Results @([pscustomobject]@{ Path = 'a.exe'; Status = 'UnknownError'; Signed = $true; Valid = $true; ThumbprintMatches = $true; Timestamped = $false; DigestSha256 = $true; Ok = $false })
            $v.Ok | Should Be $false
            $v.Reasons[0] | Should Match 'a\.exe'
            $v.Reasons[0] | Should Match 'Timestamped'
            $v.Reasons[0] | Should Match 'UnknownError'
        }

        It 'a verdict falls back to the field name Ok when every known field is true' {
            $v = Get-ReleaseVerdict -Results @([pscustomobject]@{ Path = 'b.exe'; Signed = $true; Valid = $true; ThumbprintMatches = $true; Timestamped = $true; DigestSha256 = $true; Ok = $false })
            $v.Reasons[0] | Should Match 'b\.exe: Ok is false'
        }

        It 'a verdict over all-ok results is ok' {
            $ok = [pscustomobject]@{ Path = 'a.exe'; Signed = $true; Valid = $true; ThumbprintMatches = $true; Timestamped = $true; DigestSha256 = $true; Ok = $true }
            $v = Get-ReleaseVerdict -Results @($ok, $ok)
            $v.Ok | Should Be $true
            @($v.Reasons).Count | Should Be 0
        }
    }
    finally {
        Remove-Module ReleaseSigning -ErrorAction SilentlyContinue
        # -DeleteKey removes the CNG key files along with the certificate.
        @($thumb, $thumbB) | Where-Object { $_ } | ForEach-Object {
            Remove-Item -Path "Cert:\CurrentUser\My\$_" -DeleteKey -ErrorAction SilentlyContinue
        }
        Remove-Item -Path $workDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
