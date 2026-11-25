using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
// System.Threading.Tasks (an implicit using) has a TaskStatus too; the alias says which one this file means.
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Control;

// THE `task` COMMANDS: the pipe twins of what Petre does to tasks in the bar and the window, for a script or a
// session acting for him. Design: docs/superpowers/specs/2026-11-22-tasks-board-design.md ("Left to the builder":
// the pipe commands, and `task list` stays JSON).
//
//   task list [--closed]
//   task overview                     (one line per open task, for the manager session: TaskOverview)
//   task show <id>                    (one task as `list` prints it, thread text included)
//   task add <title>|- [--workspace <name>|none] [--important] [--urgent]
//   task close <id> [--stop-claude]
//   task reopen <id>
//   task status <id> <backlog|in-progress|waiting|done>
//   task rename <id> <title>
//   task move <id> <workspace>|none
//   task comment <id> <markdown>
//   task delete <id>
//   task flag <id> important|urgent on|off
//   task order <id> top|end|<n>
//   task assign <id> me|claude
//   task pr <id> <url>                       (task e7aa49b1: a pull request on the task, found by its number in search)
//   task describe <id> <markdown>|-          (Ruling 58: his own description under the title)
//   task comment-edit <comment-id> <markdown>|-   (his own comment only; a session's is refused)
//   task goal <id> <goal-id>|none     (the goals planner; needs the goal service, RemoteControl.Goals.cs)
//   task snooze <id> 1h|3h|tomorrow|monday|<n>m|<n>h|<n>d   (task 6da8d7eb: the tasks panel's snooze menu, TaskSnooze)
//   task wake <id>                    (its "Wake now")
//   task duplicate <id> <of-id>       (task links: close <id> as a duplicate of <of-id>)
//   task relate <id> <other-id>       (task links: related, read both ways)
//   task unrelate <id> <other-id>
//   task go <id>
//   task open <id>                    (task dca4ebeb: the task in a window of its own, the menus' "Open in window")
//   task start <id> [--folder <path>] [--model <model>]
//   task open-session <id> <session>
//
// `go`, `start` and `open-session` (Task 9 of docs/superpowers/specs/2026-11-23-tasks-open-claude-sessions-design.md,
// "Where the clicks are") are the twins of a line's double-click, "Start Claude on this" and the thread's Open. They
// act through TaskSessionCommands, which the app hands in, since windows, terminals and `claude` are its business; the
// pipe only finds the task (and the session) and, for `start`, the folder. A script cannot answer a pick list, so
// `start` without --folder starts only where exactly one folder is known and otherwise refuses, naming the
// candidates; nor does it ask before a second session on a task, since a script asking for one means it. `start`
// answers once the session is on its way: `claude --bg` is waited for off the UI thread, and its failure (an
// untrusted folder) lands in the task's thread, where the session's own words are.
//
// `flag` and `order` (Task 12, Rulings 25 and 28) are the twins of the toggles on a line and of dragging it. The flags
// name Covey's quadrant (TaskOrder.cs); a flag that changes the quadrant puts the task on top of its new one, as the
// toggle does. `order` moves a task within its own quadrant, n counting from 1 and clamped to the quadrant's length
// (a drag across quadrants is a flag change plus an order, so a script does it in two steps). A task waiting on you
// or Done is pinned above the quadrants until answered or closed (task eefb986a pinned Done), so `order` moves it
// within its part of the pinned block instead, as a drag does (TaskBoard.Place says so; task f41d7857). `list` prints each task's flags, its quadrant ("I" to "IV") and its 1-based `order` in
// it, null while it is pinned.
//
// The timer build's `task timer` and `task add --in` are gone with the timers. The forms a Claude session uses for
// its own task (join, new, status-line, ask, done, comment, later), each with `--session <id>`, are in
// RemoteControl.ClaudeHook.cs with the hooks that tell a session about them.
//
// Every verb calls the same TaskService method the UI calls, so a task changed from a script obeys the rules a
// click does and the bar redraws from the service's Changed. An id is the task's Guid or any unique prefix of at
// least four characters (TaskBoard.Find); `list` prints them in full. A comment from here is Petre's (no session).
//
// STATUS NAMES on the pipe are lowercase with hyphens: backlog, in-progress, waiting, done, closed. `waiting` is
// Waiting on you, shortened because it is the one a script types. `status` takes the first four; closed is refused
// with a pointer at `close`, the one way to close (TaskBoard.SetStatus).
//
// `task` is in ControlArguments.AboutTheCaller, so the client appends `--from <folder>` to every call. It is
// stripped first. Only `add` uses it: without --workspace the note goes on the workspace of the caller's editor
// window (CallerOf, shared with `open` and `attention`), else on the workspace of the latest task worked on in the
// caller's repo (SessionWorkspace, task f905b1a7). A caller with neither and no --workspace is refused,
// because a note on the wrong workspace is worse than one that was not written.
//
// Every failure is one line and changes nothing: parsing and lookups finish before the first service call.
public sealed partial class RemoteControl
{
    const string NoWorkspaceForTask = "Not run from a workspace's folder; pass --workspace <name> or --workspace none";

