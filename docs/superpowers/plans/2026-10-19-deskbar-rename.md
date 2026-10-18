# Deskbar rename implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The app becomes Deskbar 2.0.0 everywhere a person or Windows can see it, and every 1.x user who takes the update keeps their workspaces, renames, roster and autostart.

**Architecture:** User-facing and runtime names change; C# namespaces, project files and the solution do not. A pure migration plan in Core decides what to copy, which Run value to swap and whether to refuse to start, from facts the App gathers; a thin applier in the App does the file and registry work. The update handshake gains a parent-process wait. The update checker learns to pick an asset by name.

**Tech Stack:** .NET 10, WPF, xunit (Core tests pure; Windows tests may touch temp folders and the real HKCU Run key under the test-host guard), CSharpFunctionalExtensions Result/Maybe, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-10-19-deskbar-rename-design.md`; its frame is `docs/superpowers/specs/2026-10-18-deskbar-commercial-release-design.md`. Read the rename design first; the plan argues from it.

## Global Constraints

- Branch `deskbar-rename` (cut from `licence-and-signing`). Never push to `main`, never merge; open a PR. Rebase onto `main` right before Petre merges: the other session edits `App.xaml.cs`, `FloatingBar.xaml.cs` and `ManageWindow.xaml.cs` daily, and every conflict here is a one-line string edit: take theirs, re-apply the string.
- Commit messages end with the single line `*fable5.1*` and nothing else. Conventional subjects.
- C# style per the repository: `var`, expression bodies, records, no `private` keyword, Result/Maybe for expected failures, RX where events would be, ample intention comments in the voice of the files.
- "Deskbar" replaces "TaskSpaces" one for one in user-visible strings; nothing is reworded beyond that. The Store deep link is `ms-windows-store://pdp/?productid=<id>`.
- Namespaces `TaskSpaces.*`, project and directory names, `TaskSpaces.sln` and the test project names stay. `AssemblyName` of the App project becomes `Deskbar`.
- `Directory.Build.props`: `VersionPrefix` 2.0.0, `AssemblyVersion` 2.0.0.0, `Product` Deskbar, `Description` "The taskbar for your virtual desktops: name them, see every window on one bar.", `RepositoryUrl` `https://github.com/AppifySheets/Deskbar`.
- Runtime literals, exactly: pipe `Deskbar.Control.{sessionId}`; mutexes `Local\Deskbar.SingleInstance` and (also held) `Local\TaskSpaces.SingleInstance`; folders `%APPDATA%\Deskbar` (new) and `%APPDATA%\TaskSpaces` (old); env override `DESKBAR_APPDATA`; Run value `Deskbar` (old `TaskSpaces`); shim `deskbar.cmd`; trace `DESKBAR_TRACE`, `%TEMP%\deskbar-trace.log`, `%APPDATA%\Deskbar\trace.on`; registered message `Deskbar.AppBar`; drag format `Deskbar.DraggedWindow`.
- Update URLs: `https://api.github.com/repos/AppifySheets/Deskbar/releases/latest`, `https://github.com/AppifySheets/Deskbar/releases/latest`, User-Agent `Deskbar/{version}`. Release asset `Deskbar-<version>-win-x64.exe`.
- `dotnet test TaskSpaces.sln --filter "Category!=Integration"` green at every commit. Never run the app live against Petre's real profile during this plan; the migration is proven by tests, and Petre rehearses it himself with `DESKBAR_APPDATA` if he wants to.
- A hook turns `>` in a Bash command into a stray repo file: edit with the Edit and Write tools only; `git status --short` before every `git add`.

## Review Focus

