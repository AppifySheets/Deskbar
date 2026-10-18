# Pester 3.4 tests for ReleaseSigning.psm1, run under Windows PowerShell 5.1:
#   powershell.exe -NoProfile -Command "Invoke-Pester -Path scripts/ReleaseSigning.Tests.ps1"
#
# Pester 3 has no BeforeAll/AfterAll, so setup sits at the top of the Describe block and
# teardown in a finally block around the tests. Throwaway self-signed certificates go in
# Cert:\CurrentUser\My only and are always removed again. Nothing is ever added to a
# trusted root store, so `Valid` and a fully `Ok` result cannot be exercised here; that is
# covered by a live check against the real certificate.

$modulePath = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'ReleaseSigning.psm1'

Describe 'ReleaseSigning' {
    $workDir = Join-Path ([System.IO.Path]::GetTempPath()) ("ReleaseSigning-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $workDir | Out-Null
    $certA = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=ReleaseSigning test' -CertStoreLocation Cert:\CurrentUser\My
    $certB = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=ReleaseSigning test' -CertStoreLocation Cert:\CurrentUser\My
    $thumb = $certA.Thumbprint
    $thumbB = $certB.Thumbprint

    # The things to sign: copies of a real executable. A bare copy of powershell.exe still
    # reads as Microsoft-signed (Windows finds it by hash in a catalog), and Set-
    # AuthenticodeSignature then leaves it alone. Appending one byte changes the hash, so
    # the catalog no longer matches and the copy is genuinely unsigned.
    $newExe = {
        param([string] $name)
        $target = Join-Path $workDir $name
        Copy-Item -Path (Join-Path $PSHOME 'powershell.exe') -Destination $target
        [System.IO.File]::AppendAllText($target, 'x')
        $target
    }
    $exe = & $newExe 'signme.exe'
    $unsignedExe = & $newExe 'unsigned.exe'

    try {
        Import-Module $modulePath -Force

        It 'signs a file so Get-AuthenticodeSignature sees the test certificate' {
            # A self-signed signature reports UnknownError on most machines and Valid on some,
            # so assert only that a signature is present and is ours.
            (Invoke-ExecutableSignature -Path $exe -Thumbprint $thumb).Status | Should Not Be 'NotSigned'
            (Get-AuthenticodeSignature $exe).SignerCertificate.Thumbprint | Should Be $thumb
        }

        It 'throws when the certificate is not in the store' {
            { Invoke-ExecutableSignature -Path $exe -Thumbprint ('0' * 40) } | Should Throw
        }

        It 'reports an unsigned file as not signed and not ok' {
            $r = Test-ExecutableSignature -Path $unsignedExe -Thumbprint $thumb
            $r.Signed | Should Be $false
            $r.Ok | Should Be $false
        }

        It 'reports a signed but untimestamped file as not ok' {
            Invoke-ExecutableSignature -Path $exe -Thumbprint $thumb | Out-Null
            $r = Test-ExecutableSignature -Path $exe -Thumbprint $thumb
            $r.Signed | Should Be $true
            $r.Timestamped | Should Be $false
            $r.Ok | Should Be $false
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

        It 'a verdict over one failing result names the file' {
            $v = Get-ReleaseVerdict -Results @([pscustomobject]@{ Path = 'a.exe'; Signed = $true; Valid = $true; ThumbprintMatches = $true; Timestamped = $false; Ok = $false })
            $v.Ok | Should Be $false
            $v.Reasons[0] | Should Match 'a\.exe'
            $v.Reasons[0] | Should Match 'Timestamped'
        }

        It 'a verdict over all-ok results is ok' {
            $ok = [pscustomobject]@{ Path = 'a.exe'; Signed = $true; Valid = $true; ThumbprintMatches = $true; Timestamped = $true; Ok = $true }
            $v = Get-ReleaseVerdict -Results @($ok, $ok)
            $v.Ok | Should Be $true
            @($v.Reasons).Count | Should Be 0
        }
    }
    finally {
        Remove-Module ReleaseSigning -ErrorAction SilentlyContinue
        Remove-Item -Path "Cert:\CurrentUser\My\$thumb" -ErrorAction SilentlyContinue
        Remove-Item -Path "Cert:\CurrentUser\My\$thumbB" -ErrorAction SilentlyContinue
        Remove-Item -Path $workDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