    // The pipe's name for each status, both ways (see the header).
    static readonly IReadOnlyList<(TaskStatus Status, string Name)> StatusNames =
    [
        (TaskStatus.Backlog, "backlog"),
        (TaskStatus.InProgress, "in-progress"),
        (TaskStatus.WaitingOnYou, "waiting"),
        (TaskStatus.Done, "done"),
        (TaskStatus.Closed, "closed"),
    ];

    ControlReply Task(string[] rest)
    {
        if (tasks is not { } service) return ControlReply.Fail("Tasks are not available");
        var (words, from) = SplitFrom(rest);
        // A session's own forms carry --session (RemoteControl.ClaudeHook.cs); everything below is the user's or a
        // script's. `task comment` is both: with --session it comments on the session's own task, without it on an id.
        var (args, session) = SplitSession(words);
        // The manager session (docs/superpowers/specs/2026-11-25-manager-session-design.md) is the session started in its
        // own folder, told by `--from`. An empty manager folder (no manager in this copy) matches nothing. It coordinates
        // and never finishes a task: close, delete and the done/closed statuses are refused before anything is looked up.
        var byManager = ManagerSession.IsManager(Folder(from), taskSessions?.ManagerFolder ?? "", LongPath);
        if (byManager && FinishesATask(args)) return ControlReply.Fail(ManagerSession.Refusal);
        // Nor does it touch a comment's text or fold one away (final review, item 1): comment-edit would rewrite the
        // user's own words as the user's edit, and comment-settle is checked here, before the session forms' refusal
        // below, so it gets this answer with or without its --session.
        if (byManager && TouchesAComment(args)) return ControlReply.Fail(ManagerSession.CommentRefusal);
        // The manager has no task of its own, so every session form (--session) is refused from its folder: `done` would
        // finish a task, and join, ask, answered, comment --session and the rest would answer another session's question
        // or speak as that session. Its own ids come from `task show`, so it can name any session.
        if (byManager && session is not null) return ControlReply.Fail(args is ["done"] ? ManagerSession.Refusal : ManagerSession.JoinRefusal);
        if (session is not null)
            return string.IsNullOrWhiteSpace(session)
                ? ControlReply.Fail("--session needs the session's id")
                : SessionTask(service, args, session.Trim(), from);
        return args switch
        {
            // A session's form typed without its --session: it cannot know whose task is meant.
            ["done"] or ["answered"] or ["join", _] or ["new", _] or ["status-line", _] or ["ask", _] or ["later", _] or ["subtask", _] or ["pr", _]
                or ["duplicate-of", _] or ["relate", _] or ["unrelate", _] =>
                ControlReply.Fail($"task {args[0]} is a session's form and needs --session <id>"),
            ["list"] => Success(service.Board.Pile().Select(Listed).ToArray()),
            // The closed tasks, which every other view leaves out, so one closed by mistake can be found and reopened.
            // The latest closed first: closing stamps UpdatedAt, and nothing but Petre's own edits moves it after that.
            ["list", "--closed"] => Success(service.Board.Tasks.Where(t => t.Status == TaskStatus.Closed)
                .OrderByDescending(t => t.UpdatedAt).Select(Listed).ToArray()),
            // The stats view's numbers (task 521d2b07, TaskStats): the burn-up's days with the tasks added and finished on
            // each, the open count at each day's end, and the headline numbers. Days are this machine's local days.
            ["stats"] => TaskStatsReply(service, TaskStats.DefaultDays),
            ["stats", "--days", var days] => int.TryParse(days, out var count) && count is >= 1 and <= 366
                ? TaskStatsReply(service, count)
                : ControlReply.Fail("task stats --days takes a whole number of days from 1 to 366"),
            // The manager session's cheap look at the board (TaskOverview): one line per open task, where `list` is JSON
            // with every thread. `show` is the other half: one task as `list` prints it, thread text included.
            ["overview"] => Said(TaskOverview.Text(service.Board, service.Now, id => WorkspaceNameOf(id) ?? "", Busy)),
            ["show", var id] => service.Board.Find(id).Match(t => Success(Listed(t)), ControlReply.Fail),
            ["add", .. var more] => TaskAdd(service, more, from),
            // A task Claude is still working on refuses a plain close (task 37cd7081, TaskBoard.Close), and the refusal
            // names the flag; `--stop-claude` is the menus' "Stop Claude and close", through the app (ClaudeTaskActions).
            ["close", var id] => TaskChange(service, id, t => service.Close(t.Id)
                .MapError(e => e == TaskViews.ClaudeStillWorkingRefusal ? $"{e} (task close {id} --stop-claude)" : e)),
            ["close", var id, "--stop-claude"] => WithSessions(c => TaskChange(service, id, c.StopAndClose)),
            ["reopen", var id] => TaskChange(service, id, t => service.Reopen(t.Id)),
            ["status", var id, var status] => ParseStatus(status)
                .Match(s => TaskChange(service, id, t => service.SetStatus(t.Id, s)), ControlReply.Fail),
            ["rename", var id, var title] => TaskChange(service, id, t => service.Rename(t.Id, title)),
            ["move", var id, var workspace] => ResolveWorkspace(service, workspace, null)
                .Match(w => TaskChange(service, id, t => service.MoveTo(t.Id, w)), ControlReply.Fail),
            // The manager's comment is its own with or without the --from-claude marker (the client adds it only when it sees
            // CLAUDECODE), never stored as the user's.
            ["comment", var id, var markdown] when byManager =>
                TaskChange(service, id, t => service.Comment(t.Id, TaskComment.ManagerAuthor, markdown)),
            ["comment", var id, var markdown] => TaskChange(service, id, t => service.Comment(t.Id, Maybe<string>.None, markdown)),
            // The same command from inside a Claude session (ControlArguments.WithCaller): stored as usual, wakes no one.
            // From the manager's folder it is the manager's own comment, stored under its author and woken on, even though
            // the client marked it --from-claude (the manager is a Claude session too).
            ["comment", var id, var markdown, ControlArguments.FromClaude] when byManager =>
                TaskChange(service, id, t => service.Comment(t.Id, TaskComment.ManagerAuthor, markdown)),
            ["comment", var id, var markdown, ControlArguments.FromClaude] =>
                TaskChange(service, id, t => service.Comment(t.Id, Maybe<string>.None, markdown, wakes: false)),
            ["delete", var id] => TaskDelete(service, id),
            ["flag", var id, var flag, var state] => ParseFlag(flag, state)
                .Match(f => TaskChange(service, id, t => service.SetFlag(t.Id, f.Flag, f.On)), ControlReply.Fail),
            ["order", var id, var place] => ParsePlace(place)
                .Match(p => TaskChange(service, id, t => service.Place(t.Id, t.Quadrant, p)), ControlReply.Fail),
            // A pull request on a task by hand (task e7aa49b1, TaskPullRequests): the link is read before the task is looked up.
            ["pr", var id, var url] => AskedPullRequests.In(url) is [var link]
                ? TaskChange(service, id, t => service.AddPullRequest(t.Id, link))
                : ControlReply.Fail($"'{url}' is not a GitHub pull request link (https://github.com/<owner>/<repo>/pull/<n>)"),
            ["describe", var id, var markdown] => TaskChange(service, id, t => service.Describe(t.Id, markdown)),
            ["comment-edit", var comment, var markdown] => TaskCommentEdit(service, comment, markdown),
            ["assign", var id, var who] => ParseAssignee(who)
                .Match(a => TaskChange(service, id, t => service.Assign(t.Id, a)), ControlReply.Fail),
            ["goal", var id, var goal] => TaskGoal(service, id, goal),
            // Snoozing (task 6da8d7eb): the length is read first, so a wrong one changes nothing. "tomorrow" and "monday"
            // are 09:00 on this machine's clock, as in the panel's menu (TaskSnooze).
            ["snooze", var id, var length] => TaskSnooze.Parse(length, service.Now.ToLocalTime(), TimeZoneInfo.Local.GetUtcOffset)
                .Match(until => TaskChange(service, id, t => service.Snooze(t.Id, until)), ControlReply.Fail),
            ["wake", var id] => TaskChange(service, id, t => service.Wake(t.Id)),
            // Subtasks (docs/superpowers/specs/2026-11-24-subtasks-design.md): put a task under another or free it, and read
            // the family text a session on it would read.
            ["parent", var id, var parent] => TaskParent(service, id, parent),
            ["family", var id] => service.Board.Find(id).Match(t => Said(AroundOf(service, t).GetValueOrDefault(NoFamily)), ControlReply.Fail),
            // Task links (docs/superpowers/specs/2026-11-25-task-links-design.md): both ids are looked up before anything
            // changes. `duplicate` closes under close's rules, so Claude still working refuses here too.
            ["duplicate", var id, var of] => service.Board.Find(of)
                .Match(o => TaskChange(service, id, t => service.MarkDuplicate(t.Id, o.Id)), ControlReply.Fail),
            ["relate", var id, var other] => service.Board.Find(other)
                .Match(o => TaskChange(service, id, t => service.Relate(t.Id, o.Id)), ControlReply.Fail),
            ["unrelate", var id, var other] => service.Board.Find(other)
                .Match(o => TaskChange(service, id, t => service.Unrelate(t.Id, o.Id)), ControlReply.Fail),
            ["go", var id] => WithSessions(c => TaskChange(service, id, c.Go)),
            // The task in a window of its own (task dca4ebeb), as "Open in window" on a line's menu does. The task is found
            // first, so an unknown id refuses before any window opens; the reply is the task.
            ["open", var id] => TaskChange(service, id, task => openTaskWindow is { } open ? open(task) : Result.Failure(TaskWindowUnavailable)),
            ["start", var id] => WithSessions(c => TaskStart(service, c, id, Maybe<string>.None, Maybe<string>.None)),
            ["start", var id, "--folder", var folder] => WithSessions(c => TaskStart(service, c, id, Folder(folder) ?? folder, Maybe<string>.None)),
            ["start", var id, "--model", var model] => WithSessions(c => TaskStart(service, c, id, Maybe<string>.None, model)),
            ["start", var id, "--folder", var folder, "--model", var model] => WithSessions(c => TaskStart(service, c, id, Folder(folder) ?? folder, model)),
            ["start", var id, "--model", var model, "--folder", var folder] => WithSessions(c => TaskStart(service, c, id, Folder(folder) ?? folder, model)),
            ["open-session", var id, var sessionId] => WithSessions(c => TaskOpenSession(service, c, id, sessionId)),
            // The bar's Manager button's twin (manager session design, "The button"): the App's one Manager command,
            // which answers at once; the window comes once today's manager is attached, started or resumed.
            ["manager"] => WithSessions(c => Reply(c.Manager(), () => new { opening = true })),
            _ => ControlReply.Fail($"Wrong arguments for task.\n{TaskUsage()}"),
        };
    }

