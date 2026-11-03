# Submits the newest Deskbar Store package to Partner Center when the Store is behind and nothing
# is already pending there. Run by .github/workflows/store-submit.yml after every release, once a
# day as a catch-up, and by hand. The rules it follows live in StoreSubmission.psm1, with tests.
#
# Steps:
#   1. Find the newest Store package the Release workflow has built (a workflow artifact named
#      Deskbar-<version>-store.msix, from a run that succeeded, so it passed store-smoke too).
#   2. Ask Partner Center's submission API what the app has: a pending submission and its status,
#      and the version of the last published one.
#   3. Decide (Get-StoreSubmitDecision): submit, skip (exit 0) or fail (exit 1, a person is
#      needed, and GitHub emails about a failed run).
#   4. To submit: msstore publish --noCommit uploads the package into a fresh draft copied from
#      the last published submission; the draft is edited to go live by itself and to drop the
#      old package (Set-StoreSubmissionForUpdate); then it is committed. Certification takes hours
#      to days, so this does not wait for it; the next run reports a failure if there is one.
#
# Environment:
#   STORE_TENANT_ID, STORE_CLIENT_ID, STORE_CLIENT_SECRET, STORE_SELLER_ID
#                    the Entra app Partner Center trusts with the Manager role
#   GH_TOKEN         for gh: listing artifacts and runs, downloading the package
#   GITHUB_REPOSITORY  owner/repo, set by Actions
#   DRY_RUN          'true' to stop after the decision, uploading nothing
param(
    # The Store ID of Deskbar's listing (Partner Center, Product identity).
    [string] $AppId = '9PJX26NGFPNF'
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'StoreSubmission.psm1') -Force

# Everything worth reading afterwards goes to the run's summary page as well as the log.
function Write-Summary([string] $Line) {
    Write-Host $Line
    if ($env:GITHUB_STEP_SUMMARY) { Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $Line }
}

