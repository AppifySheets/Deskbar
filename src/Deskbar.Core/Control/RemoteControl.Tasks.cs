using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;

namespace Deskbar.Core.Control;

// THE `task` COMMANDS: add, time, finish and list tasks from a script or a Claude Code session. Design:
// docs/superpowers/specs/2026-11-19-tasks-in-the-bar-design.md ("Remote control").
//
//   task list
//   task add <title> [--workspace <name>|none] [--in 1m|5m|10m|30m]
//   task timer <id> (--in 1m|5m|10m|30m | --clear)
//   task done <id>
//   task rename <id> <title>
//   task delete <id>
//
// Every verb calls the same TaskService method the task window calls, so a task changed from a script obeys the
// rules a click does and the bar redraws from the service's Changed. An id is the task's Guid or any unique prefix
// of at least four characters (TaskBoard.Find); `list` prints them in full.
//
// `task` is in ControlArguments.AboutTheCaller, so the client appends `--from <folder>` to every call. It is
// stripped first. Only `add` uses it: without --workspace the task parks on the workspace of the caller's editor
// window (CallerOf, shared with `open` and `attention`). A caller with no workspace and no --workspace is refused,
// because a task parked on the wrong workspace is worse than one that was not parked.
//
// Every failure is one line and changes nothing: parsing and lookups finish before the first service call.
public sealed partial class RemoteControl
{
    const string NoWorkspaceForTask = "Not run from a workspace's folder; pass --workspace <name> or --workspace none";

    ControlReply Task(string[] rest)
    {
        if (tasks is not { } service) return ControlReply.Fail("Tasks are not available");
        var (args, from) = SplitFrom(rest);
        return args switch
        {
            ["list"] => TaskList(service),
            ["add", .. var more] => TaskAdd(service, more, from),
            ["timer", var id, .. var more] => TaskTimer(service, id, more),
            ["done", var id] => TaskChange(service, id, t => service.Done(t.Id)),
            ["rename", var id, var title] => TaskChange(service, id, t => service.Rename(t.Id, title)),
            ["delete", var id] => TaskDelete(service, id),
            _ => ControlReply.Fail($"Wrong arguments for task.\n{TaskUsage()}"),
        };
    }

    // The client's `--from <folder>` is always last (WithCallerFolder appends it), so it is taken off the end.
    static (string[] Args, string? From) SplitFrom(string[] rest) =>
        rest is [.., "--from", var folder] ? (rest[..^2], folder) : (rest, null);

    // Every line of the Tasks section, as `deskbar.cmd task <form>`.
    static string TaskUsage() => string.Join("\n", ControlUsage.Sections.SelectMany(s => s.Commands)
        .Where(c => c.Verb == "task").Select(c => $"usage: deskbar.cmd task {c.Arguments}"));

    // --- list ----------------------------------------------------------------------------------

    // "Today" is the local date of the service's clock, so a test that moves the clock controls it.
    ControlReply TaskList(TaskService service) =>
        Success(service.Entries(manager.AskingWorkspaces, DateOnly.FromDateTime(service.Now.ToLocalTime().DateTime))
            .Select(e => Describe(e)).ToArray());

    object Describe(TaskEntry entry) => new
    {
        kind = entry.Kind == TaskEntryKind.Ask ? "ask" : "task",
        id = entry.Task.Map(t => (Guid?)t.Id).GetValueOrDefault(),
        title = entry.Task.Map(t => t.Title).GetValueOrDefault(),
        workspace = WorkspaceNameOf(entry.WorkspaceId),
        state = entry.State.ToString().ToLowerInvariant(),
        endsAt = entry.Task.Bind(t => t.Timer).Map(t => (DateTimeOffset?)t.Ends).GetValueOrDefault(),
        doneAt = entry.Task.Bind(t => t.DoneAt).Map(d => (DateTimeOffset?)d).GetValueOrDefault(),
    };

    // A task as the other verbs report it: the same shape as a `list` entry.
    object Describe(TaskItem task, DateTimeOffset now) =>
        Describe(new TaskEntry(TaskEntryKind.Task, task, task.WorkspaceId, task.StateAt(now)));