    // `task stats [--days <n>]` (TaskStats): what the panel's Stats view draws, as JSON. Each task is its id, title and
    // status, so a caller can `task show` or `task go` any of them; the median is in hours, rounded to a tenth.
    static ControlReply TaskStatsReply(TaskService service, int days)
    {
        var stats = TaskStats.Of(service.Board, service.Now, TimeZoneInfo.Local, days);
        object Brief(TaskItem t) => new { id = t.Id, title = t.Title, status = StatusName(t.Status) };
        return Success(new
        {
            openNow = stats.OpenNow,
            addedLast7Days = stats.AddedThisWeek,
            finishedLast7Days = stats.FinishedThisWeek,
            medianHoursToFinish = stats.MedianToFinish.HasValue ? Math.Round(stats.MedianToFinish.Value.TotalHours, 1) : (double?)null,
            estimated = stats.Estimated,
            days = stats.Days.Select(d => new
            {
                date = d.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                openAtEnd = d.OpenAtEnd,
                added = d.Added.Select(Brief).ToArray(),
                finished = d.Finished.Select(Brief).ToArray(),
            }).ToArray(),
        });
    }

    // `tasks-window show|hide`: the tasks panel docked beside the bar, opened as the bar's "All tasks" opens it, or
    // closed. The verb keeps its old name, so scripts written for the floating window still work.
    // `tasks-window list|matrix`: the window's view switch (Task 12, Ruling 28), a remembered setting
    // (AppState.TasksMatrix), so it needs no window to set. Only the four literal words; anything else is the usage
    // line and asks the app nothing.
    ControlReply TasksWindow(string state) => state.ToLowerInvariant() switch
    {
        "list" => Reply(manager.SetTasksMatrix(false), () => new { matrix = false }),
        "matrix" => Reply(manager.SetTasksMatrix(true), () => new { matrix = true }),
        "show" or "hide" when setTasksWindow is null => ControlReply.Fail("This copy of Deskbar has no tasks panel."),
        "show" => Reply(setTasksWindow!(true), () => new { shown = true }),
        "hide" => Reply(setTasksWindow!(false), () => new { shown = false }),
        // The Stats view (task 521d2b07), opening the panel if it was hidden. Not remembered, unlike list and matrix.
        "stats" => showTasksStats is null
            ? ControlReply.Fail("This copy of Deskbar has no tasks panel.")
            : Reply(showTasksStats(), () => new { shown = true, stats = true }),
        _ => ControlReply.Fail(ControlUsage.LineFor("tasks-window")),
    };

