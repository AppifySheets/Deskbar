# The rename to Deskbar

Step 3 of the commercial release design
(`2026-10-18-deskbar-commercial-release-design.md`): the app stops being TaskSpaces and
becomes Deskbar, shown as "Deskbar for Windows", version 2.0.0, without losing a single
user's workspaces, renames, roster or autostart. Written 2026-10-19. Decisions marked
"Petre" are his; "my call" is the author's and can be overturned.

## Intent

Everything a person or Windows can see says Deskbar: the exe, its file properties, the
tray, every window title and message box, the AppData folder, the autostart entry, the
control shim and its help, the release assets, the repository. The code inside keeps its
history. The existing users, Petre and a few colleagues on 1.x, update through the
existing checker and find everything where they left it.

## What is renamed, and what is not

Renamed (user-facing and runtime names, about a hundred lines):

- The entry assembly: `<AssemblyName>Deskbar</AssemblyName>` in `TaskSpaces.App.csproj`,
  so the published exe is `Deskbar.exe`. The `pack://` URIs in `App.xaml.cs`,
  `ManageWindow.xaml` and `PromptDialog.xaml` change their assembly segment to match,
  and `StartupRegistration.IsTheApp` compares against `Deskbar`.
- `Directory.Build.props`: `Product` Deskbar, `Description` "The taskbar for your
  virtual desktops: name them, see every window on one bar.", `RepositoryUrl`
  `https://github.com/AppifySheets/Deskbar`, `VersionPrefix` 2.0.0, `AssemblyVersion`
  2.0.0.0 (a major version, so the x.0.0.0 rule gives 2).
- Runtime identifiers, each exactly one literal today:
  pipe `Deskbar.Control.{sessionId}`; mutex `Local\Deskbar.SingleInstance`; AppData
  folder `%APPDATA%\Deskbar`; Run value name `Deskbar`; shim `deskbar.cmd` and its
  `Caller` in `ControlUsage`; trace marker `%APPDATA%\Deskbar\trace.on`, env var
  `DESKBAR_TRACE`, log `%TEMP%\deskbar-trace.log`; registered message `Deskbar.AppBar`;
  the `HwndSourceParameters` names, the drag format `Deskbar.DraggedWindow`, the tab
  counts thread name.
- Every string a user reads: window titles ("Deskbar", "Deskbar: Manage", "Deskbar
  switcher"), the tray tooltip and balloon, the stand-in's text, the update progress
  window, every message-box caption and body in `App.xaml.cs`, `FloatingBar.xaml.cs`
  and `ManageWindow.xaml.cs`, the Manage window's labels, `ControlUsage`'s exit-code
  and shim lines, the control client and server messages, `Chord.cs` and
  `WorkspaceManager.cs` messages. "Deskbar" replaces "TaskSpaces" one for one; nothing
  is reworded beyond that.
- The update checker: `LatestRelease` and `ReleasesPage` point at `AppifySheets/Deskbar`,
  the User-Agent is `Deskbar/{version}`. A new `UpdateService.StoreProductId` constant,
  empty until the Store listing exists; when non-empty the update notice adds "Also on
  the Microsoft Store" linking `ms-windows-store://pdp/?productid=<id>`. My call: a
  constant rather than a site redirect, because no site exists yet and the Store
  listing is a later step; filling it is a one-line PR.
- Release workflow: the asset is `Deskbar-<version>-win-x64.exe`, the publish command
  names the same csproj (unchanged path) and the `Move-Item` source is `Deskbar.exe`.
  The VersionPrefix check already enforces 2.0.0.
- The icon file: `Assets/deskbar.ico`, same artwork for now (new artwork is not this
  step). `taskspaces-256.png` and `docs/branding/taskspaces-mark-*.png` are renamed to
  `deskbar-*` with the README's image references.
- README: title, badges, Install section (exe name, AppData path, Run value), the shim
  name, the Releasing section's asset and script examples. The "Windows protected your
  PC" section stays, since the direct exe stays unsigned (see the frame design's
  "Store first" decision). The Install section gets a placeholder line for the Store
  badge, commented out in HTML until the listing exists.
