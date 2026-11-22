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
//   task add <title> [--workspace <name>|none]
//   task close <id>
//   task reopen <id>
//   task status <id> <backlog|in-progress|waiting|done>
//   task rename <id> <title>
//   task move <id> <workspace>|none
//   task comment <id> <markdown>
//   task delete <id>
//   task flag <id> important|urgent on|off
//   task order <id> top|end|<n>
//
// `flag` and `order` (Task 12, Rulings 25 and 28) are the twins of the toggles on a line and of dragging it. The flags
// name Covey's quadrant (TaskOrder.cs); a flag that changes the quadrant puts the task on top of its new one, as the
// toggle does. `order` moves a task within its own quadrant, n counting from 1 and clamped to the quadrant's length
// (a drag across quadrants is a flag change plus an order, so a script does it in two steps). A task waiting on you
// is refused by `order`: it is pinned above the quadrants until answered (TaskBoard.Place says so). `list` prints each
// task's flags, its quadrant ("I" to "IV") and its 1-based `order` in it, null while it waits on you.
//
// The timer build's `task timer` and `task add --in` are gone with the timers. The forms a Claude session uses for
// its own task (join, create, status line, ask, done) come with the hooks, in a later change.
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
// window (CallerOf, shared with `open` and `attention`). A caller with no workspace and no --workspace is refused,
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
        var (args, from) = SplitFrom(rest);
        return args switch
        {
            ["list"] => Success(service.Board.Pile().Select(Describe).ToArray()),
            // The closed tasks, which every other view leaves out, so one closed by mistake can be found and reopened.
            // The latest closed first: closing stamps UpdatedAt, and nothing but Petre's own edits moves it after that.
            ["list", "--closed"] => Success(service.Board.Tasks.Where(t => t.Status == TaskStatus.Closed)
                .OrderByDescending(t => t.UpdatedAt).Select(Describe).ToArray()),
            ["add", .. var more] => TaskAdd(service, more, from),
            ["close", var id] => TaskChange(service, id, t => service.Close(t.Id)),
            ["reopen", var id] => TaskChange(service, id, t => service.Reopen(t.Id)),
            ["status", var id, var status] => ParseStatus(status)
                .Match(s => TaskChange(service, id, t => service.SetStatus(t.Id, s)), ControlReply.Fail),
            ["rename", var id, var title] => TaskChange(service, id, t => service.Rename(t.Id, title)),
            ["move", var id, var workspace] => ResolveWorkspace(workspace, null)
                .Match(w => TaskChange(service, id, t => service.MoveTo(t.Id, w)), ControlReply.Fail),
            ["comment", var id, var markdown] => TaskChange(service, id, t => service.Comment(t.Id, Maybe<string>.None, markdown)),
            ["delete", var id] => TaskDelete(service, id),
            ["flag", var id, var flag, var state] => ParseFlag(flag, state)
                .Match(f => TaskChange(service, id, t => service.SetFlag(t.Id, f.Flag, f.On)), ControlReply.Fail),
            ["order", var id, var place] => ParsePlace(place)
                .Match(p => TaskChange(service, id, t => service.Place(t.Id, t.Quadrant, p)), ControlReply.Fail),
            _ => ControlReply.Fail($"Wrong arguments for task.\n{TaskUsage()}"),
        };
    }

    // `tasks-window show|hide`: the tasks window, opened as the bar's "+N more" opens it (clear of the bar), or hidden.
    // `tasks-window list|matrix`: the window's view switch (Task 12, Ruling 28), a remembered setting
    // (AppState.TasksMatrix), so it needs no window to set. Only the four literal words; anything else is the usage
    // line and asks the app nothing.
    ControlReply TasksWindow(string state) => state.ToLowerInvariant() switch
    {
        "list" => Reply(manager.SetTasksMatrix(false), () => new { matrix = false }),
        "matrix" => Reply(manager.SetTasksMatrix(true), () => new { matrix = true }),
        "show" or "hide" when setTasksWindow is null => ControlReply.Fail("This copy of Deskbar has no tasks window."),
        "show" => Reply(setTasksWindow!(true), () => new { shown = true }),
        "hide" => Reply(setTasksWindow!(false), () => new { shown = false }),
        _ => ControlReply.Fail(ControlUsage.LineFor("tasks-window")),
    };

    // The client's `--from <folder>` is always last (WithCallerFolder appends it), so it is taken off the end.
    static (string[] Args, string? From) SplitFrom(string[] rest) =>
        rest is [.., "--from", var folder] ? (rest[..^2], folder) : (rest, null);

    // Every line of the Tasks section, as `deskbar.cmd task <form>`.
    static string TaskUsage() => string.Join("\n", ControlUsage.Sections.SelectMany(s => s.Commands)
        .Where(c => c.Verb == "task").Select(c => $"usage: deskbar.cmd task {c.Arguments}"));

    static string StatusName(TaskStatus status) => StatusNames.First(n => n.Status == status).Name;

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
    // it, `sessions` who did (live or ended), `comments` the thread's length (the thread itself is the window's).
    // `important`, `urgent`, `quadrant` and `order` are Task 12's (see the header). Describe reads the order off the
    // live board, so every verb's reply says where the task is now.
    object Describe(TaskItem task) => new
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
        quadrant = task.Quadrant.Numeral(),
        order = tasks is { } service && task.IsOpen ? service.Board.PositionOf(task.Id).Map(n => (int?)n).GetValueOrDefault() : null,
    };

    string? WorkspaceNameOf(Maybe<Guid> id) =>
        id.Bind(w => manager.State.Workspaces.TryFirst(x => x.Id == w)).Map(w => w.Name).GetValueOrDefault();

    // --- add -----------------------------------------------------------------------------------

    sealed record TaskAddOptions(string? Title, string? Workspace, string? Bad);

    // One positional title, then --workspace at most once. Any other option is refused by name (the timer build's
    // --in included), so a script written for that build fails loudly instead of losing its timer quietly.
    static TaskAddOptions ParseAddFrom(string[] args, TaskAddOptions o) => args switch
    {
        [] => o,
        ["--workspace", var w, .. var more] when o.Workspace is null => ParseAddFrom(more, o with { Workspace = w }),
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

        return ResolveWorkspace(o.Workspace, from)
            .Bind(workspace => service.AddNote(o.Title!, workspace))
            .Match(task => Success(Describe(task)), ControlReply.Fail);
    }

    // `none` is a free task, a name is looked up like every other command's, and no name asks the caller (only
    // `add` has a caller to ask; `move` always names its workspace).
    Result<Maybe<Guid>> ResolveWorkspace(string? workspace, string? from) =>
        workspace is null
            ? CallerOf(from).Workspace is { } caller ? Maybe.From(caller.Id) : Result.Failure<Maybe<Guid>>(NoWorkspaceForTask)
            : workspace.Trim().Equals("none", StringComparison.OrdinalIgnoreCase) ? Maybe<Guid>.None
            : WorkspaceNamed(workspace).Map(w => Maybe.From(w.Id));

    // --- the changes ---------------------------------------------------------------------------

    // Finds the task first, so an unknown or ambiguous id fails before anything is touched, then applies the
    // change and answers with the task read back from the board.
    ControlReply TaskChange(TaskService service, string id, Func<TaskItem, Result> change) =>
        service.Board.Find(id)
            .Bind(task => change(task).Map(() => service.Board.Tasks.First(t => t.Id == task.Id)))
            .Match(task => Success(Describe(task)), ControlReply.Fail);

    // The reply is the task as it was, since it is gone afterwards.
    ControlReply TaskDelete(TaskService service, string id) =>
        service.Board.Find(id)
            .Bind(task => service.Delete(task.Id).Map(() => task))
            .Match(task => Success(Describe(task)), ControlReply.Fail);
}
