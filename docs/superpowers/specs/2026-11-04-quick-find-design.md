# Quick find: a keyboard finder and launcher for everything open

Date: 2026-11-04. Status: draft, waiting for Petre's approval.

## Why

Petre wants one place to type a few letters and get to anything: a window on any workspace, a browser
tab buried in one of several Edge windows, a VS Code editor tab, or an app that is not running yet. Today
the bar shows every window's icon, but finding "the invoice tab" means remembering which workspace and which
browser window it is in and clicking through. His words, in order: "i want a quick find for opening running
apps", "it should let me find text in all the app titles, including browser tabs", "so i press the shortcut,
it brings up a textbox, i start typing and it filters everywhere it can", "each window should say where it
is", "this should be a quick launcher as well. if a window isn't opened, it should show me, including which
deskbar it will run in, before it runs", "inv 10 should find that" (of a tab titled "Invoice 2026-10").

## What it does

A global shortcut opens a small window with a text box and an empty result list below it. Every keystroke
filters the list. Up and Down move the highlight, Enter goes to the highlighted result, Esc closes the window
and changes nothing. Clicking a result is the same as Enter on it. The window also closes when it loses focus.

### What it searches

1. Every window Deskbar lists, on every workspace and on unnamed virtual desktops, by its title and by the
   short name Deskbar gives it on the taskbar.
2. Every tab of every browser window, by tab title. Deskbar already reads browser tab strips through UI
   Automation for the bar's tab counts (`src/Deskbar.App/BrowserTabs.cs`); that reader keeps only a count
   today and has to keep each tab's name as well.
3. Every VS Code editor tab, by its name, read the same way `BrowserTabs.ReadCode` already finds them.
4. Apps that are not running, from two sources (see Launching).

### Matching

What Petre types is split on spaces into pieces. A result matches when every piece appears somewhere in its
searchable text, case-insensitively, in any order. So `inv 10` and `10 inv` both find "Invoice 2026-10".

A result's searchable text is its own title plus where it is: the workspace name, and for a tab, the title
of the browser window holding it. So `acc inv` narrows to invoices on the *Accounting* workspace. (My call:
Petre did not ask for the location to be searchable, but it costs nothing and makes short queries sharper. If
it turns out to add noise in daily use, drop the location from the searchable text and keep it on display.)

Nothing fuzzy, no typo tolerance, no learned ranking in this version. "Contains every piece" is predictable,
and predictable is what makes a keyboard finder trustworthy.

### Every result says where it is

Petre: "each window should say where it is". Each row shows, left to right: the app's icon (the same one the
bar draws, from `IconCache`), the title with the matched pieces emphasised, and on the right the location.

- A window: its workspace name, drawn in that workspace's lane colour from `WorkspacePalette`, the same
  colour its row has on the bar. A window on an unnamed desktop says so the way the bar labels those. A
  window pinned to all desktops says that.
