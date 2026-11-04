# Quick find probes, 2026-11-04

Measured on Petre's machine with `spikes/QuickFindSpike` (Task 0 of
`docs/superpowers/plans/2026-11-04-quick-find.md`). DPI awareness was set first; P/Invokes are Unicode.

## Reading tab names

`QuickFindSpike names`: every TabItem of every visible Edge and VS Code window, with the rules `BrowserTabs`
already uses (no Document between tab and window for browsers; class containing the word `tab` for VS Code).

- 25 windows took 4.9 s in all. Per window it ranged from 20 ms to 807 ms; the slow ones are Edge windows
  with many tabs or heavy pages. So quick find can never read tabs when it opens. It works from the
  background readings `BrowserTabs` already takes, and asks for a refresh of stale windows without waiting.
- Edge appends the tab's state to its accessible name: ` - Sleeping`, ` - Discarded`,
  ` - Part of tab group …`, and per the existing code ` - Audio playing` / ` - Audio muted`. These are
  stripped before showing or matching.
- VS Code appends the editor group: `Running apps quick find, Editor Group 1`. The `, Editor Group N` suffix
  is stripped too.
- Claude Code session tabs in VS Code are ordinary editor tabs named after the session title. So open
  sessions are found as VS Code tabs with no extra work. Closed sessions stay with
  [Deskbar #314](https://github.com/AppifySheets/Deskbar/issues/314).
- Installed web apps (Outlook, YouTube Music) read 0 tabs by the browser rule, as before; they are listed as
  windows.

## Selecting a tab

`QuickFindSpike select`, on an Edge window the probe opened itself with three tabs, on a workspace other than
the one Petre was looking at, then again with the window minimised. The selected tab was read back through
`SelectionItemPattern.IsSelected` on every tab, because the three pages shared one title:

    minimised, before:   False,True,False
    after select 0:      True,False,False
    after select 2:      False,False,True

Each `Select()` took 23 to 36 ms. Every Edge and VS Code tab in the names pass offered `SelectionItemPattern`
and none offered `InvokePattern`.

The same on a VS Code editor tab in this session's own window (group 2): the other tab became selected, and
selecting the original put it back.

Decision (a): tab rows promise the tab. Pull request B goes ahead with `SelectionItemPattern.Select()`.

## The Start menu

`QuickFindSpike apps`: `Shell.Application` → `NameSpace("shell:AppsFolder").Items()`.

- 494 entries. Desktop apps have a parsing name like `MSEdge` or a known-folder path
  (`{6D809377-…}\JetBrains\…`), and `ExtendedProperty("System.Link.TargetParsingPath")` gives the real exe:
  `msedge.exe`, `Code.exe`, `rider64.exe`. That is the path the roster stores, so roster entries and Start
  menu entries match on it.
- Packaged apps have an AppUserModelID as their parsing name (`Microsoft.WindowsCalculator_8wekyb3d8bbwe!App`,
  `Microsoft.Windows.Photos_8wekyb3d8bbwe!App`, `MSTeams_8wekyb3d8bbwe!MSTeams`) and no target.
- Launching `explorer.exe shell:AppsFolder\<AUMID>` through `deskbar.cmd start` started Calculator, but its
  window belongs to `ApplicationFrameHost`, so the expectation (keyed on `explorer`) never caught it and the
  window opened on the workspace Petre was looking at. It was moved back with `deskbar.cmd move` and closed.

Decision (c): a launch row always switches to its destination first, then launches, so the new window opens
there by Windows' own rule. The expectation is still registered for desktop apps (expected process = the
target exe's name), where it holds a slow window to the workspace; for packaged apps it is skipped, since
nothing it could match on is known. Packaged apps launch as `explorer.exe shell:AppsFolder\<AUMID>`.

## The default chord

`QuickFindSpike chords`, `RegisterHotKey` then release at once:

    Win+Alt+Space    free
    Win+Ctrl+F       taken
    Win+Shift+F      free
    Ctrl+Alt+Space   free
    Win+Ctrl+Q       taken
    Win+Alt+F        taken

Decision (b): `Win+Alt+Space`, the first free one. PowerToys uses it for Command Palette by default, which
does not run on this machine; anyone who has it can change quick find's chord on Manage > Shortcuts, and a
taken chord is reported once at start.
