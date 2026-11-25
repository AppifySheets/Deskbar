# The number of the next release, for .github/workflows/release.yml.
#
# WHY RELEASES ARE NUMBERED HERE, AND NOT BY THE GIT HEIGHT. Nerdbank.GitVersioning names every
# commit major.minor.<git height>, the number of commits since version.json last changed. That
# gives every commit its own fixed number, but the RELEASES jumped: a pull request merged with
# "Rebase and merge" lands all of its commits, a merge of docs or tests adds height without
# releasing, and a burst of merges releases once, from the newest. On 2026-11-24 and 25 the
# releases went 2.5.412, 413, 414, 417, 419, 421, 423, 425, 429. Petre asked for each release to be
# one more than the one before (task 32690a7e).
#
# So the release workflow numbers the release itself: the highest vMAJOR.MINOR.PATCH that already
# exists for version.json's major.minor, plus one, or MAJOR.MINOR.0 for a minor nobody has
# released yet. It writes that number into the RUNNER'S copy of version.json as a three-part
# version ("2.5.430"). Nerdbank then uses it as it stands: Version 2.5.430, FileVersion 2.5.430.0,
# InformationalVersion 2.5.430+<commit> (measured on 2026-11-25 with Nerdbank 3.10.94). The edit is
# never committed: the repository's version.json keeps the major.minor line alone, a pull request
# still never touches a version, and a build on a developer's machine still reports
# major.minor.<git height> with a -g<hash> suffix off main, as before.
#
# Drafts count. A draft is a number already handed out: a release waiting to be signed, or a manual
# run's build. Reusing its number would make two releases with one tag, which GitHub allows for
# drafts and which sign-release.ps1 cannot tell apart.
#
# THE RACE, AND WHY THE BUILD WAITS. Builds of the release workflow never overlap (a newer merge's
# build cancels an older one), but a build can start while an older run's `publish` job is still
# creating that run's release. Both would read the same highest release and pick the same number.
# So before numbering, the build waits until no other run of the workflow has a build that
# succeeded and a publish that has not finished (Wait-ReleasePublish). `publish` refuses a number
# that already belongs to another commit as the last line of defence.
#
# The pure parts (the next number, reading and writing version.json, whether a run is still
# publishing) are tested by ReleaseVersion.Tests.ps1. The parts that ask GitHub (Get-ReleaseTags,
# Wait-ReleasePublish) are exercised by the workflow itself.
#
# Written for both Windows PowerShell 5.1 (the tests, like the other modules here) and
# PowerShell 7 (the workflow's pwsh), so nothing newer than 5.1 is used.

Set-StrictMode -Version Latest

# The major.minor line of a version.json, "2.5". Throws when the file names anything else (a
# patch, a prerelease): the numbering below supplies the patch itself and would otherwise
# silently release something nobody asked for.
function Get-ReleaseMajorMinor {
    param([Parameter(Mandatory)] [string] $Path)

    $version = [string] (Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json).version
    # Case-sensitive, ASCII digits only and anchored with \A and \z, as everywhere a version
    # becomes a tag: \d also matches other scripts' digits and $ accepts a trailing newline.
    if ($version -cnotmatch '\A[0-9]+\.[0-9]+\z') {
        throw "version.json's version is '$version'; the release numbering expects major.minor alone, such as 2.5."
    }
    $version
}

# The version the next release of MajorMinor gets: one above the highest tag vMAJOR.MINOR.PATCH in
# $Tags, or MAJOR.MINOR.0 when there is none. Tags of other minors, prereleases ("v2.5.3-beta")
# and anything else that is not exactly three numbers are ignored. Highest by NUMBER, never by
# string: v2.5.10 is above v2.5.9.
function Get-NextReleaseVersion {
    param(
        [Parameter(Mandatory)] [string] $MajorMinor,
        [string[]] $Tags = @()
    )

    if ($MajorMinor -cnotmatch '\A[0-9]+\.[0-9]+\z') { throw "'$MajorMinor' is not major.minor." }

    $prefix = "v$MajorMinor."
    # Nine digits at most, so the number always fits an [int]; no real patch comes near it.
    $patches = @($Tags |
        Where-Object { $_ -and $_.StartsWith($prefix, [System.StringComparison]::Ordinal) } |
        ForEach-Object { $_.Substring($prefix.Length) } |
        Where-Object { $_ -cmatch '\A[0-9]{1,9}\z' } |
        ForEach-Object { [int] $_ })

    $next = if ($patches.Count -gt 0) { ($patches | Measure-Object -Maximum).Maximum + 1 } else { 0 }
    "$MajorMinor.$next"
}

