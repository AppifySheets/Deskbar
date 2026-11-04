# Quick find Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A shortcut opens a search box that finds any window, browser tab or VS Code tab on any workspace, and any app that is not running, and takes Petre to the one he picks.

**Architecture:** Matching, ranking, the result catalogue and launch rules are pure code in `Deskbar.Core/QuickFind/`, tested without Windows. The Windows side adds Start menu enumeration and tab selection behind small interfaces. The App side adds one WPF window and one controller, wired to a second hotkey, Manage > Shortcuts and a `find` pipe command. Three pull requests, each releasable: A finds what is open, B selects the tab, C launches.

**Tech Stack:** .NET 10, WPF, System.Windows.Automation (UIA), CSharpFunctionalExtensions, System.Reactive, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-04-quick-find-design.md` (branch `quick-find-spec`). Read it before any task.

## Global Constraints

- One branch per pull request, cut from the latest `origin/main`; never push to main, never merge. Petre merges.
- Each pull request bumps `VersionPrefix` in `Directory.Build.props` above main's at the time (a minor bump for A, patch bumps for B and C unless main has moved a minor) and carries a `## Release notes` section written for users.
- Before each pull request: `dotnet build Deskbar.sln -c Release -warnaserror` and `dotnet test Deskbar.sln --filter "Category!=Integration"`, both clean.
- UI Automation never runs on the UI thread.
- Never cache a lookup miss that can later succeed (VS Code's lazy tree, late icons).
- Static WPF brushes are frozen.
- The quick find window is ignored by `WindowMonitor` (`WindowMonitor.Ignore`), like the bar.
- Code comments carry the reasoning, quoting Petre where a decision came from him; follow the C# rules in the user's CLAUDE.md (expression bodies, `var`, readonly collections, `Result`/`Maybe`, no braces on one-line ifs).
- Commit messages end with the line `*opus5.5*` (or the executing model's short name) and nothing else as attribution.
- Matching is `StringComparison.OrdinalIgnoreCase` everywhere.
- No real people's names in code, tests, screenshots or commits.
- Debug builds run with `DESKBAR_APPDATA` pointed at a scratch folder only if the 1.x migration warning in CLAUDE.md still applies; restart the app after each visible change so Petre can see it.

## Review Focus

1. A window closes between the search and Enter: Enter must do nothing harmful (no switch to an empty workspace), drop the row and leave the box open. Pinned by `Going_to_a_closed_window_fails_without_switching` in Task 6.
2. Petre sets the quick find chord to the switcher's chord, or to the switcher's Shift variant: refused with a reason, never half-bound. Pinned by `Quick_find_shortcut_cannot_reuse_the_switcher_chord` in Task 3.
3. A browser window's title is its active tab's title, so the window and that tab would show as two identical rows. A browser window with a tab reading is listed through its tabs only. Pinned by `A_browser_window_with_tabs_is_listed_by_its_tabs_only` in Task 5.
4. A background tab reading arrives while Petre is about to press Enter: the highlighted result must stay highlighted (by identity, not index). Pinned by `Highlight_follows_the_same_result_when_results_refresh` in Task 6.
5. A query of only spaces, or with leading and trailing spaces: treated as its pieces, and all-space is empty. Pinned by `Spaces_alone_are_an_empty_query` in Task 1.

---

## Pull request A: find what is open

### Task 0: Probes (throwaway, results written down)

**Files:**
- Create: `spikes/quick-find/tab-select.csx`, `spikes/quick-find/tab-names.csx`, `spikes/quick-find/apps-folder.csx`, `spikes/quick-find/free-chords.csx`
- Create: `docs/superpowers/notes/2026-11-04-quick-find-probes.md`

Each probe is a `dotnet script`, run by hand on Petre's machine, calling `SetProcessDpiAwarenessContext(-4)` first and declaring P/Invokes `CharSet.Unicode`. Probes never move Petre's windows or switch his desktop without saying so first; tab-select uses a browser window the probe opened itself.

- [ ] **Step 1: tab-names.** Time a full read of every TabItem name in every Edge, Chrome and VS Code window (reuse `BrowserTabs.Read`'s rule: no Document between tab and window; for VS Code the class contains `tab`). Record total ms and per window ms, and whether Claude Code webview tabs report a name.
- [ ] **Step 2: tab-select.** Open a new Edge window with three tabs. For a non-selected TabItem try `SelectionItemPattern.Select()`, then `LegacyIAccessiblePattern.DoDefaultAction()`. Record which switches the tab, with the window on the current desktop, on another desktop, and minimised. Repeat with a VS Code editor tab.
- [ ] **Step 3: apps-folder.** Enumerate `shell:AppsFolder` (IShellItem, `FOLDERID_AppsFolder`): name, parsing name (AUMID or path), and for desktop apps the .lnk target path (`System.Link.TargetParsingPath`). Compare the count and a sample with Petre's Start menu "All". Try starting one desktop and one packaged app with `explorer.exe shell:AppsFolder\<parsing name>` through `DetachedLauncher.Start`; record which process owns the resulting window.
- [ ] **Step 4: free-chords.** `RegisterHotKey` each of: `Win+Alt+Space`, `Win+Ctrl+F`, `Win+Shift+F`, `Ctrl+Alt+Space`, `Win+Ctrl+Q`, `Win+Alt+F`. Record which register, release each at once.
- [ ] **Step 5: write the notes file** with every measurement, and the decisions they force: (a) tab rows promise the tab or only the window; (b) the default chord (first free one in the list order above unless Petre picks another); (c) how a Start menu app is launched and which exe name to expect its window under.
- [ ] **Step 6: commit** the notes and scripts: `git add spikes/quick-find docs/superpowers/notes/2026-11-04-quick-find-probes.md` and commit `spike: measure tab names, tab selection, the Start menu and free chords for quick find`.

### Task 1: The query

**Files:**
- Create: `src/Deskbar.Core/QuickFind/FindQuery.cs`
- Test: `tests/Deskbar.Core.Tests/QuickFind/FindQueryTests.cs`

**Interfaces:**
- Produces: `public sealed record FindQuery(IReadOnlyList<string> Pieces)` with `public static FindQuery Parse(string text)`, `public bool IsEmpty`, `public bool Matches(string haystack)`, `public bool StartsWithFirstPiece(string title)`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Theory]
[InlineData("inv 10")]
[InlineData("10 inv")]
[InlineData("INV 10")]
public void Every_piece_must_appear_in_any_order_and_case(string typed) =>
    Assert.True(FindQuery.Parse(typed).Matches("Invoice 2026-10 - Microsoft Edge"));

[Fact]
public void A_missing_piece_fails_the_match() =>
    Assert.False(FindQuery.Parse("inv 11").Matches("Invoice 2026-10"));

[Fact]
public void Spaces_alone_are_an_empty_query()
{
    Assert.True(FindQuery.Parse("   ").IsEmpty);
    Assert.Equal(["inv", "10"], FindQuery.Parse("  inv   10 ").Pieces);
}

[Fact]
public void An_empty_query_matches_nothing() =>
    Assert.False(FindQuery.Parse("").Matches("anything"));

[Fact]
public void Prefix_is_judged_on_the_first_piece_only() =>
    Assert.True(FindQuery.Parse("inv 10").StartsWithFirstPiece("Invoice 2026-10"));
```

- [ ] **Step 2: Run** `dotnet test tests/Deskbar.Core.Tests --filter FullyQualifiedName~FindQueryTests`. Expected: build fails, `FindQuery` not defined.
- [ ] **Step 3: Implement** `FindQuery` in `src/Deskbar.Core/QuickFind/FindQuery.cs`: split on whitespace with `RemoveEmptyEntries`, `OrdinalIgnoreCase` contains for every piece; empty matches nothing.
- [ ] **Step 4: Run** the same filter. Expected: PASS.
- [ ] **Step 5: Commit** `feat: quick find's query, every typed piece in any order`.

### Task 2: Results and their order

**Files:**
- Create: `src/Deskbar.Core/QuickFind/FindResult.cs`, `src/Deskbar.Core/QuickFind/FindRanking.cs`
- Test: `tests/Deskbar.Core.Tests/QuickFind/FindRankingTests.cs`

**Interfaces:**
- Consumes: `FindQuery` (Task 1).
- Produces:
  - `public enum FindKind { Window, Tab, Remembered, StartMenu }`
  - `public sealed record FindPlace(Guid? WorkspaceId, string Label)`: `Label` is what the row shows on the right ("Accounting", "unnamed desktop", "all desktops"); `WorkspaceId` null for the last two.
  - `public sealed record FindResult(string Key, FindKind Kind, string Title, FindPlace Place, string SearchText, WindowHandle? Window = null, int? TabIndex = null, string? Detail = null, string? IconPath = null, LaunchTarget? Launch = null)`. `Key` is stable across refreshes: `w:<hwnd>`, `t:<hwnd>:<index>:<title>`, `r:<identity>`, `s:<parsing name>`. `SearchText` is title plus place label plus `Detail`. `Detail` is the browser window title for a tab, "not running" for launch rows. `LaunchTarget` is defined in Task 9; in PR A the property exists and stays null, so declare `public sealed record LaunchTarget(ProgramLaunch Launch, string ExpectedProcess)` here already.
  - `public static IReadOnlyList<FindResult> FindRanking.Rank(FindQuery query, IReadOnlyList<FindResult> all, IReadOnlyList<Guid> workspacesByRecentUse)`

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void Running_things_come_before_anything_that_would_start()
// Window "Rider X" (Window), "Rider" (Remembered), "JetBrains Rider" (StartMenu), query "rider":
// order is Window, Remembered, StartMenu.

[Fact]
public void Tabs_rank_with_windows_not_after_them()
// a Tab "Invoice" and a Window "Invoice list", query "inv": both before any Remembered row.

[Fact]
public void A_title_starting_with_the_first_piece_wins_inside_its_group()
// Windows "My invoices" and "Invoices 2026", query "inv": "Invoices 2026" first.

[Fact]
public void The_most_recently_used_workspace_breaks_a_tie()
// two Windows "Notes" on workspaces A and B, recent order [B, A]: B's first.

[Fact]
public void Only_matching_results_are_returned()
// query "zzz" over any list returns empty.
```

Write each with real `FindResult` values and `Assert.Equal` on the resulting `Key` sequence.

- [ ] **Step 2: Run** `--filter FullyQualifiedName~FindRankingTests`. Expected: build fails.
- [ ] **Step 3: Implement** `Rank`: filter by `query.Matches(r.SearchText)`, then `OrderBy` group (Window and Tab = 0, Remembered = 1, StartMenu = 2), `ThenByDescending` `StartsWithFirstPiece(Title)`, `ThenBy` index of `Place.WorkspaceId` in the recent list (absent = last), `ThenBy` `Title`.
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit** `feat: quick find ranks open things first, then prefix, then recent workspace`.

### Task 3: The shortcut setting

**Files:**
- Modify: `src/Deskbar.Core/Persistence/AppState.cs` (beside `SwitcherShortcut`, ~line 245)
- Modify: `src/Deskbar.Core/WorkspaceManager.cs` (beside `SwitcherShortcut`/`SetSwitcherShortcut`, ~line 938)
- Test: `tests/Deskbar.Core.Tests/QuickFindShortcutTests.cs`

**Interfaces:**
- Produces: `AppState.QuickFindShortcut { get; init; } = DefaultQuickFindShortcut`; `public const string DefaultQuickFindShortcut` = the chord Task 0 step 5 chose; `WorkspaceManager.QuickFindShortcut` (string, falls back to the default like `SwitcherShortcut`); `WorkspaceManager.SetQuickFindShortcut(string) : Result`.

- [ ] **Step 1: Write the failing tests** (build the manager the way `RemoteControlVersionTests.Manager()` does)

```csharp
[Fact]
public void An_older_state_file_gets_the_default() =>
    Assert.Equal(AppState.DefaultQuickFindShortcut, Manager().QuickFindShortcut);

[Fact]
public void A_valid_chord_is_stored_in_canonical_spelling()
// SetQuickFindShortcut("win + alt + space") succeeds; QuickFindShortcut == "Win+Alt+Space".

[Fact]
public void Quick_find_shortcut_cannot_reuse_the_switcher_chord()
// SetQuickFindShortcut("Win+Ctrl+Tab") fails; SetQuickFindShortcut("Win+Ctrl+Shift+Tab") fails
// (the switcher's reverse half); QuickFindShortcut unchanged.

[Fact]
public void The_switcher_cannot_take_the_quick_find_chord()
// SetSwitcherShortcut(manager.QuickFindShortcut) fails.
```

- [ ] **Step 2: Run** `--filter FullyQualifiedName~QuickFindShortcutTests`. Expected: build fails.
- [ ] **Step 3: Implement** the property, constant and setter; the collision check compares parsed `Chord`s, including the switcher's chord with `Chord.Shift` added when it has no Shift. Add the mirror check to `SetSwitcherShortcut`.
- [ ] **Step 4: Run.** Expected: PASS, and the existing `ChordTests` and switcher tests still pass (`dotnet test tests/Deskbar.Core.Tests`).
- [ ] **Step 5: Commit** `feat: a configurable shortcut for quick find, never the switcher's`.

### Task 4: Tab names kept, not just counted

**Files:**
- Modify: `src/Deskbar.App/BrowserTabs.cs`
- Test: `tests/Deskbar.Windows.Tests/BrowserTabNamesTests.cs`

**Interfaces:**
- Produces on `IBrowserTabs`: `IReadOnlyList<string> NamesOf(WindowHandle window)` (empty before the first reading) and `void Refresh(IReadOnlyList<WindowInfo> windows)` (queues stale windows like `Watch` but forgets nothing). `internal static string TabTitle(string accessibleName)` strips Chromium's appended state (" - Audio playing", " - Audio muted", " - Memory usage ...", and the like, per what Task 0 step 1 recorded).
- `Changed` keeps firing only when a `TabInfo` changes. A name-only change updates `NamesOf` without firing it, so the bar does not rebuild on every page title change.

- [ ] **Step 1: Write the failing tests** for the pure part:

```csharp
[Theory]
[InlineData("Invoice 2026-10 - Audio playing", "Invoice 2026-10")]
[InlineData("Invoice 2026-10 - Audio muted", "Invoice 2026-10")]
[InlineData("Invoice 2026-10", "Invoice 2026-10")]
public void A_tab_title_drops_the_state_chromium_appends(string name, string title) =>
    Assert.Equal(title, BrowserTabs.TabTitle(name));

[Fact]
public void No_names_before_the_first_reading() =>
    Assert.Empty(new BrowserTabs().NamesOf(new WindowHandle(0x1234)));
```

- [ ] **Step 2: Run** `dotnet test tests/Deskbar.Windows.Tests --filter FullyQualifiedName~BrowserTabNamesTests`. Expected: build fails.
- [ ] **Step 3: Implement**: `Read` and `ReadCode` return names alongside the `TabInfo` (in tab strip order); the worker stores them in a `ConcurrentDictionary<WindowHandle, IReadOnlyList<string>>`; `Watch`'s forgetting also forgets names; `Refresh` shares `Watch`'s queueing without the forgetting.
- [ ] **Step 4: Run** the filter, then the full Windows tests. Expected: PASS.
- [ ] **Step 5: Live check**: restart the Debug build; the bar's tab counts look as before.
- [ ] **Step 6: Commit** `feat: the tab reader keeps each tab's title for quick find`.

### Task 5: The catalogue of open things

**Files:**
- Create: `src/Deskbar.Core/QuickFind/OpenCatalog.cs`
- Test: `tests/Deskbar.Core.Tests/QuickFind/OpenCatalogTests.cs`

**Interfaces:**
- Consumes: `FindResult`, `FindPlace` (Task 2).
- Produces: `public static IReadOnlyList<FindResult> OpenCatalog.Of(IReadOnlyList<WindowInfo> windows, Func<WindowHandle, FindPlace> placeOf, Func<WindowHandle, IReadOnlyList<string>> tabsOf, Func<WindowInfo, bool> isBrowser, Func<WindowHandle, Maybe<string>> originalTitle)`. The App passes `placeOf` built from `WorkspaceManager.WorkspaceOf` and the desktop and pin facts the bar already uses.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void Every_window_is_listed_with_its_place()
// two windows on two workspaces: two Window results, Place labels "Accounting" and "Home".

[Fact]
public void A_browser_window_with_tabs_is_listed_by_its_tabs_only()
// msedge window titled "Invoice - Microsoft Edge" with tabs ["Invoice", "Mail"]:
// two Tab results, TabIndex 0 and 1, Detail = the window title; no Window result for it.

[Fact]
public void A_browser_window_without_a_reading_is_listed_as_a_window()

[Fact]
public void A_vs_code_window_is_listed_and_so_are_its_tabs()
// Code window "app.cs - billing - Visual Studio Code", tabs ["app.cs", "README.md"]:
// one Window plus two Tab results.

[Fact]
public void A_renamed_window_is_found_by_its_original_title_too()
// Title "VS", originalTitle "app.cs - billing - Visual Studio Code": SearchText contains "billing".

[Fact]
public void Search_text_includes_the_place()
// FindQuery.Parse("acc inv").Matches(result.SearchText) for a tab "Invoice" on "Accounting".
```

- [ ] **Step 2: Run** `--filter FullyQualifiedName~OpenCatalogTests`. Expected: build fails.
- [ ] **Step 3: Implement** `OpenCatalog.Of`. Window keys `w:{hwnd:X}`, tab keys `t:{hwnd:X}:{index}:{title}`.
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit** `feat: quick find lists every open window and tab with where it is`.

### Task 6: The quick find window

**Files:**
- Create: `src/Deskbar.App/QuickFindWindow.xaml`, `src/Deskbar.App/QuickFindWindow.xaml.cs`, `src/Deskbar.App/QuickFind.cs` (the controller)
- Modify: `src/Deskbar.Core/WorkspaceManager.cs` only if `JumpTo` needs a variant that reports a dead window distinctly (it already returns `Result`)
- Test: `tests/Deskbar.Windows.Tests/QuickFindWindowTests.cs`

**Interfaces:**
- Consumes: `FindQuery`, `FindRanking.Rank`, `OpenCatalog.Of`, `IBrowserTabs.NamesOf`/`Refresh`, `WorkspaceManager.JumpTo(WindowHandle, IWindowActivator)`, `WorkspaceManager.ByRecentUse()`, `IconCache.For(WindowHandle, string?)`, `WorkspacePalette.For` (as `WorkspaceSwitchGesture` calls it, line ~113).
- Produces: `public sealed class QuickFind(WorkspaceManager manager, IBrowserTabs tabs, IWindowActivator activator)` with `public void Show(string initialText = "")`. `QuickFindWindow` is display only: `Show(IReadOnlyList<FindRow> rows, string? highlightKey)`, events `Typed` (IObservable<string>), `Chosen` (IObservable<string> key), `Dismissed` (IObservable<Unit>). `public sealed record FindRow(string Key, ImageSource? Icon, string Title, string PlaceLabel, string? PlaceColor, string? Detail)`.

- [ ] **Step 1: Mockup first.** Make an HTML mockup of the box with eight realistic rows (windows, tabs, one each of the PR C launch rows greyed as "later"), current bar styling beside it, two or three variants of row layout. Show Petre, get his pick. No XAML before that.
- [ ] **Step 2: Write the failing tests** (STA, like the other window tests in this project)

```csharp
[Fact]
public void Highlight_follows_the_same_result_when_results_refresh()
// Show rows [a, b, c] highlight "b"; Show rows [x, b, c]: highlighted key is still "b".

[Fact]
public void Highlight_falls_to_the_first_row_when_its_result_is_gone()

[Fact]
public void Escape_dismisses_and_enter_chooses_the_highlighted_key()

[Fact]
public void Going_to_a_closed_window_fails_without_switching()
// QuickFind with FakeActivator/FakeDesktops: choose a Window key whose hwnd the fake reports dead;
// no Switch recorded, the row is dropped, the window stays open.
```

- [ ] **Step 3: Run** `--filter FullyQualifiedName~QuickFindWindowTests`. Expected: build fails.
- [ ] **Step 4: Implement** the window per the chosen mockup: borderless, topmost, centred on the screen holding the foreground window, `WindowMonitor.Ignore`d, closes on deactivate. Up/Down move the highlight, Enter chooses, Esc dismisses. The controller: on `Show`, `tabs.Refresh(manager.KnownWindows)`, builds the catalogue, ranks on every keystroke and on `tabs.Changed`/a 1 s timer while open (results refresh; highlight is by key). Enter on a Window: `manager.JumpTo`. Enter on a Tab: `JumpTo` its window (selection comes in Task 8). Never switch desktops before Enter.
- [ ] **Step 5: Run** the filter and the full suite. Expected: PASS.
- [ ] **Step 6: Commit** `feat: the quick find window`.

### Task 7: Summoning it, and shipping A

**Files:**
- Modify: `src/Deskbar.App/HotkeyService.cs` (new id 5, `BindQuickFind(Chord)`, constructor takes `Action showQuickFind` and the chord)
- Modify: `src/Deskbar.App/App.xaml.cs` (~line 1511, construct `QuickFind`, pass to `HotkeyService` and `RemoteControl`; rebinding where the switcher rebinds, ~line 1921)
- Modify: `src/Deskbar.App/ManageWindow.xaml`, `src/Deskbar.App/ManageWindow.xaml.cs` (a Quick find row on the Shortcuts tab, same shape as the switcher's, lines ~118-152)
- Modify: `src/Deskbar.Core/Control/RemoteControl.cs` (constructor gains `Func<string, Result>? showFind = null`; dispatch arm `("find", _) => Find(string.Join(' ', rest))`), `src/Deskbar.Core/Control/ControlUsage.cs` (new command `find`, arguments `[text...]`, description "open quick find, with the text already typed")
- Modify: `Directory.Build.props` (`VersionPrefix`)
- Test: `tests/Deskbar.Core.Tests/Control/RemoteControlFindTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void Find_opens_quick_find_with_the_text()
// RemoteControl with showFind capturing its argument; Execute(["find", "inv", "10"]) succeeds, captured "inv 10".

[Fact]
public void Find_without_the_app_refuses()
// showFind null: Execute(["find"]) fails.
```

The existing test that fails when a dispatched command is missing from `ControlUsage` covers the usage line.

- [ ] **Step 2: Run** `--filter FullyQualifiedName~RemoteControlFindTests`. Expected: build fails.
- [ ] **Step 3: Implement** the dispatch arm, usage line, hotkey binding (a failed registration reported once, like the switcher's), Manage row, composition.
- [ ] **Step 4: Run** the full suite and `dotnet build Deskbar.sln -c Release -warnaserror`. Expected: all green, no warnings.
- [ ] **Step 5: Live check**: restart the Debug build; press the chord; type a word from a background tab's title; Enter lands on its window. `deskbar.cmd find inv` opens it pre-filled.
- [ ] **Step 6: Bump** `VersionPrefix`, commit `feat: quick find, a search box for every open window and tab (2.x.0)`, push the branch, open the pull request with `## Release notes`, start the base watch from CLAUDE.md.

---

## Pull request B: select the tab

Only if Task 0 step 2 found a reliable way. If it did not, skip this section and say so in pull request A's description: tab rows go to the window.

### Task 8: Tab selection

**Files:**
- Modify: `src/Deskbar.App/BrowserTabs.cs` (`Result Select(WindowHandle window, int index, string title)` on `IBrowserTabs`, run on the worker thread, re-finding the TabItem by index and confirming its title before selecting)
- Modify: `src/Deskbar.App/QuickFind.cs` (Enter on a Tab: `JumpTo`, then `Select`; a failed select leaves Petre on the window)
- Test: `tests/Deskbar.Windows.Tests/BrowserTabNamesTests.cs`

- [ ] **Step 1: Write the failing test** `Select_refuses_when_the_tab_at_that_index_has_another_title` against the pure title check `internal static bool SameTab(string expectedTitle, string accessibleName)` (uses `TabTitle`).
- [ ] **Step 2: Run** it. Expected: build fails.
- [ ] **Step 3: Implement** with the pattern Task 0 recorded; an index whose title no longer matches searches the strip for that title once, else fails.
- [ ] **Step 4: Run** the suite, Release build. Live check on Edge and VS Code tabs, on another workspace.
- [ ] **Step 5: Bump**, commit `feat: quick find selects the tab, not just its window`, pull request.

---

## Pull request C: launch what is not running

### Task 9: What can be launched

**Files:**
- Create: `src/Deskbar.Core/QuickFind/LaunchCatalog.cs`
- Test: `tests/Deskbar.Core.Tests/QuickFind/LaunchCatalogTests.cs`

**Interfaces:**
- Consumes: `FindResult`, `LaunchTarget` (Task 2), `InventoryEntry`, `CommandLines.ArgumentsOf`.
- Produces: `public sealed record StartMenuApp(string Name, string ParsingName, string? TargetPath, string? IconPath)`; `public static IReadOnlyList<FindResult> LaunchCatalog.Of(IReadOnlyList<InventoryEntry> roster, IReadOnlyList<StartMenuApp> startMenu, IReadOnlyList<WindowInfo> running, Func<string, bool> pathExists, Func<string, string?, FindPlace> destinationOf)`.

- [ ] **Step 1: Write the failing tests**, using the shapes the builder check found in the real roster:

```csharp
[Fact]
public void Many_session_variants_of_one_program_are_one_row()
// eleven ms-teams.exe entries with --update-trigger/--restart-count args: one Remembered row, launched bare.

[Fact]
public void A_content_argument_makes_its_own_row()
// rider64 X.sln and rider64 Y.sln (pathExists true for both): two rows, each launching with its .sln.

[Fact]
public void A_running_program_has_no_row()
// ms-teams running: no Teams row from roster or Start menu.

[Fact]
public void Open_content_hides_only_its_own_row()
// a rider64 window whose command line holds X.sln: Y.sln row remains, X.sln row gone.

[Fact]
public void A_packaged_path_launches_through_its_start_menu_entry()
// roster entry under C:\Program Files\WindowsApps\...Photos...: row's Launch uses the matching StartMenuApp's ParsingName;
// with no matching StartMenuApp: no row.

[Fact]
public void A_start_menu_app_already_covered_by_the_roster_is_listed_once()

[Fact]
public void Launch_rows_say_not_running_and_where()
// Detail == "not running"; Place from destinationOf.
```

- [ ] **Step 2: Run** `--filter FullyQualifiedName~LaunchCatalogTests`. Expected: build fails.
- [ ] **Step 3: Implement.** Content argument = the first argument from `CommandLines.ArgumentsOf` that `pathExists` accepts. Packaged = path contains `\WindowsApps\` or process is `ApplicationFrameHost`. Matching roster to Start menu: `TargetPath` equal to the process path (case-insensitive), else AUMID; launch for Start menu rows per Task 0 step 5(c).
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit** `feat: quick find knows what it could launch`.

### Task 10: Where a launch lands, and starting there

**Files:**
- Modify: `src/Deskbar.Core/WorkspaceManager.cs`
- Modify: `src/Deskbar.Core/Control/RemoteControl.Windows.cs` (`Start` calls the new `StartIn` instead of its own expectation sequence)
- Test: `tests/Deskbar.Core.Tests/LaunchDestinationTests.cs`

**Interfaces:**
- Produces: `public Guid? LaunchDestination(string processPath, string? commandLine)`: the workspace a new window of that program would be placed in by `Remembered` (memory) then `RulesEngine.MatchWorkspace`, judged on a synthetic `WindowInfo(new WindowHandle(0), 0, <exe name>, processPath, "", commandLine)`; null means the current workspace. `public Result StartIn(Guid workspaceId, LaunchTarget target, Func<ProgramLaunch, Result<int?>> starter)`: `ExpectWindow(target.ExpectedProcess, workspaceId)`, start, `NoteExpectedProcess`, `CancelExpectation` on failure (the sequence now in `RemoteControl.Start`).

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void A_remembered_app_lands_in_its_remembered_workspace()
[Fact]
public void A_rule_decides_for_an_app_memory_has_never_seen()
[Fact]
public void Memory_standing_down_means_the_current_workspace()
// another live window shares the identity: LaunchDestination is null.
[Fact]
public void Nothing_known_means_the_current_workspace()
[Fact]
public void A_failed_start_withdraws_the_expectation()
// starter returns failure: Awaits(expectation) false; no window of that name is claimed afterwards.
```

- [ ] **Step 2: Run** `--filter FullyQualifiedName~LaunchDestinationTests`. Expected: build fails.
- [ ] **Step 3: Implement** both; switch `RemoteControl.Start` to `StartIn` and keep its JSON reply identical.
- [ ] **Step 4: Run** the full Core suite, including the existing `start` tests. Expected: PASS, unchanged replies.
- [ ] **Step 5: Commit** `feat: work out where a launch lands, and start a program there`.

### Task 11: The Start menu, and wiring launch rows in

**Files:**
- Create: `src/Deskbar.Windows/Shell/StartMenuApps.cs` (`public static IReadOnlyList<StartMenuApp> Read()`, enumerating `shell:AppsFolder` as Task 0 step 3 did; run off the UI thread and cached for the session with a refresh when the box opens and the cache is older than a minute)
- Modify: `src/Deskbar.App/QuickFind.cs` (adds `LaunchCatalog.Of(...)` to the results; Enter on a launch row: `manager.Switch(destination)` when it is not current, then `manager.StartIn(destination, target, ProgramStarter.Start)`)
- Modify: `src/Deskbar.App/QuickFindWindow.xaml(.cs)` (launch rows styled as the mockup decided)
- Modify: `Directory.Build.props`
- Test: `tests/Deskbar.Windows.Tests/StartMenuAppsTests.cs`

- [ ] **Step 1: Write the failing test** `Reads_at_least_one_app_with_a_name_and_parsing_name` (marked `[Trait("Category", "Integration")]` only if it proves slow or machine-dependent; it reads, never launches).
- [ ] **Step 2: Run** it. Expected: build fails.
- [ ] **Step 3: Implement** `StartMenuApps.Read` and the wiring.
- [ ] **Step 4: Run** the suite, Release build. Live check: type an app that is not running, see "not running" and its workspace, Enter, land there with the app opening. Type an app that is running: no launch row.
- [ ] **Step 5: Bump**, commit `feat: quick find launches apps that are not running, where they belong`, pull request with `## Release notes`.