- CLAUDE.md: the title, the terminology line, the dev run path
  (`src/TaskSpaces.App/bin/Debug/net10.0-windows10.0.19041.0/Deskbar.exe`), the shim
  path, and a line saying the code namespaces are still `TaskSpaces.*` by decision.
- The manual test script and the LICENSE Required Notice URL.
- The stray tracked file `155000'` at the repo root is deleted.

Not renamed (my call, and the reason matters): the C# namespaces, the project and
directory names, the solution file, the test project names, and the `TaskSpaces.*`
DLL names inside the single-file bundle. A thousand-line namespace sweep touches every
file in the repository and would collide with the work another session has in flight
on the bar; nobody sees those names once the exe is built. It can be a mechanical PR of
its own on a quiet day, and CLAUDE.md says so.

## The migration, on first start of 2.0

Runs once, in `App.OnStartup` before the state store is opened, inside the app that
owns the state (the frame design's constraint: nothing outside the app edits it).

1. If `%APPDATA%\Deskbar` does not exist and `%APPDATA%\TaskSpaces` does: create the
   new folder and copy `state.json`, `time.json` and `last-used.json` when present.
   Copy, not move: a rollback to 1.x or a second machine syncing the old folder keeps
   working. `trace.on` and the old shim are not copied. The old folder is left as it
   is, forever; the README says it may be deleted by hand.
2. Autostart: if the Run value `TaskSpaces` exists, delete it, and set `Deskbar` to the
   running exe's path if `Deskbar` is not already set. This is what keeps "start with
   Windows" true across the rename, including for a user whose 1.x updater just started
   the 2.0 exe. Without it, two bars would start at the next login.
3. The old shim `%APPDATA%\TaskSpaces\taskspaces.cmd` is deleted if present, so a
   stale shim cannot launch the old exe; the new `deskbar.cmd` is written as today.
4. A running 1.x: the mutex names differ, so 2.0 would not see it. 2.0 holds BOTH
   mutexes, `Local\Deskbar.SingleInstance` and `Local\TaskSpaces.SingleInstance`, so a
   1.x exe started while 2.0 runs sees "already running" and does what it does today.
   If the old mutex is already held at 2.0's start and this start is not the updater
   handshake, 2.0 shows "Exit TaskSpaces from its tray icon first, then start Deskbar."
   and exits. My call: the control pipe deliberately exposes no Exit, so there is no
   clean way to stop the old instance from here.
5. The handshake. Today `--await-previous` waits on the mutex name, and 1.x releases its
   mutex before it starts the new exe, so with the new name 2.0 would not wait at all
   and could copy state while 1.x's exit handler is still writing `time.json`. So on
   `--await-previous`, 2.0 waits for its PARENT PROCESS to exit, bounded at 15 seconds,
   before anything else, and only then migrates. The parent is found through the
   process's parent id (`NtQueryInformationProcess` or the WMI `ParentProcessId`); if
   it is already gone, the wait is over.

Development on a machine that still runs 1.x (Petre's, until this merges): the
environment variable `DESKBAR_APPDATA`, when set, replaces `%APPDATA%` as the root under
which both `TaskSpaces` and `Deskbar` folders are resolved, and in that mode the
migration touches no registry value and writes the shim under the override root. A
Debug 2.0 build therefore runs against a scratch root, and the migration can be
rehearsed by copying the real `TaskSpaces` folder into that root. CLAUDE.md says: until
2.0 is on main, run Debug Deskbar builds with `DESKBAR_APPDATA` set, or the first start
migrates the real profile and the 1.x builds from main stop seeing later changes.

Rollback is "the old files are where they were at migration time", nothing more: a copy
happens once, changes made in 2.0 are not carried back, and a second upgrade after a
rollback does not copy again. After a rollback, login still starts 2.0 until "Start
with Windows" is unticked and ticked in 1.x. The README's rollback paragraph says so.

The migration is a pure plan computed from facts (which files exist, which Run values
exist, whether the old mutex is held, whether the root is overridden) and applied by a
thin Windows layer, so the decision logic is unit-tested in `TaskSpaces.Core.Tests` and
the registry and file operations are tested in `TaskSpaces.Windows.Tests` against a
temporary directory and the real HKCU Run key, as `StartupRegistrationTests` already
does.

## The 2.x update checker picks its asset by name

Settled now, because every 2.0 client runs whatever this PR ships and a later change
would need a 2.0.x release just to fix selection. Release assets are
`Deskbar-<version>-win-x64.exe` (portable) and, once an installer exists,
`Deskbar-Setup-<version>-win-x64.exe`. `UpdateCheck.ReadRelease` takes a flag for the
running copy's kind: the portable build prefers the first `.exe` whose name does not
contain `Setup`, falling back to the first `.exe`; an installed build (future) prefers
the one that does. The 1.x checker keeps taking the first `.exe`, so the installer, if
it ever ships, is uploaded first for them and the 2.x portable clients skip it by name.

## The handover from 1.x

The 1.x checker reads `releases/latest` of the old repository URL, takes the first
`.exe` asset, downloads it beside the running exe and starts it with
`--await-previous`. So the first 2.0 release must satisfy:

- The repository is renamed on GitHub before the release is published (Petre, in the
  repository settings); GitHub redirects the old API and download URLs, and the 1.x
  `HttpClient` follows redirects (default handler, measured in the inventory).
- The release carries exactly one `.exe`, `Deskbar-2.0.0-win-x64.exe`.
- `Deskbar.exe` accepts `--await-previous` and waits for the old process to exit (see
  the migration's step 5), since the mutex-name wait no longer applies.
- The migration's autostart step runs on that first start, so the Run value points at
  `Deskbar-2.0.0-win-x64.exe` rather than the old file.

The old exe is left in place next to the new one, as the 1.x updater has always done.

## Goals

- A user on 1.26 or 1.27 accepts the update and sees the same bar, same workspaces,
  same renames, same autostart, under the new name.
- `taskspaces.cmd` stops working and `deskbar.cmd help` works, with the same commands.
- Nothing is lost on rollback: the old folder and the old exe are untouched.

## Non-goals

- New artwork, a new icon, a new README hero. Same mark, new file names.
- The installer, the Store package, the free tier, the key. Later steps.
- Renaming the C# namespaces and projects.
- Deleting `%APPDATA%\TaskSpaces`.

## Anti-goals

- A user who starts Deskbar while TaskSpaces 1.x runs and gets two bars.
- A migration that moves rather than copies, so a rollback finds nothing.
- A 1.x client that downloads 2.0 and cannot start it, because the asset name or the
  handshake changed.
- A release published before the repository is renamed, so the new checker URL 404s on
  the next check.

## Constraints

- Everything the frame design lists, plus: the branch rebases onto `main` right before
  Petre merges, because the other session changes `App.xaml.cs`, `FloatingBar.xaml.cs`
  and `ManageWindow.xaml.cs` daily and this PR touches the same files in a few dozen
  single-line string edits. Conflicts are expected to be trivial; the plan says how to
  resolve them (take theirs, re-apply the string).
- `dotnet test TaskSpaces.sln --filter "Category!=Integration"` green; the Windows
  tests that assert the Run value, the shim name and `IsTheApp` are updated, not
  deleted.
- Version 2.0.0 in `Directory.Build.props` in this PR, per the version convention.

## Order of work for Petre, outside the code

1. Rename the GitHub repository to `AppifySheets/Deskbar` just before merging this PR.
2. Merge, run the Release workflow with version 2.0.0, publish the draft by hand (the
   interim unsigned path) as the README's Releasing section says.
3. Watch one 1.x machine accept the update.
4. On his own machine, after the update: the global `~/.claude/CLAUDE.md` and the
   session memory still name `%APPDATA%\TaskSpaces\taskspaces.cmd`; they change to
   `%APPDATA%\Deskbar\deskbar.cmd` the same day, or every session's window moves fail.
   Outside this repository, so it is a line here and not a task.

## Left to the builder

- The exact shape of the migration plan type and its Windows applier.
- Which tests carry the new literals; keeping fixture strings that merely use
  "TaskSpaces" as sample window titles unchanged.
- Whether the `HwndSourceParameters` names need to change at all (they are not
  user-visible; rename for consistency if it costs nothing).

## Left open for Petre

Nothing. The namespace decision and the Store link constant are the author's calls
and are reversible.
