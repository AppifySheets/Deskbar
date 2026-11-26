# Workspace bookmarks

[Deskbar task f7ac687c](https://deskbar.app/task#f7ac687c-1048-4983-8e3a-625d0389baf3) ·
[mockup](https://claude.ai/artifact/4Qtz9eHVzo44UmhkJBPEi2) (option A chosen)

## Intent

Each workspace keeps the things Petre starts there: a web link (Portainer), a program (RustDesk), a
PWA (the iam.ge PWA belongs in the iam.ge workspace, stock in gph). They show as chips in the
workspace's row of the bar, one click starts them there, and a program's chip disappears while that
program is running. His words, from the task:

- "i'd have portainer bookmark, programs as well"
- "browser links should open in the same workspace but they can go into the same browser as the one open"
- "software links, like starting rustdesk could live inside the tabs; so i'd have iam.ge pwa in the
  iam.ge workspace, while stock would live in the gph workspace"
- "when an app is already running, its chip should disappear"
- "let there be - open all; i like a" (option A of the mockup, and an "open all")

The why: the tools of a piece of work live with that work. Today he finds RustDesk or the stock PWA
through Start or the taskbar, which know nothing about workspaces.

## Goals

- Bookmarks of three kinds per workspace: a link, a program, an installed app (PWA or Store app).
- Added by dropping on the row, or from a window's right-click menu; renamed and removed from the
  chip's right-click menu; all of it also through `deskbar.cmd`.
- A click opens the bookmark ON its workspace. "Open all" opens every bookmark of the workspace.
- A program or app chip hides while it is running anywhere; a link chip never hides.

## Non-goals

- Chips made automatically from the roster. The roster remembers every app that ever lived in a
  workspace, so the row would fill with chips nobody chose, and it holds no URLs.
- Importing browser bookmarks, folders of bookmarks, or reordering chips by drag (add order for now).
- Bookmarks for unnamed desktops. A bookmark belongs to a workspace, which is a named desktop.

## Anti-goals

- Anything that starts on its own. "No launching, no restore prompt" (CLAUDE.md) stands: a bookmark
  opens only on a click, a menu item or a pipe command. Switching to a workspace opens nothing.
- A chip that could pass for a running window.

## Approaches considered

How a bookmark gets in (Petre picked "both"):

1. Drop on the row. The only way to get a URL in: Deskbar cannot read which page a browser window
   shows.
2. Right-click a window, "Bookmark in this workspace". The only easy way to capture a PWA, whose
   window carries the AppUserModelID that starts it.
3. Both, each with a pipe twin. Chosen.

How chips look (mockup, Petre picked A):

- A: faded icons after the row's windows. Chosen.
- B: the same icons fixed at the row's right end. Rejected: they land in the last screen's lane and
  look like they belong to that screen.
- C: labelled pills. Rejected: three of them take the width of about twelve windows.

Where they are stored (my call): on the `Workspace` record in state.json, as an init property like
`Folded` and the tiling flag, so they survive a restart and a rename with no second store to keep in
step. A separate bookmarks.json was the alternative and buys nothing.

## The design

### Model (Deskbar.Core)

`Workspace.Bookmarks : IReadOnlyList<Bookmark>`, init, default empty, so an old state.json loads
with none and no migration.

`Bookmark(Guid Id, string Name, BookmarkKind Kind, string Target, string? Arguments = null,
string? WorkingDirectory = null)`:

- `Link`: `Target` is an absolute http or https URL. Nothing else is accepted (no `file:`,
  `javascript:` or custom schemes), so a dropped string cannot run anything.
- `Program`: `Target` is an exe path, `Arguments` its arguments as one command-line string, and
  `WorkingDirectory` optional.
- `App`: `Target` is an AppUserModelID, started as `shell:AppsFolder\<id>`, the way `TaskbarPins`
  starts an installed-app pin.

`WorkspaceManager` gains `AddBookmark`, `RenameBookmark`, `RemoveBookmark` (each a `Result`, saved
like every other workspace edit). A duplicate add (same kind and target, and the same arguments for a
program) is refused with a message rather than making a second chip.

### Adding

Drop on a row. The row's `DragOver` accepts today only `DraggedWindow.DragFormat`; it also accepts:

- a URL: `UniformResourceLocatorW`, or text that parses as an absolute http(s) URL. Chromium's address
  bar also offers a `FileGroupDescriptorW` whose file name is the page title plus `.url`; when it is
  there, the title is the bookmark's name, else the host name.
- files (`FileDrop`), the first one only: an `.exe` becomes a Program named after its file
  description; a `.url` an internet shortcut, a Link; a `.lnk` is resolved through `IShellLink` to
  its target, arguments and working folder and named after the shortcut. A shortcut that has an
  AppUserModelID and no plain target (Start menu entries of Store apps and PWAs) becomes an App.
- Anything else is refused, with the readout saying why.

The readout says `→ bookmark in <workspace>` while a droppable thing is over the row, the way it
says `→ move to …` for a window.

Right-click a window: "Bookmark in this workspace" adds a bookmark to the workspace the window is in:

- the window has an AppUserModelID that `shell:AppsFolder` can start (a PWA, a Store app): an App;
- otherwise a Program from the process's path and command line (arguments only; the exe path is the
  target).

The item is left out for Deskbar's own windows and for windows with no identity
(`RosterIdentity`'s `StaysWhereOpened` and `IsShell` cases).

Right-click a workspace row: "Bookmark link from clipboard" (added 2026-11-26, Petre approved). It is
enabled only while the clipboard's text, trimmed, is an absolute http(s) URL. A click adds a Link
bookmark to that workspace, named after the URL's host (renamed later from the chip's menu), with no
dialog. A refusal (a duplicate, or text that stopped being a URL) is reported the way the bar reports
any other refused action. Its pipe twin is the existing `bookmark add <workspace> --url <url>`, so it
needs no command of its own.

Builder, measure first: whether an Edge PWA window's AppUserModelID starts it through
`shell:AppsFolder`, and what a Chromium address-bar drag offers. Both are assumptions until a probe
says so. If the PWA probe fails (my call), a PWA window becomes a Program bookmark instead: the
browser exe with its `--profile-directory` and `--app-id` arguments, matched through
`RosterIdentity`'s app-id handling.

### Showing

Option A of the mockup. The chips go after the row's windows in the row's first screen lane, in
reading order, behind a 1 px divider, at the same cell width as a window icon (`IconCellWidth`) so
`IconRowLimit` wraps them with the icons. A chip draws at half opacity with a dashed outline; hover
brings it to full strength, and the tooltip gives the name and the target. A link's icon carries a
small dot so it reads apart from a program.

A folded row shows no chips, as it shows no icons. An empty workspace with bookmarks shows only the
chips.

Icons:

- Program: the exe's icon, through the same loader the bar uses for windows.
- App: the app's icon from its shell item, as `TaskbarPins.ImageOf` gets a pin's.
- Link: the site's favicon, fetched once from the URL's origin (`/favicon.ico`) and kept under
  `%APPDATA%\Deskbar\bookmark-icons`, keyed by origin. Petre chose the fetch over a no-network letter
  tile ("favicon fetch is what we need. change the privacy page."). Until it arrives, or when the site
  has none, a letter tile: the first letter of the bookmark's name on a colour derived from the host,
  so two links still look different. A failed fetch is not cached for good (CLAUDE.md's "never cache
  a lookup failure that can succeed later"); it is tried again on the next start.
- The declared-icon fallback (added 2026-11-26, Petre's option A, because Portainer answers 404 for
  `/favicon.ico` and names its logo in the page): when `/favicon.ico` gives nothing Deskbar can decode,
  one GET of the bookmarked page itself (the URL without its `#fragment` or any user information),
  `text/html` only and at most 512 KB read while streaming; then the first `<link>` whose `rel`
  contains `icon` (`icon`, `shortcut icon`, `apple-touch-icon`) with an `href` that, resolved against
  the page, is on the SAME origin (scheme, host, port) and is not an SVG (WPF cannot decode one); then
  that icon with the same 256 KB and image-only limits. Three requests at most, never one to another
  site. Found by a small tolerant scan (`DeclaredIcon` in Core, with tests), not an HTML library. Saved
  the same way, keyed by origin; a failure is not saved and is tried again at the next start.
- The favicon request (my calls): a plain GET to the bookmarked origin only, no cookies, no
  credentials, a `Deskbar` user agent, 5 s timeout per request, 256 KB cap, image content types only. A
  certificate error is accepted for this request alone, because the typical target (Portainer) is an
  internal host with a self-signed certificate and what comes back is only decoded as an image. Nothing
  else in the app relaxes certificate checks.
- The privacy page (deskbar-site, `site/privacy.html`) and the README must say so before the release
  that fetches: a separate deskbar-site PR, saying that a web-link bookmark makes Deskbar ask that
  site for its icon, once, and that the request carries nothing about you beyond what any web request
  carries.

### Hiding while running

Worked out on each bar rebuild, against every live window on every desktop:

- Program (my call): hidden while a window on any desktop belongs to the same exe path. Arguments are
  ignored, because a bookmark from a `.lnk` or `.exe` drop rarely carries the running copy's
  arguments. Chromium exes are the exception: they match through `RosterIdentity` on profile and app
  id, so a PWA bookmark does not hide whenever any browser window is open.
- A program with no window (RustDesk in the tray only) does not count as running (my call): its chip
  stays, and a click starts it, which for a single-instance app brings up its window. Only the window
  list the bar already walks is consulted; no process scan.
- App: hidden while a window has that AppUserModelID (the `OpenAppIds` cache the pins already fill).
- Link: never hidden, because Deskbar sees windows, not tabs. Instead a link is drawn open (full
  strength, solid outline, the link dot kept) while the browser window its last open went into is
  still open (option B, Petre's pick after the first live try: his two links opened as tabs in the
  browser already on that workspace, and the chips still looked "not started"). Closing only the tab
  is not noticed. A click on an open link opens it again. `bookmark list` reports such a link as
  `"running": true`, so the pipe and the bar agree. See Opening for how the window is found.

### Opening

A click on a chip:

1. switches to the workspace if it is not the one in front (the way a task link does,
   `TasksBoard.OpenLink`);
2. Program: `WorkspaceManager.StartIn(workspace, target, ProgramStarter.Start)`, so the new window is
   claimed for the workspace even if the switch has not settled;
3. App: `explorer.exe shell:AppsFolder\<id>` after the switch, so the window opens on the desktop in
   front;
4. Link: `App.OpenUrl`, the shell's open, after the switch, so Chromium puts it in its window on that
   desktop or opens one there. This is the mechanism task links use, measured working in v2.6.34
   ([Deskbar task 26f15d70](https://deskbar.app/task#26f15d70-4c48-46c6-a24c-43b90b0ddff3)), so it needs no new code. Whatever
   the default browser does with a shell open is what happens; Firefox is not special-cased.

A failure (the exe is gone, the app was uninstalled) goes to the bar's problem line with the
bookmark's name.

After a link opens (a chip click, its menu's Open, Open all, `bookmark open` or `open-all`), Deskbar
notes which browser window received it (`OpenedLinks`, `LinkWindowWatch` in Core,
`LinkWindowSource` in the app):

- the first window to come to the front within 5 seconds that receives links and sits on the
  bookmark's workspace. A window receives links when its process is a browser (the Chromium family
  `RosterIdentity` knows, plus `firefox.exe`), it is not an app window, and, when the default
  browser for https can be read, its exe is that browser's. An app window is one whose
  AppUserModelID has the packaged `!` form or Chromium's `_crx_` form; only when the window gives no
  AppUserModelID does an `--app-id` in the process's command line decide, because an Edge PWA shares
  the browser's process and the probe found that process carrying another PWA's `--app-id`. This
  matters because a click from another workspace switches first, and the switch brings that
  workspace's last active window forward before the link arrives: a PWA or a second browser there
  must not be taken for the link's window;
- if none comes forward in that time (the browser on that desktop was already in front, so the tab
  opened without a focus change), the window in front when the 5 seconds end, if it is such a window;
- otherwise nothing, and the chip stays at rest.

A lookup that fails for one window passes that window over and the watch carries on.

The watch starts just before the link is handed to the browser and ends at its first answer; a link
that fails to open watches for nothing. Open all opening two links into one window notes that window
for both. A note lasts while the window exists and belongs to the same process (`IsWindow` plus the
process id). It is dropped when the window list reports the window gone for real (keyed off
`IsWindow`, never off a window missing from the taskbar list) or when the bookmark is removed.
Moving the window to another workspace keeps it. Notes live in memory only: a restart starts with
every link chip at rest, and nothing is written to `state.json`. Nothing here opens anything; it only
watches after an explicit open.

"Open all bookmarks" is on a chip's menu and on the workspace's row menu (only when it has
bookmarks): it switches once, then opens every chip that is showing, so running programs are skipped
and every link opens.

### A chip's menu

Open, Open all bookmarks, Rename…, Remove.

### Pipe commands

Every menu item gets a remote twin through `ControlUsage`:

- `bookmark list [<workspace>]`
- `bookmark add <workspace> --url <url> | --program <path> [--cwd <folder>] [-- <args>] | --app <id>
  [--name <name>]`
- `bookmark rename <workspace> <name|id> --name <name>`
- `bookmark remove <workspace> <name|id>`
- `bookmark open <workspace> <name|id>`
- `bookmark open-all <workspace>`

### Docs

README gets a Bookmarks section, the manual test script gets steps, and the PR's release notes are
written for users.

## Left to the builder

- Exact names and file split, within the repo's patterns (the bar is split into `FloatingBar.*.cs`
  partials; bookmarks get their own, `FloatingBar.Bookmarks.cs`).
- Whether the favicon fetch also reads the page's `<link rel="icon">` when `/favicon.ico` is missing:
  v1 skipped it, and the second bookmarks PR added it (see Icons above).
- Tests: Core tests for the model, adding rules, duplicate refusal, the running test and the pipe
  parsing; Windows tests only where the drop parsing needs real data objects.