- The 1.x updater starts `Deskbar-2.0.0-win-x64.exe --await-previous` beside the old exe: 2.0 waits for the parent to exit, migrates, swaps the Run value to its own path, and the user sees one bar with their workspaces. Tests in Task 3 (plan: facts with old folder present, new absent, awaiting previous) and Task 3's handshake test.
- A user double-clicks `Deskbar.exe` while TaskSpaces 1.x runs: the old mutex is held, no handshake switch, so 2.0 shows "Exit TaskSpaces from its tray icon first, then start Deskbar." and exits without migrating. Test in Task 3 (plan: old mutex held and not awaiting).
- A fresh machine with no old folder: no copy, no registry change, no message, `%APPDATA%\Deskbar` created as today. Test in Task 3.
- Both folders already exist (second start, or a second upgrade after a rollback): nothing copied, nothing deleted. Test in Task 3.
- Run value `TaskSpaces` and `Deskbar` both present: only the old one is deleted; the new one is not overwritten. Test in Task 3.
- Under the test host (entry assembly `testhost`), the migration never touches the registry, the same guard as `StartupRegistration`. Test in Task 3.

---

## File structure

- `src/TaskSpaces.App/TaskSpaces.App.csproj`: `AssemblyName`, icon paths.
- `src/TaskSpaces.App/Assets/deskbar.ico` (renamed), `deskbar-256.png` (renamed); `docs/branding/deskbar-mark-{256,512}.png` (renamed).
- `Directory.Build.props`: identity and version.
- `src/TaskSpaces.App/AppPaths.cs` (new): resolves the AppData root (env override or `%APPDATA%`), the old and new state folders, and whether the root is overridden. One place, because three call sites today build the path by hand.
- `src/TaskSpaces.Core/Migration/RenameMigration.cs` (new): `RenameFacts` record, `RenameStep` union, `Plan(RenameFacts) -> IReadOnlyList<RenameStep>`.
- `src/TaskSpaces.App/RenameMigrator.cs` (new): gathers facts, applies steps (copy, delete, registry), guarded like `StartupRegistration`.
- `src/TaskSpaces.App/App.xaml.cs`: mutexes, handshake parent wait, migration call, state folder via `AppPaths`, strings.
- `src/TaskSpaces.App/StartupRegistration.cs`: value name, `IsTheApp`, plus `MigrateFromTaskSpaces()`.
- `src/TaskSpaces.App/ControlCommandLine.cs`, `src/TaskSpaces.Core/Control/ControlWire.cs`, `src/TaskSpaces.Core/Control/ControlUsage.cs`, `src/TaskSpaces.Windows/Control/*.cs`: shim, pipe, usage, messages.
- `src/TaskSpaces.App/UpdateService.cs`, `src/TaskSpaces.Core/Updates/UpdateCheck.cs`: URLs, agent, Store constant, asset selection.
- Strings across `App.xaml.cs`, `FloatingBar.xaml`, `FloatingBar.xaml.cs`, `ManageWindow.xaml`, `ManageWindow.xaml.cs`, `WorkspaceSwitcher.xaml`, `BarStandIn.cs`, `UpdateProgress.cs`, `ClickTrace.cs`, `AppBar.cs`, `HotkeyService.cs`, `DraggedWindow.cs`, `BrowserTabs.cs`, `ShellHookAttentionMonitor.cs`, `Chord.cs`, `WorkspaceManager.cs`.
- `.github/workflows/release.yml`: asset and exe names.
- Tests: `tests/TaskSpaces.Core.Tests/Migration/RenameMigrationTests.cs` (new), `UpdateCheckTests.cs`, `RemoteControlMenuTests.cs`, `OwnWindowTests.cs`, `RenameAppTests.cs`; `tests/TaskSpaces.Windows.Tests/RenameMigratorTests.cs` (new), `StartupRegistrationTests.cs`, `ControlShimTests.cs`, `CrashRelaunchTests.cs`.
- `README.md`, `CLAUDE.md`, `docs/superpowers/notes/manual-test-script.md`, `LICENSE` (URL), delete `155000'`.

### Task 1: Build identity

