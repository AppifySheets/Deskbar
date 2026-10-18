# Licence and signing pipeline implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Put the repository under PolyForm Strict with a contribution permission, and make every release reach users only after its executables are Authenticode-signed on Petre's machine.

**Architecture:** The hosted workflow keeps building and testing, but now creates a draft release and attaches unsigned executables to it. A PowerShell module holds the signing and verification logic; a driver script on Petre's machine pulls the draft's assets, signs them with the certificate on his token, verifies, re-uploads and publishes. A second workflow job runs when a release is published and turns it back into a draft if any executable is unsigned, untimestamped or signed by the wrong certificate, so an unsigned build cannot reach the update checker.

**Tech Stack:** GitHub Actions (windows-latest), gh CLI 2.76, Windows PowerShell 5.1 for the signing module (Set-AuthenticodeSignature, Get-AuthenticodeSignature, Pester 3.4 which ships with it), PowerShell 7 for the workflow steps.

**Spec:** `docs/superpowers/specs/2026-10-18-deskbar-commercial-release-design.md`, steps 1 and 2 of "Order of work". Read it first; this plan argues from it.

## Global Constraints

- Branch `licence-and-signing` from `main`. Never push to `main`, never merge; open a PR. Commit messages end with the line `*fable5.1*` and nothing else after it.
- The product is still called TaskSpaces in this plan. The rename is a later plan; nothing here names Deskbar in code or workflow.
- Windows only. The release workflow stays on `windows-latest`; the publish step keeps `PublishSingleFile`, `--self-contained`, no trimming.
- Signing uses SHA-256 for the file digest and the timestamp digest, and an RFC 3161 timestamp. An unsigned or untimestamped executable never reaches a published release.
- The private key never enters the repository or GitHub secrets: it lives on the USB token. The only certificate fact GitHub holds is the public thumbprint, as the repository variable `SIGNING_CERT_THUMBPRINT`.
- Until `SIGNING_CERT_THUMBPRINT` is set, the guard warns and does not revert: the pipeline can merge before the certificate arrives.
- The README says "source available", never "open source".
- Ample intention comments in scripts and workflows, as in `release.yml` today.

## Review Focus

- A published release with no `.exe` asset at all: the guard must revert it, not pass vacuously, because a 1.x client pointed at it would announce an update it cannot fetch. Test in Task 2.
- The token's PIN prompt cancelled mid-run: `Set-AuthenticodeSignature` returns a non-Valid status; the driver must stop before uploading anything. Test in Task 2 (status handling) and Task 3 (driver stops on first failure).
- Timestamp server unreachable: the signature is Valid but carries no timestamp; verification must reject it so the driver never uploads it. Test in Task 2.
- An asset that is already signed: re-signing replaces the signature; verification then sees the new one. Test in Task 2.
- A publish of an old, pre-signing release re-triggered by hand once the thumbprint variable is set: the guard reverts it, which is correct and must be said in the workflow comment, not surprising.

---

## File structure

- `LICENSE`: PolyForm Strict 1.0.0 text plus the Required Notice line.
- `CONTRIBUTING.md`: the contribution permission and how to send a change.
- `README.md`: a "Licence" section; the "Development" section gains the release procedure.
- `scripts/ReleaseSigning.psm1`: pure functions, no gh calls: sign a file, verify a file, decide a release's verdict from per-file results.
- `scripts/ReleaseSigning.Tests.ps1`: Pester 3.4 tests for the module, using a throwaway self-signed certificate.
- `scripts/sign-release.ps1`: the driver Petre runs: gh download, sign, verify, upload, publish.
- `.github/workflows/release.yml`: build on `workflow_dispatch` into a draft; guard on `release: published`.

### Task 1: Licence, contribution permission, README wording

**Files:**
- Create: `LICENSE`
- Create: `CONTRIBUTING.md`
- Modify: `README.md` (new section before "## Tech"; one-line check of the whole file)

**Interfaces:**
- Produces: nothing code-facing. Later plans cite `LICENSE` from the Pro tab.

- [ ] **Step 1: Fetch the exact licence text**

Run from the repo root in bash:
```bash
gh api repos/polyformproject/polyform-licenses/contents/PolyForm-Strict-1.0.0.md --jq '.content' | base64 -d
```
Expected: markdown starting `# PolyForm Strict License 1.0.0` and containing the "Copyright License" clause that ends "other than distributing the software or making changes or new works based on the software."

- [ ] **Step 2: Write `LICENSE`**

The fetched text verbatim, preceded by exactly one line, then a blank line:
```
Required Notice: Copyright Petre Chitashvili (https://github.com/AppifySheets/TaskSpaces)
```
Use the Write tool, not a shell redirect (a hook turns `>` in Bash into a repo file).

- [ ] **Step 3: Write `CONTRIBUTING.md`**

