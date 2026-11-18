# Taskbar flashing switch implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** While a Manage checkbox is ticked and Deskbar runs, Windows' "Show flashing on taskbar apps" is off; when Deskbar stops, it is back to what it was.

**Architecture:** A pure plan in `Deskbar.Core` decides what to write and what to remember from three facts. An applier in `Deskbar.App` gathers the facts (the setting, a marker file, the registry value) and carries the plan out. `App` runs it from the state pulse and from every orderly way out.

**Tech stack:** C# / .NET 10, WPF, CSharpFunctionalExtensions, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-18-taskbar-flashing-switch-design.md`

## Global constraints

- Registry value: `TaskbarFlashing` (DWORD) under `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced`. 1 or missing is on, 0 is off. No broadcast after writing; Explorer reads it per flash (measured).
- Marker file: `taskbar-flashing.before` in the state folder, content `1` (flashing was on) or `0`. Anything else reads as on.
- Only the app itself writes the registry (`StartupRegistration.IsTheApp` on the entry assembly). A test host never does.
- Store copy writes through `reg.exe` with `DetachedLauncher.RunOutsidePackage`; the switch on `PackageIdentity.IsPackaged` carries the one-line comment naming the Store packaging design.
- Checkbox copy, exact: `Stop apps flashing on Windows' taskbar while Deskbar runs (the bar's amber dot shows them instead)`.
- Note copy, exact: `Your taskbar is on auto-hide. An app that flashes brings it up and keeps it there until you click that app. Ticking this stops that.`
- Project C# rules: expression bodies, `var`, no `private` keyword, immutable records, Result for expected failures, long comments that carry the why.
- Build check before the PR: `dotnet build Deskbar.sln -c Release -warnaserror`. Tests: `dotnet test Deskbar.sln --filter "Category!=Integration"`.

## Review focus

- A crash while holding the switch: the next start must keep the remembered value, not remember the 0 Deskbar wrote. Pinned in task 1 (`Plan` with `Remembered` set) and task 2 (marker left alone).
- The user had flashing off before Deskbar: exit must not turn it on. Pinned in task 1.
- The registry write fails on release: the marker must stay, so a later release can try again. Pinned in task 2.
- A marker with unreadable content: reads as "was on". Pinned in task 2.
- A test host building a ManageWindow or calling `Sync(bool)`: the real registry is untouched. Pinned in task 2.

---

### Task 1: the plan and the note (Deskbar.Core)

**Files:**
- Create: `src/Deskbar.Core/Shell/TaskbarFlashing.cs`
- Test: `tests/Deskbar.Core.Tests/Shell/TaskbarFlashingTests.cs`

**Interfaces (produces):**

```csharp
namespace Deskbar.Core.Shell;
// Wanted: the setting is on and Deskbar is staying up. Remembered: the marker's value, null when there is none.
public sealed record TaskbarFlashingFacts(bool Wanted, bool? Remembered, bool FlashingOn);
// Write: the value to put in the registry, null for none. Remember: the value to put in the marker, null for none.
public sealed record TaskbarFlashingStep(bool? Write, bool? Remember, bool Forget);
public static class TaskbarFlashing
{
    public const string MarkerName = "taskbar-flashing.before";
    public const string NoteText = "..."; // the exact note copy above
    public static TaskbarFlashingStep Plan(TaskbarFlashingFacts facts);
    public static string Note(bool ticked, bool autoHide, bool flashingOn); // NoteText or ""
}
```

- [ ] **Step 1: failing tests**, one fact each:

| Facts (Wanted, Remembered, FlashingOn) | Step (Write, Remember, Forget) |
|---|---|
| true, null, true | false, true, false |
| true, null, false | null, false, false |
| true, true, true (crash, then someone turned it on) | false, null, false |
| true, true, false (crash leftover) | null, null, false |
| false, null, true | null, null, false |
| false, null, false | null, null, false |
| false, true, false | true, null, true |
| false, false, false (was off before Deskbar) | null, null, true |
| false, true, true | null, null, true |

`Note(false, true, true)` is `NoteText`; every other combination is `""`.

- [ ] **Step 2:** run `dotnet test tests/Deskbar.Core.Tests --filter TaskbarFlashing`, expect compile failure.
- [ ] **Step 3:** implement.
- [ ] **Step 4:** run again, expect pass.
- [ ] **Step 5:** commit `feat: the plan for holding Windows' taskbar flashing off`.

### Task 2: the applier (Deskbar.App)

**Files:**
- Create: `src/Deskbar.App/TaskbarFlashingSwitch.cs`
- Test: `tests/Deskbar.Windows.Tests/TaskbarFlashingSwitchTests.cs`

**Interfaces:**
- Consumes: task 1's `TaskbarFlashing.Plan`, `MarkerName`.
- Produces:

```csharp
static class TaskbarFlashingSwitch
{
    // Whether Windows flashes taskbar buttons now. Read-only, safe from a test host.
    public static bool FlashingOn { get; }
    // The app's entry point: real registry, real state folder. Returns at once outside the app itself.
    public static void Sync(bool wanted);
    // The seam the tests drive. Order: write the marker BEFORE the registry when remembering; delete the
    // marker only AFTER a successful registry write when forgetting. A failed write is traced, never thrown.
    internal static void Sync(bool wanted, string stateDir, Func<bool> flashingOn, Func<bool, Result> write, Action<string> trace);
}
```

- [ ] **Step 1: failing tests** against the seam, with a temp folder and a fake registry (`var value = true; Func<bool,Result> write = v => { value = v; return Result.Success(); }`):
  - wanted, no marker, on: marker contains `1`, value false.
  - wanted, no marker, off: marker contains `0`, value false.
  - wanted, marker `1` already there, value false: marker still `1`.
  - not wanted, marker `1`, value false: value true, marker gone.
  - not wanted, marker `0`, value false: value false, marker gone.
  - not wanted, marker `1`, write fails: marker still there.
  - not wanted, marker with content `garbage`, value false: value true, marker gone.
  - `Sync(true)` (the public one) from the test host: no marker appears in `AppPaths.NewStateDir` and `FlashingOn` is unchanged.
- [ ] **Step 2:** run `dotnet test tests/Deskbar.Windows.Tests --filter TaskbarFlashingSwitch`, expect compile failure.
- [ ] **Step 3:** implement. The real write: `Registry.CurrentUser.CreateSubKey(key).SetValue("TaskbarFlashing", on ? 1 : 0, RegistryValueKind.DWord)` in the direct build; packaged, `reg.exe add HKCU\<key> /v TaskbarFlashing /t REG_DWORD /d <0|1> /f` through `DetachedLauncher.RunOutsidePackage` with a 5 s timeout.
- [ ] **Step 4:** run again, expect pass.
- [ ] **Step 5:** commit `feat: Deskbar can hold Windows' taskbar flashing off and put it back`.

### Task 3: the setting, Manage's box and the wiring

**Files:**
- Modify: `src/Deskbar.Core/Persistence/AppState.cs` (after `DockBar`): `public bool QuietTaskbar { get; init; }`
- Modify: `src/Deskbar.Core/WorkspaceManager.cs` (after `SetDockBar`): `public Result SetQuietTaskbar(bool quiet)`, a no-op when unchanged.
- Modify: `src/Deskbar.App/ManageWindow.xaml`, `ManageWindow.xaml.cs`: checkbox `QuietTaskbar` and note `QuietTaskbarNote` in a bottom-docked StackPanel beside `DockBar`; `OnQuietTaskbarToggled` calls `SetQuietTaskbar` and refreshes the note from `TaskbarFlashing.Note(ticked, AppBar.Taskbar()?.AutoHide == true, TaskbarFlashingSwitch.FlashingOn)`.
- Modify: `src/Deskbar.App/App.xaml.cs`: after the `ClaudeSkill.Sync` wiring, `TaskbarFlashingSwitch.Sync(manager.State.QuietTaskbar)` now and on the distinct state pulse; `TaskbarFlashingSwitch.Sync(false)` in `ExitApp` (which `RestartApp` goes through) and in `SessionEnding`.
- Modify: `README.md`, "Taskbar pieces": two sentences on the box.
- Test: `tests/Deskbar.Core.Tests` beside the `SetDockBar` tests; `tests/Deskbar.Windows.Tests` beside the ManageWindow settings tests.

- [ ] **Step 1: failing tests:** `SetQuietTaskbar(true)` persists `QuietTaskbar` and pulses once; a second `true` does not pulse. A ManageWindow built over a state with `QuietTaskbar = true` shows the box ticked; unticking it leaves `manager.State.QuietTaskbar` false.
- [ ] **Step 2:** run both test projects filtered on `QuietTaskbar`, expect compile failure.
- [ ] **Step 3:** implement.
- [ ] **Step 4:** whole suite, `dotnet test Deskbar.sln --filter "Category!=Integration"`, then `dotnet build Deskbar.sln -c Release -warnaserror`.
- [ ] **Step 5:** commit `feat: Manage's box that stops taskbar flashing while Deskbar runs`.

### Task 4: live check on Petre's machine

- [ ] Set `TaskbarFlashing` back to 1 (it was turned off by hand during the investigation), so Deskbar remembers "on".
- [ ] Swap the running Deskbar for this build in one stop-build-start command, tick the box, confirm the value reads 0 and `%APPDATA%\Deskbar\taskbar-flashing.before` contains `1`.
- [ ] Exit Deskbar from the tray: value reads 1, marker gone. Start again: value 0, marker `1`.
- [ ] Open the PR with a `## Release notes` section and arm the base watch.