**Files:**
- Modify: `src/TaskSpaces.App/TaskSpaces.App.csproj`, `Directory.Build.props`, `.github/workflows/release.yml:154-172`, `LICENSE:1`
- Rename: `src/TaskSpaces.App/Assets/taskspaces.ico` to `deskbar.ico`; `Assets/taskspaces-256.png` to `deskbar-256.png`; `docs/branding/taskspaces-mark-256.png` and `-512.png` to `deskbar-mark-*.png`
- Modify: `src/TaskSpaces.App/App.xaml.cs:533`, `ManageWindow.xaml:10`, `PromptDialog.xaml:6` (pack URIs), `src/TaskSpaces.App/StartupRegistration.cs:28`
- Delete: `155000'`
- Test: `tests/TaskSpaces.Windows.Tests/StartupRegistrationTests.cs:25`

**Interfaces:**
- Produces: entry assembly name `Deskbar`; `StartupRegistration.IsTheApp("Deskbar") == true`, `IsTheApp("TaskSpaces.App") == false`.

- [ ] **Step 1: Update the test**

In `StartupRegistrationTests.cs`, the theory's first row becomes `[InlineData("Deskbar", true)]` and a new row `[InlineData("TaskSpaces.App", false)]` is added, with a comment: the old entry assembly name is refused on purpose, so a 1.x build never registers under the new value.

- [ ] **Step 2: Run it to see it fail**

Run: `dotnet test tests/TaskSpaces.Windows.Tests --filter "FullyQualifiedName~StartupRegistrationTests" --no-restore`
Expected: the two rows fail.

- [ ] **Step 3: Rename the assembly and identity**

- `TaskSpaces.App.csproj`: add `<AssemblyName>Deskbar</AssemblyName>` to the PropertyGroup with a comment (the exe and entry assembly are Deskbar; the project and namespaces keep their names, see the design); `Resource` and `ApplicationIcon` point at `Assets\deskbar.ico`.
- `git mv` the four image files.
- Pack URIs become `pack://application:,,,/Deskbar;component/Assets/deskbar.ico`.
- `StartupRegistration.IsTheApp`: `entryAssembly == "Deskbar"`; update its comment (release exe `Deskbar-2.0.0-win-x64.exe`, debug `Deskbar.exe`, both entry assembly `Deskbar`).
- `Directory.Build.props` values per Global Constraints; update the comment that says AssemblyVersion is kept at x.0.0.0 (2.0.0.0 now).
- `release.yml`: `$name = "Deskbar-..."`, `Move-Item artifacts/publish/Deskbar.exe`, and the two comment lines naming the old exe.
- `LICENSE` line 1 URL to `AppifySheets/Deskbar`.
- `git rm "155000'"`.

- [ ] **Step 4: Build and run the test**

Run: `dotnet build TaskSpaces.sln -c Debug` then the Step 2 command.
Expected: build clean; `src/TaskSpaces.App/bin/Debug/net10.0-windows10.0.19041.0/Deskbar.exe` exists; tests pass.

- [ ] **Step 5: Commit**

```bash
git add -A src/TaskSpaces.App/TaskSpaces.App.csproj src/TaskSpaces.App/Assets docs/branding Directory.Build.props .github/workflows/release.yml LICENSE src/TaskSpaces.App/App.xaml.cs src/TaskSpaces.App/ManageWindow.xaml src/TaskSpaces.App/PromptDialog.xaml src/TaskSpaces.App/StartupRegistration.cs tests/TaskSpaces.Windows.Tests/StartupRegistrationTests.cs "155000'"
git commit -m "feat: the exe, its identity and its icon are Deskbar (2.0.0)" -m "*fable5.1*"
```

### Task 2: Runtime names and every visible string

**Files:**
- Create: `src/TaskSpaces.App/AppPaths.cs`
- Modify: `App.xaml.cs` (mutex at 577 and 611, state dir at 708, shim at 838, tray at 849, every caption and body the inventory lists), `ControlWire.cs:18`, `ControlCommandLine.cs:79-86`, `ControlUsage.cs:71,90,96`, `ControlClient.cs`, `ControlPipeServer.cs`, `ClickTrace.cs:160-165,184`, `AppBar.cs:45`, `HotkeyService.cs:49`, `FloatingBar.xaml:4`, `FloatingBar.xaml.cs:644,5935,6733`, `ShellHookAttentionMonitor.cs:46`, `DraggedWindow.cs:16`, `BrowserTabs.cs:70`, `ManageWindow.xaml:9,38,85,105,109,126`, `ManageWindow.xaml.cs:307,429,436`, `WorkspaceSwitcher.xaml:4`, `BarStandIn.cs:52,80`, `UpdateProgress.cs:52,63,93`, `Chord.cs:127`, `WorkspaceManager.cs:811,2967`, `UpdateService.cs:23,29,44`
- Test: `tests/TaskSpaces.Windows.Tests/ControlShimTests.cs:39`, `tests/TaskSpaces.Core.Tests/RemoteControlMenuTests.cs:417`, `OwnWindowTests.cs:26-29`, `RenameAppTests.cs:151`, `CrashRelaunchTests.cs:36`