    // The client's `--from <folder>` is always last (WithCallerFolder appends it to every `task` call, even one that
    // already has a --from), so the LAST pair is the caller and is taken off the end. Any earlier `--from <folder>` pair
    // was typed by the caller and is dropped (final review, item 4): the manager's allow rule matches
    // `<shim> task close <id> --from C:/x`, and taking that typed folder as the caller made the manager someone else.
    // Nothing legitimate types --from on a `task` call; the hooks pass it to claude-hook and claude-wait only.
    static (string[] Args, string? From) SplitFrom(string[] rest) =>
        rest is [.. var words, "--from", var folder] ? (WithoutFrom(words), folder) : (rest, null);

    static string[] WithoutFrom(string[] words) => words switch
    {
        [] => [],
        ["--from", _, .. var tail] => WithoutFrom(tail),
        [var head, .. var tail] => [head, .. WithoutFrom(tail)],
    };

    // Every line of the Tasks section, as `deskbar.cmd task <form>`.
    static string TaskUsage() => string.Join("\n", ControlUsage.Sections.SelectMany(s => s.Commands)
        .Where(c => c.Verb == "task").Select(c => $"usage: deskbar.cmd task {c.Arguments}"));

    static string StatusName(TaskStatus status) => StatusNames.First(n => n.Status == status).Name;

    // The forms that finish a task, which the manager may not run. `status ... closed` is refused by ParseStatus for
    // everyone; for the manager it gets the manager's refusal too, so the answer is the same however it is spelled.
    static bool FinishesATask(string[] args) => args switch
    {
        ["close", _] or ["close", _, "--stop-claude"] or ["delete", _] => true,
        ["status", _, var status] => status.Trim().ToLowerInvariant() is "done" or "closed",
        _ => false,
    };

    // The comment forms the manager may not run (ManagerSession.CommentRefusal): editing a comment's text, and settling
    // one, which is the user's call. Matched on the verb alone, so a malformed call gets the same answer.
    static bool TouchesAComment(string[] args) => args is ["comment-edit", ..] or ["comment-settle", ..];

    // The four names `status` takes; closed and anything else are refused before the task is looked up.
    static Result<TaskStatus> ParseStatus(string name) =>
        name.Trim().ToLowerInvariant() switch
        {
            "closed" => Result.Failure<TaskStatus>("Use close to close a task"),
            var lower => StatusNames.Where(n => n.Name == lower && n.Status != TaskStatus.Closed)
                .Select(n => Result.Success(n.Status))
                .DefaultIfEmpty(Result.Failure<TaskStatus>($"'{name}' is not a status; use backlog, in-progress, waiting or done"))
                .First(),
        };

