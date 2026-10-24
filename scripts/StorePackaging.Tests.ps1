# Pester 3.4 tests for StorePackaging.psm1, run under Windows PowerShell 5.1:
#   powershell.exe -NoProfile -Command "Invoke-Pester -Path scripts/StorePackaging.Tests.ps1"
#
# These cover the pack script's pure parts: the version token, the identity check and the
# presence of every image the manifest and the resource index expect. Packing itself (MakePri
# and MakeAppx) is exercised by running scripts/pack-store.ps1, not here, because it needs a
# full publish of the app first. They also cover the smoke script's pure parts (the install
# paths, the version reply check, the WACK lookup); installing the package is
# scripts/store-smoke.ps1's job, on an elevated machine.
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

        # The smoke test's pure parts (scripts/store-smoke.ps1): where the installed package and
        # its alias live, what the version reply must say, and whether WACK is there to run. The
        # install itself needs an elevated machine and runs in CI and the spike, not here.

        It 'derives the installed exe and the alias from the version and the family name' {
            $paths = Get-StoreInstallPaths -Version '2.1.43' -ProgramFiles 'C:\Program Files' -LocalAppData 'C:\Users\someone\AppData\Local'
            $paths.FullName | Should Be 'Petre.Deskbar_2.1.43.0_x64__n8ch8v53xw03y'
            $paths.InstallDir | Should Be 'C:\Program Files\WindowsApps\Petre.Deskbar_2.1.43.0_x64__n8ch8v53xw03y'
            $paths.Exe | Should Be 'C:\Program Files\WindowsApps\Petre.Deskbar_2.1.43.0_x64__n8ch8v53xw03y\Deskbar.exe'
            $paths.Alias | Should Be 'C:\Users\someone\AppData\Local\Microsoft\WindowsApps\deskbar.exe'
        }

        It 'takes the install roots from the environment when none are given' {
            $paths = Get-StoreInstallPaths -Version '2.1.43'
            $paths.Exe | Should Be (Join-Path $env:ProgramFiles 'WindowsApps\Petre.Deskbar_2.1.43.0_x64__n8ch8v53xw03y\Deskbar.exe')
            $paths.Alias | Should Be (Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\deskbar.exe')
        }

        It 'refuses a version that is not three parts when deriving the install paths' {
            { Get-StoreInstallPaths -Version '2.1.43.0' } | Should Throw 'three-part'
        }

        It 'reads the three-part version back out of a packed manifest' {
            $text = Get-StoreManifest -TemplatePath $realTemplate -Version '2.1.43'
            Get-StoreManifestVersion -ManifestText $text | Should Be '2.1.43'
        }

        It 'refuses a manifest whose fourth version part is not 0' {
            { Get-StoreManifestVersion -ManifestText '<Package><Identity Name="Petre.Deskbar" Version="2.1.43.1" /></Package>' } | Should Throw '2.1.43.1'
        }

        # The reply as deskbar-cli prints it: indented JSON, which PowerShell hands back as one
        # string per line.
        $goodReply = @('{', '  "version": "2.1.43",', '  "exe": "C:\\Program Files\\WindowsApps\\Petre.Deskbar_2.1.43.0_x64__n8ch8v53xw03y\\Deskbar.exe",', '  "packaged": true', '}')

        It 'accepts a packaged reply of the expected version, given line by line, and returns it' {
            $reply = Assert-StoreVersionReply -Reply $goodReply -Version '2.1.43'
            $reply.packaged | Should Be $true
            $reply.exe | Should Match 'WindowsApps'
        }

        It 'accepts a version carrying a +commit suffix, which the SDK can append from source control' {
            { Assert-StoreVersionReply -Reply '{"version":"2.1.43+0123abc","exe":"x","packaged":true}' -Version '2.1.43' } | Should Not Throw
        }

        It 'refuses a reply that says it is not packaged' {
            { Assert-StoreVersionReply -Reply '{"version":"2.1.43","exe":"x","packaged":false}' -Version '2.1.43' } | Should Throw 'packaged'
        }

        It 'refuses a reply with no packaged field at all' {
            { Assert-StoreVersionReply -Reply '{"version":"2.1.43","exe":"x"}' -Version '2.1.43' } | Should Throw 'packaged'
        }

        It 'refuses a reply of another version' {
            { Assert-StoreVersionReply -Reply '{"version":"2.1.42","exe":"x","packaged":true}' -Version '2.1.43' } | Should Throw '2.1.42'
        }

        It 'refuses a version that merely starts with the expected one' {
            { Assert-StoreVersionReply -Reply '{"version":"2.1.430","exe":"x","packaged":true}' -Version '2.1.43' } | Should Throw '2.1.430'
        }

        It 'refuses a reply that is not JSON, quoting it' {
            { Assert-StoreVersionReply -Reply 'Deskbar is not running.' -Version '2.1.43' } | Should Throw 'Deskbar is not running.'
        }

        It 'finds appcert.exe under the App Certification Kit when it is there' {
            $kits = Join-Path $workDir 'Kits'
            New-Item -ItemType Directory -Path (Join-Path $kits 'App Certification Kit') -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $kits 'App Certification Kit\appcert.exe') -Value ''
            Find-AppCert -KitsRoot $kits | Should Be (Join-Path $kits 'App Certification Kit\appcert.exe')
        }

        It 'answers nothing, rather than throwing, when WACK is not installed' {
            Find-AppCert -KitsRoot (Join-Path $workDir 'NoKits') | Should BeNullOrEmpty
        }
    }
    finally {
        Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