Four short paragraphs, plain prose, no headings in title case:
1. The code is source available under PolyForm Strict (link to `LICENSE`): read it, build it for yourself, verify it phones nobody. It is not open source.
2. The additional permission, as the licence's own language: "In addition to the licence in LICENSE, the licensor grants you permission to make changes to the software for the purpose of submitting them to this repository, and to distribute those changes to the licensor by submitting them. By submitting a change you license it to the licensor under terms that allow its use, modification and distribution in the software under any licence the licensor chooses." That paragraph is the permission; keep its wording.
3. How to send a change: a pull request against `main`, one topic per PR, tests passing with `dotnet test TaskSpaces.sln --filter "Category!=Integration"`.
4. Commercial use needs the paid licence, which the app's Pro tab and the site explain once they exist.

- [ ] **Step 4: Add the "Licence" section to `README.md`**

Insert before `## Tech`:
```markdown
## Licence

The source is available under the [PolyForm Strict License 1.0.0](LICENSE): you may read
it, build it and run it for noncommercial purposes, and you may not distribute it or
publish changed versions. Contributions are welcome under the extra permission in
[CONTRIBUTING.md](CONTRIBUTING.md). This is source-available software, not open source.
```

- [ ] **Step 5: Check the README never says "open source"**

Run: `grep -n -i "open source\|open-source" README.md CONTRIBUTING.md`
Expected: only the two deliberate "not open source" sentences from steps 3 and 4. Reword anything else.

- [ ] **Step 6: Commit**

```bash
git add LICENSE CONTRIBUTING.md README.md
git commit -m "docs: PolyForm Strict licence, contribution permission, source-available wording

*fable5.1*"
```

### Task 2: The signing module and its tests

**Files:**
- Create: `scripts/ReleaseSigning.psm1`
- Create: `scripts/ReleaseSigning.Tests.ps1`

**Interfaces:**
- Produces, all exported from the module:
  - `Invoke-ExecutableSignature -Path <string> -Thumbprint <string> [-TimestampUrl <string>]` returns the `System.Management.Automation.Signature` from `Set-AuthenticodeSignature`. Finds the certificate in `Cert:\CurrentUser\My` by thumbprint; throws if absent. Omitting `-TimestampUrl` signs without a timestamp (tests only; the driver always passes one).
  - `Test-ExecutableSignature -Path <string> -Thumbprint <string>` returns a `[pscustomobject]` with `Path`, `Signed` (bool: status is not `NotSigned`), `Valid` (bool: status `Valid`), `ThumbprintMatches` (bool), `Timestamped` (bool: `TimeStamperCertificate` is not null), `Ok` (bool: all four true).
  - `Get-ReleaseVerdict -Results <object[]>` returns `[pscustomobject]` with `Ok` (bool) and `Reasons` (string[]). `Ok` is false when `Results` is empty ("no executable attached") or any result's `Ok` is false (reason names the file and the first failing field).

- [ ] **Step 1: Write the failing tests**

`scripts/ReleaseSigning.Tests.ps1`, Pester 3.4 syntax (`Describe`, `It`, `Should Be`), run under Windows PowerShell 5.1. `BeforeAll`-style setup goes at the top of the `Describe`: create a self-signed code signing certificate with `New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=ReleaseSigning test" -CertStoreLocation Cert:\CurrentUser\My`, remember its thumbprint, copy `$PSHOME\powershell.exe` to a temp file as the thing to sign; `AfterAll`: remove the certificate from the store and the temp file. Tests:

```powershell
It "signs a file so Get-AuthenticodeSignature sees the test certificate" {
    (Invoke-ExecutableSignature -Path $exe -Thumbprint $thumb).Status | Should Be 'UnknownError'   # self-signed: chain untrusted, signature present
    (Get-AuthenticodeSignature $exe).SignerCertificate.Thumbprint | Should Be $thumb
}
It "reports an unsigned file as not signed and not ok" {
    $r = Test-ExecutableSignature -Path $unsignedExe -Thumbprint $thumb
    $r.Signed | Should Be $false; $r.Ok | Should Be $false
}
It "reports a signed but untimestamped file as not ok" {
    Invoke-ExecutableSignature -Path $exe -Thumbprint $thumb | Out-Null
    $r = Test-ExecutableSignature -Path $exe -Thumbprint $thumb
    $r.Signed | Should Be $true; $r.Timestamped | Should Be $false; $r.Ok | Should Be $false
}
It "reports a thumbprint mismatch" {
    (Test-ExecutableSignature -Path $exe -Thumbprint ('0' * 40)).ThumbprintMatches | Should Be $false
}
It "re-signing replaces the signature" {
    # sign with cert A, then cert B (a second throwaway), expect B's thumbprint
}
It "a verdict over no results is not ok and says why" {
    $v = Get-ReleaseVerdict -Results @()
    $v.Ok | Should Be $false; $v.Reasons[0] | Should Be 'no executable attached'
}
It "a verdict over one failing result names the file" {
    $v = Get-ReleaseVerdict -Results @([pscustomobject]@{Path='a.exe';Signed=$true;Valid=$true;ThumbprintMatches=$true;Timestamped=$false;Ok=$false})
    $v.Ok | Should Be $false; $v.Reasons[0] | Should Match 'a\.exe'
}
It "a verdict over all-ok results is ok" { ... }
```

