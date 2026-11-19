# Screen strips: the current workspace's window list along the top of each screen

Design agreed with Petre on 2026-11-19. Where a line is marked "my call" it was not discussed and is
the author's proposal.

## Why

Petre: "let's add a feature for a new taskbar-like bar at the top of each monitor, for its tasks", and,
pointing at the menu item "Expand the current workspace", "which would be the third option in the
taskbar".

The current workspace's windows are listed with their titles in one of two places today: the
workspace's own row on the bar expands into the list (`AppState.ExpandCurrentWorkspace`, the default),
or the list is a section at the bottom of the bar (`AppState.WindowListAtBottom`). Both put every
screen's windows in the one bar, in columns about 80 DIP wide that hold roughly eight characters of a
title. The third place is on the screens themselves: each screen gets a strip along its top edge with
that screen's windows of the current workspace, as wide as the screen, so titles have room and you read
a screen's windows on that screen.

## What it does

- The window list setting has three values: in the workspace's row, at the bottom of the bar, at the
  top of each screen. On the third, every row of the main bar stays icons (as with the bottom list) and
  the bar gets no list section.
- Every connected screen gets one strip along its top edge, the full width of the screen. On a screen
  where the main bar is docked, the strip still spans the whole top and the docked bar starts below it,
  32 DIP shorter. Petre: "strip spans the whole top". (The mockup drew the bar at full height with the
  strip beside it; this replaces that.)
- The strips stay while the main bar is minimized to its taskbar button, and when the app starts with
  the bar minimized (Petre: yes). They are the window list, and a strip that left with the bar would
  move every screen's work area twice. So the tabs keep being rebuilt while the bar is hidden, and
  neither the strip guard nor the topmost re-assertion for strips may key off the bar's `Minimized` or
  `IsVisible`.
- A strip lists the windows of the current workspace that are on its screen, left to right, icon and
  title. Switching workspace changes every strip together. On an unnamed virtual desktop the strips
  list that desktop's windows, as the bottom list does today.
- The strip reserves its space with the shell, the way the docked bar does, so maximized windows, Snap,
  tiling and new windows start below it (my call, accepted).
- A screen with no windows of the current workspace still shows its strip with only the name tag, so a
  screen's work area does not change on a switch (my call, accepted).
- Start, search, the tray buttons and the clock stay on the main bar only (my call, accepted).

### The look

Petre picked "B" from three mockups (`.superpowers/brainstorm/1753-1795083773/content/top-strip-look.html`).

```
┌─────────┬──────────────────────────┬─────────────────────┬──────────┬───────────────┐
│ deskbar │ ▣ FloatingBar.xaml.cs -… │ ▣ Deskbar #412 - E… │ ▣ pwsh ● │               │
└─────────┴━━━━━━━━━━━━━━━━━━━━━━━━━━┴─────────────────────┴──────────┴───────────────┘
  name tag   active: fill + underline    behind: dim title    attention dot
             in the workspace's colour
```

- 32 DIP high, on the bar's own background (`#FF141518`), square corners, no gap to the screen edge.
- At the left end the workspace's name on its colour (the colour the main bar gives that workspace,
  group colour included). On an unnamed desktop the desktop's name on a neutral grey.
- Then one tab per window: fixed width (150 DIP), icon then title, the title trimmed with an ellipsis.
  Every tab has a 2 DIP underline, grey at rest. The active window's tab has a lighter fill, a white
  title and the underline in the workspace's colour.
- The title colours are the titled list's: `TitleActive`, `TitleResting`, `TitleBehind`. The icon keeps
  everything it carries on the bar: the opacity ladder, the attention dot, the tab count, the sound
  ring, the family band, the pin badge for a pinned window, ageing.
- When the tabs do not fit, all of them narrow equally, down to icon-only. The strip is always one
  line. Past icon-only the last tabs are clipped (my call: at 1920 px that is over sixty windows on one
  screen in one workspace).

### What a tab does

A tab is the titled list's line in a different shape, and behaves the same:

- Click brings the window up; a click on the window you are in minimizes it.
- Right-click opens the window's menu.
- Hover shows the hover card.
- Drag moves the window: onto another screen's strip sends it to that screen (the call the list's
  drop makes, `AssignWindow(handle, workspace, screen)`), onto a row of the main bar sends it to that
  workspace. An icon dragged from the main bar onto a strip lands in the current workspace on that
  screen. On an unnamed desktop there is no workspace to assign to, so a drop on another screen's strip
  calls `WorkspaceManager.MoveWindowToMonitor(handle, screen)`, which exists for the window menu's
  move to another screen (my call).
- Order is the list's order for that screen (my call). Pinned windows are included on the screen they
  are on, as in the list.