    // `important|urgent` and `on|off`, in any case; both are checked before the task is looked up.
    static Result<(TaskFlag Flag, bool On)> ParseFlag(string flag, string state) =>
        (flag.Trim().ToLowerInvariant(), state.Trim().ToLowerInvariant()) switch
        {
            ("important", ("on" or "off") and var on) => (TaskFlag.Important, on == "on"),
            ("urgent", ("on" or "off") and var on) => (TaskFlag.Urgent, on == "on"),
            ("important" or "urgent", _) => Result.Failure<(TaskFlag, bool)>($"'{state}' is not on or off"),
            _ => Result.Failure<(TaskFlag, bool)>($"'{flag}' is not a flag; use important or urgent"),
        };

    // `me` or `claude`, in any case (Ruling 57). A colleague is not assignable from here yet.
    static Result<Assignee> ParseAssignee(string who) =>
        who.Trim().ToLowerInvariant() switch
        {
            "me" => Assignee.Me,
            "claude" => Assignee.Claude,
            _ => Result.Failure<Assignee>($"'{who}' is not an assignee; use me or claude"),
        };

    // `top`, `end` or a position counted from 1. Zero and below are refused rather than read as the top, since they
    // are more likely a script counting from 0 than a wish for the top.
    static Result<TaskPlace> ParsePlace(string place) =>
        place.Trim().ToLowerInvariant() switch
        {
            "top" => TaskPlace.Top,
            "end" => TaskPlace.End,
            var n when int.TryParse(n, out var position) && position >= 1 => TaskPlace.At(position),
            _ => Result.Failure<TaskPlace>($"'{place}' is not a place; use top, end or a position from 1"),
        };

    // --- the JSON shape of a task ------------------------------------------------------------------

    // One task as `list` prints it and every other verb answers with it. `claudes` is whether a session has worked on
    // it, `sessions` who did (live or ended), `comments` the thread's length, `thread` the comments themselves.
    // `important`, `urgent`, `quadrant` and `order` are Task 12's (see the header). Describe reads the order off the
    // live board, so every verb's reply says where the task is now.
    //
    // `withText` is on for `list` only: each thread entry then carries the comment's text (below). Every other verb answers
    // with the thread's ids alone, because a session runs `task status-line` every turn and would otherwise read the
    // whole thread back each time (final review of the images branch: one live thread was 12.6k characters).
    object Describe(TaskItem task) => Describe(task, withText: false);

    object Listed(TaskItem task) => Describe(task, withText: true);

    object Describe(TaskItem task, bool withText) => new
    {
        id = task.Id,
        title = task.Title,
        workspace = WorkspaceNameOf(task.WorkspaceId),
        status = StatusName(task.Status),
        statusLine = task.StatusLine,
        question = task.Question.GetValueOrDefault(),
        claudes = task.IsClaudes,
        sessions = task.Sessions.Select(s => new { id = s.Id, folder = s.Folder, live = s.IsLive }).ToArray(),
        comments = task.Comments.Count,
        updatedAt = task.UpdatedAt,
        important = task.Important,
        urgent = task.Urgent,
        // Ruling 57: who the task is on, "me" or "claude" (a colleague, later, by id).
        assignee = task.Assignee.Id,
        // Ruling 58: his description (markdown, "" for none), and the thread's comment ids with who wrote each, which
        // `comment-edit` takes, "deskbar" for Deskbar's own lines. Images in tasks (docs/superpowers/specs/2026-11-24-task-images-design.md): each comment's
        // text is listed too, so a Claude session can read Petre's comments, which no hook used to pass on; and in both,
        // every pasted image's path is made absolute (TaskService.ExpandImages), ready for a session's Read tool.
        description = Expanded(task.Description),
        thread = task.Comments.Select(c => withText
            ? (object)new
            {
                id = c.Id,
                by = c.BySession ? "claude" : c.ByDeskbar ? "deskbar" : c.ByManager ? "manager" : "you",
                at = c.At,
                settled = c.Settled.HasValue ? (DateTimeOffset?)c.Settled.Value : null,
                markdown = Expanded(c.Markdown),
            }
            : new
            {
                id = c.Id,
                by = c.BySession ? "claude" : c.ByDeskbar ? "deskbar" : c.ByManager ? "manager" : "you",
                at = c.At,
                settled = c.Settled.HasValue ? (DateTimeOffset?)c.Settled.Value : null,
            }).ToArray(),
        quadrant = task.Quadrant.Numeral(),
        order = tasks is { } service && task.IsOpen ? service.Board.PositionOf(task.Id).Map(n => (int?)n).GetValueOrDefault() : null,
        // The goal it counts toward (goals plan, Task 5), as {id, title}, or null: no link, no goal service, or a link
        // to a goal that no longer exists (which reads as no goal everywhere).
        goal = goals?.GoalOf(task).Map(g => (object?)new { id = g.Id, title = g.Title }).GetValueOrDefault(),
        // Subtasks: the parent as {id, title} (null for none, or a parent no longer on the board) and the ids of the
        // task's own subtasks, open and closed.
        parent = tasks?.Board.Family(task.Id).Bind(f => f.Parent).Map(p => (object?)new { id = p.Id, title = p.Title }).GetValueOrDefault(),
        subtasks = tasks?.Board.Tasks.Where(t => t.ParentId == task.Id).Select(t => t.Id).ToArray() ?? [],
        // Snoozing (task 6da8d7eb): when a snoozed task comes back, or null while it is awake. `list` keeps listing a
        // snoozed task, so a session still sees the work; only the bar and the tasks panel hide it.
        snoozedUntil = task.SnoozedUntil.HasValue ? (DateTimeOffset?)task.SnoozedUntil.Value : null,
        // The pull requests its sessions opened (task e7aa49b1), as URLs, oldest first.
        pullRequests = task.PullRequests.Select(p => p.Url).ToArray(),
        // Task links (2026-11-25-task-links-design.md): the original as {id, title} while the task is a closed duplicate
        // (status stays "closed", so a script reading status is unchanged), the ids of its duplicates, and the ids of the
        // tasks related to it either way.
        duplicateOf = tasks?.Board.DuplicateTarget(task).Map(o => (object?)new { id = o.Id, title = o.Title }).GetValueOrDefault(),
        duplicates = tasks?.Board.DuplicatesOf(task.Id).Select(t => t.Id).ToArray() ?? [],
        related = tasks?.Board.RelatedOf(task.Id).Select(t => t.Id).ToArray() ?? [],
    };