# A native command that must succeed: its exit code is checked, since $ErrorActionPreference does
# not cover native programs in Windows PowerShell or in PowerShell 7 before 7.4.
function Invoke-Checked([string] $What, [scriptblock] $Command) {
    $output = & $Command
    if ($LASTEXITCODE -ne 0) { throw "$What failed with exit code $LASTEXITCODE`n$($output -join "`n")" }
    $output
}

$repo = $env:GITHUB_REPOSITORY

# --- 1. The newest Store package from a successful Release run -----------------------------------

# Successful Release runs, so a package whose run failed (its store-smoke install, say) is never
# submitted. 100 runs reach well past the artifacts' 90-day retention at the current pace.
$okRuns = Invoke-Checked 'Listing Release runs' {
    gh api "repos/$repo/actions/workflows/release.yml/runs?status=success&per_page=100" --jq '.workflow_runs[].id'
}
$okRuns = @($okRuns | ForEach-Object { [long]$_ })

$artifacts = Invoke-Checked 'Listing artifacts' {
    gh api "repos/$repo/actions/artifacts?per_page=100" --paginate `
        --jq '.artifacts[] | {Name: .name, Expired: .expired, RunId: .workflow_run.id}'
}
$artifacts = @($artifacts | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object { $okRuns -contains [long]$_.RunId })

$candidate = Select-StoreCandidate $artifacts
if (-not $candidate) {
    Write-Summary 'No Store package from a successful Release run is left to submit. Nothing to do.'
    exit 0
}
Write-Summary "Newest Store package: $($candidate.Name), from Release run $($candidate.RunId)."

# --- 2. What Partner Center has ------------------------------------------------------------------

# A token for the submission API, from the Entra app's client credentials.
$token = (Invoke-RestMethod -Method Post -Uri "https://login.microsoftonline.com/$env:STORE_TENANT_ID/oauth2/token" -Body @{
        grant_type    = 'client_credentials'
        client_id     = $env:STORE_CLIENT_ID
        client_secret = $env:STORE_CLIENT_SECRET
        resource      = 'https://manage.devcenter.microsoft.com'
    }).access_token
$headers = @{ Authorization = "Bearer $token" }
$api = "https://manage.devcenter.microsoft.com/v1.0/my/applications/$AppId"

$app = Invoke-RestMethod -Uri $api -Headers $headers

$pendingStatus = ''
$pendingDetails = ''
if ($app.pendingApplicationSubmission) {
    $pending = Invoke-RestMethod -Uri "$api/submissions/$($app.pendingApplicationSubmission.id)" -Headers $headers
    $pendingStatus = $pending.status
    # What Partner Center says went wrong, for a failed submission's log and summary.
    $pendingDetails = (@($pending.statusDetails.errors) + @($pending.statusDetails.certificationReports) |
        Where-Object { $_ } | ConvertTo-Json -Depth 5 -Compress)
}

$publishedVersion = ''
if ($app.lastPublishedApplicationSubmission) {
    $published = Invoke-RestMethod -Uri "$api/submissions/$($app.lastPublishedApplicationSubmission.id)" -Headers $headers
    $publishedVersion = @($published.applicationPackages) |
        Where-Object { $_.version -and $_.fileStatus -ne 'PendingDelete' } |
        ForEach-Object { ConvertTo-StoreVersion $_.version } |
        Sort-Object -Descending | Select-Object -First 1 | ForEach-Object { $_.ToString() }
}
Write-Summary "Partner Center: published $(if ($publishedVersion) { $publishedVersion } else { 'nothing' }), pending $(if ($pendingStatus) { $pendingStatus } else { 'nothing' })."

# --- 3. Decide -----------------------------------------------------------------------------------

$decision = Get-StoreSubmitDecision -PendingStatus $pendingStatus -PublishedVersion $publishedVersion -CandidateVersion $candidate.Version.ToString()
Write-Summary "Decision: $($decision.Action), because $($decision.Reason)."

if ($decision.Action -eq 'Skip') { exit 0 }
if ($decision.Action -eq 'Fail') {
    if ($pendingDetails) { Write-Summary "Partner Center's details: $pendingDetails" }
    Write-Host "::error::Store submission needs a person: $($decision.Reason)"
    exit 1
}
if ($env:DRY_RUN -eq 'true') {
    Write-Summary 'Dry run: nothing uploaded.'
    exit 0
}

# --- 4. Submit -----------------------------------------------------------------------------------

$dir = Join-Path $env:RUNNER_TEMP 'store-submit'
Invoke-Checked 'Downloading the Store package' { gh run download $candidate.RunId --repo $repo --name $candidate.Name --dir $dir } | Out-Null
$package = Join-Path $dir $candidate.Name

Invoke-Checked 'Configuring msstore' {
    msstore reconfigure --tenantId $env:STORE_TENANT_ID --sellerId $env:STORE_SELLER_ID `
        --clientId $env:STORE_CLIENT_ID --clientSecret $env:STORE_CLIENT_SECRET
} | Out-Null

# Into a draft, uncommitted, so the draft can be edited before it goes in. msstore publish deletes
# a pending draft before it makes its own; the decision above has made sure there is none.
Invoke-Checked 'Uploading the package into a draft' { msstore publish $package --appId $AppId --noCommit } | Write-Host

$draft = (Invoke-Checked 'Reading the draft' { msstore submission get $AppId }) -join "`n" | ConvertFrom-Json
$draft = Set-StoreSubmissionForUpdate $draft $candidate.Version.ToString()
$json = $draft | ConvertTo-Json -Depth 100 -Compress
Invoke-Checked 'Saving the edited draft' { msstore submission update $AppId $json } | Write-Host

Invoke-Checked 'Committing the submission' { msstore submission publish $AppId } | Write-Host
Write-Summary "Submitted $($candidate.Name) for certification. It goes live by itself once it passes; the daily run reports a failure."
