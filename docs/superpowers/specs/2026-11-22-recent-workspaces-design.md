# Recent workspaces keep their colour

Date: 2026-11-22. Short design (a small change). Mockup:
[Recent Workspaces Mockup](https://claude.ai/artifact/MSAwm3YqweBe54NkT9qUz8), option B.

## Why

Petre: "i get lost in so many workspaces; how can i have workspaces that i've been working on active
while others are hidden or dimmed?" Today every row has a full-colour rail and a bright name, so
twenty rows all ask for the same attention, and the few he is working in do not stand out.

## What changes

Every workspace gets one AGE: the time since any of its own windows was last used, read from the
stamps `WorkspaceManager.LastUsed` already keeps and `last-used.json` already persists. Pinned windows
do not count, since they sit on every workspace. The workspace you are on has age 0. A workspace with
no windows counts as fully aged.

That one age drives the whole row:

- Icons fade by the workspace's age on the existing curve (full colour up to 15 minutes, grey at
  8 hours), not by each window's own age. Every icon in a row is the same shade.
- Recent (age under 8 hours, the point at which the icons are fully grey): the rail keeps its colour
  at about 70%, and the row gets a faint tint of its colour, about a third of the current row's
  `0x38`.
- Idle (8 hours or more): grey rail, no tint, dimmed name.
- The current row is drawn as today: full rail, full tint, ring, coloured name.
- A group's header and bracket dim when every member is idle.

## Approaches considered

- A: rail colour on recent rows only, idle rows grey. Quietest bar.
- B (picked by Petre): A plus a faint tint on recent rows. Recent rows stand out more; the bar gets
  busier on a day with many recent workspaces.
- Hiding or auto-folding idle rows was raised first and set aside: the bar is how you reach a
  workspace, and Petre asked for colour, not removal.

## Goals, non-goals

- Goal: at a glance, the workspaces in use today stand apart from the rest.
- Non-goals: no hiding, folding or reordering of rows; every row stays clickable where it is. The
  manual fold stays as it is.
- Anti-goal: anything that moves rows around as ages change. A row that jumps loses clicks.

## Trade-off accepted

Per-window aging goes. Inside a busy workspace (the Deskbar row has eleven windows) the bar no longer
shows which windows have been left alone; the hover card's "last used 2 h ago" still does, per window.

## Calls left to the builder (Claude's, Petre can overrule)

- The 70% rail and the third-strength tint are first values, to tune live, as the aging thresholds
  were.
- The per-minute aging timer already refreshes icons in place. Crossing the 8-hour line changes the
  rail and tint, so the timer redraws the bar only when some workspace's recent/idle state flipped,
  which is rare, and never otherwise (a rebuild every minute costs clicks, see `ClickTrace`).
- No setting to turn it off for now.
