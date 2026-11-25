# A task in its own window: design

Deskbar task dca4ebeb, 2026-11-25. Petre: "build a separate view that will open a task in, especially for longer
tasks". His screenshot showed a long brief (headings, numbered steps, bullet lists) squeezed into the tasks panel's
narrow column.

## Intent

The tasks panel is an app bar of 360 DIP and more, docked beside the bar. That suits a list. It does not suit reading
or writing one task with a long description and a long thread: the text wraps into a tall column, the thread shows
only its latest three comments, each cut to three lines, and the in-place editor is two small boxes. A long task needs
a page of its own at a width you choose.

Goals: read the whole task at a comfortable width; edit its title and description in a large box; comment on it; see
its sessions and subtasks; reach it from wherever tasks are (the panel, the bar, a script).

Non-goals: replacing the panel or the bar's task list; a new way to create tasks; a second editor for comments beyond
what the panel has.

Anti-goals: a window that goes stale while the task changes; one that takes Petre to another desktop; a redraw that
loses a half-written comment.

## Approaches

1. A separate WPF window per task, managed by the tasks board and reusing its actions (picked). It looks like Deskbar,
   updates live from the same TaskService pulse, and every click runs the panel's own TasksBoard method, so the rules
   cannot drift.
2. A "reading mode" that widens the panel. Rejected: the panel is an app bar, so a wider panel takes the width from
   every maximized window on that screen for as long as it is open.
3. The task as an HTML page in the browser. Rejected: it would look nothing like Deskbar, could not post a comment or
   follow changes, and would be one more browser window to place.

## Decisions (Petre's, from the mockup, approved 2026-11-25)

- Editing happens in the window ("edit in the window"): Edit, F2 or Ctrl+E turn the title and description into large
  boxes with Markdown and Preview tabs; Ctrl+S or Ctrl+Enter saves, Esc cancels.
- One size and position, shared by every task window, remembered across restarts ("remember size and position"):
  AppState.TaskWindow, a WINDOWPLACEMENT rectangle in device pixels plus maximized.
- The window opens on the desktop in front; one already open elsewhere is moved here before it is brought forward.
- "Open in window" is on the bar's task menu as well as the panel's.

The mockup is the artifact "Deskbar Task Window" (reading and editing, beside the panel as it is).

## What was built

- `TaskDetailWindow` (XAML and code-behind): header (title, status, workspace, assignee, flag toggles, age, Edit, "⋯"),
  the question or status line in full, the description rendered with its pictures, the whole thread (settled comments
  still fold), a comment box (Ctrl+Enter posts, pictures paste in), and a side column for sessions, "Start Claude on
  this", subtasks and details that moves under the thread below 1100 DIP.
- `TasksBoard.OpenWindow(id)`: one window per task, cascaded 28 px when another is open, redrawn on every board redraw
  whether the panel is open or not, closed when its task is deleted, closed with the app.
- Entry points: "Open in window" right after Edit on the panel's line menu and the bar's line menu, a button at the top
  of an open thread in the panel, and `deskbar.cmd task open <id>`.

## Calls made while building (the builder's, for Petre to check)

- No double-click to open it. The proposal listed one, but a double-click on a line already goes to the task's
  workspace (Ruling 54), and taking it over would break that. The menu item and the thread's button stand in.
- The window draws with the shared pieces (MarkdownBlocks, TaskImageRow, PicturePaste, TaskImageFiles) and the panel's
  colours, but its layout code is its own rather than the panel's builders moved into a shared class. The panel's
  builders are tied to its line, fold and drag state, and pulling them apart was a larger change than this task.
- The comment box posts on Ctrl+Enter, not Enter, because comments written in this window tend to be long. The panel
  keeps Enter.
- Subtasks in the side column open in their own windows. "Add subtask" stays in the panel.
