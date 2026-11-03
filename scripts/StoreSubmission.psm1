# The pure parts of submitting Deskbar's Store package to Partner Center, for
# scripts/store-submit.ps1 and .github/workflows/store-submit.yml. Kept apart from the parts that
# talk to Partner Center so the rules below can be tested without credentials
# (scripts/StoreSubmission.Tests.ps1).
#
# The rules, as Petre set them on 2026-11-03:
#   - EVERY release reaches the Store, with no per-PR opt-in.
#   - A release that lands while an earlier submission is still in Partner Center is SKIPPED,
#     never replaced: replacing restarts certification, and with several releases a day it might
#     never finish. The daily run of the workflow then submits whatever is newest once the Store
#     has caught up, so the Store trails a burst of releases by at most a day.
#   - An update goes live by itself once it passes certification (TargetPublishMode Immediate).
#     Only the first launch was held for "Publish now".
#
# Written for Windows PowerShell 5.1 as well as PowerShell 7, like StorePackaging.psm1, so the
# Pester 3.4 tests run under the same shell as the other script tests.

# Partner Center's submission statuses (the submission API's `status` field) that mean a
# submission is still on its way and will finish without anyone doing anything.
$script:InFlight = @('CommitStarted', 'PreProcessing', 'Certification', 'Release', 'Publishing')

# Statuses that need a person: a failure to read in Partner Center, a draft somebody left open,
# or a certified submission held for "Publish now" (only the first launch was held; a later
# submission sitting there means someone switched the hold back on).
$script:NeedsPerson = @{
    'CommitFailed'          = 'the last submission failed to commit'
    'PreProcessingFailed'   = 'the last submission failed pre-processing'
    'CertificationFailed'   = 'the last submission failed certification'
    'ReleaseFailed'         = 'the last submission failed to release'
    'PendingCommit'         = 'a draft submission is open in Partner Center; submit or delete it there'
    'PendingPublication'    = 'a certified submission is waiting for Publish now in Partner Center'
}

function ConvertTo-StoreVersion {
    # "2.2.25.0" (a package's four-part version) and "2.2.25" (a release's) as one comparable
    # [version] with three parts, since the fourth part of a Deskbar package is always 0.
    param([Parameter(Mandatory)] [string] $Text)
    $parts = $Text.Trim().TrimStart('v').Split('.')
    if ($parts.Count -lt 3) { throw "Not a three- or four-part version: '$Text'" }
    [version]::new([int]$parts[0], [int]$parts[1], [int]$parts[2])
}

function Get-StoreSubmitDecision {
    # What to do with the newest Store package, given what Partner Center holds now.
    #   PendingStatus     the status of the app's pending submission, or empty when there is none
    #   PublishedVersion  the highest package version in the last published submission, or empty
    #   CandidateVersion  the version of the newest Store package the release runs have built
    # Returns Action 'Submit', 'Skip' (nothing to do, the run passes) or 'Fail' (a person is
    # needed, so the run fails and GitHub emails about it), with the Reason in words.
    param(
        [string] $PendingStatus,
        [string] $PublishedVersion,
        [Parameter(Mandatory)] [string] $CandidateVersion
    )
    $candidate = ConvertTo-StoreVersion $CandidateVersion

    # A pending submission decides it before any version does: submitting now would replace it.
    if ($PendingStatus -and $PendingStatus -ne 'Canceled' -and $PendingStatus -ne 'Published') {
        if ($script:InFlight -contains $PendingStatus) {
            return [pscustomobject]@{ Action = 'Skip'; Reason = "a submission is still in Partner Center ($PendingStatus); $candidate waits for the next run" }
        }
        if ($script:NeedsPerson.ContainsKey($PendingStatus)) {
            return [pscustomobject]@{ Action = 'Fail'; Reason = "$($script:NeedsPerson[$PendingStatus]) ($PendingStatus)" }
        }
        # A status this script has never heard of: fail closed rather than guess it is safe to replace.
        return [pscustomobject]@{ Action = 'Fail'; Reason = "the pending submission has a status this script does not know: $PendingStatus" }
    }

    if ($PublishedVersion) {
        $published = ConvertTo-StoreVersion $PublishedVersion
        if ($candidate -le $published) {
            return [pscustomobject]@{ Action = 'Skip'; Reason = "the Store already has $published, and the newest package is $candidate" }
        }
        return [pscustomobject]@{ Action = 'Submit'; Reason = "the Store has $published and $candidate is newer" }
    }
    [pscustomobject]@{ Action = 'Submit'; Reason = "no published submission was found, so $candidate goes in" }
}

function Select-StoreCandidate {
    # The newest Store package among the repository's workflow artifacts. Each item needs Name
    # ("Deskbar-2.2.26-store.msix") and Expired; anything else on it is passed through. Returns
    # the chosen item with a Version property added, or nothing when no artifact qualifies.
    param([object[]] $Artifacts)
    $Artifacts |
        Where-Object { -not $_.Expired -and $_.Name -match '^Deskbar-(\d+\.\d+\.\d+)-store\.msix$' } |
        ForEach-Object { $_ | Add-Member -NotePropertyName Version -NotePropertyValue (ConvertTo-StoreVersion ($_.Name -replace '^Deskbar-(.+)-store\.msix$', '$1')) -PassThru -Force } |
        Sort-Object Version -Descending |
        Select-Object -First 1
}

function Set-StoreSubmissionForUpdate {
    # Edits a draft submission (the object `msstore submission get` prints, read with
    # ConvertFrom-Json) so it goes live on its own and carries only the new package:
    #   - TargetPublishMode becomes Immediate. The draft is copied from the last published
    #     submission, and the first launch was held for "Publish now", so without this every
    #     update would wait for a click.
    #   - Every package whose version is not the new one is marked PendingDelete, so the Store
    #     serves one package rather than keeping the old one beside it.
    # Only properties the submission already has are set. PowerShell matches property names
    # without regard to case, so this works whether the CLI prints camelCase or PascalCase.
    # Returns the same object.
    param(
        [Parameter(Mandatory)] [object] $Submission,
        [Parameter(Mandatory)] [string] $Version
    )
    $new = ConvertTo-StoreVersion $Version
    if ($null -eq $Submission.PSObject.Properties['TargetPublishMode']) { throw 'The submission has no TargetPublishMode to set.' }
    $Submission.TargetPublishMode = 'Immediate'
    @($Submission.ApplicationPackages) |
        Where-Object { $_ -and $_.Version -and (ConvertTo-StoreVersion $_.Version) -ne $new } |
        ForEach-Object { $_.FileStatus = 'PendingDelete' }
    $Submission
}

Export-ModuleMember -Function ConvertTo-StoreVersion, Get-StoreSubmitDecision, Select-StoreCandidate, Set-StoreSubmissionForUpdate