**Interfaces:**
- Produces: `static class AppPaths { static string Root { get; } static bool RootOverridden { get; } static string NewStateDir { get; } static string OldStateDir { get; } }` where `Root` is `DESKBAR_APPDATA` when set and non-empty, else `Environment.GetFolderPath(ApplicationData)`; `NewStateDir = Root\Deskbar`; `OldStateDir = Root\TaskSpaces`.
- Produces: `UpdateService.StoreProductId` (`public const string`, empty), used by the update notice in `App.xaml.cs` (`StartUpdateChecks` / the balloon and dialog around lines 189-260): when non-empty, the dialog gains a line "Also on the Microsoft Store" that opens `ms-windows-store://pdp/?productid={StoreProductId}` through `OpenUrl`.
- Produces: both mutexes held for the process lifetime; `singleInstance` stays the new one, a second field `legacyInstance` holds the old name and is only ever disposed at exit.

- [ ] **Step 1: Update the tests**

- `ControlShimTests`: the written file is `deskbar.cmd`; fixture exe names may stay.
- `RemoteControlMenuTests:417`: `"usage: deskbar.cmd rename <workspace> <new name>"`.
- `OwnWindowTests` and `RenameAppTests`: the own window's process becomes `"Deskbar"`, path `C:\apps\Deskbar.exe`, title `"Deskbar: Manage"`. If `WorkspaceManager.IsOurs` compares against a literal process name, that literal becomes `Deskbar`; if it takes the running process name, nothing in code changes. Say which in the report.
- `CrashRelaunchTests:36`: fixture path `C:\nowhere\Deskbar.exe`.

- [ ] **Step 2: Run the touched test classes to see the new literals fail**

Run: `dotnet test TaskSpaces.sln --filter "FullyQualifiedName~ControlShimTests|FullyQualifiedName~RemoteControlMenuTests|FullyQualifiedName~OwnWindowTests|FullyQualifiedName~RenameAppTests|FullyQualifiedName~CrashRelaunchTests" --no-restore`
Expected: the shim and usage tests fail; the own-window tests fail only if a literal exists.

- [ ] **Step 3: Add `AppPaths` and route the three call sites through it**

`App.xaml.cs:708` uses `AppPaths.NewStateDir`; `ClickTrace.cs:160` marker path uses it too; the shim write at 838 passes `AppPaths.NewStateDir`. Head comment on `AppPaths`: why the override exists (a Debug 2.0 on a machine still running 1.x must not migrate the real profile) and that in override mode the migration touches no registry (Task 3 reads `RootOverridden`).

- [ ] **Step 4: Rename every runtime literal and visible string**

Per Global Constraints and the inventory in the design. The second mutex: created right after the first with the old name, `initiallyOwned: true`, result ignored except for the Task 3 probe; disposed where `singleInstance` is. The handshake loop at 598-611 re-creates only the new-name mutex. Update the comments that explain the mutex names.

- [ ] **Step 5: Update URLs and the Store constant**

`UpdateService`: both URLs, the User-Agent, and `public const string StoreProductId = "";` with a comment: set when the Store listing exists; the update notice shows a Store line only then. In `App.xaml.cs`'s update dialog, add the conditional line and link.

- [ ] **Step 6: Run the full suite**

