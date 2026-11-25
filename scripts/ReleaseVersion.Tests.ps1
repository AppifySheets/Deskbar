# Pester 3.4 tests for ReleaseVersion.psm1, run under Windows PowerShell 5.1:
#   powershell.exe -NoProfile -Command "Invoke-Pester -Path scripts/ReleaseVersion.Tests.ps1"
# Run them there and not under PowerShell 7: Pester 3.4's `Should Throw` does not work on pwsh 7
# (even `{ throw 'x' } | Should Throw` fails there, measured 2026-11-25), so the four refusal tests
# fail under pwsh for a reason that is not this module's.
#
# These cover the pure parts: the next release number, reading and writing version.json, and
# whether another run is still publishing. Get-ReleaseTags and Wait-ReleasePublish ask GitHub, so
# the release workflow is their test.

$scriptsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Import-Module (Join-Path $scriptsDir 'ReleaseVersion.psm1') -Force

function New-Job([string] $name, [string] $status, [string] $conclusion) {
    [pscustomobject]@{ name = $name; status = $status; conclusion = $conclusion }
}

Describe 'Get-NextReleaseVersion' {
    It 'is one more than the highest release, whatever the gaps before it' {
        Get-NextReleaseVersion '2.5' @('v2.5.423', 'v2.5.425', 'v2.5.429') | Should Be '2.5.430'
    }

    It 'compares as numbers, so v2.5.10 is above v2.5.9' {
        Get-NextReleaseVersion '2.5' @('v2.5.9', 'v2.5.10') | Should Be '2.5.11'
    }

    It 'starts a minor nobody has released at .0' {
        Get-NextReleaseVersion '2.6' @('v2.5.429', 'v2.4.80') | Should Be '2.6.0'
    }

    It 'starts at .0 when there are no tags at all' {
        Get-NextReleaseVersion '2.5' @() | Should Be '2.5.0'
    }

    It 'ignores other minors, prereleases and names that are not three numbers' {
        Get-NextReleaseVersion '2.5' @('v2.5.3', 'v2.50.900', 'v2.5.7-beta', 'v2.5.x', 'v3.5.99', '2.5.80', 'v2.5.12.1') |
            Should Be '2.5.4'
    }

    It 'does not take v2.50 for v2.5' {
        Get-NextReleaseVersion '2.5' @('v2.50.1') | Should Be '2.5.0'
    }

    It 'refuses a major.minor that is not two numbers' {
        { Get-NextReleaseVersion '2.5.1' @() } | Should Throw
    }
}

Describe 'Get-ReleaseMajorMinor' {
    It 'reads the major.minor line' {
        $path = Join-Path $TestDrive 'version.json'
        Set-Content -LiteralPath $path -Value '{ "version": "2.5", "versionHeightOffset": -1 }'
        Get-ReleaseMajorMinor $path | Should Be '2.5'
    }

    It 'refuses a version that already names a patch' {
        $path = Join-Path $TestDrive 'version.json'
        Set-Content -LiteralPath $path -Value '{ "version": "2.5.430" }'
        { Get-ReleaseMajorMinor $path } | Should Throw
    }

    It 'refuses a prerelease' {
        $path = Join-Path $TestDrive 'version.json'
        Set-Content -LiteralPath $path -Value '{ "version": "2.6-beta" }'
        { Get-ReleaseMajorMinor $path } | Should Throw
    }
}

Describe 'Set-ReleaseVersionFile' {
    It 'writes the release number and keeps every other setting' {
        $path = Join-Path $TestDrive 'version.json'
        Set-Content -LiteralPath $path -Value '{ "version": "2.5", "versionHeightOffset": -1, "publicReleaseRefSpec": ["^refs/heads/main$"], "assemblyVersion": { "precision": "major" } }'
        Set-ReleaseVersionFile $path '2.5.430'
        $json = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        $json.version | Should Be '2.5.430'
        $json.versionHeightOffset | Should Be -1
        @($json.publicReleaseRefSpec)[0] | Should Be '^refs/heads/main$'
        $json.assemblyVersion.precision | Should Be 'major'
    }

    It 'writes no byte order mark' {
        $path = Join-Path $TestDrive 'version.json'
        Set-Content -LiteralPath $path -Value '{ "version": "2.5" }'
        Set-ReleaseVersionFile $path '2.5.1'
        [System.IO.File]::ReadAllBytes($path)[0] | Should Be ([byte][char]'{')
    }

    It 'refuses anything but three plain numbers' {
        $path = Join-Path $TestDrive 'version.json'
        Set-Content -LiteralPath $path -Value '{ "version": "2.5" }'
        { Set-ReleaseVersionFile $path '2.5' } | Should Throw
        { Set-ReleaseVersionFile $path '2.5.1-beta' } | Should Throw
    }
}

Describe 'Test-ReleasePublishPending' {
    It 'holds while a run built and its publish is still running' {
        Test-ReleasePublishPending @((New-Job 'build' 'completed' 'success'), (New-Job 'publish' 'in_progress' $null)) |
            Should Be $true
    }

    It 'holds while a run built and its publish is not listed yet' {
        Test-ReleasePublishPending @((New-Job 'plan' 'completed' 'success'), (New-Job 'build' 'completed' 'success')) |
            Should Be $true
    }

    It 'lets go once the publish finished, whatever its outcome' {
        Test-ReleasePublishPending @((New-Job 'build' 'completed' 'success'), (New-Job 'publish' 'completed' 'failure')) |
            Should Be $false
        Test-ReleasePublishPending @((New-Job 'build' 'completed' 'success'), (New-Job 'publish' 'completed' 'skipped')) |
            Should Be $false
    }

    It 'does not hold for a run still building, cancelled or failed: that build makes no release' {
        Test-ReleasePublishPending @((New-Job 'build' 'in_progress' $null)) | Should Be $false
        Test-ReleasePublishPending @((New-Job 'build' 'completed' 'cancelled')) | Should Be $false
        Test-ReleasePublishPending @((New-Job 'build' 'completed' 'failure')) | Should Be $false
    }

    It 'does not hold for a run with no build at all, such as the guard' {
        Test-ReleasePublishPending @((New-Job 'guard' 'in_progress' $null)) | Should Be $false
        Test-ReleasePublishPending @() | Should Be $false
    }
}
