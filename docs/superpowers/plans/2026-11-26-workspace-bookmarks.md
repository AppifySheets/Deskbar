# Workspace Bookmarks Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Each workspace keeps bookmarks (web links, programs, installed apps) that show as faded chips in its row of the bar, open on that workspace with one click, and hide while their program is running.

**Architecture:** The model, its rules, the "is it running" decision, drop and window parsing, the open sequence and the pipe commands all live in Deskbar.Core as pure or fake-driven code with tests. Deskbar.Windows adds the shell pieces (shortcut resolution, starting an installed app, the favicon fetch and its disk cache). Deskbar.App draws the chips in a new `FloatingBar.Bookmarks.cs` partial, hooks the row's existing drop handler, the window menu and the row menu, and wires everything in `App.xaml.cs`.

**Tech Stack:** .NET 10, WPF, System.Text.Json, CSharpFunctionalExtensions (`Result`, `Maybe`), xunit, `HttpClient` with `SocketsHttpHandler`, shell COM (`IShellLinkW`, `IPropertyStore`).

**Spec:** `docs/superpowers/specs/2026-11-26-workspace-bookmarks-design.md` (including the 2026-11-26 "Bookmark link from clipboard" paragraph under Adding).

## Global Constraints

- Work in the worktree `E:\repos\TaskSpaces\.claude\worktrees\workspace-bookmarks`, branch `worktree-workspace-bookmarks`. Never push to main, never merge.
- "No launching, no restore prompt" stands: a bookmark opens only on a click, a menu item or a pipe command. Switching to a workspace opens nothing.
- A chip must never pass for a running window: its button carries `Tag = "bookmark"`, never `IconTag` ("icon"), so press-drag, hover rings and `IconTitles`-style test walks do not take it for a window.
- `Link` targets: absolute `http` or `https` only, and no `"` and no control character anywhere (the same rule `ControlArguments.Link` applies, because the URL reaches a cmd line under the Store copy). No `file:`, `javascript:` or custom schemes.
- `Program` target: a fully qualified path ending in `.exe`; `Arguments` one command-line string; `WorkingDirectory` optional.
- `App` target: an AppUserModelID, started as `explorer.exe shell:AppsFolder\<id>`.
- Duplicate add (same kind and target, and for a program the same arguments) is refused with a message.
- Favicon request, exactly: plain GET to `<origin>/favicon.ico` only, no cookies, no credentials, user agent `Deskbar`, 5 s timeout, 256 KB cap, `image/*` content types only, certificate errors accepted for this handler alone. Cached under `%APPDATA%\Deskbar\bookmark-icons` (that is `AppPaths.NewStateDir`, so `DESKBAR_APPDATA` is honoured), keyed by origin. A failed fetch is not written to disk and is tried again on the next start.
- Never cache a lookup failure that can succeed later (CLAUDE.md): app icons, AppUserModelIDs and favicons are retried.
- Static WPF brushes are frozen (`FloatingBar.Frozen`).
- Every new menu action has a pipe twin listed in `ControlUsage`; "Bookmark link from clipboard" is covered by `bookmark add <workspace> --url` and gets no line of its own.
- Sizes in DIP: a chip's cell is `IconCellWidth` (`20 * IconScale + 4`), its icon `20 * IconScale`.
- Core tests: `dotnet test tests/Deskbar.Core.Tests --filter "Category!=Integration"` (the routine filter, run on the Core project so no WPF window opens on Petre's desktop). Windows tests only through `pwsh scripts/test-hidden.ps1 -Arguments 'tests/Deskbar.Windows.Tests --filter FullyQualifiedName~Bookmark'`. Before the PR: `dotnet build Deskbar.sln -c Release -warnaserror`.
- Comments carry the reasoning, as the rest of the code does. C# style per Petre: expression bodies, `var`, readonly collections, init over set, lambdas over loops, `Result` for expected failures.
- Commits end with the line `*opus5.5*`. PR body has a `## Release notes` section written for users (Task 10).
- External prerequisite, not part of this plan: the deskbar-site privacy page (`site/privacy.html`) must say that a web-link bookmark makes Deskbar ask that site for its icon, once, before the release that fetches favicons. The PR stays a draft (or carries `no-release`) until that site PR is merged.

## Review Focus

- A state.json with one bookmark this build cannot read (an unknown `Kind` from a newer build, a null `Target`, a hand edit) loads with that one bookmark dropped; the workspaces, rules and other bookmarks survive and the file does not fall back to `state.json.previous`. Test in Task 1.
- A PWA opened while its browser was already running shares the browser's process and command line (CLAUDE.md), so it has no `--app-id` and no roster identity. "Bookmark in this workspace" is still offered when its window's AppUserModelID is startable, and its App chip hides by that ID. Tests in Tasks 3 and 4.
- Text that looks like a link but is not safe (`javascript:alert(1)`, `file:///C:/x`, `https://a.com/"&calc`, a URL with a line break) is refused everywhere a URL enters: drop, clipboard, pipe. Test in Task 2.
- Two bookmarks with the same name in one workspace: the pipe refuses the name as ambiguous and lists both ids, rather than acting on the first. Test in Task 6.
- A favicon server that answers slowly, with a 2 MB file, with an HTML login page, or with a redirect: nothing is stored, the letter tile stays, and the next start tries again. Tests in Task 7.

---

### Task 0: Probes

Measure before building; nothing here may disturb Petre's desktop (CLAUDE.md, "No live experiments on his bar"). No desktop switches, no moving or closing his windows, no Deskbar restart. Reading AppUserModelIDs of existing windows and Start menu shortcuts is allowed; starting a PWA is allowed only if the probe closes the window it started (by the hwnd that appeared) and nothing else.

**Files:**
- Create: `docs/superpowers/notes/2026-11-26-bookmarks-probes.md`
- Probe scripts stay in a scratch folder, not the repo. Declare every P/Invoke `CharSet.Unicode` and call `SetProcessDpiAwarenessContext(-4)` first (CLAUDE.md probe traps).

- [ ] **Step 1: AppUserModelIDs of open windows.** For every top-level window, read `PKEY_AppUserModel_ID` through `SHGetPropertyStoreForWindow` (the code in `ShellTaskbarPins.AppIdOf`), with process path and command line. Record for: a plain Edge window, a plain Chrome window if any, every Edge PWA window (whether it runs as its own process or inside the browser's), a framed Store app (Calculator under `ApplicationFrameHost`), a desktop app (RustDesk or Notepad).
- [ ] **Step 2: Start menu entries of PWAs.** For the Edge PWAs in `shell:AppsFolder` (`StartMenuApps.Read`): parsing name, `System.Link.TargetParsingPath`, and for the `.lnk` behind each in `%APPDATA%\Microsoft\Windows\Start Menu\Programs` its target, arguments and `System.AppUserModel.ID` (through `IShellLinkW` + `IPropertyStore`). Note whether the target is `msedge_proxy.exe` or `msedge.exe`.
- [ ] **Step 3: does `shell:AppsFolder\<id>` start the PWA?** For one PWA that is NOT open, run `explorer.exe shell:AppsFolder\<id>` with the ID from Step 1/2, see whether its window appears and with which process path and command line, then close that window by its hwnd. Repeat with the ID of a plain Edge window (does it start a browser window?).
- [ ] **Step 4: what a Chromium address-bar drag offers.** A small WPF probe window (not Deskbar) on Petre's current desktop with `AllowDrop` that logs `IDataObject.GetFormats()` and the contents of `UniformResourceLocatorW`, `FileGroupDescriptorW` (the first `cFileName` starts at byte 76 of the stream: a 4-byte count, then 72 bytes of the descriptor before the name), `UnicodeText` and `FileDrop`, for an Edge address-bar drag and a Chrome one. Ask Petre to do the drag (one drag each); close the probe window after.
- [ ] **Step 5: write the notes.** One section per step with the measured values, then a "Decisions" section that answers each of these explicitly:
  - (a) the rule `BookmarkCandidates.IsStartableAppId` uses (default assumption: a packaged ID `<family>!<app>`, which is what YouTube Music shows in the `TaskbarPin` comment, `music.youtube.com-5929F88E_vezhnr0wkvrcy!App`). Whether a plain browser window's ID must be excluded.
  - (b) if Step 3 failed: the spec's fallback, a PWA window becomes a Program bookmark (browser exe with `--profile-directory` and `--app-id`), and Task 4's window rule changes accordingly.
  - (c) whether `msedge_proxy.exe`/`chrome_proxy.exe` targets need mapping to the sibling browser exe for the running test (Task 3, `ProxyBrowsers`).
  - (d) whether the title in `FileGroupDescriptorW` ends in `.url` as the spec assumes, and which format Task 9 reads first.
- [ ] **Step 6: Commit** `git add docs/superpowers/notes/2026-11-26-bookmarks-probes.md` then `git commit -m "docs: bookmark probes" -m "*opus5.5*"`.

Later tasks that depend on this: Task 3 (proxy exes, decision c), Task 4 (`IsStartableAppId`, decision a/b), Task 9 (formats, decision d).

---

### Task 1: The model and its persistence

**Files:**
- Create: `src/Deskbar.Core/Bookmarks/Bookmark.cs` (`Bookmark`, `BookmarkKind`)
- Create: `src/Deskbar.Core/Persistence/BookmarkConverter.cs`
- Modify: `src/Deskbar.Core/Domain/Workspace.cs` (new init property after `Pinned`)
- Modify: `src/Deskbar.Core/Persistence/JsonPersistenceStore.cs` (register the converter in `Options`; a load pass beside `MigrateTileSizes`)
- Test: `tests/Deskbar.Core.Tests/Bookmarks/BookmarkPersistenceTests.cs`

**Interfaces:**
- Produces: `public enum BookmarkKind { Link, Program, App }`; `public sealed record Bookmark(Guid Id, string Name, BookmarkKind Kind, string Target, string? Arguments = null, string? WorkingDirectory = null)`; `Workspace.Bookmarks { get; init; } : ValueList<Bookmark> = []`.

`ValueList<Bookmark>` rather than the spec's `IReadOnlyList<Bookmark>`: it is an `IReadOnlyList`, and the record comment on `TileSizes` explains why a list member must compare by contents.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Bookmarks_round_trip_through_state_json()
// Save a workspace holding one Link, one Program (Arguments "--x \"a b\"", WorkingDirectory @"C:\w") and one App;
// Load: Assert.Equal(saved.Workspaces[0].Bookmarks, loaded.Workspaces[0].Bookmarks); the file text contains "\"Kind\": \"Program\"".

[Fact] public void A_file_written_before_bookmarks_loads_with_none()
// {"Workspaces":[{"Id":"...","Name":"Work","DesktopId":null}],"WorkspaceRules":[],"RenameRules":[],"Inventory":{}}
// Assert.Empty(loaded.Workspaces[0].Bookmarks)

[Fact] public void An_unreadable_bookmark_is_dropped_and_the_rest_of_the_file_loads()
// Bookmarks: [{"Kind":"Teleport",...}, {"Kind":"Link","Target":null,...}, a good Link]; plus a second workspace.
// Assert.True(load.IsSuccess); Assert.Single(bookmarks of the first workspace); both workspaces present, which a
// fallback to state.json.previous (none exists in the test folder) could not give.

[Fact] public void Renaming_a_workspace_keeps_its_bookmarks()   // via WorkspaceManager.RenameWorkspace, FakeStore
```

- [ ] **Step 2: Run** `dotnet test tests/Deskbar.Core.Tests --filter "FullyQualifiedName~BookmarkPersistence"`. Expected: build FAIL, `Bookmark` not defined.
- [ ] **Step 3: Implement.** `BookmarkConverter : JsonConverter<Bookmark>` modelled on `ScreenTileLayoutConverter`: parse the entry to a `JsonDocument`, deserialize a private twin record inside a `try`, return `null` on `JsonException`/`NotSupportedException`/`InvalidOperationException` or when `BookmarkRules.Check` (Task 2; until then a null/empty `Name` or `Target` check) fails. Writes the plain record shape. In `JsonPersistenceStore`, extend the post-load pass to drop null bookmarks (`(w.Bookmarks ?? []).Where(b => b is not null).ToValueList()`); nothing in that pass may throw (see its comment).
- [ ] **Step 4: Run** the same filter. Expected: PASS.
- [ ] **Step 5: Commit** `git add src/Deskbar.Core/Bookmarks src/Deskbar.Core/Persistence src/Deskbar.Core/Domain/Workspace.cs tests/Deskbar.Core.Tests/Bookmarks` then `git commit -m "feat: bookmarks on the workspace record" -m "*opus5.5*"`.

---

### Task 2: Adding, renaming and removing

**Files:**
- Create: `src/Deskbar.Core/Bookmarks/BookmarkRules.cs`
- Modify: `src/Deskbar.Core/WorkspaceManager.cs` (three methods beside `SetWorkspaceFolded`, ~line 1844, same `Workspace(id).Tap(_ => Persist(...))` shape)
- Test: `tests/Deskbar.Core.Tests/Bookmarks/BookmarkEditTests.cs`

**Interfaces:**
- Consumes: Task 1's `Bookmark`.
- Produces:
  - `public static Result<Bookmark> BookmarkRules.Check(Bookmark bookmark)`: kind-specific target rules from Global Constraints, `Name` trimmed and non-empty.
  - `public static Result<Bookmark> BookmarkRules.Link(string url, string? name = null)`: trims, checks, names it `name ?? uri.Host`, new `Guid`.
  - `public static bool BookmarkRules.SameTarget(Bookmark a, Bookmark b)`: same kind; Link by `Uri.AbsoluteUri` ordinal; Program by path ignoring case and by arguments (null equals empty, trimmed); App ignoring case.
  - `public Result<Bookmark> WorkspaceManager.AddBookmark(Guid workspaceId, Bookmark bookmark)`
  - `public Result WorkspaceManager.RenameBookmark(Guid workspaceId, Guid bookmarkId, string name)`
  - `public Result WorkspaceManager.RemoveBookmark(Guid workspaceId, Guid bookmarkId)`
  - `public Maybe<Bookmark> WorkspaceManager.BookmarkOf(Guid workspaceId, Guid bookmarkId)`

- [ ] **Step 1: Write the failing tests** (manager built as in `FoldTests.Started`, `FakeStore`)

```csharp
[Fact] public void Adding_a_link_saves_it_and_pulses()          // store.Stored has it; StateChanged fired once
[Fact] public void A_duplicate_is_refused_with_a_message()
// add Link https://portainer.local:9443/ twice -> second IsFailure, Error contains "already"; Single(bookmarks)
[Fact] public void A_program_with_other_arguments_is_not_a_duplicate()
[Theory]
[InlineData("javascript:alert(1)")] [InlineData("file:///C:/x")] [InlineData("ftp://x")]
[InlineData("https://a.com/\"&calc")] [InlineData("https://a.com/\nx")] [InlineData("portainer")]
public void Only_plain_http_and_https_links_are_accepted(string url) => Assert.True(BookmarkRules.Link(url).IsFailure);
[Fact] public void A_link_is_named_after_its_host() // BookmarkRules.Link("https://portainer.local:9443/#!/home").Value.Name == "portainer.local"
[Fact] public void A_program_needs_a_full_exe_path()  // "rustdesk.exe" and @"C:\x\readme.txt" refused
[Fact] public void Renaming_trims_and_refuses_empty()
[Fact] public void Removing_a_bookmark_leaves_the_others()
[Fact] public void Every_edit_on_a_missing_workspace_or_bookmark_fails()
```

- [ ] **Step 2: Run** `dotnet test tests/Deskbar.Core.Tests --filter "FullyQualifiedName~BookmarkEdit"`. Expected: build FAIL.
- [ ] **Step 3: Implement** the rules and the four manager methods. `AddBookmark` runs `BookmarkRules.Check`, refuses `SameTarget` with `"<workspace> already has this bookmark ('<existing name>')."`, appends (add order is display order). Switch `BookmarkConverter` (Task 1) to use `Check`.
- [ ] **Step 4: Run** the filter, then the whole Core suite. Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "feat: add, rename and remove workspace bookmarks" -m "*opus5.5*"` (after `git add` of the touched files).

---

### Task 3: Is it running

**Files:**
- Create: `src/Deskbar.Core/Bookmarks/BookmarkRunning.cs`
- Modify: `src/Deskbar.Core/Rehydration/RosterIdentity.cs` (expose the Chromium test)
- Test: `tests/Deskbar.Core.Tests/Bookmarks/BookmarkRunningTests.cs`

**Interfaces:**
- Produces:
  - `public static bool RosterIdentity.IsChromium(string processPath)` (reads the existing `Browsers` set).
  - `public static bool BookmarkRunning.IsRunning(Bookmark bookmark, IEnumerable<WindowInfo> windows, IReadOnlySet<string> openAppIds)`
  - `public static IReadOnlyList<Bookmark> BookmarkRunning.Showing(IEnumerable<Bookmark> bookmarks, IReadOnlyList<WindowInfo> windows, IReadOnlySet<string> openAppIds)` (order kept).

Rules: Link never. App when `openAppIds` contains the target (ignoring case). Program, not Chromium: any window whose `ProcessPath` equals the target ignoring case, arguments ignored. Program, Chromium: `RosterIdentity.Of(target, $"\"{target}\" {arguments}") == RosterIdentity.Of(window.ProcessPath, window.CommandLine)` for some window. If Task 0 decision (c) says so, a `ProxyBrowsers` map turns `msedge_proxy.exe`/`chrome_proxy.exe` into the sibling `msedge.exe`/`chrome.exe` first. Windows on every desktop count; a program with no window (tray only) does not.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void A_link_never_hides()
[Fact] public void A_program_hides_while_its_exe_has_a_window_whatever_the_arguments()
[Fact] public void A_program_in_the_tray_only_still_shows()           // no window with that path
[Fact] public void A_pwa_program_does_not_hide_for_a_plain_browser_window()
// target C:\Edge\msedge.exe, args "--profile-directory=Default --app-id=abc"; window msedge.exe "--profile-directory=Default" -> false
[Fact] public void A_pwa_program_hides_while_its_app_window_runs()     // window cmdline carries --app-id=abc -> true
[Fact] public void An_app_hides_by_its_app_user_model_id_even_inside_the_browser_process()
// window msedge.exe with the browser's own cmdline, openAppIds { "music.youtube.com-5929F88E_vezhnr0wkvrcy!App" } -> true
[Fact] public void Showing_keeps_add_order_and_drops_the_running()
```

- [ ] **Step 2: Run** `--filter "FullyQualifiedName~BookmarkRunning"`. Expected: build FAIL.
- [ ] **Step 3: Implement** as above.
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "feat: a bookmark knows when its program is running" -m "*opus5.5*"`.

---

### Task 4: Where a bookmark comes from (drops and windows)

**Files:**
- Create: `src/Deskbar.Core/Bookmarks/BookmarkDrop.cs` (`BookmarkDrop`, `ShortcutTarget`, `IBookmarkSources`)
- Create: `src/Deskbar.Core/Bookmarks/BookmarkDropParser.cs`
- Create: `src/Deskbar.Core/Bookmarks/InternetShortcut.cs`
- Create: `src/Deskbar.Core/Bookmarks/BookmarkCandidates.cs`
- Modify: `src/Deskbar.Core/WorkspaceManager.cs` (`BookmarkFor`, beside `CanRememberWorkspace` ~line 3271, because it needs the private `IsOurs`)
- Test: `tests/Deskbar.Core.Tests/Bookmarks/BookmarkDropTests.cs`, `tests/Deskbar.Core.Tests/Bookmarks/BookmarkCandidateTests.cs`

**Interfaces:**
- Consumes: `BookmarkRules.Link`, `BookmarkRules.Check` (Task 2), `RosterIdentity.Of(WindowInfo)`, `CommandLines.ArgumentsOf`.
- Produces:
  - `public sealed record BookmarkDrop(string? Url, string? UrlTitle, string? Text, IReadOnlyList<string> Files)`; `UrlTitle` is the `FileGroupDescriptorW` file name as given (with `.url`).
  - `public sealed record ShortcutTarget(string? Target, string? Arguments, string? WorkingDirectory, string? AppId)`
  - `public interface IBookmarkSources { Maybe<ShortcutTarget> ResolveShortcut(string lnkPath); string? DescribeExe(string exePath); string? NameOfApp(string appId); string? ReadText(string path); }`
  - `public static Result BookmarkDropParser.Precheck(BookmarkDrop drop)`: no IO, for `DragOver`; failure text is the readout's reason.
  - `public static Result<Bookmark> BookmarkDropParser.Parse(BookmarkDrop drop, IBookmarkSources sources)`
  - `public static Maybe<string> InternetShortcut.UrlOf(string iniText)` (the `URL=` line of `[InternetShortcut]`).
  - `public static bool BookmarkCandidates.IsStartableAppId(string appId)` (rule from Task 0, decision a).
  - `public static Maybe<Bookmark> BookmarkCandidates.FromWindow(WindowInfo window, string? appId, IBookmarkSources sources)`
  - `public Maybe<Bookmark> WorkspaceManager.BookmarkFor(WindowHandle window, string? appId, IBookmarkSources sources)`: none for Deskbar's own windows and windows the manager does not know.

Parse order: `Url` (named `UrlTitle` minus `.url`, else host); else `Text` that `BookmarkRules.Link` accepts (named host); else the first file only: `.url` via `ReadText` + `UrlOf`, named after the file; `.exe` a Program named `DescribeExe ?? file name without extension`; `.lnk` via `ResolveShortcut`: AppId with no target an App, a target ending `.exe` a Program (arguments, working folder) named after the shortcut, anything else refused. Every other drop refused with a reason ("only links, programs and shortcuts can be bookmarked").

`FromWindow` order (spec's App first, then Program): a startable `appId` gives an App named `NameOfApp(appId) ?? window.DisplayName`; else a window with a roster identity (`RosterIdentity.Of(window).HasValue`) gives a Program from `ProcessPath` and `CommandLines.ArgumentsOf`, named `DescribeExe ?? ProcessName`; else none. This offers the item for a PWA sharing the browser's process (no identity, but a startable ID), which the spec's "left out for no identity" sentence would otherwise exclude; a plain browser window stays excluded unless Task 0 shows its ID is startable and decision (a) keeps it out explicitly.

- [ ] **Step 1: Write the failing tests** (a `FakeSources : IBookmarkSources` in the test file)

```csharp
[Fact] public void A_chromium_url_drop_is_named_after_the_page_title()
// new BookmarkDrop("https://portainer.local:9443/", "Portainer.url", null, []) -> Link, Name "Portainer"
[Fact] public void A_url_with_no_title_is_named_after_its_host()
[Fact] public void Plain_text_that_is_a_link_becomes_a_link()
[Fact] public void Plain_text_that_is_not_a_link_is_refused_with_a_reason()
[Fact] public void Only_the_first_file_counts()
[Fact] public void An_exe_becomes_a_program_named_by_its_description()      // DescribeExe -> "RustDesk"
[Fact] public void An_internet_shortcut_file_becomes_a_link()             // "[InternetShortcut]\r\nURL=https://x.org/"
[Fact] public void A_shortcut_to_an_exe_keeps_its_arguments_and_folder()
[Fact] public void A_shortcut_with_only_an_app_id_becomes_an_app()
[Fact] public void A_shortcut_to_a_document_is_refused()
[Fact] public void Precheck_refuses_a_text_file_without_reading_it()       // FakeSources asserts no call
[Fact] public void A_window_with_a_startable_app_id_becomes_an_app()
[Fact] public void A_pwa_inside_the_browser_process_becomes_an_app()       // msedge.exe, browser cmdline, packaged id
[Fact] public void A_desktop_app_window_becomes_a_program_with_its_arguments()
[Fact] public void A_plain_browser_window_terminal_or_explorer_offers_nothing()
[Fact] public void Deskbars_own_windows_offer_nothing()                    // WorkspaceManager.BookmarkFor, ownProcessId set
```

- [ ] **Step 2: Run** `--filter "FullyQualifiedName~BookmarkDrop|FullyQualifiedName~BookmarkCandidate"`. Expected: build FAIL.
- [ ] **Step 3: Implement** the records, the parser, `UrlOf`, `FromWindow`, `BookmarkFor`.
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "feat: bookmarks from drops and from windows" -m "*opus5.5*"`.

---

### Task 5: Opening

**Files:**
- Create: `src/Deskbar.Core/Bookmarks/BookmarkOpener.cs`
- Test: `tests/Deskbar.Core.Tests/Bookmarks/BookmarkOpenerTests.cs`

**Interfaces:**
- Consumes: `WorkspaceManager.Switch`, `CurrentWorkspaceId`, `StartIn(Guid, QuickFind.LaunchTarget, Func<ProgramLaunch, Result<int?>>)`, `KnownWindows`, `BookmarkOf` (Task 2), `BookmarkRunning.Showing` (Task 3).
- Produces:
  - `public sealed record BookmarkStarts(Func<string, string, string?, Result<int?>> Program, Func<string, Result> App, Func<string, Result> Link)`; Program takes (exe, verbatim arguments, working folder).
  - `public sealed class BookmarkOpener(WorkspaceManager manager, BookmarkStarts starts, Func<IReadOnlySet<string>> openAppIds)`
  - `public Result Open(Guid workspaceId, Guid bookmarkId)`
  - `public Result<IReadOnlyList<Bookmark>> OpenAll(Guid workspaceId)` (answers what it opened)
  - `public IReadOnlyList<Bookmark> Showing(Workspace workspace)` and `public bool IsRunning(Bookmark bookmark)` (Task 3 against `manager.KnownWindows` and `openAppIds()`), so the bar and the pipe ask one place.

Sequence: switch only when `CurrentWorkspaceId` is not the workspace (as `TasksBoard.OpenLink` does); then Program through `manager.StartIn(workspaceId, new LaunchTarget(new ProgramLaunch(target, [], cwd), target), _ => starts.Program(target, arguments ?? "", cwd))`, so the window is expected for the workspace; App `starts.App(id)`; Link `starts.Link(url)`. Every failure is rewritten `"<bookmark name>: <error>"`. `OpenAll` switches once and opens `Showing(workspace)` in order; a failure does not stop the rest, and the combined failures name each bookmark.

- [ ] **Step 1: Write the failing tests** (fake starts recording calls in order, `FakeDesktops` records switches)

```csharp
[Fact] public void A_click_from_another_workspace_switches_then_starts()
[Fact] public void A_click_on_the_current_workspace_does_not_switch()
[Fact] public void A_program_is_expected_in_its_workspace()
// start returns pid 5000; window of that exe appears via FakeMonitor -> placed on the bookmark's desktop (as RemoteControlStartTests)
[Fact] public void Program_arguments_reach_the_start_verbatim()     // "--x \"a b\"" arrives unchanged
[Fact] public void A_failed_start_names_the_bookmark_and_withdraws_the_expectation()
[Fact] public void Open_all_switches_once_skips_running_programs_and_opens_every_link()
[Fact] public void Open_all_carries_on_past_a_failure_and_reports_it_by_name()
```

- [ ] **Step 2: Run** `--filter "FullyQualifiedName~BookmarkOpener"`. Expected: build FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "feat: opening a bookmark on its workspace" -m "*opus5.5*"`.

---

### Task 6: Pipe commands

**Files:**
- Create: `src/Deskbar.Core/Control/BookmarkArguments.cs`
- Create: `src/Deskbar.Core/Control/RemoteControl.Bookmarks.cs`
- Modify: `src/Deskbar.Core/Control/RemoteControl.cs` (constructor parameter `BookmarkOpener? bookmarks = null` at the end, with a header comment like the others; dispatch arm `("bookmark", _) => Bookmark(rest)`)
- Modify: `src/Deskbar.Core/Control/ControlUsage.cs` (new section "Bookmark chip menu" after "Workspace row menu")
- Modify: `tests/Deskbar.Core.Tests/RemoteControlMenuTests.cs` (`Help_lists_every_command`: add `"bookmark"`)
- Test: `tests/Deskbar.Core.Tests/Control/RemoteControlBookmarkTests.cs`

**Interfaces:**
- Consumes: Tasks 2, 3, 5.
- Produces:
  - `public abstract record BookmarkRequest` with `List(string? Workspace)`, `Add(string Workspace, BookmarkKind Kind, string Target, string? Arguments, string? WorkingDirectory, string? Name)`, `Rename(string Workspace, string Bookmark, string Name)`, `Remove(string Workspace, string Bookmark)`, `Open(string Workspace, string Bookmark)`, `OpenAll(string Workspace)`.
  - `public static Result<BookmarkRequest> BookmarkArguments.Parse(IReadOnlyList<string> args)`
  - `public static string BookmarkArguments.Join(IReadOnlyList<string> arguments)`: C runtime quoting (quote an argument that is empty or holds whitespace or `"`; inside quotes a `"` becomes `\"` and the backslashes before it are doubled).

ControlUsage lines, verb `bookmark`, arguments exactly as the spec:

```
list [<workspace>]
add <workspace> --url <url> | --program <path> [--cwd <folder>] [-- <args>] | --app <id> [--name <name>]
rename <workspace> <name|id> --name <name>
remove <workspace> <name|id>
open <workspace> <name|id>
open-all <workspace>
```

Descriptions name the menu items: "Rename…", "Remove", "Open", "Open all bookmarks"; the `add --url` line also says it is the twin of the row menu's "Bookmark link from clipboard" and of a drop.

`list` replies with JSON per bookmark: `workspace`, `id`, `name`, `kind` (lower case), `target`, `arguments`, `workingDirectory`, `running` (from the opener; false for every one when the opener is null). A bookmark named by text matches its name trimmed, ignoring case, or its id; two name matches refuse with `"Two bookmarks in <workspace> are named '<n>': <id1>, <id2>. Name one by its id."`. `open` and `open-all` refuse with `"This copy of Deskbar cannot open bookmarks."` when the opener is null.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Add_url_list_rename_remove_round_trip()
[Fact] public void Add_program_joins_the_arguments_after_the_separator()
// "bookmark","add","Work","--program",@"C:\RustDesk\rustdesk.exe","--","--connect","a b" -> Arguments "--connect \"a b\""
[Fact] public void Add_app_takes_the_id_and_an_optional_name()
[Fact] public void Add_with_two_kinds_or_none_prints_the_usage_line()
[Fact] public void A_duplicate_add_is_refused()                                   // exit ControlReply.Failed
[Fact] public void An_unsafe_url_is_refused()                                     // "javascript:alert(1)"
[Fact] public void An_ambiguous_name_is_refused_and_lists_both_ids()
[Fact] public void Open_switches_and_starts_through_the_opener()
[Fact] public void Open_all_reports_what_it_opened()
[Fact] public void Open_without_an_opener_is_refused()
[Fact] public void Join_quotes_like_the_c_runtime()
// ["a b","c"] -> "\"a b\" c"; ["x\"y"] -> "\"x\\\"y\""; [@"C:\dir\"] (with space? no) -> unchanged; [""] -> "\"\""
```

- [ ] **Step 2: Run** `--filter "FullyQualifiedName~RemoteControlBookmark|FullyQualifiedName~Help_lists_every_command"`. Expected: FAIL.
- [ ] **Step 3: Implement** parsing, the six handlers (each calling the same manager or opener method the menus call) and the usage section.
- [ ] **Step 4: Run** the filter, then the whole Core suite. Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "feat: bookmark commands on the control pipe" -m "*opus5.5*"`.

---

### Task 7: Shell and network pieces

**Files:**
- Create: `src/Deskbar.Windows/Shell/ShellBookmarkSources.cs` (implements `IBookmarkSources`)
- Modify: `src/Deskbar.Windows/Shell/TaskbarPins.cs` (pull the installed-app arm of `Launch` into `public static Result LaunchApp(string appId, string name)` and call it from `Launch`)
- Create: `src/Deskbar.Windows/Shell/Favicons.cs` (`FaviconFetcher`, `FaviconStore`)
- Create: `src/Deskbar.Core/Bookmarks/LetterTile.cs`, `src/Deskbar.Core/Bookmarks/FaviconKey.cs`
- Test: `tests/Deskbar.Core.Tests/Bookmarks/LetterTileTests.cs`, `tests/Deskbar.Windows.Tests/BookmarkFaviconTests.cs`, `tests/Deskbar.Windows.Tests/BookmarkShortcutTests.cs`

**Interfaces:**
- Produces:
  - `ShellBookmarkSources : IBookmarkSources`: `ResolveShortcut` through `IShellLinkW` + `IPersistFile.Load`, reading `GetPath`, `GetArguments`, `GetWorkingDirectory` and `PKEY_AppUserModel_ID` through the link's `IPropertyStore` (declare the interfaces in full, as `WindowAppId` explains, `CharSet.Unicode`); `DescribeExe` from `FileVersionInfo.FileDescription`; `NameOfApp` delegating to `ShellTaskbarPins.NameOfApp`; `ReadText` with a 64 KB cap.
  - `public static Result ShellTaskbarPins.LaunchApp(string appId, string name)`
  - `public static string FaviconKey.Of(Uri origin)`: `<scheme>_<idn host>_<port>`, lower case, with any character invalid in a file name replaced by `-`.
  - `public static (char Letter, double Hue) LetterTile.Of(string name, string host)`: first letter or digit of the name upper-cased (`?` when none); hue from an FNV-1a hash of the lower-cased host, mod 360 (never `string.GetHashCode`, which changes per process).
  - `public sealed class FaviconFetcher(HttpMessageHandler? handler = null)` with `public Task<Result<byte[]>> Fetch(Uri link, CancellationToken cancel)` and `internal static SocketsHttpHandler CreateHandler()`.
  - `public sealed class FaviconStore(string folder)` with `Maybe<byte[]> Cached(Uri link)` and `Result Save(Uri link, byte[] image)` (writes `<FaviconKey>.ico` through a temp file and rename).

`CreateHandler`: `UseCookies = false`, `Credentials = null`, `PreAuthenticate = false`, `AllowAutoRedirect = false` (a redirect leaves the origin or lands on a login page, so it counts as no icon), `SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true` with a comment saying this is the one place certificate checks are relaxed and why (Portainer's self-signed certificate; the answer is only decoded as an image). `Fetch`: `GET <scheme>://<authority>/favicon.ico`, `User-Agent: Deskbar`, 5 s timeout, refuse non-2xx, refuse a content type not starting `image/`, refuse `Content-Length` over 262144, read at most 262145 bytes and refuse if exceeded. Nothing here writes to disk; the caller saves only an image it decoded (Task 8), so a body WPF cannot decode is never stored.

- [ ] **Step 1: Write the failing tests**

```csharp
// Core
[Fact] public void The_tile_letter_is_the_first_letter_of_the_name()      // "portainer" -> 'P'; "  9gag" -> '9'; "--" -> '?'
[Fact] public void The_tile_colour_is_the_same_every_time_for_a_host()
[Fact] public void Two_hosts_get_different_colours()                       // portainer.local vs grafana.local
[Fact] public void The_cache_key_is_the_origin()                           // https://Portainer.local:9443/x -> "https_portainer.local_9443"

// Windows (fake HttpMessageHandler, no network)
[Fact] public void The_request_is_a_plain_get_of_favicon_ico_at_the_origin()  // path, method, User-Agent "Deskbar", no Cookie header
[Fact] public void The_handler_sends_no_cookies_or_credentials_and_follows_no_redirect()
[Fact] public void A_page_that_is_not_an_image_is_no_icon()                // 200 text/html
[Fact] public void A_body_over_256_KB_is_no_icon()                         // no Content-Length, 300 KB stream
[Fact] public void A_redirect_is_no_icon()                                 // 302 to /login
[Fact] public void A_slow_server_times_out_after_five_seconds()            // handler delays past the timeout; injected clock or a short override
[Fact] public void A_failure_is_not_written_to_the_store()
[Fact] public void A_saved_icon_comes_back_from_the_store()

// Windows, real shell (a .lnk written to a temp folder by the test through IShellLinkW)
[Fact] public void A_shortcut_resolves_to_its_target_arguments_and_folder()
[Fact] public void A_shortcut_with_an_app_id_reports_it()
```

- [ ] **Step 2: Run** Core: `--filter "FullyQualifiedName~LetterTile|FullyQualifiedName~FaviconKey"`; Windows: `pwsh scripts/test-hidden.ps1 -Arguments 'tests/Deskbar.Windows.Tests --filter FullyQualifiedName~Bookmark'`. Expected: build FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** both. Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "feat: shortcut, app start and favicon pieces for bookmarks" -m "*opus5.5*"`.

---

### Task 8: Chips on the bar, their menu and the row menu

**Files:**
- Create: `src/Deskbar.App/FloatingBar.Bookmarks.cs` (chip button, chip menu, row menu items, `BookmarkIcons`)
- Create: `src/Deskbar.Core/Bookmarks/ChipLayout.cs`
- Modify: `src/Deskbar.App/FloatingBar.xaml.cs`: `GroupRow` (~line 4209) gains `IReadOnlyList<Bookmark>? bookmarks = null`; the workspace-row call (~line 2880) passes `bookmarkOpener?.Showing(g.Workspace) ?? g.Workspace.Bookmarks`; `emptyRow` (~line 4269) is false when chips show; the wrap (~line 4498) goes through `ChipLayout.Split` for the first lane; `LineOf` (~line 3156) gains `IReadOnlyList<Bookmark>? chips = null` and appends them to the first lane's stack, giving the first lane an empty run when a line has chips and no windows; `WorkspaceMenu` (~line 7418) gets the two items.
- Modify: `src/Deskbar.App/App.xaml.cs`: build one `BookmarkOpener` (starts: `DetachedLauncher.StartVerbatim` mapped to `"Could not start <exe>: ..."`, `ShellTaskbarPins.LaunchApp`, `url => ProgramStarter.Start(new ProgramLaunch(url, [], null))` turned into a plain `Result`, as `open` does in `RemoteControl.AsUsual`; app ids `() => floatingBar?.OpenAppIds ?? empty`) and hand it to the bar (`BookmarkOpener` init property) and to `RemoteControl` (`bookmarks:`); hand the bar `BookmarkSources = new ShellBookmarkSources()` and a `BookmarkIcons` over `Path.Combine(AppPaths.NewStateDir, "bookmark-icons")`.
- Test: `tests/Deskbar.Core.Tests/Bookmarks/ChipLayoutTests.cs`, `tests/Deskbar.Windows.Tests/FloatingBarBookmarksTests.cs` (the `Harness` from `FloatingBarRebuildTests.cs`)

`App.OpenUrl` is not used for links: on failure it shows a box saying "Could not open the download page", and the bar wants a `Result`.

**Interfaces:**
- Consumes: Tasks 2, 3, 5, 7.
- Produces:
  - `public sealed record RowCell<T>(Maybe<T> Window, Bookmark? Chip)`
  - `public static (IReadOnlyList<IReadOnlyList<T>> Windows, IReadOnlyList<IReadOnlyList<Bookmark>> Chips) ChipLayout.Split<T>(IReadOnlyList<T> windows, IReadOnlyList<Bookmark> chips, Func<IReadOnlyList<RowCell<T>>, IReadOnlyList<IReadOnlyList<RowCell<T>>>> wrap)`: lines aligned by index; the bar passes `cells => IconRowLimit.LinesThatFit(cells, _ => IconCellWidth, share, floor)` or `IconRowLimit.Lines(cells)` for a bar with no width, so chips wrap with the icons by the same arithmetic.
  - `FloatingBar.BookmarkOpener`, `FloatingBar.BookmarkSources` (public init properties); `internal BookmarkIcons BookmarkIcons { get; init; }` with `ImageSource? For(Bookmark b, Action arrived)`.
  - `internal ContextMenu BookmarkMenu(Guid workspaceId, Bookmark bookmark, ContextMenu? into = null)`.
  - `internal static Maybe<string> ClipboardLink()`: the clipboard's text trimmed when `BookmarkRules.Link` accepts it; any clipboard exception is `None`.

The chip, decided here: a `Button` with `Tag = "bookmark"`, `ShellButtonTemplate`, width `IconCellWidth`, automation name `bookmark.Name`; content a `Grid` of the icon (`20 * IconScale`), a dashed outline (`Rectangle`, `StrokeDashArray = {2, 2}`, stroke a frozen `#80FFFFFF`, radius 3) and, for a Link, a 4 DIP dot at the bottom right (frozen `#C0FFFFFF` with a dark rim, like the attention dot). The first chip of a row carries a 1 DIP divider at its own left edge inside its cell, so the divider costs no width in the wrap. Opacity 0.5, 1.0 while `IsMouseOver` (a trigger or the MouseEnter/Leave pair). Tooltip: name, then the target (for a Program its arguments too). Click: `MarkPressConsumed()`, then `Report(opener.Open(...))`. A middle-drag on a chip moves the bar as on a pin. `LazyMenu(button, menu => BookmarkMenu(...))`.

Icons: Program `IconCache.For(target)`; App `IconCache.Shell.ImageOfApp(id, 48)` through `IconCache.ToImageSource`, kept per id, a miss not kept; Link from `BookmarkIcons`: memory cache by `FaviconKey`, else `FaviconStore.Cached`, else the letter tile drawn now and one `FaviconFetcher.Fetch` per origin per run on a pool thread; the bytes are decoded on the UI thread (`BitmapDecoder`, frame nearest 32 px, frozen), saved only when the decode succeeded, then `arrived()` asks for a rebuild. A failure marks the origin tried for this run only.

Folded rows show no chips (they sit in `icons`, which a folded row does not add). Titled rows (the window list in the row) get the chips on one extra icon line under the list, wrapped by the same `Split`, rather than inside the titled lines.

Chip menu: Open; Open all bookmarks; separator; Rename… (`PromptDialog.Ask("Rename bookmark", "Name:", bookmark.Name, owner: this)` then `manager.RenameBookmark`); Remove (no confirmation, as Unpin from taskbar). Row menu, after Insert after… and before the group items: "Open all bookmarks" when the workspace has any (`opener.OpenAll`), and "Bookmark link from clipboard", always present, its `IsEnabled` set from `ClipboardLink()` in the menu's `Opened` event (a lazy menu is filled once, and the clipboard changes while it lives); a click adds `BookmarkRules.Link(url)` and reports a refusal through `Report`.

- [ ] **Step 1: Write the failing tests**

```csharp
// Core
[Fact] public void Chips_follow_the_windows_on_the_last_line_and_wrap_with_them()
// 3 windows, 2 chips, wrap of 4 per line -> Windows [[w1,w2,w3],[]], Chips [[b1],[b2]]
[Fact] public void A_row_with_no_windows_holds_only_chips()
[Fact] public void No_chips_changes_nothing()

// Windows (StaThread.Run, Harness)
[Fact] public void A_workspace_shows_its_bookmarks_after_its_windows()     // Buttons with Tag "bookmark", names in add order, after the "icon" buttons in GEPHA's row
[Fact] public void A_chip_is_not_an_icon()                                 // IconTitles(bar.Rows) does not contain the bookmark name
[Fact] public void A_folded_row_shows_no_chips()
[Fact] public void An_empty_workspace_with_bookmarks_shows_its_chips()     // Sparrow's window removed
[Fact] public void A_running_program_hides_its_chip()                      // bookmark target @"C:\beeper.exe" in Sparrow, Beeper window open
[Fact] public void Clicking_a_chip_opens_it_through_the_opener()           // fake BookmarkStarts records the call; FakeDesktops recorded a switch
[Fact] public void The_chip_menu_has_open_open_all_rename_and_remove()
[Fact] public void Remove_takes_the_chip_off_the_row()
[Fact] public void The_row_menu_offers_open_all_only_with_bookmarks()
[Fact] public void Bookmark_link_from_clipboard_is_enabled_only_for_a_web_link()  // Clipboard.SetText on the STA thread; "https://x.org " enabled, "hello" disabled
[Fact] public void Bookmark_link_from_clipboard_adds_a_link_named_after_the_host()
```

- [ ] **Step 2: Run** Core `--filter "FullyQualifiedName~ChipLayout"`; Windows through `scripts/test-hidden.ps1` with `FullyQualifiedName~FloatingBarBookmarks`. Expected: FAIL.
- [ ] **Step 3: Implement** as described.
- [ ] **Step 4: Run** both, then the full Windows suite through `pwsh scripts/test-hidden.ps1` (the bar's layout tests, `FloatingBarFoldTests`, `IconRowLimitTests` and the lane tests must stay green). Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "feat: bookmark chips in a workspace's row" -m "*opus5.5*"`.

---

### Task 9: Adding from the bar (drop and window menu)

**Files:**
- Create: `src/Deskbar.App/BookmarkDropData.cs`
- Modify: `src/Deskbar.App/FloatingBar.xaml.cs`: the row container's `DragOver` and `Drop` (~lines 4980-5030) and `IconMenu` (~line 7997)
- Test: `tests/Deskbar.Windows.Tests/BookmarkDropDataTests.cs`, additions to `tests/Deskbar.Windows.Tests/FloatingBarBookmarksTests.cs`

**Interfaces:**
- Consumes: Task 4 (`BookmarkDrop`, `BookmarkDropParser`, `WorkspaceManager.BookmarkFor`), Task 2 (`AddBookmark`), `FloatingBar.AppIdOf(WindowInfo)` (the pins' cache), `BookmarkSources` (Task 8).
- Produces:
  - `internal static Maybe<BookmarkDrop> BookmarkDropData.From(IDataObject data)`: none when the data holds `DraggedWindow.DragFormat` or none of `UniformResourceLocatorW`, `FileGroupDescriptorW`, `UnicodeText`, `FileDrop`. Reads the formats Task 0 decision (d) names; `FileGroupDescriptorW` decoded from its `MemoryStream` (count, then `cFileName` of the first descriptor).

DragOver, for a workspace row only (`bookmarks is not null`): a `DraggedWindow` keeps today's path untouched. Otherwise `BookmarkDropData.From` then `BookmarkDropParser.Precheck`: accepted gives `e.Effects = e.AllowedEffects & (Copy | Link)`, the row highlight and the readout `→ bookmark in <workspace>`; refused gives `None` and the readout `can't bookmark: <reason>`. Drop runs `Parse` with `BookmarkSources`, then `manager.AddBookmark`, and reports a refusal through `Report`. Rows that are not workspaces (the pinned row, unnamed desktops, group headers) refuse as today.

Window menu: "Bookmark in this workspace" (glyph `☆`) after "Remember …'s workspace", only when the window is in a workspace (`manager.WorkspaceOf`) and `manager.BookmarkFor(handle, AppIdOf(window), BookmarkSources)` has a value; the click adds it and reports.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void A_chromium_drag_gives_the_url_and_the_page_title()
// DataObject with UniformResourceLocatorW "https://portainer.local:9443/\0" and a FileGroupDescriptorW stream naming "Portainer.url"
[Fact] public void A_file_drop_gives_the_files()
[Fact] public void A_window_drag_is_not_a_bookmark()
[Fact] public void Dropping_a_link_on_a_row_adds_it_there()        // raise DragOver/Drop on Sparrow's container; Sparrow.Bookmarks has it
[Fact] public void The_readout_says_where_the_bookmark_goes()      // "→ bookmark in Sparrow"
[Fact] public void A_refused_drop_says_why_and_adds_nothing()      // .txt file
[Fact] public void The_window_menu_offers_bookmark_for_a_desktop_app()
[Fact] public void The_window_menu_has_no_bookmark_item_for_an_explorer_window()
```

- [ ] **Step 2: Run** through `scripts/test-hidden.ps1` with `FullyQualifiedName~Bookmark`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the filter, then the full Windows suite through `test-hidden.ps1`, then `dotnet build Deskbar.sln -c Release -warnaserror`. Expected: PASS, no warnings.
- [ ] **Step 5: Commit** `git commit -m "feat: add bookmarks by dropping on a row or from a window's menu" -m "*opus5.5*"`.

---

### Task 10: Docs and release notes

**Files:**
- Modify: `README.md` (a `### Bookmarks` section under `## The bar`, after `### The workspace row menu`; one sentence in that menu section naming the two new items)
- Modify: `docs/superpowers/notes/manual-test-script.md` (a `## Workspace bookmarks` section at the end, steps `BM1` to `BM12` in the file's lettered style)

- [ ] **Step 1: README.** What a bookmark is (link, program, installed app); the three ways in (drop a link, `.exe`, `.lnk` or `.url` on a row; a window's "Bookmark in this workspace"; the row menu's "Bookmark link from clipboard"); how chips look and that a program's chip hides while it has a window; Open, Open all bookmarks, Rename…, Remove; the `deskbar.cmd bookmark` commands by pointing at `help`; and the privacy line: a web-link bookmark makes Deskbar ask that site, once, for `/favicon.ico`, with no cookies or credentials, and the icon is kept under `%APPDATA%\Deskbar\bookmark-icons`. Name the screencast to re-film: the bar overview.
- [ ] **Step 2: Manual test steps.** BM1 drag Portainer's address-bar URL onto a row: readout, chip named by page title, faded with a dashed outline and a dot. BM2 hover: full strength, tooltip with the URL. BM3 click from another workspace: switches, the page opens in a browser window on that desktop. BM4 drop `rustdesk.exe`: chip named "RustDesk"; click starts it there; its chip hides while its window is open and comes back when it closes to the tray. BM5 Start menu `.lnk` of a Store app or PWA dropped: App chip. BM6 "Bookmark in this workspace" on the iam.ge PWA window: App chip in iam.ge; hides while the PWA is open, including when Edge was already running. BM7 Open all bookmarks from the row menu with RustDesk running: one switch, links open, RustDesk not started twice. BM8 Rename… and Remove from the chip menu. BM9 fold the row: no chips; an empty workspace with bookmarks shows only chips. BM10 copy a URL, row menu "Bookmark link from clipboard" enabled; copy plain text, disabled. BM11 the Portainer chip shows its favicon after a moment (self-signed certificate); a site with no favicon keeps its letter tile; restart with the network off: cached icons stay, missing ones are tiles, nothing breaks. BM12 `deskbar.cmd bookmark list Work`, `add`, `open`, `open-all`, `remove` mirror the menus.
- [ ] **Step 3: Release notes text** for the PR body, kept in the PR (not a file):

```
## Release notes

- Workspaces can keep bookmarks: web links, programs and installed apps (PWAs and Store apps). They show as faded
  chips after the workspace's windows, and one click opens them in that workspace. A program's chip hides while
  the program has a window open.
- Add one by dropping a link or a program onto a workspace's row, from a window's right-click menu ("Bookmark in
  this workspace"), or with "Bookmark link from clipboard" on the row's menu. "Open all bookmarks" opens the lot.
- A web link's icon is fetched once from that site (/favicon.ico) and kept on your PC; until then, or if the site
  has none, the chip shows a letter.
- Scripts and Claude sessions can do the same with `deskbar.cmd bookmark`.
```

- [ ] **Step 4: Commit** `git commit -m "docs: workspace bookmarks in the README and the manual test script" -m "*opus5.5*"`.
- [ ] **Step 5: Before the PR.** `dotnet build Deskbar.sln -c Release -warnaserror`; Core suite; Windows suite through `test-hidden.ps1`. Open the PR (draft until the deskbar-site privacy PR is merged), then watch its CI run to green and keep it on top of main with the `refresh-pr` skill.
