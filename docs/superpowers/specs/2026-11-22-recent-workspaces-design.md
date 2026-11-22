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

That one age drives the whole row, GRADUALLY. Petre: "recency has to be gradual, not 8 hours", and
"more time goes by, less pronounced a bar and all its icons become". There is no recent/idle line.
The age goes through the existing aging curve (`FloatingBar.AgeOf`: 0 up to 15 minutes, 1 at 8 hours,
eased by ^0.7 so most of the change is in the first hours) and the result, 0 to 1, sets every part:

- Icons: the grey overlay's opacity, as today, but from the workspace's age instead of each
  window's own. Every icon in a row is the same shade.
- Rail: the workspace colour blended toward grey.
- Tint: a faint wash of the workspace colour, about a third of the current row's `0x38` at age 0,
  fading to none.
- Name: full brightness fading to about 42%, never lower, so every name stays readable.
- The current row is drawn as today: full rail, full tint, ring, coloured name.
- A group's header and bracket follow the freshest member's age.

## Approaches considered

- A: rail colour on recent rows only, idle rows grey. Quietest bar.
- B (picked by Petre): A plus a faint tint on recent rows. Recent rows stand out more; the bar gets
  busier on a day with many recent workspaces.
- B stepped against B gradual: first drawn with an 8-hour line between recent and idle; Petre chose
  the gradual fade instead.
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

- The curve's ends (15 minutes, 8 hours), the third-strength tint and the 42% name floor are first
  values, to tune live, as the aging thresholds were. Past 8 hours every row looks the same; if a
  row from last week should look fainter than yesterday's, the tail gets stretched.
- The per-minute aging timer refreshes in place, as it does for icons today: it keeps each row's
  rail, tint and name with its workspace from the rebuild that drew them, and rewrites only those.
  Never a rebuild a minute, which costs clicks (see `ClickTrace`).
- No setting to turn it off for now.