`Valid` cannot be exercised with a self-signed certificate (the chain is untrusted); `Ok` on a real certificate is covered by Task 3's live check. Do not install the test certificate as trusted root to force it.

- [ ] **Step 2: Run the tests to see them fail**

Run: `powershell.exe -NoProfile -Command "Invoke-Pester -Path scripts/ReleaseSigning.Tests.ps1"`
Expected: every `It` fails because the module does not exist.

- [ ] **Step 3: Write `scripts/ReleaseSigning.psm1`**

The three functions from Interfaces, `Export-ModuleMember` on all three. `Invoke-ExecutableSignature` calls `Set-AuthenticodeSignature -FilePath -Certificate -HashAlgorithm SHA256 -IncludeChain All` and adds `-TimestampServer` only when `-TimestampUrl` is given. Head comment: why PowerShell's own cmdlets rather than signtool (no Windows SDK dependency on the machine that signs; the cmdlets drive a token-backed key through its CSP the same way), and why the verdict is a separate pure function (the workflow guard and the driver both call it, and it is the one piece with branching worth testing).

- [ ] **Step 4: Run the tests to see them pass**

Run: `powershell.exe -NoProfile -Command "Invoke-Pester -Path scripts/ReleaseSigning.Tests.ps1"`
Expected: all pass; the store no longer holds `CN=ReleaseSigning test` afterwards (`Get-ChildItem Cert:\CurrentUser\My | Where-Object Subject -like '*ReleaseSigning test*'` prints nothing).

- [ ] **Step 5: Commit**

```bash
git add scripts/ReleaseSigning.psm1 scripts/ReleaseSigning.Tests.ps1
git commit -m "feat: release signing module with Pester tests

*fable5.1*"
```

### Task 3: The driver Petre runs

**Files:**
- Create: `scripts/sign-release.ps1`

