# Pester 3.4 tests for ReleaseNotes.psm1, run under Windows PowerShell 5.1:
#   powershell.exe -NoProfile -Command "Invoke-Pester -Path scripts/ReleaseNotes.Tests.ps1"
# and they pass under PowerShell 7 too, which is what the release workflow runs the module on.
#
# These cover the pure parts: which release a range starts after, how a description's Release
# notes section is cut out, how several pull requests' sections become one release's notes, and
# when a range is worth releasing. Get-ReleaseRange asks git and the GitHub API, so the release
# workflow is its test.

$scriptsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Import-Module (Join-Path $scriptsDir 'ReleaseNotes.psm1') -Force

function New-Release([string] $tag, [bool] $draft = $false, [bool] $prerelease = $false) {
    [pscustomobject]@{ tagName = $tag; isDraft = $draft; isPrerelease = $prerelease }
}

function New-Pr([int] $number, [string] $body, [string[]] $labels = @()) {
    [pscustomobject]@{ number = $number; body = $body; labels = $labels }
}

Describe 'Select-PreviousRelease' {
    It 'compares versions as numbers, so v2.4.10 is above v2.4.7' {
        Select-PreviousRelease @((New-Release 'v2.4.7'), (New-Release 'v2.4.10'), (New-Release 'v2.3.9')) |
            Should Be 'v2.4.10'
    }

    It 'skips drafts, so a release that never went out does not swallow its pull requests' {
        Select-PreviousRelease @((New-Release 'v2.4.10'), (New-Release 'v2.4.13' $true)) | Should Be 'v2.4.10'
    }

    It 'skips prereleases and tags that are not three plain numbers' {
        Select-PreviousRelease @(
            (New-Release 'v2.4.10'),
            (New-Release 'v2.5.0' $false $true),
            (New-Release 'v2.6.0-beta'),
            (New-Release 'nightly'),
            (New-Release 'V2.7.0')
        ) | Should Be 'v2.4.10'
    }

    It 'answers nothing when there is no published release' {
        Select-PreviousRelease @() | Should BeNullOrEmpty
        Select-PreviousRelease @((New-Release 'v1.0.0' $true)) | Should BeNullOrEmpty
    }
}

Describe 'Get-ReleaseNotesSection' {
    It 'cuts the section up to the next heading' {
        $body = "## What was wrong`nStuff.`n`n## Release notes`n- The bar keeps its rows.`n`n## Testing`nRan it."
        Get-ReleaseNotesSection $body | Should Be '- The bar keeps its rows.'
    }

    It 'takes the section to the end when it is last, and reads CRLF line ends' {
        $body = "## Fix`r`nThing.`r`n`r`n## Release notes`r`n- One.`r`n- Two.`r`n"
        Get-ReleaseNotesSection $body | Should Be "- One.`r`n- Two."
    }

    It 'answers nothing for a missing or empty section, or no description' {
        Get-ReleaseNotesSection "## Fix`nThing." | Should BeNullOrEmpty
        Get-ReleaseNotesSection "## Release notes`n`n## Testing`nRan it." | Should BeNullOrEmpty
        Get-ReleaseNotesSection '' | Should BeNullOrEmpty
        Get-ReleaseNotesSection $null | Should BeNullOrEmpty
    }

    It 'leaves out the attribution line that ends a description' {
        # The robot emoji built from its code point, so this file needs no byte order mark either.
        $robot = [char]::ConvertFromUtf32(0x1F916)
        $body = "## Release notes`n- The bar keeps its rows.`n`n$robot Generated with Sonnet 5.5, Opus 5.5`n"
        Get-ReleaseNotesSection $body | Should Be '- The bar keeps its rows.'
        Get-ReleaseNotesSection "## Release notes`r`n- One.`r`n`r`n*fable5.1*`r`n" | Should Be '- One.'
        Get-ReleaseNotesSection "## Release notes`n- One.`n`n*opus5*" | Should Be '- One.'
    }

    It 'keeps an italic note that is not a model name' {
        Get-ReleaseNotesSection "## Release notes`n- One.`n*Needs Windows 11.*" | Should Be "- One.`n*Needs Windows 11.*"
    }

    It 'does not take a "### Release notes" subheading for the section' {
        Get-ReleaseNotesSection "## Fix`n### Release notes`n- No." | Should BeNullOrEmpty
    }
}

Describe 'Join-ReleaseNotes' {
    It 'puts every section in merge order and names every pull request' {
        $notes = Join-ReleaseNotes @(
            (New-Pr 325 "## Release notes`n- Store apps show their own icon."),
            (New-Pr 326 "## Release notes`n- Search opens quick find.")
        )
        $notes | Should Be "- Store apps show their own icon.`n- Search opens quick find.`n`nIn this release: #325, #326"
    }

    It 'names a pull request that has no section, since its change ships too' {
        $notes = Join-ReleaseNotes @((New-Pr 324 "## Release notes`n- Rows stay."), (New-Pr 400 '## Fix'))
        $notes | Should Be "- Rows stay.`n`nIn this release: #324, #400"
    }

    It 'answers nothing when no pull request has a section, so the workflow generates notes' {
        Join-ReleaseNotes @((New-Pr 400 '## Fix')) | Should BeNullOrEmpty
        Join-ReleaseNotes @() | Should BeNullOrEmpty
    }
}

Describe 'Test-Releasable' {
    It 'releases a range with one pull request that is not held back' {
        Test-Releasable @((New-Pr 1 '' @('no-release')), (New-Pr 2 '')) 0 | Should Be $true
    }

    It 'waits when every pull request in the range is labelled no-release' {
        Test-Releasable @((New-Pr 1 '' @('no-release')), (New-Pr 2 '' @('docs', 'no-release'))) 0 | Should Be $false
    }

    It 'releases a direct push that no pull request owns' {
        Test-Releasable @((New-Pr 1 '' @('no-release'))) 1 | Should Be $true
        Test-Releasable @() 1 | Should Be $true
    }

    It 'does not release an empty range' {
        Test-Releasable @() 0 | Should Be $false
    }
}