- The split-line setting (right third switches workspace) does not apply: there is no other workspace
  to switch to from a strip.

Right-click on the name tag or on the strip's free space opens the bar's own menu (my call), which
holds the window list choice, so the way back to the other two places is on the strip itself.

### Choosing it

- The one flipping menu item ("Expand the current workspace" / "Window list at the bottom of the bar")
  becomes a submenu "Window list" with three checked entries: "In the workspace's row", "At the bottom
  of the bar", "At the top of each screen". It is on every workspace's menu and the bar's own, where
  the item is now. The collapsed-bar click item stays offered only for the first.
- The setting has no control pipe command today. This adds one, `window-list row|bottom|screens`, with
  its line in `ControlUsage` (my call, following the rule that a menu command has a remote twin).
- Nothing is gated to Pro (my call; nothing else is gated either).

## Goals, non-goals, anti-goals

Goals

- Read a screen's windows, with usable titles, at the top of that screen.
- One behaviour for a window's button wherever it is drawn.
- Strips follow screens coming and going without the user doing anything.

Non-goals

- Other edges, a height setting, or per-screen choices (strip on one screen and not another).
- Showing other workspaces, switching workspace, or shell buttons on a strip.
- Replacing or changing the main bar's rows.
- Drawing the tray's icons.

Anti-goals, any of which makes this a failure even if it works

- A work area that jumps: on a workspace switch, on unlock, or while a screen wakes.
- A strip that ends up on the wrong screen, two strips on one screen, or a reserved band with no strip
  in it.
- A second copy of the button's behaviour that drifts from the bar's.
- A window left under a strip where its title bar cannot be reached.

## Constraints

From the code and from earlier measurements on Petre's machine:

- `AppBar` registers one app bar per window, speaks physical pixels, and knows only the left and right
  edges. Each strip is its own window with its own registration, at its own screen's DPI. The app is
  PerMonitorV2.
- A crash while docked leaves nothing behind (measured for the docked bar; Explorer gives the strip
  back within a second).
- A screen waking on unlock drops out for a moment. `MONITOR_DEFAULTTONEAREST` then answers with
  another screen, and a guard that re-registered on that answer once moved the docked bar to the
  primary (`FloatingBar.DockGuard.cs`). A strip must be tied to its screen's identity and must wait
  when that screen cannot be read; it never accepts another screen's rectangle.
- Display changes arrive as a burst over a couple of seconds with intermediate states. The bar acts on
  the last event after 1.2 s of quiet (`WatchDisplayLayout`). Strips are reconciled on the same settle.
- Explorer restarting forgets every app bar (`TaskbarCreated`), and the shell can stop holding a strip
  without saying so. The docked bar checks the outcome once a second (`KeepDockedStrip`). Each strip
  needs the same check for the top edge.
- Topmost is a shared band: the bar re-asserts it on foreground change and on the 1 s timer. Strips are
  topmost like the taskbar and join that re-assertion, and step out of the band for a full-screen
  window on their screen (`ABN_FULLSCREENAPP`), as the docked bar does.
- Own windows appear in the window list unless ignored. Each strip's hwnd goes through
  `WindowMonitor.Ignore`, is a tool window, and is pinned to every virtual desktop after its first
  show, like the bar and the detached stats window.
- COM calls pump messages and a rebuild can re-enter itself. The strips are filled inside the bar's
  guarded rebuild, never by a rebuild of their own.
- Static brushes are frozen.
- The window list setting is stored as `WindowListAtBottom` with false as the default, for a migration
  reason written beside it in `AppState.cs`. Existing files must keep meaning what they mean.

## Approaches

1. The bar builds the buttons, the strips host them (picked). A strip is a thin window that owns its
   app bar registration and a panel. The bar's rebuild makes each tab with the code that makes a list
   line today (`IconButton`, then a strip variant of `Titled`) and hands every screen's tabs to that
   screen's strip. Click, menu, drag, hover card and marks are one implementation. The cost is the
   state `IconButton` closes over that assumes the button is inside the bar's window: press tracking
   (`MarkPressConsumed`, `orphanRelease`), the candidate ring (`ApplyCandidate`), the hover card's
   placement, the fade (`PointerOnBar`, `UpdateFade`), and the drag callbacks. Each needs the strip to
   forward to the bar or the bar to look in the strips too.
2. A self-contained strip with its own button, reading the overview from `WorkspaceManager`. No
   coupling to the bar. Every behaviour is written twice and the strip's copy lags; that is the third
   anti-goal.
3. Extract the window button from `FloatingBar` into a shared component, then use it in both. The best
   end state, and a large change to an 8,000-line file that every other open branch touches.

