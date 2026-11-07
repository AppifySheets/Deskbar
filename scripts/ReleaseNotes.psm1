# What a release contains and what its notes say, for .github/workflows/release.yml.
#
# WHY A RANGE, AND NOT THE ONE MERGED PULL REQUEST. A burst of merges releases ONCE, from its
# newest commit: the release workflow cancels a build that a newer merge has overtaken (see the
# top of release.yml). That is only safe if the release that survives also carries the notes of
# every pull request it overtook. So a release is described by the RANGE from the previous
# published release to the commit being released, and every pull request merged in that range
# whose commits touch src/ contributes its "## Release notes" section. A cancelled, failed or
# skipped build then loses nothing: the next release names its pull request.
#
# Before this, the notes came from the single pull request of the pushed commit, and on
# 2026-11-07 two of four quick merges were cancelled, so #325 shipped inside v2.4.10 without its
# line in the notes and #328 shipped in no release at all.
#
# The pure parts (choosing the previous release, cutting a section out of a description, joining
# sections, deciding whether a range is worth releasing) are tested by ReleaseNotes.Tests.ps1. The
# parts that ask git and the GitHub API (Get-ReleaseRange) are exercised by the workflow itself.
#
# Written for both Windows PowerShell 5.1 (the tests, like the other modules here) and
# PowerShell 7 (the workflow's pwsh), so nothing newer than 5.1 is used.

Set-StrictMode -Version Latest

# The highest PUBLISHED release with a plain vMAJOR.MINOR.PATCH tag, or $null when there is none.
#
# Published only, not drafts: a draft is either a manual run waiting to be signed or a release
# that never went out, and starting the next range after it would drop its pull requests from the
# notes of the release that does go out. The cost is the other way round and smaller: while
# signing is enforced, two merges in a row both list the first one's pull requests until the
# first draft is signed and published.
#
# Highest by VERSION, compared as numbers, not by date or by string: "v2.4.10" is above "v2.4.7",
# and a release published late for an older version must not become the start of the range.
#
# $Releases: objects with tagName, isDraft and isPrerelease, the shape `gh release list --json`
# gives.
function Select-PreviousRelease {
    param([object[]] $Releases)

    $published = @($Releases | Where-Object {
        $_ -and -not $_.isDraft -and -not $_.isPrerelease -and ($_.tagName -cmatch '\Av[0-9]+\.[0-9]+\.[0-9]+\z')
    })
    if ($published.Count -eq 0) { return $null }

    $highest = $published |
        Sort-Object -Property @{ Expression = { [version] $_.tagName.Substring(1) } } -Descending |
        Select-Object -First 1
    return $highest.tagName
}

# The text of a pull request description's "## Release notes" section, up to the next "## "
# heading, trimmed; $null when the section is missing or empty. The same rule release.yml used
# before ranges existed, kept word for word so a description written for it still works.
function Get-ReleaseNotesSection {
    param([AllowNull()][AllowEmptyString()][string] $Body)

    if ([string]::IsNullOrWhiteSpace($Body)) { return $null }
    # \r?\n: a description edited in the browser comes back with CRLF line ends.
    $match = [regex]::Match($Body, '(?ms)^##\s+Release notes\s*$\r?\n(.*?)(?=^##\s|\z)')
    if (-not $match.Success) { return $null }
    # The section is usually the LAST one in a description, so the attribution line that ends a
    # description lands inside it, and every merge release until 2.4.10 published it to users as
    # a note (the robot emoji, then "Generated with ..."). Dropped here: a line starting with the
    # robot emoji, and a line that is only an italic model name ("*opus5*", "*fable5.1*"), the
    # house form. The emoji is built from its code point rather than typed, because Windows
    # PowerShell 5.1 reads a .psm1 with no byte order mark as ANSI and would garble it.
    $robot = [regex]::Escape([char]::ConvertFromUtf32(0x1F916))
    $text = $match.Groups[1].Value
    $text = [regex]::Replace($text, "(?m)^[ \t]*$robot.*$", '')
    $text = [regex]::Replace($text, '(?m)^[ \t]*\*[a-z]+[0-9][0-9.]*\*[ \t]*\r?$', '')
    $text = $text.Trim()
    if ($text) { return $text } else { return $null }
}

# The notes of one release: every pull request's section, oldest merge first, one after another,
# then one line naming the pull requests, which GitHub turns into links in a release body. $null
# when no pull request in the range has a section, so the caller falls back to generated notes.
#
# A pull request without a section is still named on the last line: its change ships, and the
# reader can follow the link.
#
# $PullRequests: objects with number and body, oldest merge first.
function Join-ReleaseNotes {
    param([object[]] $PullRequests)

    $prs = @($PullRequests | Where-Object { $_ })
    $sections = @($prs | ForEach-Object { Get-ReleaseNotesSection $_.body } | Where-Object { $_ })
    if ($sections.Count -eq 0) { return $null }

    $named = ($prs | ForEach-Object { "#$($_.number)" }) -join ', '
    return (($sections -join "`n") + "`n`nIn this release: $named")
}