# Writes $Version ("2.5.430") into version.json as its version, keeping every other setting. Only
# the release workflow calls this, on its own checkout, and nothing commits the result.
function Set-ReleaseVersionFile {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Version
    )

    if ($Version -cnotmatch '\A[0-9]+\.[0-9]+\.[0-9]+\z') { throw "'$Version' is not three plain numbers." }

    $json = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    $json.version = $Version
    # UTF-8 without a byte order mark, which both PowerShells write through .NET this way.
    [System.IO.File]::WriteAllText((Resolve-Path -LiteralPath $Path).Path, ($json | ConvertTo-Json -Depth 10))
}

# Every tag name that can hold a release number: each release's tag, drafts included (a draft has
# its tag name before the tag exists), and the repository's v* tags. Throws when GitHub cannot be
# asked: a release numbered without knowing the existing ones could reuse a number, and a build
# that stops is better than a wrong release.
function Get-ReleaseTags {
    param([Parameter(Mandatory)] [string] $Repository)

    # --paginate, because there are hundreds of releases and a page holds thirty. The token needs
    # push access for drafts to be listed, which the release workflow's contents: write gives.
    $releases = @(gh api --paginate "repos/$Repository/releases?per_page=100" --jq '.[].tag_name')
    if ($LASTEXITCODE -ne 0) { throw "Could not list the releases of $Repository (gh exit $LASTEXITCODE)." }

    $gitTags = @(git tag --list 'v*')
    if ($LASTEXITCODE -ne 0) { throw "Could not list the git tags (git exit $LASTEXITCODE)." }

    @($releases + $gitTags | Where-Object { $_ } | Sort-Object -Unique)
}

# Whether one run is between "its build succeeded" and "its publish finished", the window in which
# its release does not exist yet but its number is taken. $Jobs: objects with name, status and
# conclusion, the shape GitHub's list-jobs-for-a-run API gives. A publish not listed yet counts as
# pending: it starts only after the build, so a succeeded build with no publish is a publish about
# to be queued. A skipped publish is listed as completed, so it does not hold anything.
function Test-ReleasePublishPending {
    param([object[]] $Jobs = @())

    $build = @($Jobs | Where-Object { $_.name -eq 'build' -and $_.conclusion -eq 'success' })
    if ($build.Count -eq 0) { return $false }
    $publish = @($Jobs | Where-Object { $_.name -eq 'publish' })
    $publish.Count -eq 0 -or @($publish | Where-Object { $_.status -ne 'completed' }).Count -gt 0
}

# Waits until no other unfinished run of the release workflow is still publishing (see THE RACE
# above), checking every 15 seconds for at most $TimeoutMinutes. Throws on a timeout or a failed
# lookup rather than numbering a release that may collide.
function Wait-ReleasePublish {
    param(
        [Parameter(Mandatory)] [string] $Repository,
        [Parameter(Mandatory)] [long] $RunId,
        [int] $TimeoutMinutes = 30
    )

    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    while ($true) {
        $runs = @(gh run list --repo $Repository --workflow release.yml --limit 30 --json databaseId,status |
            ConvertFrom-Json | Where-Object { $_.status -ne 'completed' -and $_.databaseId -ne $RunId })
        if ($LASTEXITCODE -ne 0) { throw "Could not list the release workflow's runs (gh exit $LASTEXITCODE)." }

        $pending = @($runs | Where-Object {
            $jobs = @(gh api "repos/$Repository/actions/runs/$($_.databaseId)/jobs?per_page=100" --jq '.jobs[] | {name, status, conclusion}' |
                ForEach-Object { $_ | ConvertFrom-Json })
            if ($LASTEXITCODE -ne 0) { throw "Could not list the jobs of run $($_.databaseId) (gh exit $LASTEXITCODE)." }
            Test-ReleasePublishPending $jobs
        })

        if ($pending.Count -eq 0) { return }
        if ((Get-Date) -gt $deadline) {
            throw "Run(s) $(($pending | ForEach-Object { $_.databaseId }) -join ', ') still had not published after $TimeoutMinutes minutes."
        }
        Write-Host "Waiting for run(s) $(($pending | ForEach-Object { $_.databaseId }) -join ', ') to publish before numbering this release."
        Start-Sleep -Seconds 15
    }
}

Export-ModuleMember -Function Get-ReleaseMajorMinor, Get-NextReleaseVersion, Set-ReleaseVersionFile, Get-ReleaseTags,
    Test-ReleasePublishPending, Wait-ReleasePublish