- A tab: the workspace, plus which browser window it is in (that window's title, shortened).
- A not-running app: "not running" and the workspace it will open in, in that workspace's colour. Petre
  wants this shown before anything runs, so the row itself carries it and Enter is the only thing that
  launches.

With more than one screen, whether to also name the screen is left to the mockup round.

### Ranking

Running things always come before things that would have to be started, so Enter never starts a second copy
of something already open by accident. Within that: running windows and their tabs first, then remembered
apps, then Start menu apps. Inside each of those, the most recently used workspace comes first
(`WorkspaceMru`). Petre, after trying the first build: "search should show me most recently used bars first",
so recency outranks how well a title matches. Inside one workspace, a result whose title starts with the first
piece sorts above one that merely contains it.

### Going to a result

- A window: switch to its workspace and bring the window to the front, through the same paths the bar's
  click uses (`WorkspaceManager.SwitchToDesktop`, `IWindowActivator.Activate`). A minimised window is
  restored; mind the `ShowWindowAsync` trap in CLAUDE.md.
- A tab: as for its window, then select that tab. How to select a tab from outside the browser is not yet
  measured (see Measure first). If it turns out not to be reliable, the fallback is to go to the window and
  leave the tab as it is, and the row must not promise more than that.
- A VS Code editor tab: the same as a browser tab.

### Launching

Petre: "this should be a quick launcher as well", and then "it can also launch". The "no launching" decision
in CLAUDE.md is about Deskbar starting things on its own or prompting to; this launch happens only because
Petre picked a row and pressed Enter, which is the same footing as `deskbar.cmd start` and the icon menu's
restart, and it goes through the same `ProgramStarter`.

Two sources of not-running apps:

1. Remembered apps, from the roster (`AppState.Inventory`; `InventoryEntry` holds ProcessPath, CommandLine
   and Title). The roster is not a clean list of launchable command lines. Of the 29 entries in Petre's
   real `state.json`, 11 are `ms-teams.exe` with throwaway session arguments (`--update-trigger`,
   `--restart-count`), Photos and Snipping Tool are stored as versioned paths under `WindowsApps` that
   cannot be started directly, and others are `ApplicationFrameHost.exe -Embedding`, protocol activations
   (`ms-screenclip:`) or Edge apps with mojo arguments. So, my call:
   - One row per program (per exe path), plus a separate row only where the arguments name real content,
     meaning an existing file or folder path, such as `rider64 X.sln`. Session arguments are never replayed:
     the program starts bare, or with its content argument.
   - A packaged app (path under `WindowsApps`, or `ApplicationFrameHost`) launches through its Start menu
     entry (AppUserModelID), never its stored path. With no matching Start menu entry it gets no row.
   - A program counts as running if any of its windows is open, whatever its arguments. A content row counts
     as running only if a window with that content is open, so `rider64 X.sln` being open does not hide
     `rider64 Y.sln`. A running program gets no not-running row; its windows cover it.
2. Start menu apps, for everything installed that the roster does not cover. One whose program is running
   gets no row, for the same reason: typing `edge` shows Edge's windows and nothing that would open another.
   Start menu entries map to running processes by .lnk target path for desktop apps and by AppUserModelID
   for packaged ones; where neither matches, the row is shown.

Folder and project rows (a VS Code folder that is not open, a JetBrains project, a Remote Desktop Manager
session) are not in this version. `ContainerHome` stores the process name, the folder's display name from
the window title and the workspace, and no path; `restart-app` relies on VS Code restoring its own session.
Getting paths means reading VS Code's internal recent list or recording paths a new way, which is its own
piece of work.

Where a launch lands. The row's destination comes from the same chain the placement code runs on a new
window (StartedFor expectation, container tier, launched-by, placement memory, rules), exposed as one query,
never from a parallel guess. A rule can send an app somewhere other than the current workspace, and the row
must say so.

Enter on a not-running row switches to the destination workspace first, then starts the program, and
registers the start the way `deskbar.cmd start` does (`ExpectWindow` / `StartedFor` in `WorkspaceManager`),
so the window is held to the workspace the row promised even if it is slow to appear. This matches Enter on
a running window, which also takes Petre there.

### The shortcut

The project has held to exactly one global hotkey (see CLAUDE.md and `HotkeyService`), and the reason was
the price of eleven exclusive chords that bound by list position. One more chord for a feature this central
is a small price, so quick find gets its own:

- Configurable on Manage > Shortcuts, stored as text in `AppState` like `SwitcherShortcut`, parsed by `Chord`.
- The default is picked the way `Win+Ctrl+Tab` was: by trying candidates against `RegisterHotKey` on Petre's
  machine and keeping one that is free and does not take a chord apps rely on. A registration failure is
  reported once, like the switcher's.
- A remote twin, `deskbar.cmd find`, that opens the same window, added to `ControlUsage`, since every
  command has one. An optional query argument (`deskbar.cmd find inv 10`) pre-fills the box.

The bar's existing search button keeps sending `Win+S` to Windows' own search. Whether it should open quick
find instead is a separate question for later.

## Non-goals

- No file search, no web search, no calculator, no settings search. This finds what is open or launchable.
- No history of closed tabs or windows.
- No Claude Code sessions yet. Petre asked for them, then deferred them:
  [Deskbar #314](https://github.com/AppifySheets/Deskbar/issues/314) holds what is known.
- No folder or project rows for things that are not open (see Launching).
- Firefox tabs only if the same tab-strip reading happens to work; Chromium browsers and VS Code are the
  target. Firefox windows are still found by window title.
- No new launching anywhere else. Nothing starts without Petre pressing Enter on a row that said what it
  would start and where.

## Anti-goals (it would be a failure even if it works)

- Typing feels laggy, or results reshuffle under the cursor while he is about to press Enter. Results for
  what he has typed must appear without waiting on a browser; a slow browser just has fewer tabs listed.
- Enter starts a second copy of something already running.
- A row says one workspace and the launched window lands on another.
- It steals focus back from where he went, or leaves the previous app half-activated.
- Opening it moves him to another workspace before he has chosen anything.

## Constraints

- UI Automation never runs on the UI thread (the existing rule in `BrowserTabs`). Tab names are read in the
  background on the same schedule the counts already are, and quick find works from the latest readings.
  Opening the window may ask for a fresh read of stale windows, but must show what it has immediately.
- VS Code builds its accessibility tree lazily: a first read can find no tabs. Treat that as "not ready",
  as `BrowserTabs` does, not as "no tabs". Never cache a miss that can later succeed.
- Static WPF brushes frozen; the window ignored by `WindowMonitor` like the bar and the switcher, so it never
  lists itself.
- The window opens on the screen Petre is looking at (the one with the foreground window, or the pointer if
  none), topmost, and does not move him between desktops by appearing.
- The pull request bumps `VersionPrefix` and has a `## Release notes` section, per CLAUDE.md.
- Code comments carry the reasoning, as everywhere else in this repo.

## Measure first

Each of these gets a throwaway probe on Petre's machine before code depends on it. CLAUDE.md's probe traps
apply (DPI awareness first, `CharSet.Unicode`).

1. Selecting a tab from outside: does `SelectionItemPattern.Select` (or `LegacyIAccessible` default action)
   on a Chromium `TabItem` switch Edge and Chrome to that tab, on a window on another desktop, minimised or
   not? The same for a VS Code editor tab. This decides whether tab rows can promise the tab or only the
   window.
2. Reading tab names for every browser window at once: how long a full pass takes with his usual load, so
   the background schedule keeps names fresh enough.
3. Enumerating Start menu apps: `shell:AppsFolder` gives both desktop apps and Store apps with an
   AppUserModelID, and launching through it covers both. Confirm it lists what his Start menu lists and that
   `ProgramStarter`/`DetachedLauncher` can start an AppUserModelID, packaged copy included.
4. The default chord, as above.

## Left to the builder

- Exact look, sizes and colours: decided from mockups (current vs proposed) before code, as usual.
- How many rows to show before scrolling, and whether groups get headings.
- How the matched pieces are emphasised.
- Whether the window remembers the last query (my lean: no, it opens empty).
- Splitting the work into pull requests. A reasonable cut: tab names kept and windows searchable first, then
  tab selection, then launching.

## Decided in conversation

- Summoned by a shortcut, a text box, filters as you type (Petre).
- Searches window titles, browser tabs and VS Code editor tabs, "everywhere it can" (Petre: "also vscode
  tabs").
- Every result shows where it is (Petre).
- Launches apps that are not running and shows the destination workspace before running (Petre).
- Multi-word matching in any order (Petre's `inv 10`).
- Both remembered apps and Start menu apps; running outranks not running (my calls, stated in chat and not
  objected to).
- Its own configurable shortcut plus `deskbar.cmd find` (my call, stated in chat).
- Claude sessions deferred to [Deskbar #314](https://github.com/AppifySheets/Deskbar/issues/314) (Petre).
- After the builder check, my calls, stated in chat: one roster row per program plus content rows; no row
  for a running program from either source; destination from the placement chain; Enter on a not-running
  row switches there and then starts it; folder rows left for later. Petre answered "yes, let's go".
