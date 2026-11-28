# The manager as an Inbox page

[Deskbar task 6c4c067f](https://deskbar.app/task#6c4c067f-e0f8-4853-831a-d86150aaa423): "open a manager session in the
same way as you open a task in the inbox". Mockup: [manager page mockup](https://claude.ai/artifact/S9UBAm5gmxkXxWjiKR7kSf).

## Intent

Today the Manager button attaches a terminal to the manager session and docks it, 640 DIP wide, beside the tasks panel
(ManagerDock, task f171bca6). Petre reads and answers tasks in the panel's Inbox, where a task opens as a page: its
session's messages and steps as a chat (InboxTranscript, ChatStreamPanel) and a box at the foot. He wants the manager to
open the same way, inside the panel, instead of as a second, wide window.

Petre's picks (2026-11-28, on the mockup): **1A**, the page replaces the docked terminal, and "Open in terminal" stays on
the page's menu; **no always-on Manager line** in the Inbox ("manager already sits in the bar": the bar's Manager
button is the way in). The violet "Manager asks" / "Manager answered" line stays as it is.

## Goals

- The Manager button (bar list foot, Inbox footer, the panel's Manager chip) and the violet manager line open the tasks
  panel on the Inbox with the manager's page: a header, the manager's chat, a message box docked at the foot.
- The chat is the manager session's transcript, drawn by the same ChatStreamPanel a task page uses, with the user's
  page messages as their bubbles in it.
- Typing in the box and pressing Enter (Shift+Enter: new line) sends the text to the manager as the user's own words.
- "Open in terminal" on the page's ⋯ menu does exactly what the button does today (ManagerActions.Open, attach, dock).

## Non-goals

- No change to the event wakes, the briefing's rules, the manager's day/start/resume logic, or the dock code itself
  (it still serves "Open in terminal").
- No manager line pinned in the Inbox list, no manager in the List or Stats views.
- No images in the manager box (a task's box takes pasted pictures; the manager cannot read files anyway).

## Approaches considered

1. **A page drawn by its own small path in TasksWindow (chosen).** A `managerPage` state beside `inboxPage`, its own
   `DrawManagerPage`, reusing ChatStreamPanel, the transcript poll and the docked-box look. The task page's code is
   built around a TaskRowView (description, question, sessions, subtasks, PRs, Close); the manager has none of those.
2. Fake a task row for the manager and send it through DrawInboxPage. Rejected: every task-only branch (tick, move
   menu, subtasks, close, comments store, PR links) would need a "not for the manager" guard, and the board would carry
   a task that is not on the board.
3. Keep the terminal and only move it into the panel's place. Rejected: a terminal cannot be hosted inside a WPF panel,
   and Petre picked the page.

## Design

### Opening

- New `TasksBoard.ShowManager()`: shows the panel (as `Show`), switches it to the Inbox view, and tells the window to
  open the manager page. The App's `OpenManager` (both wirings: bar and panel) now calls ShowManager, then
  `ManagerActions.Open(attach: false)`.
- `ManagerActions.Open` gains an `attach` flag. With false it runs the same Next/Take steps (attach to, resume, start or
  claim today's manager) but ends without `AttachTo`. Unread and Seen are cleared as today, since the user is looking.
  "Open in terminal" calls `Open(attach: true)`, today's behaviour.
- In the window: `managerPage` (bool) and `managerFrom` (the task page open when the manager page opened, if any).
  Opening it remembers the list offset like a task page. Back (arrow, Esc outside the box) returns to `managerFrom`'s
  page if there was one, else to the list. Opening a task page, another view, or ShowTask from outside leaves it.

### The page

- Top: back arrow, the violet manager mark (ManagerLook), "Manager", the state in words (running, asleep or not
  running yet, from the poll's ChatSourceKind and the sessions list) and ⋯ with "Open in terminal".
- Body: a ChatStreamPanel bottom-aligned in a FastScrollViewer, kept across redraws and held at its end exactly as the
  task page does (KeepPlace / pageHold logic shared, keyed by a fixed `ManagerSession.PageId` Guid).
- Foot: the message box, styled as the task page's docked comment box, hint "Message the manager…".

### The chat source

- `ManagerSession.PageId`: a fixed Guid. `FollowTranscript(PageId)`, and `TasksBoard.ChatSourceOf` answers for PageId
  from today's ManagerRecord: its session id, the manager folder, live/ended from the sessions list
  (ClaudeChatSource.Choose over one TaskSession). No record: ChatSource.Nothing and the page says "No manager yet today:
  send a message to start it."
- The user's page messages are stored on the record: `ManagerRecord.Messages`, a list of `ManagerMessage(Guid Id,
  DateTimeOffset At, string Text)`, the newest 100 kept. A new record (new day, new manager) starts empty. They are drawn
  as the user's bubbles, merged by time with the transcript items (ClaudeChatStream.Build, as comments are).

### Sending

`ManagerActions.Send(text, openTask)`:

1. Claude sessions off: refused (FeatureOff) on the problem line. Empty text: nothing.
2. The message is stored on the record (Messages), and ManagerQuestion.AfterPrompt is applied (question cleared, Replying
   set), since the waiter road never fires the prompt hook.
3. The text delivered is `ManagerSession.PageMessage(text, openTaskLine)`: an opening
   "Deskbar: the user wrote to you on your page in Deskbar. Their own words, as if typed in your terminal:", the text,
   then OpenTaskLine for `managerFrom`'s task when there was one. It carries its own meaning, so the 40-line briefing
   needs no new line. It holds no "Deskbar wake." prefix, so the prompt hook counts it as the user's (IsUsersMessage).
4. The road: no manager today, or the stored one gone: today's manager is started (Open(attach: false)'s path) and the
   message delivered once it is listed. Otherwise CommentWake.PlanFor over the manager's one session, as Wake does:
   Inbox (waiter alive), Resume with the message (not listed, only after Gone), StopThenResume (listed, waiter lapsed).
   A click or wake in flight (`pending`): the send is queued and run when it ends, never dropped.
5. ClaudeChatStream.Visible drops a user prompt starting with the page opening (the resume road writes it as a prompt),
   so the stored bubble is the only one.

### What else changes

- The manager line's and Manager buttons' click: ShowManager instead of the terminal.
- ManagerQuestion's answered line: cleared when the manager page is shown with the panel visible (it is the user
  reading the answer), as the manager window coming to the front clears it today.
- The pipe's `task manager` keeps opening the terminal (a script has no panel to look at). Its help text says so.

### Settled after the builder check (my calls unless marked)

- **A send with no manager today** starts one with the page message as its first prompt in place of FirstMessage (the
  user asked something, so the overview turn is skipped). A send while the manager is listed and busy (its first turn,
  say) goes to the inbox: CommentWake.PlanFor never stops a busy session, and the waiter its turn's Stop starts finds the
  message at once. A listed idle manager with no waiter is stopped and resumed with it, as wakes do.
- **No double bubble on the inbox road**: a waiter-delivered message is a "user" line with origin "task-notification",
  which ClaudeChat never shows (measured 2026-11-26, ClaudeChatStream's header). Only the resume road needs the drop.
- **The open-task line**: while the manager page shows, no task page is open (`inboxPage` is null), so the prompt hook
  adds nothing; the page message carries `managerFrom`'s line itself. No line twice.
- **ManagerRecord.Messages** goes into the hand-written Equals and GetHashCode (content compare, as HandedBack) and into
  the panel's redraw key (TasksBoard.ShownStateKey), or a new message would not redraw. Messages end with the day's
  manager, as its transcript does: a new session is a new chat.
- **Mid-turn sends** show their bubble at once; the header's "working" says the manager has not read it yet. No queued mark.
- **Sends during a pending click or wake** wait in a list and go in order once it ends.
- `Open`'s `attach` flag defaults to true, so the pipe's `task manager` keeps the terminal.
- The PageId branch of the chat source is a Windows test (ChatSourceOf lives in the App), or a small pure helper in Core
  if one falls out naturally.

## Left to the builder

- Exact header layout and spacing, following the task page's PageTop.
- Whether the docked box reuses TasksWindow.DockedBox's code or a copy trimmed of task specifics.
- Tests: Core (PageMessage, Visible drop, record Messages cap, ChatSourceOf for PageId), Windows (page opens, back, box
  sends through a fake action, stream kept across redraws).
