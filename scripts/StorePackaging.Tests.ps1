# Pester 3.4 tests for StorePackaging.psm1, run under Windows PowerShell 5.1:
#   powershell.exe -NoProfile -Command "Invoke-Pester -Path scripts/StorePackaging.Tests.ps1"
#
# These cover the pack script's pure parts: the version token, the identity check and the
# presence of every image the manifest and the resource index expect. Packing itself (MakePri
# and MakeAppx) is exercised by running scripts/pack-store.ps1, not here, because it needs a
# full publish of the app first.
#
# Pester 3 has no BeforeAll/AfterAll, so setup sits at the top of the Describe block and the
# temp folder is removed in a finally block around everything after it is created.

$scriptsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$modulePath = Join-Path $scriptsDir 'StorePackaging.psm1'
$packageDir = Join-Path $scriptsDir '..\src\Deskbar.App\Package'

Describe 'StorePackaging' {
    $workDir = Join-Path ([System.IO.Path]::GetTempPath()) ("StorePackaging-" + [guid]::NewGuid().ToString('N'))

    try {
        New-Item -ItemType Directory -Path $workDir | Out-Null
        Import-Module $modulePath -Force

        # A minimal template with the token in the one place the real manifest has it, so the
        # replacement is tested without depending on the rest of the real manifest's text.
        $template = Join-Path $workDir 'AppxManifest.xml'
        Set-Content -LiteralPath $template -Encoding UTF8 -Value '<Identity Name="Petre.Deskbar" Version="__VERSION__" />'

        # The real template, for the identity checks: what ships is what gets checked.
        $realTemplate = Join-Path $packageDir 'AppxManifest.xml'

        It 'replaces the version token with the version and a fourth part of 0' {
            $text = Get-StoreManifest -TemplatePath $template -Version '2.1.43'
            $text | Should Match 'Version="2\.1\.43\.0"'
            $text | Should Not Match '__VERSION__'
        }

        It 'throws on a two-part version' {
            { Get-StoreManifest -TemplatePath $template -Version '2.1' } | Should Throw 'three-part'
        }

        It 'throws on a four-part version, since the fourth part is always 0 and added here' {
            { Get-StoreManifest -TemplatePath $template -Version '2.1.43.0' } | Should Throw 'three-part'
        }

        It 'throws when the template has no version token' {
            $noToken = Join-Path $workDir 'NoToken.xml'
            Set-Content -LiteralPath $noToken -Encoding UTF8 -Value '<Identity Version="1.0.0.0" />'
            { Get-StoreManifest -TemplatePath $noToken -Version '2.1.43' } | Should Throw '__VERSION__'
        }

        It 'accepts the real manifest template as the Store identity' {
            $text = Get-StoreManifest -TemplatePath $realTemplate -Version '2.1.43'
            { Test-StoreIdentity -ManifestText $text } | Should Not Throw
        }

        It 'throws when the Publisher is not the Store one' {
            $text = (Get-StoreManifest -TemplatePath $realTemplate -Version '2.1.43') -replace 'CN=804EE50D-C32E-4285-AAF6-E1C851F9FCEF', 'CN=Someone Else'
            { Test-StoreIdentity -ManifestText $text } | Should Throw 'Publisher'
        }

        It 'throws when the Name is not the Store one' {
            $text = (Get-StoreManifest -TemplatePath $realTemplate -Version '2.1.43') -replace 'Name="Petre\.Deskbar"', 'Name="Petre.Other"'
            { Test-StoreIdentity -ManifestText $text } | Should Throw 'Name'
        }

        It 'throws when the PublisherDisplayName is not the Store one' {
            $text = (Get-StoreManifest -TemplatePath $realTemplate -Version '2.1.43') -replace '<PublisherDisplayName>AppifySheets</PublisherDisplayName>', '<PublisherDisplayName>Petre</PublisherDisplayName>'
            { Test-StoreIdentity -ManifestText $text } | Should Throw 'PublisherDisplayName'
        }

        It 'lists the full image set the spec names' {
            # 5 scales of the plated Square44x44Logo, 5 target sizes in 3 forms each, and
            # Square150x150Logo, StoreLogo and Wide310x150Logo at 2 scales each.
            @(Get-RequiredImages).Count | Should Be 26
        }

        It 'has every required image checked in under Package\Images' {
            $missing = @(Get-RequiredImages | Where-Object { -not (Test-Path -LiteralPath (Join-Path $packageDir "Images\$_") -PathType Leaf) })
            $missing -join ', ' | Should Be ''
        }

        It 'points the manifest only at images that resolve through the resource index' {
            # The manifest names unqualified files (Images\StoreLogo.png); each must have at
            # least one qualified variant in the set, or MakePri has nothing to resolve it to.
            $text = Get-Content -LiteralPath $realTemplate -Raw
            $referenced = @([regex]::Matches($text, 'Images\\([A-Za-z0-9]+)\.png') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
            $referenced.Count | Should Be 4
            $images = @(Get-RequiredImages)
            $unresolved = @($referenced | Where-Object { $base = $_; -not ($images | Where-Object { $_ -like "$base.*" }) })
            $unresolved -join ', ' | Should Be ''
        }

        It 'finds MakeAppx.exe in the Windows SDK' {
            $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
            if (-not (Test-Path -LiteralPath $kits)) {
                Set-TestInconclusive -Message 'No Windows SDK on this machine (no Windows Kits\10\bin), so there is no MakeAppx.exe to find.'
                return
            }
            $path = Find-SdkTool -Name 'MakeAppx.exe'
            Test-Path -LiteralPath $path -PathType Leaf | Should Be $true
            $path | Should Match '\\x64\\MakeAppx\.exe$'
        }

        It 'throws a clear error for a tool the SDK does not have' {
            { Find-SdkTool -Name 'NoSuchTool.exe' } | Should Throw 'NoSuchTool.exe not found'
        }
    }
    finally {
        Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