**Interfaces:**
- Consumes: the module from Task 2.
- Produces: `scripts/sign-release.ps1 -Tag <string> -Thumbprint <string> [-TimestampUrl <string>] [-NoPublish]`. Default `-TimestampUrl` is `http://timestamp.digicert.com` (a free RFC 3161 server any certificate may use; the issuer's own is fine too).

- [ ] **Step 1: Write the driver**

Windows PowerShell 5.1 compatible (`#Requires -Version 5.1`), `Set-StrictMode -Version Latest`, `$ErrorActionPreference = 'Stop'`. Sequence, each a commented block:
1. `gh release view $Tag --json isDraft,assets` and refuse unless `isDraft` is true: "only drafts are signed here; a published release is already live".
2. The `.exe` assets' names; refuse with "no executable attached" if none.
3. Download each into a temp folder with `gh release download $Tag --pattern <name> --dir <tmp>`.
4. For each: `Invoke-ExecutableSignature` with the timestamp, then `Test-ExecutableSignature`; collect results.
5. `Get-ReleaseVerdict`; if not `Ok`, print the reasons and exit 1 before any upload.
6. `gh release upload $Tag <files> --clobber`.
7. Unless `-NoPublish`: `gh release edit $Tag --draft=false`, then print the release URL.
Head comment: the whole reason this runs on Petre's machine (the token) and why the repository's workflow does not trust it (the guard in Task 4 checks again).

- [ ] **Step 2: Live check against a throwaway draft**

With the Task 2 test certificate recreated for the moment (or any code signing certificate in `Cert:\CurrentUser\My`):
```bash
gh release create v0.0.0-signing-test --draft --title "signing test" --notes "throwaway" --target main
gh release upload v0.0.0-signing-test "src/TaskSpaces.App/bin/Debug/net10.0-windows10.0.19041.0/TaskSpaces.App.exe"
powershell.exe -NoProfile -File scripts/sign-release.ps1 -Tag v0.0.0-signing-test -Thumbprint <thumb> -NoPublish
```
Expected with the self-signed certificate: the script signs, then stops at the verdict with a reason naming the file and `Valid` (untrusted chain), uploads nothing, exits 1. Expected with a real certificate: uploads, prints the asset names, does not publish. Then:
```bash
gh release delete v0.0.0-signing-test --yes
```
Expected: the draft is gone; `gh release list` shows no `v0.0.0` entry.

- [ ] **Step 3: Commit**

```bash
git add scripts/sign-release.ps1
git commit -m "feat: sign-release driver: download a draft's executables, sign, verify, upload, publish

*fable5.1*"
```

### Task 4: The workflow: drafts in, guard on publish

**Files:**
- Modify: `.github/workflows/release.yml` (whole file; keep every existing comment that still applies)

**Interfaces:**
- Consumes: the module from Task 2 (the guard imports it from the checkout).
- Produces: the release procedure in Task 5.

- [ ] **Step 1: Rewrite the triggers and the build job**

`on:` becomes `workflow_dispatch` with a required string input `version` (example `2.0.0`, no leading `v`) plus `release: types: [published]`. The existing `build` job runs only on `workflow_dispatch` (`if: github.event_name == 'workflow_dispatch'`), takes its version from the input, builds, tests and publishes as today, then replaces "Attach the executable to the release" with:
```pwsh
gh release create "v${{ inputs.version }}" --draft --target ${{ github.sha }} --title "v${{ inputs.version }}" --generate-notes "artifacts/publish/${{ steps.artifact.outputs.name }}"
```
Comment why a draft: the executable is unsigned until Petre's machine signs it; drafts are invisible to the update checker (`releases/latest` skips them), so nothing unsigned is ever announced. Keep `permissions: contents: write`. Remove the old "a manual run builds and uploads nothing" comment since a manual run now creates the draft.

- [ ] **Step 2: Add the guard job**

Job `guard`, `runs-on: windows-latest`, `if: github.event_name == 'release'`, steps:
1. Checkout.
2. Download every `.exe` asset of `${{ github.event.release.tag_name }}` with `gh release download --pattern '*.exe' --dir assets`.
3. pwsh step: `Import-Module ./scripts/ReleaseSigning.psm1`; if `${{ vars.SIGNING_CERT_THUMBPRINT }}` is empty, print a `::warning::` that signing is not enforced and exit 0 (comment: lets this merge before the certificate exists). Otherwise `Test-ExecutableSignature` on each file, `Get-ReleaseVerdict`; if not ok, `gh release edit <tag> --draft` and fail with `::error::` listing the reasons.
Comment above the job: what it defends against (a release published without running the driver, a driver run against the wrong certificate, a timestamp that never happened), that the thumbprint is public and so safe as a variable, and that re-publishing an old 1.x release by hand after the variable is set will revert it, by design.

- [ ] **Step 3: Validate the workflow file**

Run: `gh workflow view Release` after pushing the branch, or locally `pwsh -c "(Get-Content .github/workflows/release.yml -Raw) | ConvertFrom-Yaml"` if the PowerShell-Yaml module is present; otherwise push the branch and run `gh workflow run Release --ref licence-and-signing -f version=0.0.1`.
Expected: the run creates a draft `v0.0.1` with one unsigned exe. Then `gh release delete v0.0.1 --yes`.

- [ ] **Step 4: Guard dry run**

Set no variable. Publish a draft created in step 3 (`gh release edit v0.0.1 --draft=false`).
Expected: the `guard` job runs, prints the "not enforced" warning, passes. Delete the release and its tag:
```bash
gh release delete v0.0.1 --yes --cleanup-tag
```

- [ ] **Step 5: Commit**

```bash
git add .github/workflows/release.yml
git commit -m "ci: releases are built as drafts and published only once signed

*fable5.1*"
```

### Task 5: The release procedure, written down

**Files:**
- Modify: `README.md` ("## Development" section)
- Modify: `CLAUDE.md` (the "Current state" bullet about releases)

- [ ] **Step 1: Write the procedure in `README.md`**

Under "## Development", a "Releasing" subsection, numbered:
1. Bump `VersionPrefix` in `Directory.Build.props` in the PR that earns it; merge.
2. Actions → Release → Run workflow, version `x.y.z`. A draft release `vx.y.z` appears with an unsigned exe.
3. On the signing machine, with the token plugged in: `scripts/sign-release.ps1 -Tag vx.y.z -Thumbprint <thumbprint>`. It signs, verifies, uploads and publishes.
4. The guard job checks the published assets and reverts the release to a draft if anything is unsigned.
One sentence: until the certificate exists, `SIGNING_CERT_THUMBPRINT` is unset and the guard only warns.

- [ ] **Step 2: Update `CLAUDE.md`**

Replace the sentence about `release.yml` building on publish with two lines: releases are drafted by `workflow_dispatch` and published by `scripts/sign-release.ps1`; the guard reverts unsigned publishes once `SIGNING_CERT_THUMBPRINT` is set.

- [ ] **Step 3: Commit and open the PR**

```bash
git add README.md CLAUDE.md
git commit -m "docs: the release procedure with signing

*fable5.1*"
git push -u origin licence-and-signing
gh pr create --title "Licence and signed releases" --body "<summary of the five tasks, link to the spec>

*fable5.1*"
```
