# Pester 3.4 tests for StoreSubmission.psm1, run under Windows PowerShell 5.1:
#   powershell.exe -NoProfile -Command "Invoke-Pester -Path scripts/StoreSubmission.Tests.ps1"
#
# These cover the rules that decide whether a release goes to the Store (skip while a submission
# is in flight, fail when a person is needed, submit only a newer version), which artifact is the
# newest, and the edits to a draft before it is committed. Talking to Partner Center is
# scripts/store-submit.ps1's job and needs credentials, so it is not tested here.

$modulePath = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'StoreSubmission.psm1'
Import-Module $modulePath -Force

Describe 'ConvertTo-StoreVersion' {
    It 'reads a package version and a release version as the same version' {
        (ConvertTo-StoreVersion '2.2.25.0') | Should Be (ConvertTo-StoreVersion '2.2.25')
    }

    It 'drops a leading v' {
        (ConvertTo-StoreVersion 'v2.2.26').ToString() | Should Be '2.2.26'
    }

    It 'throws on a two-part version' {
        { ConvertTo-StoreVersion '2.2' } | Should Throw 'three- or four-part'
    }
}

Describe 'Get-StoreSubmitDecision' {
    It 'submits a newer version when nothing is pending' {
        $d = Get-StoreSubmitDecision -PendingStatus '' -PublishedVersion '2.2.25.0' -CandidateVersion '2.2.26'
        $d.Action | Should Be 'Submit'
    }

    It 'skips the version the Store already has' {
        $d = Get-StoreSubmitDecision -PendingStatus '' -PublishedVersion '2.2.26.0' -CandidateVersion '2.2.26'
        $d.Action | Should Be 'Skip'
    }

    It 'skips an older version than the Store has' {
        $d = Get-StoreSubmitDecision -PendingStatus '' -PublishedVersion '2.3.0.0' -CandidateVersion '2.2.26'
        $d.Action | Should Be 'Skip'
    }

    It 'compares versions as numbers, so 2.2.100 is newer than 2.2.26' {
        $d = Get-StoreSubmitDecision -PendingStatus '' -PublishedVersion '2.2.26.0' -CandidateVersion '2.2.100'
        $d.Action | Should Be 'Submit'
    }

    It 'skips, never replaces, a submission still in certification' {
        $d = Get-StoreSubmitDecision -PendingStatus 'Certification' -PublishedVersion '2.2.25.0' -CandidateVersion '2.2.27'
        $d.Action | Should Be 'Skip'
        $d.Reason | Should Match 'Certification'
    }

    It 'skips every in-flight status' {
        'CommitStarted', 'PreProcessing', 'Certification', 'Release', 'Publishing' | ForEach-Object {
            (Get-StoreSubmitDecision -PendingStatus $_ -PublishedVersion '2.2.25.0' -CandidateVersion '2.2.27').Action | Should Be 'Skip'
        }
    }

    It 'fails, so someone is told, when certification failed' {
        $d = Get-StoreSubmitDecision -PendingStatus 'CertificationFailed' -PublishedVersion '2.2.25.0' -CandidateVersion '2.2.27'
        $d.Action | Should Be 'Fail'
        $d.Reason | Should Match 'certification'
    }

    It 'fails on an open draft rather than letting msstore publish delete it' {
        (Get-StoreSubmitDecision -PendingStatus 'PendingCommit' -PublishedVersion '2.2.25.0' -CandidateVersion '2.2.27').Action | Should Be 'Fail'
    }

    It 'fails on a submission held for Publish now' {
        (Get-StoreSubmitDecision -PendingStatus 'PendingPublication' -PublishedVersion '2.2.25.0' -CandidateVersion '2.2.27').Action | Should Be 'Fail'
    }

    It 'fails closed on a status it does not know' {
        $d = Get-StoreSubmitDecision -PendingStatus 'SomethingNew' -PublishedVersion '2.2.25.0' -CandidateVersion '2.2.27'
        $d.Action | Should Be 'Fail'
        $d.Reason | Should Match 'SomethingNew'
    }

    It 'treats a canceled submission as nothing pending' {
        (Get-StoreSubmitDecision -PendingStatus 'Canceled' -PublishedVersion '2.2.25.0' -CandidateVersion '2.2.27').Action | Should Be 'Submit'
    }

    It 'submits when there is no published submission at all' {
        (Get-StoreSubmitDecision -PendingStatus '' -PublishedVersion '' -CandidateVersion '2.2.27').Action | Should Be 'Submit'
    }
}

Describe 'Select-StoreCandidate' {
    It 'picks the highest version, ignoring expired artifacts and other names' {
        $artifacts = @(
            [pscustomobject]@{ Name = 'Deskbar-2.2.25-store.msix'; Expired = $false; RunId = 1 },
            [pscustomobject]@{ Name = 'Deskbar-2.2.100-store.msix'; Expired = $true; RunId = 2 },
            [pscustomobject]@{ Name = 'Deskbar-2.2.26-store.msix'; Expired = $false; RunId = 3 },
            [pscustomobject]@{ Name = 'wack-report-2.2.27'; Expired = $false; RunId = 4 }
        )
        $c = Select-StoreCandidate $artifacts
        $c.RunId | Should Be 3
        $c.Version.ToString() | Should Be '2.2.26'
    }

    It 'returns nothing when no Store package is left' {
        Select-StoreCandidate @([pscustomobject]@{ Name = 'wack-report-2.2.27'; Expired = $false }) | Should BeNullOrEmpty
    }
}

Describe 'Set-StoreSubmissionForUpdate' {
    # The shape `msstore submission get` prints, cut down to the properties this edits.
    function New-Draft {
        '{"TargetPublishMode":"Manual","ApplicationPackages":[' +
        '{"FileName":"Deskbar-2.2.25-store.msix","FileStatus":"Uploaded","Version":"2.2.25.0"},' +
        '{"FileName":"Deskbar-2.2.26-store.msix","FileStatus":"PendingUpload","Version":"2.2.26.0"}]}' | ConvertFrom-Json
    }

    It 'makes the update go live by itself' {
        (Set-StoreSubmissionForUpdate (New-Draft) '2.2.26').TargetPublishMode | Should Be 'Immediate'
    }

    It 'marks the old package for deletion and leaves the new one alone' {
        $s = Set-StoreSubmissionForUpdate (New-Draft) '2.2.26'
        ($s.ApplicationPackages | Where-Object Version -eq '2.2.25.0').FileStatus | Should Be 'PendingDelete'
        ($s.ApplicationPackages | Where-Object Version -eq '2.2.26.0').FileStatus | Should Be 'PendingUpload'
    }

    It 'matches property names without regard to case, as the CLI may print camelCase' {
        $s = '{"targetPublishMode":"Manual","applicationPackages":[{"fileStatus":"Uploaded","version":"2.2.25.0"}]}' | ConvertFrom-Json
        $s = Set-StoreSubmissionForUpdate $s '2.2.26'
        $s.targetPublishMode | Should Be 'Immediate'
        $s.applicationPackages[0].fileStatus | Should Be 'PendingDelete'
    }

    It 'throws when the draft has no publish mode to set' {
        { Set-StoreSubmissionForUpdate ([pscustomobject]@{ ApplicationPackages = @() }) '2.2.26' } | Should Throw 'TargetPublishMode'
    }
}