# Whether a range is worth a release. It is when at least one change under src/ is not held back:
# a commit no pull request owns (a direct push), or a pull request WITHOUT the `no-release` label.
# A range in which every change is labelled no-release waits for the next merge, which then
# carries those changes and their notes.
#
# $PullRequests: objects with a labels array of names. $OrphanCommits: how many src/ commits in the
# range belong to no merged pull request.
function Test-Releasable {
    param([object[]] $PullRequests, [int] $OrphanCommits)

    if ($OrphanCommits -gt 0) { return $true }
    $free = @($PullRequests | Where-Object { $_ -and -not (@($_.labels) -contains 'no-release') })
    return ($free.Count -gt 0)
}

# THE IMPURE PART: asks git and the GitHub API what a release from $Head would contain.
#
# Returns an object with:
#   Previous       the previous published release's tag, or $null (first release, or none found)
#   Base           the commit that tag points at, or $null
#   AlreadyIn      $true when $Head is already inside the previous release (a re-run): nothing new
#   PullRequests   the merged pull requests whose commits in Base..Head touch src/, oldest merge
#                  first, each with number, body and labels
#   OrphanCommits  src/ commits in the range that no merged pull request owns
#   ApiFailed      $true when a pull request lookup failed; the caller then releases anyway
#
# Needs a checkout with full history and tags (fetch-depth: 0) and GH_TOKEN in the environment.
#
# Pull requests are found through each commit (commits/{sha}/pulls), the same lookup the old
# single-PR notes used, which keeps the link for squash, merge and rebase merges alike. Only
# pull requests that were MERGED INTO $BaseBranch count: the same API also returns open pull
# requests whose branches happen to contain the commit.
function Get-ReleaseRange {
    param(
        [Parameter(Mandatory)] [string] $Repository,
        [Parameter(Mandatory)] [string] $Head,
        [string] $BaseBranch = 'main'
    )

    $result = [pscustomobject]@{
        Previous      = $null
        Base          = $null
        AlreadyIn     = $false
        PullRequests  = @()
        OrphanCommits = 0
        ApiFailed     = $false
    }

    # Parsed into a variable BEFORE any pipeline filter, and from the lines joined into one string:
    # Windows PowerShell 5.1's ConvertFrom-Json reads piped lines one at a time and writes a JSON
    # array as ONE object, so a filter straight after it would see the whole array as one item.
    $json = gh release list --repo $Repository --limit 100 --json tagName,isDraft,isPrerelease
    if ($LASTEXITCODE -ne 0) { throw 'Could not list the releases.' }
    $releases = ($json -join "`n") | ConvertFrom-Json
    $result.Previous = Select-PreviousRelease @($releases)

    if ($result.Previous) {
        $tagged = git rev-parse --verify --quiet "refs/tags/$($result.Previous)^{commit}"
        if ($LASTEXITCODE -eq 0 -and $tagged) {
            # FIRST: Head already inside the previous release. That is a re-run, typically of a run
            # a newer merge cancelled and whose changes a later release has since shipped. Nothing new.
            git merge-base --is-ancestor $Head $tagged
            if ($LASTEXITCODE -eq 0) {
                $result.Base = $tagged
                $result.AlreadyIn = $true
                return $result
            }
            # The range starts where Head and the release last shared history. On a linear main that
            # is the release's own commit; a tag moved by hand or a rewritten history still gives a
            # bounded range this way, never the whole history (which would be one API call per
            # commit since the first one).
            $common = git merge-base $tagged $Head
            if ($LASTEXITCODE -eq 0 -and $common) { $result.Base = $common }
        }
    }

    $range = if ($result.Base) { "$($result.Base)..$Head" } else { $Head }
    # Oldest first, so the notes read in the order the changes landed.
    $commits = @(git log --reverse --format=%H $range -- src)
    if ($LASTEXITCODE -ne 0) { throw "git log $range failed." }

    $byNumber = [ordered]@{}
    foreach ($commit in $commits) {
        if (-not $commit) { continue }
        $json = gh api "repos/$Repository/commits/$commit/pulls"
        if ($LASTEXITCODE -ne 0) { $result.ApiFailed = $true; continue }
        # Into a variable first, for the 5.1 reason given above the release list.
        $pulls = ($json -join "`n") | ConvertFrom-Json
        $merged = @($pulls | Where-Object { $_.merged_at -and $_.base.ref -eq $BaseBranch })
        if ($merged.Count -eq 0) { $result.OrphanCommits++; continue }
        foreach ($pr in $merged) {
            if ($byNumber.Contains([string] $pr.number)) { continue }
            $byNumber[[string] $pr.number] = [pscustomobject]@{
                number   = $pr.number
                body     = $pr.body
                labels   = @($pr.labels | ForEach-Object { $_.name })
                mergedAt = $pr.merged_at
            }
        }
    }

    # Commit order already follows merge order on a linear main; sorting by merge time keeps it so
    # when one pull request's commits sit between another's.
    $result.PullRequests = @($byNumber.Values | Sort-Object -Property mergedAt)
    return $result
}

Export-ModuleMember -Function Select-PreviousRelease, Get-ReleaseNotesSection, Join-ReleaseNotes, Test-Releasable, Get-ReleaseRange