Run: `dotnet test TaskSpaces.sln --filter "Category!=Integration"`
Expected: green. Then `grep -rn "TaskSpaces" src --include=*.cs --include=*.xaml | grep -v "namespace TaskSpaces\|using TaskSpaces\|x:Class=\"TaskSpaces\|global::TaskSpaces\|TaskSpaces\.\(Core\|Windows\|App\)\b" | grep -v "SingleInstance\|OldStateDir\|\"TaskSpaces\"" | head -40` prints only comments that talk about the old name deliberately (migration, the old mutex, the old folder). Anything else is a miss.

- [ ] **Step 7: Commit**

```bash
git commit -am "feat: every name Windows or a person sees says Deskbar" -m "*fable5.1*"
```
(Use `git add` on the new file first; `-a` does not add untracked files.)

### Task 3: The migration and the handshake

**Files:**
- Create: `src/TaskSpaces.Core/Migration/RenameMigration.cs`, `src/TaskSpaces.App/RenameMigrator.cs`
- Modify: `src/TaskSpaces.App/StartupRegistration.cs` (add `MigrateFromTaskSpaces`), `src/TaskSpaces.App/App.xaml.cs` (handshake wait, migration call, refusal message)
- Test: `tests/TaskSpaces.Core.Tests/Migration/RenameMigrationTests.cs` (new), `tests/TaskSpaces.Windows.Tests/RenameMigratorTests.cs` (new)

**Interfaces:**
- Produces, in Core:
  ```csharp
  public sealed record RenameFacts(
      bool OldFolderExists, bool NewFolderExists,
      IReadOnlyList<string> OldFilesPresent,   // names among state.json, time.json, last-used.json
      bool OldShimExists,
      bool OldRunValueExists, bool NewRunValueExists,
      bool OldMutexHeld, bool AwaitingPrevious,
      bool RootOverridden, bool IsTheApp);
  public abstract record RenameStep;
  public sealed record RefuseToStart(string Message) : RenameStep;   // the exact message from the design
  public sealed record CopyFile(string Name) : RenameStep;
  public sealed record DeleteOldShim : RenameStep;
  public sealed record DeleteOldRunValue : RenameStep;
  public sealed record SetNewRunValueToThisExe : RenameStep;
  public static class RenameMigration { public static IReadOnlyList<RenameStep> Plan(RenameFacts facts); }
  ```
  Rules: `RefuseToStart` alone when `OldMutexHeld && !AwaitingPrevious`. `CopyFile` for each present old file only when `OldFolderExists && !NewFolderExists`. `DeleteOldShim` when `OldShimExists`. Registry steps only when `IsTheApp && !RootOverridden`: `DeleteOldRunValue` when `OldRunValueExists`; `SetNewRunValueToThisExe` when `OldRunValueExists && !NewRunValueExists`.
- Produces, in App: `static class RenameMigrator { static RenameFacts Gather(bool awaitingPrevious, bool oldMutexHeld); static Result Apply(IReadOnlyList<RenameStep> steps, Action<string> trace); }`. `Apply` returns a failure carrying the `RefuseToStart` message when that step is present and performs nothing else; copies with `File.Copy(overwrite: false)` after creating the new folder; registry steps go through `StartupRegistration.MigrateFromTaskSpaces(deleteOld, setNew)`, which is guarded by `OwnedByThisProcess()` like `Enable`.
- Produces, in App: on `--await-previous`, before the mutex loop, `AwaitParentExit(TimeSpan.FromSeconds(15))`: parent pid via `NtQueryInformationProcess` (ProcessBasicInformation, `InheritedFromUniqueProcessId`) with P/Invoke declared `CharSet.Unicode` where strings are involved; `Process.GetProcessById(parent).WaitForExit(timeout)` wrapped so a vanished parent is not an error.

- [ ] **Step 1: Write the failing Core tests**

