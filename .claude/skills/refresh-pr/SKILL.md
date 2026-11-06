---
name: refresh-pr
description: Bring an open Deskbar pull request back on top of its base, main or the pull request below it in a stack. Rebases the branch, builds Release with warnings as errors, runs the tests, force-pushes with a lease and watches the CI run until it is green. Use when GitHub says a pull request is out of date or conflicts, when its base moved, when asked to refresh or rebase a pull request, or when asked to stack several pull requests so they can be merged one after another.
argument-hint: [optional-branch-name-or-pr-number]
allowed-tools: Bash(git *), Bash(gh *), Bash(dotnet *), Read, Grep, Glob, Edit
---

# Refresh a pull request

A pull request here should sit directly on top of its base, so "Rebase and merge" can land it
without rewriting it. Main moves several times a day, so a branch that was fine when it was
opened falls behind and sometimes conflicts. This skill puts it back. No version number is
involved: the version comes from git history and a pull request never edits it. CLAUDE.md's rule on keeping open pull
requests on top of their base says when to run it.

## Input

A branch name or a pull request number: ``. If empty, use the branch checked out in the
current worktree.

To stack several pull requests, run the steps below once per pull request, bottom up: the first
on main, each next one on the branch of the one before it, with its base set to that branch
(`gh pr edit {number} --base {branch-below}`) so its diff shows only its own commits.

## Steps

### 1. Find the pull request and where to work on it

```bash
gh pr view {input} --json number,url,headRefName,headRefOid,baseRefName,state
git worktree list
```

- `MERGED` or `CLOSED`: stop and say so. There is nothing to refresh.
- `OPEN`: carry on.

Work in the worktree that has the branch checked out if it is yours. If another session's
worktree has it (it is often locked), look before touching anything:

```bash
git -C {their-worktree} status --short --branch
```

Uncommitted changes, or a branch ahead of origin, mean someone is working on it: stop and tell
the user. Clean and in step with origin means it is safe to rebase from your own worktree on a
local branch of a different name, since git will not check out a branch another worktree holds:

```bash
git fetch origin
git switch -c refresh-{number} origin/{headRefName}
```

Tell the user afterwards that the other worktree's copy of the branch is now behind origin.

### 2. Check whether there is anything to do

```bash
gh api repos/AppifySheets/Deskbar/compare/{baseRefName}...{headRefOid} --jq '{status,behind_by}'
```

`ahead` or `identical` means the branch already sits on its base: skip to step 6 and watch the
run. `behind` or `diverged` means rebase.

### 3. Rebase onto the base

```bash
git rebase origin/{baseRefName}
```

Never merge the base into the branch: a merge commit stops "Rebase and merge" from keeping the
commits, and every pull request stacked above would have to be rebuilt.

A conflict: `git rebase --abort` and tell the user which files. Resolving code
conflicts is a decision about the change, not a refresh. One exception: a purely mechanical
commit, such as a rename, can be replayed by redoing the mechanical step on the new base, as long
as the result is checked by hand afterwards.

### 4. Build and test what will be pushed

The rebase brought in other people's changes, so the branch is now code nobody has built:

```bash
dotnet build Deskbar.sln -c Release -warnaserror
dotnet test Deskbar.sln -c Release --no-build --filter "Category!=Integration"
```

A running Deskbar started from this worktree's `bin` locks the output; restart it from a copy
elsewhere first, never leave Petre without his bar. A test that captures the live screen can fail
while he is using the machine: rerun it alone before calling it real, and say in the report that
you did.

### 5. Push to the same branch with a lease

```bash
git push --force-with-lease={headRefName}:{headRefOid} origin HEAD:{headRefName}
```

The lease names the head read in step 1, so the push refuses if anyone pushed since. If it
refuses, fetch, look at what arrived, and tell the user rather than retrying. Never a plain
`--force`, never main.

### 6. Watch the CI run to the end

```bash
gh run list --branch {headRefName} --workflow CI --limit 1 --json databaseId,headSha,status
gh run watch {id} --exit-status
```

Check the run's `headSha` is the head just pushed. If it fails, read the failing step
(`gh run view {id} --log-failed`), fix it on the branch, push, and watch again. If the base moved
again meanwhile, go back to step 2.

### 7. Report

The pull request URL, its base, its new head, the test result
including anything rerun, and the CI run's result. Do not merge it.
