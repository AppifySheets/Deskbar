# Recent Workspaces Keep Their Colour Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Each workspace row on the floating bar fades (rail, tint, name, icons) with the time since any of its windows was last used, so the workspaces in use stand out.

**Architecture:** A pure `WorkspaceFade` in Core turns a workspace's window stamps into a fade from 0 to 1 on the existing aging curve. The bar rounds that fade to one of 16 steps for the rail, tint and caption, drawn at rebuild, and uses the unrounded fade for the icons' grey overlays, refreshed in place each minute. The aging timer asks for a rebuild only when a workspace's step has moved.

**Tech Stack:** C# / .NET 10, WPF, xUnit. CSharpFunctionalExtensions where a Result fits (none needed here).

**Spec:** `docs/superpowers/specs/2026-11-22-recent-workspaces-design.md`

## Global Constraints

- Work in the worktree `E:\repos\TaskSpaces\.claude\worktrees\recent-workspaces`, branch `feat/recent-workspaces-colour`.
- Curve: 0 up to 15 minutes, 1 at 8 hours, eased by `^0.7` (today's `FloatingBar.AgeOf`, unchanged values).
- Steps: 16. Rail at step 0 is the palette colour, opaque; at step 16 it is `#4A4E58` at alpha `0x30`, both colour and alpha linear in the step. Recent tint alpha at step 0 is `0x13` (a third of `LaneTint`'s `0x38`), 0 at step 16. Resting caption alpha goes from `0xA8` (step 0, today's `RestingRowForeground`) to `0x6B` (step 16).
- The current row is drawn exactly as today. An expanded (titled-list) row keeps `ListSpine`. `AskingCaption` wins over the fade.
- Pinned windows, the pinned row, unbound desktops and Unplaced keep today's per-window aging and colours.
- No rows move, fold or hide. Never a rebuild every minute.
- Comments: long and reasoned, in the file's existing voice, quoting Petre ("recency has to be gradual, not 8 hours"; "more time goes by, less pronounced a bar and all its icons become"; "let's fade windows not based on individual age, but the age of the workspace").
- C# style: expression bodies, `var`, readonly collections, lambdas over loops, no `private` keyword.
- Before the PR: `dotnet build Deskbar.sln -c Release -warnaserror` and `dotnet test Deskbar.sln --filter "Category!=Integration"`.

## Review Focus

- A workspace with no windows: fade 1, no exception from an empty max (Task 1 test).
- A clock that reads earlier than a stamp (clock moved back, or a restored stamp from the future): fade 0, not negative (Task 1 test).
- The current workspace whose windows are all old (just switched to, nothing clicked yet): drawn at step 0 (Task 3 test).
- A folded row and a folded group's member names fade too; a folded row has no icons but still has a caption (Task 3 test on a folded Sparrow).
- A row asking for attention keeps the pulsing `AskingCaption` whatever its age (Task 3 test).

---

### Task 1: WorkspaceFade in Core

**Files:**
- Create: `src/Deskbar.Core/Overview/WorkspaceFade.cs`
- Modify: `src/Deskbar.App/FloatingBar.Aging.cs` (the `AgingStarts`/`FullyAged` constants and `AgeOf(TimeSpan)` delegate to Core)
- Test: `tests/Deskbar.Core.Tests/WorkspaceFadeTests.cs`

**Interfaces:**
- Produces, namespace `Deskbar.Core.Overview`:
  - `public static class WorkspaceFade`
  - `public static readonly TimeSpan Starts` (15 min), `public static readonly TimeSpan Full` (8 h), `public const int Steps = 16`
  - `public static double Curve(TimeSpan unused)`: today's `FloatingBar.AgeOf(TimeSpan)` body, moved
  - `public static double Of(IEnumerable<DateTimeOffset?> windowsLastUsed, DateTimeOffset now)`: `Curve(now - newest stamp)`; 1 when there is no non-null stamp
  - `public static int Step(double fade)`: `(int)Math.Round(fade * Steps)`, so 0..16

- [ ] **Step 1: Write the failing tests**

```csharp
public class WorkspaceFadeTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact] public void The_newest_window_sets_the_age() =>
        Assert.Equal(0, WorkspaceFade.Of([Now.AddDays(-3), Now.AddMinutes(-5), null], Now));
    [Fact] public void A_workspace_untouched_for_eight_hours_is_fully_faded() =>
        Assert.Equal(1, WorkspaceFade.Of([Now.AddHours(-8)], Now));
    [Fact] public void A_workspace_with_no_windows_is_fully_faded() =>
        Assert.Equal(1, WorkspaceFade.Of([], Now));
    [Fact] public void Unstamped_windows_count_as_no_windows() =>
        Assert.Equal(1, WorkspaceFade.Of([null, null], Now));
    [Fact] public void A_stamp_after_now_is_fresh_not_negative() =>
        Assert.Equal(0, WorkspaceFade.Of([Now.AddMinutes(10)], Now));
    [Fact] public void Two_hours_is_partway() =>
        Assert.InRange(WorkspaceFade.Of([Now.AddHours(-2)], Now), 0.2, 0.5);
    [Theory, InlineData(0.0, 0), InlineData(0.5, 8), InlineData(1.0, 16), InlineData(0.03, 0), InlineData(0.97, 16)]
    public void Steps_round_to_sixteenths(double fade, int step) => Assert.Equal(step, WorkspaceFade.Step(fade));
}
```

- [ ] **Step 2: Run, expect a compile failure (no `WorkspaceFade`)**

Run: `dotnet test tests/Deskbar.Core.Tests --filter "FullyQualifiedName~WorkspaceFadeTests"`

- [ ] **Step 3: Implement `WorkspaceFade`; make `FloatingBar.AgeOf(TimeSpan)` return `WorkspaceFade.Curve(unused)` and drop the two App constants**

Header comment: why a workspace has one age (Petre's quote), why the newest window sets it, why no windows means faded.

- [ ] **Step 4: Run Core tests and the existing aging tests**

Run: `dotnet test tests/Deskbar.Core.Tests --filter "FullyQualifiedName~WorkspaceFadeTests"` then `dotnet test tests/Deskbar.Windows.Tests --filter "FullyQualifiedName~FloatingBarActivityTests"`
Expected: PASS both (the `AgeOf` theory still holds).

- [ ] **Step 5: Commit** `feat: a workspace's age from its newest window`

---

### Task 2: Faded brushes

**Files:**
- Create: `src/Deskbar.App/FloatingBar.Recency.cs` (partial `FloatingBar`)
- Test: `tests/Deskbar.Windows.Tests/FloatingBarRecencyTests.cs`

**Interfaces:**
- Consumes: `WorkspaceFade.Steps`; the existing `Lane`, `Tint`, `WorkspacePalette`, `Frozen`, `GroupHeaderForeground`.
- Produces (all `internal static` so tests reach them):
  - `Brush? FadedAccent(string? color, int index, int step)`: the palette colour blended toward `#4A4E58` by `step / 16`, with alpha from `0xFF` down to `0x30` by the same fraction, frozen, cached per hex and step; null where `Lane` is null (a row opted out of colour)
  - `Brush? FadedTint(string? color, int index, int step)`: `Lane(color, index, alpha)` with `alpha = round(0x13 * (1 - step / 16.0))`; null when alpha is 0
  - `Brush RestingCaption(int step)`: white, alpha linear from `0xA8` to `0x6B`, frozen, cached per step
  - `Brush GroupHeaderAt(int step)`: `GroupHeaderForeground`'s colour with its alpha scaled by the same ratio as `RestingCaption` (`alpha(step) / 0xA8`)

- [ ] **Step 1: Write the failing tests**

```csharp
public class FloatingBarRecencyTests
{
    static Color ColourOf(Brush? b) => Assert.IsType<SolidColorBrush>(b).Color;

    [Fact] public void A_fresh_rail_is_the_workspace_colour() =>
        Assert.Equal(Color.FromRgb(0x9B, 0x6C, 0xF0), ColourOf(FloatingBar.FadedAccent("#9B6CF0", 0, 0)));
    [Fact] public void A_fully_faded_rail_is_a_faint_grey() =>
        Assert.Equal(Color.FromArgb(0x30, 0x4A, 0x4E, 0x58), ColourOf(FloatingBar.FadedAccent("#9B6CF0", 0, 16)));
    [Fact] public void A_fresh_row_has_a_third_of_the_current_tint() =>
        Assert.Equal(0x13, ColourOf(FloatingBar.FadedTint("#9B6CF0", 0, 0)).A);
    [Fact] public void A_fully_faded_row_has_no_tint() =>
        Assert.Null(FloatingBar.FadedTint("#9B6CF0", 0, 16));
    [Fact] public void The_caption_dims_from_resting_to_its_floor()
    {
        Assert.Equal(0xA8, ColourOf(FloatingBar.RestingCaption(0)).A);
        Assert.Equal(0x6B, ColourOf(FloatingBar.RestingCaption(16)).A);
    }
}
```

Use the palette's "none" value from `WorkspacePalette` for one more test: `FadedAccent(<none>, 0, 5)` is null.

- [ ] **Step 2: Run, expect compile failure**

Run: `dotnet test tests/Deskbar.Windows.Tests --filter "FullyQualifiedName~FloatingBarRecencyTests"`

- [ ] **Step 3: Implement the four helpers in `FloatingBar.Recency.cs`**

File header comment: the design (one age per workspace, gradual, B with tint), the three Petre quotes, why 16 steps (a brush cache that stays small, and a rebuild only when a step moves), why the current row is exempt.

- [ ] **Step 4: Run, expect PASS**

- [ ] **Step 5: Commit** `feat: faded rail, tint and caption brushes`

---

### Task 3: Rows fade with their workspace

**Files:**
- Modify: `src/Deskbar.App/FloatingBar.Recency.cs` (fade bookkeeping)
- Modify: `src/Deskbar.App/FloatingBar.Aging.cs` (icons by workspace; the timer tick)
- Modify: `src/Deskbar.App/FloatingBar.xaml.cs`: `WorkspaceRow` (~2446), `GroupRow` signature (~3787) and its `RowLabel` call, `RowLabel` (~5606, the `resting` line ~5625), `GroupHeader` (~3409), `FoldedGroupLine` (~3549, the `resting` line ~3575), the group `FamilyBox` calls (~2548, ~2564), the `Aged(icon, row)` call (~6454), `RebuildCore` start (~2250, beside `ForgetAgingIcons`)
- Modify: `tests/Deskbar.Windows.Tests/FloatingBarRebuildTests.cs` (`Harness.Build` gains `Func<DateTimeOffset>? clock = null`, passed to `WorkspaceManager`)
- Modify: `docs/superpowers/notes/manual-test-script.md` (a short "recent workspaces fade" check)
- Test: `tests/Deskbar.Windows.Tests/FloatingBarRecencyTests.cs` (bar-level tests, using `Harness` and `StaThread.Run`)

**Interfaces:**
- Consumes: Task 1's `WorkspaceFade.Of/Step`, Task 2's four brushes, `manager.LastUsed(WindowHandle)`, `manager.Now`.
- Produces, in `FloatingBar.Recency.cs`:
  - `readonly Dictionary<Guid, (Func<double> Fade, int Step)> drawnFades`: per workspace row drawn in this rebuild, how to recompute its fade and the step it was drawn at
  - `void ForgetDrawnFades()`: called at the start of a rebuild, next to `ForgetAgingIcons()`
  - `int NoteFade(WorkspaceGroup g)`: registers `g` (fade func: 0 when `g.IsCurrent`, else `WorkspaceFade.Of(g.Running.Select(r => manager.LastUsed(r.Window.Handle)), manager.Now)`) and returns its step
  - `double FadeOf(Guid? workspace, WindowHandle window)`: the workspace's fade when it is in `drawnFades`, otherwise today's per-window `AgeOf(window)`
  - `bool FadesMoved()`: true when any entry's current step differs from its drawn step
- Changes:
  - `GroupRow(..., int fadeStep = 0)` passes `fadeStep` to `RowLabel(..., int fadeStep = 0)`, whose resting brush becomes `asking ? AskingCaption : isCurrent ? ... : RestingCaption(fadeStep)`.
  - `agingIcons` becomes `List<(Guid? Workspace, WindowHandle Window, UIElement Grey)>`; `Aged(ImageSource icon, WindowRow row, Guid? workspace)`; the overlay opacity and the tick both use `FadeOf(workspace, window)`; `row.IsActive` still forces 0.
  - The aging timer's tick, after refreshing the overlays: `if (FadesMoved()) Rebuild();`.
  - `GroupHeader(group, colourSlot, int fadeStep)` and `FoldedGroupLine` use `GroupHeaderAt(step)` for the group name; folded-line member names use `RestingCaption(member step)`; the group's `FamilyBox` outline uses `FadedAccent(group.Color, slot, groupStep)`, where `groupStep` is the smallest step among its members (computed from `NoteFade` results, so the members must be noted before the header is built).

- [ ] **Step 1: Add the clock to `Harness.Build`, then write the failing bar tests**

Use a mutable clock: `var now = T0; Harness.Build(clock: () => now)`. Every window is stamped `T0` on arrival, so `now = T0.AddDays(2)` followed by a rebuild (triggered the way the file's tests already do) makes every workspace two days old. GEPHA is current.

```csharp
[Fact] public void An_idle_workspace_row_is_drawn_faded()          // Sparrow's rail Border (Width == SpineWidth) is #304A4E58 (ARGB),
                                                                    // its caption TextBlock's alpha is 0x6B, its row has no tint
[Fact] public void The_current_row_is_not_faded_however_old()       // GEPHA's window is two days old too: rail is ListSpine's
                                                                    // colour, caption CurrentRowForeground, tint LaneTint
[Fact] public void A_fresh_row_keeps_its_colour_and_a_faint_tint()  // no clock move: Sparrow's rail is its palette colour, tint alpha 0x13
[Fact] public void A_folded_idle_row_dims_its_name()                // fold Sparrow (manager's fold command): caption alpha 0x6B
[Fact] public void A_row_asking_for_you_keeps_the_asking_colour()   // Sparrow wants attention: caption is AskingCaption
[Fact] public void A_workspace_used_again_regains_its_colour()      // after the clock move, raise StubMonitor's foreground event for
                                                                    // Sparrow's window (stamps it at `now`), rebuild: rail is the palette colour
```

Find rows the way the file's existing helpers do (the row Border holding a TextBlock with that text).

- [ ] **Step 2: Run, expect FAIL (rail still the palette colour)**

Run: `dotnet test tests/Deskbar.Windows.Tests --filter "FullyQualifiedName~FloatingBarRecencyTests"`

- [ ] **Step 3: Implement the bookkeeping and the changes listed under Interfaces**

In `WorkspaceRow`: `var step = NoteFade(g);` then `tint: g.IsCurrent ? LaneTint(...) : FadedTint(colour, slot, step)`, `spine: g.IsCurrent || Expanded(...) ? ListSpine(...) : FadedAccent(colour, slot, step)`, `fadeStep: step`. `NoteWorkspaceAccent` keeps the full `LaneAccent` (the stopwatch swatch is not a recency signal). Update the "ACCENT RAIL style" comment there: non-current rows now carry a faint tint while fresh.

- [ ] **Step 4: Run the new tests, then the whole non-integration suite**

Run: `dotnet test Deskbar.sln --filter "Category!=Integration"`
Expected: all PASS.

- [ ] **Step 5: Release build**

Run: `dotnet build Deskbar.sln -c Release -warnaserror`
Expected: 0 warnings, 0 errors.

- [ ] **Step 6: Commit** `feat: workspace rows fade with the time since you used them`

- [ ] **Step 7: Live check**

Stop the running Deskbar, build Debug, start `src/Deskbar.App/bin/Debug/net10.0-windows10.0.19041.0/Deskbar.exe` (per the briefing's restart rules), and screenshot the bar for Petre. Values (tint `0x13`, floor `0x6B`, 8 h) are first guesses to tune with him.