`RenameMigrationTests`, one test per rule, names say the scenario:
```csharp
[Fact] public void A_first_start_after_the_update_copies_the_old_files_and_swaps_the_run_value()
  // facts: old folder, new absent, files [state.json, time.json], old shim, old run value, no new run value, not held, awaiting, not overridden, is the app
  // expect: CopyFile state.json, CopyFile time.json, DeleteOldShim, DeleteOldRunValue, SetNewRunValueToThisExe (order: copies, shim, registry)
[Fact] public void Starting_beside_a_running_taskspaces_refuses_and_does_nothing_else()
  // old mutex held, not awaiting -> exactly [RefuseToStart("Exit TaskSpaces from its tray icon first, then start Deskbar.")]
[Fact] public void The_updater_handshake_does_not_refuse_even_though_the_old_mutex_is_held()
[Fact] public void A_fresh_machine_has_nothing_to_migrate()           // no old folder -> empty
[Fact] public void A_second_start_copies_nothing()                     // both folders exist -> no CopyFile
[Fact] public void An_existing_new_run_value_is_not_overwritten()      // both values -> DeleteOldRunValue only
[Fact] public void A_test_host_never_touches_the_registry()            // IsTheApp false -> no registry steps, copies still planned
[Fact] public void An_overridden_root_never_touches_the_registry()     // RootOverridden -> same
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/TaskSpaces.Core.Tests --filter "FullyQualifiedName~RenameMigrationTests" --no-restore`
Expected: compile failure on the missing types.

- [ ] **Step 3: Implement `RenameMigration.Plan`**

Pure, expression-bodied where it reads well, with a head comment carrying the design's reasoning: copy not move, once only, registry only from the app and never under an override, refusal because the control pipe exposes no Exit.

- [ ] **Step 4: Run the Core tests**

Expected: all pass.

- [ ] **Step 5: Write the failing Windows tests**

`RenameMigratorTests` in `TaskSpaces.Windows.Tests`, each with a temp root and `DESKBAR_APPDATA` set for the test's duration (set the env var, restore in `finally`):
```csharp
[Fact] public void Apply_copies_the_old_state_files_into_the_new_folder_and_leaves_the_old_folder()
[Fact] public void Apply_deletes_the_old_shim()
[Fact] public void Apply_refuses_when_the_plan_says_so_and_touches_nothing()
[Fact] public void Gather_sees_the_old_folder_and_its_files_under_the_override_root()
[Fact] public void Registry_steps_are_refused_under_the_test_host()   // StartupRegistration.MigrateFromTaskSpaces(...).IsFailure, Run key unchanged
```

- [ ] **Step 6: Run them to see them fail, implement `RenameMigrator` and `StartupRegistration.MigrateFromTaskSpaces`, run again**

Run: `dotnet test tests/TaskSpaces.Windows.Tests --filter "FullyQualifiedName~RenameMigratorTests|FullyQualifiedName~StartupRegistrationTests" --no-restore`
Expected: fail, then pass.

- [ ] **Step 7: Wire it into startup**

In `App.OnStartup`: after `ClickTrace.Announce()` and the two mutexes, compute `oldMutexHeld` from the legacy mutex's `createdNew`; if `--await-previous`, call `AwaitParentExit` before the existing wait loop; then `RenameMigrator.Gather` and `Plan` and `Apply`, and on a failure show the message with caption "Deskbar", `singleInstance = null` is NOT set (this process owns its mutexes), `Shutdown()` and return. Trace every step applied. Comments carry the design's section "The migration, on first start of 2.0" in short.

- [ ] **Step 8: Full suite and commit**

Run: `dotnet test TaskSpaces.sln --filter "Category!=Integration"`
Expected: green.
```bash
git add src/TaskSpaces.Core/Migration src/TaskSpaces.App/RenameMigrator.cs src/TaskSpaces.App/StartupRegistration.cs src/TaskSpaces.App/App.xaml.cs tests/TaskSpaces.Core.Tests/Migration tests/TaskSpaces.Windows.Tests/RenameMigratorTests.cs
git commit -m "feat: first start of Deskbar carries TaskSpaces' state, autostart and shim across" -m "*fable5.1*"
```

### Task 4: The update checker picks its asset by name