    // Markdown as a session reads it: image paths absolute, through the task service (as stored without one).
    string Expanded(string markdown) => tasks is { } service ? service.ExpandImages(markdown) : markdown;

    // --- subtasks -------------------------------------------------------------------------------------------------

    const string NoFamily = "This task has no parent, no subtasks and no linked tasks.";

    // The family text a session on this task reads (SessionBriefing.Family), with transcripts found by the app
    // (TaskSessionCommands.Transcript); none for a task with no parent and no subtasks.
    Maybe<string> FamilyOf(TaskService service, TaskItem task) =>
        service.Board.Family(task.Id).Map(f => SessionBriefing.Family(f, Transcript));

    // Whether a session is busy, from the latest `claude agents` list the app handed in; none without one.
    Maybe<bool> Busy(string sessionId) =>
        taskSessions is { } commands ? commands.Busy(sessionId) : Maybe<bool>.None;

    // The linked tasks' text (task links spec, SessionBriefing.Links); none for a task with no links.
    Maybe<string> LinksOf(TaskService service, TaskItem task) =>
        service.Board.Links(task.Id).Map(l => SessionBriefing.Links(l, Transcript));

    // Everything around a task a session reads: its family, then its links; none when it has neither. What `task family`
    // prints and the SessionStart briefing carries.
    Maybe<string> AroundOf(TaskService service, TaskItem task) =>
        new[] { FamilyOf(service, task), LinksOf(service, task) }.Where(m => m.HasValue).Select(m => m.Value).ToArray() is { Length: > 0 } parts
            ? string.Join("\n", parts)
            : Maybe<string>.None;

    Maybe<string> Transcript(TaskSession session) =>
        taskSessions is { } commands ? commands.Transcript(session) : Maybe<string>.None;

    // A task's JSON with its family text added as `family` (null for none): the `task join` reply, which is what a
    // session reads when it takes a task up (SessionStart ran before it joined, so the briefing could not say it).
    object WithFamily(TaskService service, TaskItem task)
    {
        var json = System.Text.Json.JsonSerializer.SerializeToNode(Describe(task), Json)!.AsObject();
        json["family"] = FamilyOf(service, task).Map(text => (System.Text.Json.Nodes.JsonNode?)text).GetValueOrDefault();
        // The linked tasks (task links spec; Petre: "when working on a task, claude should examine what's related to it").
        json["linked"] = LinksOf(service, task).Map(text => (System.Text.Json.Nodes.JsonNode?)text).GetValueOrDefault();
        return json;
    }

