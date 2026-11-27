<#
.SYNOPSIS
  Runs one CI shard of the test suite. ci.yml runs it on two runners at once, each after its own build.

.DESCRIPTION
  WHY SHARDS ON SEPARATE RUNNERS. Nearly all of CI's test time is Deskbar.Windows.Tests (about two of the
  job's three and a half minutes), and that assembly runs one test at a time on purpose: its tests share
  process-wide caches (tests/Deskbar.Windows.Tests/Parallelism.cs). Splitting it into several test
  PROCESSES on one runner was measured on 2026-11-27 (Deskbar task 78d5481a): 9 of 10 runs failed, mostly
  in TasksBoardTests, so the tests also share something machine-wide. On separate runners every run
  passed. So the split is by runner, and each runner restores and builds for itself.

  HOW THE SPLIT IS MADE. Every shard lists the window tests of the same build, groups them by class and
  deals the classes out the same way, the biggest class first, each to the shard with the fewest tests so
  far. A test count was a good enough stand-in for time with two shards (71 s against 78 s in the
  benchmark), so no timings file has to be kept up to date.

  NO TEST CAN FALL THROUGH THE SPLIT. Shards 1 and up run the classes dealt to them, by name. Shard 0 runs
  everything NOT dealt to another shard, so a test whose class cannot be read off its listed name, or a
  class added between the listing and the run, still runs there. Shard 0 also runs Deskbar.Core.Tests
  (about 10 s), and with one shard it runs the whole suite.

  Category=Integration is excluded in every shard: those tests create, switch and delete real virtual
  desktops. Same filter as a local run.

.EXAMPLE
  ./scripts/Test-CiShard.ps1 -Shard 0 -Of 2    # after `dotnet build Deskbar.sln -c Release`
#>
param(
  [Parameter(Mandatory)][int]$Shard,
  [Parameter(Mandatory)][int]$Of,
  [string]$Configuration = 'Release',
  # Prints each `dotnet test` filter instead of running it: checks the split on a machine where the window
  # tests must not open their windows (listing the tests opens none).
  [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
if ($Of -lt 1 -or $Shard -lt 0 -or $Shard -ge $Of) { throw "Shard $Shard of $Of does not exist." }

$base = 'Category!=Integration'
$core = 'tests/Deskbar.Core.Tests/Deskbar.Core.Tests.csproj'
$win = 'tests/Deskbar.Windows.Tests/Deskbar.Windows.Tests.csproj'

# Runs `dotnet test` on one project with one filter, and stops the script if a test failed.
function Invoke-Tests($project, $filter) {
  if ($DryRun) { "Would test $project with: $filter"; return }
  dotnet test $project -c $Configuration --no-build --filter $filter --verbosity normal
  if ($LASTEXITCODE -ne 0) { throw "Tests failed in $project." }
}

# Every window test by full name (xunit lists them indented by four spaces), then its class: the name up to
# the method, with a theory's arguments cut off first.
$listed = dotnet test $win -c $Configuration --no-build --list-tests
if ($LASTEXITCODE -ne 0) { throw "Could not list the tests in $win." }
$classes = $listed |
  Where-Object { $_ -match '^\s{4}\S' } |
  ForEach-Object { $_.Trim() -replace '\(.*$', '' } |
  Where-Object { $_.Contains('.') } |
  ForEach-Object { $_.Substring(0, $_.LastIndexOf('.')) } |
  Group-Object |
  # Biggest first, ties by name, so every shard deals the same hand.
  Sort-Object @{ Expression = 'Count'; Descending = $true }, @{ Expression = 'Name' }

# The deal: each class to the shard with the fewest tests so far (the lowest number on a tie).
$shards = 0..($Of - 1) | ForEach-Object { [pscustomobject]@{ Tests = 0; Classes = [System.Collections.Generic.List[string]]::new() } }
$classes | ForEach-Object {
  $least = $shards | Sort-Object Tests | Select-Object -First 1
  $least.Tests += $_.Count
  $least.Classes.Add($_.Name)
}
0..($Of - 1) | ForEach-Object { "Shard $_ of $Of has $($shards[$_].Tests) window tests in $($shards[$_].Classes.Count) classes." }

# The trailing dot keeps a class from matching another whose name starts with it (Foo against FooBar).
if ($Shard -eq 0) {
  Invoke-Tests $core $base
  # (A PowerShell range counts down when its end is lower, so 1..0 would be 1, 0: hence the guard.)
  $others = if ($Of -gt 1) { 1..($Of - 1) | ForEach-Object { $shards[$_].Classes } | ForEach-Object { "FullyQualifiedName!~$_." } }
  Invoke-Tests $win ((@($base) + $others) -join '&')
} else {
  $mine = $shards[$Shard].Classes | ForEach-Object { "FullyQualifiedName~$_." }
  if (-not $mine) { "Shard $Shard got no classes; nothing to run."; exit 0 }
  Invoke-Tests $win "($base)&($($mine -join '|'))"
}