**Files:**
- Modify: `src/TaskSpaces.Core/Updates/UpdateCheck.cs:95-101`, `src/TaskSpaces.App/UpdateService.cs` (the call site)
- Test: `tests/TaskSpaces.Core.Tests/UpdateCheckTests.cs`

**Interfaces:**
- Produces: `UpdateCheck.ReadRelease(string payload, bool preferSetup = false)`. Selection: among `.exe` assets, the first whose name contains `Setup` (ordinal, case-insensitive) when `preferSetup`, else the first whose name does not; falling back to the first `.exe` when none matches. The App calls it with `false` (portable); the comment says an installed build will pass `true`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void A_portable_build_skips_the_installer_even_when_it_is_listed_first()
  // assets: Deskbar-Setup-2.1.0-win-x64.exe, Deskbar-2.1.0-win-x64.exe -> AssetName == "Deskbar-2.1.0-win-x64.exe"
[Fact] public void An_installed_build_prefers_the_installer()               // preferSetup: true -> the Setup one
[Fact] public void With_only_an_installer_a_portable_build_still_takes_it() // fallback
```
Existing tests keep their fixtures; only the first-exe tests may need `preferSetup` left default.

- [ ] **Step 2: Run to see them fail, implement, run to see them pass**

Run: `dotnet test tests/TaskSpaces.Core.Tests --filter "FullyQualifiedName~UpdateCheckTests" --no-restore`

- [ ] **Step 3: Commit**

```bash
git add src/TaskSpaces.Core/Updates/UpdateCheck.cs src/TaskSpaces.App/UpdateService.cs tests/TaskSpaces.Core.Tests/UpdateCheckTests.cs
git commit -m "feat: the update checker picks the portable exe by name, never an installer" -m "*fable5.1*"
```

### Task 5: Documents

**Files:**
- Modify: `README.md`, `CLAUDE.md`, `docs/superpowers/notes/manual-test-script.md`

- [ ] **Step 1: README**

Title and hero image (`docs/branding/deskbar-mark-256.png`, alt Deskbar), the release badge URL (`AppifySheets/Deskbar`), every "TaskSpaces" in prose becomes "Deskbar" except where the text explains the migration or the old folder. Install section: asset name `Deskbar-…-win-x64.exe`, folder suggestion `C:\Users\<you>\Programs\Deskbar\`, the table's paths under `%APPDATA%\Deskbar`, the Run value `Deskbar`, uninstall deletes `%APPDATA%\Deskbar`. A new short paragraph "Coming from TaskSpaces": first start copies state, swaps autostart, leaves `%APPDATA%\TaskSpaces` for you to delete; rollback keeps the old files as they were at the copy and does not carry changes back; untick and tick "Start with Windows" in 1.x after a rollback. Above the download button, an HTML comment holding the Store badge markup with `PRODUCTID` to fill in. Releasing section: `Deskbar-2.0.0-win-x64.exe`, and the Store line in the release notes template if one exists.

- [ ] **Step 2: CLAUDE.md**

Line 1 title "Deskbar: session briefing"; the first paragraph names the product Deskbar and says the code namespaces are still `TaskSpaces.*` by decision (the rename design); the dev run path becomes `.../Deskbar.exe`; the shim becomes `%APPDATA%\Deskbar\deskbar.cmd`; the live-test line `deskbar.cmd help`; a new bullet under "Traps": until 2.0 is on main, a Debug Deskbar build on this machine must run with `DESKBAR_APPDATA` set, or its first start migrates the real profile and the 1.x builds stop seeing later changes.

- [ ] **Step 3: Manual test script**

Replace the product name and paths; add one scenario: update from 1.x, check workspaces, renames, autostart value, shim.

- [ ] **Step 4: Check and commit**

Run: `grep -n -i "taskspaces" README.md CLAUDE.md docs/superpowers/notes/manual-test-script.md`
Expected: only the migration, the old folder, the namespaces note, and `TaskSpaces.sln`/project paths in commands.
```bash
git add README.md CLAUDE.md docs/superpowers/notes/manual-test-script.md
git commit -m "docs: Deskbar in the README, the briefing and the manual test script" -m "*fable5.1*"
```