    string? WorkspaceNameOf(Maybe<Guid> id) =>
        id.Bind(w => manager.State.Workspaces.TryFirst(x => x.Id == w)).Map(w => w.Name).GetValueOrDefault();

    // --- add -----------------------------------------------------------------------------------

    sealed record TaskAddOptions(string? Title, string? Workspace, string? Length, string? Bad);

    // One positional title, then --workspace and --in in any order, each at most once.
    static TaskAddOptions ParseAddFrom(string[] args, TaskAddOptions o) => args switch
    {
        [] => o,
        ["--workspace", var w, .. var more] when o.Workspace is null => ParseAddFrom(more, o with { Workspace = w }),
        ["--in", var l, .. var more] when o.Length is null => ParseAddFrom(more, o with { Length = l }),
        [var option, ..] when option.StartsWith("--", StringComparison.Ordinal) => o with { Bad = $"Unexpected {option}" },
        [var title, .. var more] when o.Title is null => ParseAddFrom(more, o with { Title = title }),
        [var extra, ..] => o with { Bad = $"Unexpected '{extra}': a title with spaces needs quotes" },
    };

    ControlReply TaskAdd(TaskService service, string[] args, string? from)
    {
        var o = ParseAddFrom(args, new TaskAddOptions(null, null, null, null));
        // Parse and look everything up first, so a refusal happens before the service is touched.
        var failure =
            o.Bad is { } bad ? bad
            : o.Title is null ? "A task needs a title"
            : null;
        if (failure is not null) return ControlReply.Fail(failure);

        var workspace = ResolveWorkspace(o.Workspace, from);
        var length = ResolveLength(o.Length);
        return Result.FirstFailureOrSuccess(workspace, length)
            .Bind(() => service.Park(o.Title!, workspace.Value, length.Value))
            .Match(task => Success(Describe(task, service.Now)), ControlReply.Fail);
    }

    // `none` is a free task, a name is looked up like every other command's, and no --workspace asks the caller.
    Result<Maybe<Guid>> ResolveWorkspace(string? workspace, string? from) =>
        workspace is null
            ? CallerOf(from).Workspace is { } caller ? Maybe.From(caller.Id) : Result.Failure<Maybe<Guid>>(NoWorkspaceForTask)
            : workspace.Trim().Equals("none", StringComparison.OrdinalIgnoreCase) ? Maybe<Guid>.None
            : WorkspaceNamed(workspace).Map(w => Maybe.From(w.Id));

    static Result<Maybe<TimeSpan>> ResolveLength(string? length) =>
        length is null ? Maybe<TimeSpan>.None : TaskPresets.Parse(length).Map(l => Maybe.From(l));

    // --- timer, done, rename, delete -------------------------------------------------------------

    ControlReply TaskTimer(TaskService service, string id, string[] more) =>
        (more switch
        {
            ["--clear"] => Result.Success<Func<TaskItem, Result>>(t => service.ClearTimer(t.Id)),
            ["--in", var length] => TaskPresets.Parse(length).Map<TimeSpan, Func<TaskItem, Result>>(l => t => service.SetTimer(t.Id, l)),
            _ => Result.Failure<Func<TaskItem, Result>>($"Wrong arguments for task.\n{TaskUsage()}"),
        }).Match(change => TaskChange(service, id, change), ControlReply.Fail);

    // Finds the task first, so an unknown or ambiguous id fails before anything is touched, then applies the
    // change and answers with the task read back from the board.
    ControlReply TaskChange(TaskService service, string id, Func<TaskItem, Result> change) =>
        service.Board.Find(id)
            .Bind(task => change(task).Map(() => service.Board.Tasks.First(t => t.Id == task.Id)))
            .Match(task => Success(Describe(task, service.Now)), ControlReply.Fail);

    // The reply is the task as it was, since it is gone afterwards.
    ControlReply TaskDelete(TaskService service, string id) =>
        service.Board.Find(id)
            .Bind(task => service.Delete(task.Id).Map(() => task))
            .Match(task => Success(Describe(task, service.Now)), ControlReply.Fail);
}