Petre answered the look and left the build to the author, so 1 is the author's pick. If the first
measurement below shows the coupling cannot be forwarded cleanly, the fallback is 3 limited to the
button, and that is a change of plan to bring back to Petre, not to make quietly.

## Measure first

Three things are unknown and are settled by throwaway probes before the plan's tasks are fixed. This
project's rule: measure, do not theorise.

1. Top-edge app bars on his screens. `ABM_NEW`, `QUERYPOS` and `SETPOS` with `ABE_TOP` on a borderless
   layered window, on each screen, at each screen's scale. What the shell grants when the main bar is
   docked left or right on the same screen, and across the seam between two screens: the wanted result
   is the strip across the whole top with the docked bar below it, and the order of registration may
   decide it, so measure both orders and what a re-dock of either does to the other. If the shell
   cannot be made to give that, stop and show Petre what it does. What it grants beside a taskbar on
   the top edge. Whether the work area comes back on `ABM_REMOVE` per screen.
2. A bar-built button in another window. Host one `IconButton` in a second WPF window and find which
   of the behaviours listed under approach 1 break, with the click trace on.
3. A screen dropping out. Lock and unlock, and unplug a screen, with strips registered: what the shell
   does with a strip whose screen is gone, and what `DisplaySettingsChanged` reports in what order.

## The parts

- `AppBar` gains the top edge. Its `Dock` takes the edge and a thickness and derives the rectangle for
  any of the three edges.
- `ScreenStrip`, a new window class in `Deskbar.App`: borderless, tool window, topmost, not activating
  more than the bar does (a click must still earn the foreground rights the bar's click earns, see the
  comment in `IconButton`). It owns one `AppBar`, knows its screen by the identity `MonitorLayoutKey`
  and `ScreenLayout` use, converts its 32 DIP to that screen's pixels, and exposes a panel to fill and
  a drop target.
- A strip set, owned by `App` beside the bar: creates, moves and closes strips so there is exactly one
  per connected screen while the setting is on, and none while it is off. It reconciles on the settle
  after a display change, on the setting changing, and on start. The decision "these screens, these
  existing strips, therefore add, move, remove" is a pure function in `Deskbar.Core` with tests.
- The strip guard: `StripHeld` learns the top edge, and each strip is checked on the 1 s timer under
  the same rules as the docked bar (settle time, retry interval, no verdict from another screen).
- The bar's rebuild: when the setting is on screens, it builds no list section and no expanded row,
  builds each screen's tabs, and hands them over. Most rebuild triggers are gated on the bar being
  visible today; with strips on, the tabs are rebuilt inside the same guard even while the bar is
  hidden, and the bar's own rows may be skipped then. With the setting elsewhere it hands over nothing and
  the strip set has already closed the strips.
- `AppState`: a third value for the list's place that leaves `WindowListAtBottom` readable by existing
  files. How it is stored is left to the builder, within that constraint.
- `WorkspaceManager`: one setter for the place, replacing `SetExpandCurrentWorkspace`'s bool, a no-op
  when unchanged like the other settings.
- The menu submenu, the `window-list` pipe command and its `ControlUsage` line.
- Instrumentation, behind the existing `trace.on` switch and written only when it changes: per strip,
  its screen, the rectangle asked for and granted, the work area, and each registration or repair. The
  debug dump lists the strips.
- Exit, Restart and turning the setting off remove every registration before the windows close.

Windows that are already positioned under a new strip when it appears: maximized and snapped windows
are moved by the shell when the work area changes. A restored window whose title bar is under the
strip is the fourth anti-goal; whether the shell nudges it is part of measurement 1, and if it does
not, the strip set moves such windows down by the strip's height once, when a strip is created.

## Left to the builder

- How the three-valued setting is stored.
- The exact tab width, paddings and the fill colour of the active tab, starting from the mockup's
  values, to be shown to Petre in the running app.
- How the strip forwards presses and hover to the bar, once measurement 2 says what needs forwarding.
- Whether a strip's icons are drawn from the cache at the strip's own scale or the bar's.

## Testing

- `Deskbar.Core.Tests`: the setter, the stored setting read from an old file, the reconcile function
  (screen added, removed, rearranged, a transient empty layout), `StripHeld` for the top edge, the
  tab-width rule.
- `Deskbar.Windows.Tests`: a strip window built on the STA thread, filled with rows: tab count per
  screen, the name tag, the active tab's marks, narrowing, the empty strip, the menu's three entries.
  No test registers a real app bar; that is an integration concern and mutates the machine.
- By hand, with Petre: the manual script gains a section (dock the main bar on each side with strips
  on, switch workspaces, lock and unlock, unplug a screen, full-screen video, RDP in).

## Shipping

One PR for the feature, with a `## Release notes` section for users. The setting is off until chosen,
so the merge changes nothing for anyone who does not pick the third place.