    // `task parent <id> <parent-id>|none`: both ids are looked up first; the board refuses a second level.
    ControlReply TaskParent(TaskService service, string id, string parent) =>
        (parent.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)
            ? Result.Success(Maybe<Guid>.None)
            : service.Board.Find(parent).Map(p => Maybe.From(p.Id)))
        .Match(p => TaskChange(service, id, t => service.SetParent(t.Id, p)), ControlReply.Fail);

    string? WorkspaceNameOf(Maybe<Guid> id) =>
        id.Bind(w => manager.State.Workspaces.TryFirst(x => x.Id == w)).Map(w => w.Name).GetValueOrDefault();

    // --- add -----------------------------------------------------------------------------------

    sealed record TaskAddOptions(string? Title, string? Workspace, string? Bad)
    {
        // --parent: the task the note is a subtask of (subtasks spec).
        public string? Parent { get; init; }

        public bool Important { get; init; }

        public bool Urgent { get; init; }
    }

    // One positional title, then --workspace, --important and --urgent at most once each, in any order (Ruling 37: the
    // flags a note is made with, the add box's two toggles). Any other option is refused by name (the timer build's
    // --in included), so a script written for that build fails loudly instead of losing its timer quietly.
    static TaskAddOptions ParseAddFrom(string[] args, TaskAddOptions o) => args switch
    {
        [] => o,
        ["--workspace", var w, .. var more] when o.Workspace is null => ParseAddFrom(more, o with { Workspace = w }),
        ["--important", .. var more] when !o.Important => ParseAddFrom(more, o with { Important = true }),
        ["--urgent", .. var more] when !o.Urgent => ParseAddFrom(more, o with { Urgent = true }),
        ["--parent", var p, .. var more] when o.Parent is null => ParseAddFrom(more, o with { Parent = p }),
        [var option, ..] when option.StartsWith("--", StringComparison.Ordinal) => o with { Bad = $"Unexpected {option}" },
        [var title, .. var more] when o.Title is null => ParseAddFrom(more, o with { Title = title }),
        [var extra, ..] => o with { Bad = $"Unexpected '{extra}': a title with spaces needs quotes" },
    };

    ControlReply TaskAdd(TaskService service, string[] args, string? from)
    {
        var o = ParseAddFrom(args, new TaskAddOptions(null, null, null));
        // Parse and look everything up first, so a refusal happens before the service is touched.
        var failure =
            o.Bad is { } bad ? bad
            : o.Title is null ? "A task needs a title"
            : null;
        if (failure is not null) return ControlReply.Fail(failure);

        // With --parent and no --workspace the note goes where its parent is, not to the caller's workspace (subtasks
        // spec, after the builder check): a subtask belongs with its parent. `--workspace none` still means none (final
        // review, finding 4), so the parent's workspace is looked up here rather than defaulted by the board.
        var parent = o.Parent is { } p ? service.Board.Find(p).Map(Maybe.From) : Result.Success(Maybe<TaskItem>.None);
        return parent
            .Bind(up => (up.HasValue && o.Workspace is null ? Result.Success(up.Value.WorkspaceId) : ResolveWorkspace(service, o.Workspace, from))
                .Bind(workspace => service.AddNote(o.Title!, workspace, QuadrantOf.Flags(o.Important, o.Urgent), up.Map(t => t.Id))))
            .Match(task => Success(Describe(task)), ControlReply.Fail);
    }

    // `none` is a free task, a name is looked up like every other command's, and no name asks the caller (only
    // `add` has a caller to ask; `move` always names its workspace). The caller is asked the way a session's own forms
    // ask (SessionWorkspace): its VS Code window, else where the latest task in its repo went (task f905b1a7). `add`
    // carries no session, so there is no own task to ask. Refused only when neither answers.
    Result<Maybe<Guid>> ResolveWorkspace(TaskService service, string? workspace, string? from) =>
        workspace is null
            ? SessionWorkspace(service, Folder(from), Maybe<string>.None).ToResult(NoWorkspaceForTask).Map(Maybe.From)
            : workspace.Trim().Equals("none", StringComparison.OrdinalIgnoreCase) ? Maybe<Guid>.None
            : WorkspaceNamed(workspace).Map(w => Maybe.From(w.Id));

    // --- the changes ---------------------------------------------------------------------------

    // Finds the task first, so an unknown or ambiguous id fails before anything is touched, then applies the
    // change and answers with the task read back from the board.
    ControlReply TaskChange(TaskService service, string id, Func<TaskItem, Result> change) =>
        service.Board.Find(id)
            .Bind(task => change(task).Map(() => service.Board.Tasks.First(t => t.Id == task.Id)))
            .Match(task => Success(Describe(task)), ControlReply.Fail);

    // `task comment-edit <comment-id> <markdown>`: finds the comment first, then Board.EditComment refuses one a session
    // wrote. The reply is the task the comment is on.
    ControlReply TaskCommentEdit(TaskService service, string comment, string markdown) =>
        service.Board.FindComment(comment)
            .Bind(c => service.EditComment(c.Id, markdown).Map(() => c))
            .Match(c => Success(Describe(service.Board.Tasks.First(t => t.Comments.Any(x => x.Id == c.Id)))), ControlReply.Fail);

    // `task goal <id> <goal-id>|none`, the twin of the More menu's "Goal". Both ids are looked up before anything
    // changes; the link goes through GoalService.LinkTask, the one place a task is linked, so a done or dropped goal is
    // refused there exactly as in the menu.
    ControlReply TaskGoal(TaskService service, string id, string goal) =>
        goals is not { } goalService ? ControlReply.Fail(GoalsUnavailable)
        : (goal.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)
            ? Result.Success(Maybe<Guid>.None)
            : goalService.Board.Find(goal).Map(g => Maybe.From(g.Id)))
        .Match(g => TaskChange(service, id, t => goalService.LinkTask(t.Id, g)), ControlReply.Fail);

    // The reply is the task as it was, since it is gone afterwards.
    ControlReply TaskDelete(TaskService service, string id) =>
        service.Board.Find(id)
            .Bind(task => service.Delete(task.Id).Map(() => task))
            .Match(task => Success(Describe(task)), ControlReply.Fail);

    // --- a task's Claude sessions (Task 9) ------------------------------------------------------------------------

    ControlReply WithSessions(Func<TaskSessionCommands, ControlReply> act) =>
        taskSessions is { } commands ? act(commands) : ControlReply.Fail("Claude sessions are not available in this copy of Deskbar");

    // The folder: the one given, or the one the task's candidates leave (ClaudeStart.Folders, as the menu offers them).
    // Several or none refuse before anything starts, naming what --folder could be.
    // A closed task is refused before anything else (final review, item 12): a session started on it could not even join
    // it (a closed task takes none of a session's changes), so `claude --bg` would run for nothing.
    //
    // --model names the model of this one session (design docs/superpowers/specs/2026-11-25-session-model-pick-design.md):
    // a name `claude` could misread is refused before anything starts, and without it the session runs on the model
    // `claude-model` holds. Either way the start names a model.
    ControlReply TaskStart(TaskService service, TaskSessionCommands commands, string id, Maybe<string> given, Maybe<string> model) =>
        service.Board.Find(id)
            .Ensure(task => task.IsOpen, ClosedTaskRefusal)
            .Bind(task => model.Match(name => ClaudeStart.Model(name).Map(Maybe.From), () => Maybe<string>.None)
                .Bind(named => given.Or(() => commands.Folders(task) is [var only] ? only : Maybe<string>.None)
                    .ToResult(FolderRefusal(commands.Folders(task)))
                    .Bind(folder => commands.Start(task, folder, named)))
                .Map(() => service.Board.Tasks.First(t => t.Id == task.Id)))
            .Match(task => Success(Describe(task)), ControlReply.Fail);

    /// <summary>Why `task open` refuses in a copy of Deskbar with no task windows (no openTaskWindow handed in).</summary>
    public const string TaskWindowUnavailable = "Task windows are not available in this copy of Deskbar";

    /// <summary>Why Claude is not started on a closed task, said by `task start` and by the menu (ClaudeTaskActions).</summary>
    public const string ClosedTaskRefusal = "The task is closed. Reopen it to start Claude on it.";

    static string FolderRefusal(IReadOnlyList<string> folders) =>
        folders.Count == 0
            ? "No folder is known for this task; pass --folder <path>"
            : $"Several folders could be meant: {string.Join("; ", folders)}. Pass --folder <path>";

    // The session by its full id or any unique start of it (its eight-character short id is what `claude agents`
    // shows), among the task's own sessions only.
    ControlReply TaskOpenSession(TaskService service, TaskSessionCommands commands, string id, string session) =>
        TaskChange(service, id, task => task.Sessions
            .Where(s => s.Id.StartsWith(session.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList() switch
            {
                [var one] when session.Trim().Length > 0 => commands.OpenSession(task, one.Id),
                [] or [_] => Result.Failure($"The task has no session {session}"),
                _ => Result.Failure($"{session} names several of the task's sessions; give more of its id"),
            });
}

/// <summary>
/// What `task go`, `task start` and `task open-session` ask of the app (Task 9; RemoteControl.Tasks.cs): go to a task
/// (its workspace, then its session's window), the folders a new session could start in (ClaudeStart.Folders over the
/// task's workspace), start a session in a folder, and open one of the task's sessions by its full id. The app answers
/// with ClaudeTaskActions; each returns at once and reports a later failure in the task's thread.
/// </summary>
public sealed record TaskSessionCommands(
    Func<TaskItem, Result> Go,
    Func<TaskItem, IReadOnlyList<string>> Folders,
    Func<TaskItem, string, Maybe<string>, Result> Start,
    Func<TaskItem, string, Result> OpenSession)
{
    /// <summary>
    /// Where a session's transcript file is, when the app finds one (ClaudeSessions.TranscriptOf), for the family text a
    /// session on a subtask reads (subtasks spec). An init property so every construction that predates it still
    /// compiles; the default finds none, and the family text then lists sessions without a transcript.
    /// </summary>
    public Func<TaskSession, Maybe<string>> Transcript { get; init; } = _ => Maybe<string>.None;

    /// <summary>
    /// A session's busy flag from the latest `claude agents` list, for `task overview` to tell working from idle (the
    /// manager session design). None when the session is not in the list. An init property for the same reason as
    /// Transcript; the default knows nothing, so every live session reads as idle.
    /// </summary>
    public Func<string, Maybe<bool>> Busy { get; init; } = _ => Maybe<bool>.None;

    /// <summary>
    /// The manager session's folder (ManagerSession.FolderIn), which tells a call from the manager by its `--from`. An init
    /// property for the same reason as Transcript; "" means no manager, and matches no caller.
    /// </summary>
    public string ManagerFolder { get; init; } = "";

    /// <summary>
    /// `task manager`, the bar's Manager button's twin (ManagerActions.Open in the app): opens today's manager session,
    /// attaching to it, resuming it or starting it. Answers at once; a later failure is told in a message. An init
    /// property for the same reason as Transcript; the default refuses.
    /// </summary>
    public Func<Result> Manager { get; init; } = () => Result.Failure("The manager is not available");

    /// <summary>
    /// `task close <id> --stop-claude` (task 37cd7081): closes a task Claude is still working on and stops its background
    /// sessions, as the ticks' "Stop Claude and close" does (ClaudeTaskActions.StopAndClose). An init property for the same
    /// reason as Transcript; the default refuses, so a copy without it never closes under a running session.
    /// </summary>
    public Func<TaskItem, Result> StopAndClose { get; init; } = _ => Result.Failure("Claude sessions cannot be stopped in this copy of Deskbar");
}
