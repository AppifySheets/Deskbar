# The manager's question line

Task 61e4d143, 2026-11-25. Step 3 of the manager session, after
[the manager session](2026-11-25-manager-session-design.md) and
[its event wakes](2026-11-25-manager-event-wakes-design.md).

## The gap

The manager could not put a question in front of the user. `task ask` is a session's form, and every
`--session` form is refused from the manager's folder, because the manager has no task. So a decision it needed
waited until the user happened to open its window, and the orange dot on the Manager button only said that a wake
had happened, not what it asked.

Three ways were weighed in session 0af1a941:

1. Leave it. Costs nothing; a question only exists once the user looks.
2. A manager line at the top of "Waiting on you". Picked.
3. The manager's question on the task it is about. Natural for a question about one task, but it collides with
   that task's own session asking: two askers on one task, and answering one could settle the other.

Petre picked 2, asked for "a different color" and for it to "have a priority". He also asked whether the manager
should vet every task's question before it reaches him. Every question on the board that day needed him
personally (photos, a password, a design pick, credits, a captcha, merges), so a gate would only have added up to
half an hour of batching delay and a Fable turn per batch, and blocked nothing. Left out.

## What it does

- `task ask -` from the manager's folder, with no `--session`, stores one question on the manager's record
  (`ManagerRecord.Question` and `AskedAt` in state.json). A new question replaces the old one. A refusal when no
  manager is on record, or the text is blank.
- The bar's task list and the tasks panel show it as one violet line above every task line, outside the scrolling,
  so it is always in sight. The panel hides it over the Stats view.
- A click opens the manager (the Manager button's own click). Opening does not clear the question.
- The user's reply clears it: a `UserPromptSubmit` hook from the manager's folder, for the stored session id, whose
  prompt is not one of Deskbar's wakes (they start with `ManagerWakes.Prefix`, "Deskbar wake."). The briefing tells
  the manager to ask again when the reply left the question open.
- `task answered` from the manager clears it too.
- A new manager (a new day, a claim) is a new record, so yesterday's question goes with yesterday's session. Until
  that click, yesterday's line stays.
- The line is no task: no tick, no toggles, no menu, no thread, nothing to close. "The manager is not a task" from
  step 1 holds.

## Colour

Violet (`#B48CFF` stripe), Petre's pick from the
[mockup](https://claude.ai/artifact/BkuegZx2E2cxLKwHjSrg9f) over teal. Orange is a task waiting on him, blue is In
progress and the selection, green is Done, red and yellow are the Important and Urgent flags. All the manager line's
brushes are in `ManagerLook`, shared by the bar and the panel.

## Where the code is

- Rules, pure: `Deskbar.Core/Tasks/ManagerQuestion.cs`.
- Pipe: the two `when byManager` arms in `RemoteControl.Tasks.cs`; the prompt in `RemoteControl.ClaudeHook.cs`
  (`ManagerPrompted`).
- Briefing: `ManagerSession.Briefing`, five lines.
- Bar: `FloatingBar.ManagerLine.cs`. Panel: `TasksWindow.Manager.cs`, the `ManagerLineHost` border in
  `TasksWindow.xaml`, `TasksView.ManagerAsks` and `TasksBoard.OpenManager`.
- Manual steps MQ1 to MQ7 in `docs/superpowers/notes/manual-test-script.md`.
