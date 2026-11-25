using System.Text.Json;
using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;
using Deskbar.Core.Goals;
using Deskbar.Core.Tasks;

namespace Deskbar.Core.Control;

// Commands from OUTSIDE the app: another process asking the running Deskbar to do what its menus do.
//
// Petre: "i want you to add ability to create workspaces and move windows into them programatically, so
// that when i have a claude session start working in a new worktree, i can tell it to move the windows
// to a new workspace which it creates." Then: "expose all the commands that are available via context
// menu", "moving existing workspaces, renaming, etc."
//
// Every command calls the SAME WorkspaceManager method its menu item calls (ControlUsage names the menu
// item beside each one), so a remote rename or reorder obeys exactly the rules a click does, and the bar
// redraws from the same StateChanged. Nothing here keeps state of its own.
//
// Split by menu into partial files: workspaces (RemoteControl.Workspaces.cs), groups
// (RemoteControl.Groups.cs) and windows (RemoteControl.Windows.cs). This file holds the dispatch and the
// lookups they share.
//
// Runs on the app's UI thread, always: the manager is not thread-safe and its COM calls are STA. The
// pipe server is responsible for getting here through the dispatcher (see ControlPipeServer).
//
// Output is JSON for every success, because the expected caller is an agent that reads it back, and a
// plain sentence for every failure, because the expected reader of a failure is whoever is debugging.
//
// `launch` is how `start` starts a program, handed in by the composition root (ProgramStarter) because
// starting a process is the Windows side's business. It answers the new process's id, or null when the shell
// handed the request to something already running. Left null, as the tests that do not exercise `start`
// leave it, `start` refuses.
//
// `dump` writes the debug dump the tray's "Save debug dump" writes and answers the file's path. It reads the
// bar and the screens, which only the app can see, so the app hands it in; left null, `dump` refuses.
//
// `defaultBrowser` answers the default browser's exe for `open` (DefaultBrowser on the Windows side). Left null, `open`
// still works and always opens a link as usual.
//
// `identity` answers `version`: the running build's version, its exe path and whether it runs from the
// Microsoft Store package (docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md). Whether
// the process is packaged is the Windows side's business, so the app hands it in; left null, `version` refuses.
//
// `showFind` opens quick find with the given text already typed (`find`). The box is a window of the app's, so
// the app hands the opener in; left null, `find` refuses.
//
// `setStatsDetached` detaches the stats line into its window of its own, or puts it back (`stats detach|attach`).
// The window is the app's, so the app hands in how, as for `bar minimize`; left null, `stats` refuses.
//
// `setArranging` turns the bar's arrange mode on or off (`arrange on|off`, FloatingBar.Arrange.cs). The mode is the
// bar's, so the app hands in how, as for `stats`; left null, `arrange` refuses.
//
// `isPro` answers whether this build is Deskbar Pro, for `tile` only (tiling is a Pro feature). Entitlement
// is the Windows side's business, so the app hands it in; left null it means NOT Pro, so a caller that
// passes nothing never gets tiling.
//
// `tasks` is the task service for `task` (RemoteControl.Tasks.cs). Left null, every `task` command refuses.
//
// `setTileLocked` locks or unlocks a tile (`lock-tile <hwnd> on|off`, the twin of the icon menu's "Don't split this
// tile"; Tiler.Lock.cs). The lock is the Tiler's, which lives in the app, so the app hands in how; left null,
// `lock-tile` refuses.
//
// `setTasksWindow` shows or hides the tasks panel (`tasks-window show|hide`, the twin of the bar's "All tasks";
// TasksBoard in the app). The panel is the app's, so the app hands in how; left null, `tasks-window` refuses.
//
// `longPath` expands an 8.3 short path (LongPath.Of on the Windows side), so a caller's folder is matched in its long
// form (FolderPath). Left null, folders are taken as given.
// `goals` is the goal service for `goal` and `task goal` (RemoteControl.Goals.cs; the goals planner,
// docs/superpowers/specs/2026-11-22-goals-planner-design.md). Left null, both refuse with "Goals are not available".
//
// `taskSessions` is how `task go`, `task start` and `task open-session` reach a task's Claude sessions (Task 9 of
// docs/superpowers/specs/2026-11-23-tasks-open-claude-sessions-design.md): going to its workspace and its session's
// window, starting `claude --bg` in a folder and opening one session. Windows, terminals and `claude` are the app's
// business, so the app hands them in (ClaudeTaskActions); left null, the three refuse.
//
// `openTaskWindow` opens a task in its own window (`task open <id>`, the twin of the line menus' "Open in window"; task
// dca4ebeb, TasksBoard.OpenWindow in the app). Windows are the app's business; left null, `task open` refuses.
public sealed partial class RemoteControl(WorkspaceManager manager, Func<bool, Result>? setBarVisible = null, Func<ProgramLaunch, Result<int?>>? launch = null,
    Func<Result<string>>? dump = null, Func<Maybe<string>>? defaultBrowser = null,
    Func<(string Version, string? Exe, bool Packaged)>? identity = null,
    Func<string, Result>? showFind = null,
    Func<bool, Result>? setStatsDetached = null,
    Func<bool, Result>? setArranging = null,
    Func<bool>? isPro = null,
    TaskService? tasks = null,
    Func<WindowHandle, bool, Result>? setTileLocked = null,
    Func<bool, Result>? setTasksWindow = null,
    Func<string, string>? longPath = null,
    GoalService? goals = null,
    TaskSessionCommands? taskSessions = null,
    CommentInbox? inbox = null,
    Func<TaskItem, Result>? openTaskWindow = null)
{
    // `longPath` turns an 8.3 short path into its long form (GetLongPathNameW, LongPath on the Windows side), for
    // FolderPath. Left null, a path is taken as it is, which is all a test that names long paths needs.
    string LongPath(string path) => longPath?.Invoke(path) ?? path;

    // A caller's folder in the one form it is matched and stored in (FolderPath), or null for none.
    string? Folder(string? raw) => FolderPath.Normalise(raw, LongPath) is { Length: > 0 } folder ? folder : null;

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public ControlReply Execute(IReadOnlyList<string> args) =>
        args.Count == 0
            ? ControlReply.Fail(ControlUsage.Text)
            : Dispatch(args[0].ToLowerInvariant(), args.Skip(1).ToArray());

    // One arm per command, matched on the verb AND the shape of its arguments, so a command given the
    // wrong number of them falls through to the arm that prints that command's own usage line.
    ControlReply Dispatch(string verb, string[] rest) => (verb, rest) switch
    {
        ("workspaces", []) => Workspaces(),
        ("windows", []) => Windows(),
        ("desktops", []) => Desktops(),
        ("version", []) => Version(),
        ("find", _) => Find(string.Join(' ', rest)),

        ("create", _) => ControlArguments.ParseCreate(rest).Match(Create, ControlReply.Fail),
        ("move", _) => ControlArguments.ParseMove(rest).Match(Move, ControlReply.Fail),
        ("start", _) => ControlArguments.ParseStart(rest).Match(Start, ControlReply.Fail),
        ("open", _) => ControlArguments.ParseOpen(rest).Match(Open, ControlReply.Fail),
        ("attention", []) => Attention(null),
        ("attention", ["--from", var folder]) => Attention(folder),
        // Claude Code's hooks (RemoteControl.ClaudeHook.cs): the hook's JSON and the session's folder.
        (ControlArguments.ClaudeHookVerb, _) => ClaudeHook(rest),
        // A session's comment waiter registering (RemoteControl.ClaudeHook.cs, ClaudeWait).
        (ControlArguments.ClaudeWaitVerb, _) => ClaudeWait(rest),
        ("task", _) => Task(rest),
        ("tasks-window", [var state]) => TasksWindow(state),
        ("goal", _) => Goal(rest),
        // A bare `tasks-window` (or one with too many words) gets its own usage line, not the whole usage.
        ("tasks-window", _) => ControlReply.Fail(ControlUsage.LineFor("tasks-window")),

        ("rename", [var workspace, var name]) => Rename(workspace, name),
        ("reorder", [var workspace, var where]) => Reorder(workspace, where),
        ("fold", [var workspace]) => SetRowFolded(workspace, true),
        ("unfold", [var workspace]) => SetRowFolded(workspace, false),
        ("pin-workspace", [var workspace]) => SetRowPinned(workspace, true),
        ("unpin-workspace", [var workspace]) => SetRowPinned(workspace, false),
        ("jump", [var workspace]) => Jump(workspace),
        ("tile", [var workspace]) => SetRowTiled(workspace, true),
        ("untile", [var workspace]) => SetRowTiled(workspace, false),
        ("even", [var workspace]) => EvenOutRow(workspace),
        ("reset-grid", [var workspace]) => ResetGridRow(workspace),
        // Only the literal words on and off match; anything else falls through to the "Wrong arguments" reply with the usage line.
        ("keep-space", [var workspace, "on"]) => SetRowKeepsSpace(workspace, true),
        ("keep-space", [var workspace, "off"]) => SetRowKeepsSpace(workspace, false),
        ("color", [var workspace, var colour]) => Colour(workspace, colour),
        ("delete", [var workspace]) => Delete(workspace, closeWindows: false),
        ("delete", [var workspace, "--close-windows"]) => Delete(workspace, closeWindows: true),
        ("switch", [var workspace]) => Switch(workspace),

        ("group-create", [var name, var first]) => GroupCreate(name, first),
        ("group-join", [var workspace, var group]) => GroupJoin(workspace, group),
        ("group-leave", [var workspace]) => GroupLeave(workspace),
        ("group-rename", [var group, var name]) => GroupRename(group, name),
        ("group-color", [var group, var colour]) => GroupColour(group, colour),
        ("group-fold", [var group]) => GroupFold(group, true),
        ("group-unfold", [var group]) => GroupFold(group, false),
        ("group-reorder", [var group, var where]) => GroupReorder(group, where),
        ("ungroup", [var group]) => Ungroup(group),

        ("name-desktop", [var desktop, var name]) => NameDesktop(desktop, name),

        ("rename-window", [var hwnd, var name]) => RenameWindow(hwnd, name),
        ("rename-app", [var hwnd, var name]) => RenameApp(hwnd, name),
        ("rename-pattern", [var pattern, var name]) => RenamePattern(pattern, name),
        ("name-by-folder", [var hwnd, var onOff]) => NameByFolder(hwnd, onOff),
        ("remember-workspace", [var hwnd, var onOff]) => RememberWorkspace(hwnd, onOff),
        ("split-last", [var hwnd, var onOff]) => SplitLast(hwnd, onOff),
        ("restore-title", [var hwnd]) => RestoreTitle(hwnd),
        ("lock-tile", [var hwnd, var onOff]) => LockTile(hwnd, onOff),
        ("pin", [var hwnd]) => Pin(hwnd, pin: true),
        ("unpin", [var hwnd]) => Pin(hwnd, pin: false),
        ("close-window", [var hwnd]) => CloseWindow(hwnd),
        ("force-close", [var hwnd]) => ForceClose(hwnd),
        ("close-all", [var hwnd]) => CloseAll(hwnd),
        ("restart-app", [var hwnd]) => RestartApp(hwnd),

        ("bar", [var state]) => Bar(state),
        ("stats", [var state]) => Stats(state),
        ("arrange", [var state]) => Arrange(state),
        ("window-lines", [var mode]) => WindowLines(mode),
        ("window-list", [var place]) => WindowList(place),
        ("collapsed-click", [var mode]) => CollapsedClick(mode),
        ("full-screen", [var mode]) => FullScreen(mode),
        ("claude-skill", [var onOff]) => ClaudeSkill(onOff),
        ("claude-tasks", [var onOff]) => ClaudeTasks(onOff),
        ("claude-resume", [var onOff]) => ClaudeResume(onOff),
        ("focus-ring", [var onOff]) => FocusRing(onOff),
        ("see-through", [var onOff]) => SeeThrough(onOff),
        ("previous-workspace", [var onOff]) => PreviousWorkspace(onOff),

        // The file is the answer, not a window: whoever asked through the pipe reads it back from the path.
        ("dump", []) => dump is null
            ? ControlReply.Fail("A debug dump cannot be written from here.")
            : dump().Match(file => Success(new { file }), ControlReply.Fail),

        ("help" or "--help" or "-h" or "/?", _) => new ControlReply(ControlReply.Ok, ControlUsage.Text),
        _ when ControlUsage.IsVerb(verb) => ControlReply.Fail($"Wrong arguments for {verb}.\n{ControlUsage.LineFor(verb)}"),
        _ => ControlReply.Fail($"unknown command '{verb}'.\n\n{ControlUsage.Text}"),
    };

    // --- lookups shared by every command ------------------------------------------------------

    // Names are compared the way the manager compares them for collisions (trimmed, ignoring case), so
    // "find" and "already exists" can never disagree. An id works too, for a caller holding one from a
    // listing, since two workspaces can be renamed into each other's old names between calls.
    Maybe<Workspace> FindWorkspace(string nameOrId) =>
        manager.State.Workspaces.TryFirst(w => Matches(w.Name, w.Id, nameOrId));

    Result<Workspace> WorkspaceNamed(string nameOrId) =>
        FindWorkspace(nameOrId).ToResult($"There is no workspace named '{nameOrId}'. `workspaces` lists them.");

    Result<Group> GroupNamed(string nameOrId) =>
        manager.State.Groups.TryFirst(g => Matches(g.Name, g.Id, nameOrId)).ToResult(
            manager.State.Groups.Count == 0
                ? $"There is no group named '{nameOrId}'; there are no groups yet. group-create makes one."
                : $"There is no group named '{nameOrId}'. The groups are: {string.Join(", ", manager.State.Groups.Select(g => g.Name))}.");

    // Only windows the app knows: a handle it has never seen is either closed or not a taskbar window,
    // and nothing the menus do applies to either.
    Result<WindowInfo> WindowAt(string raw) =>
        ControlArguments.Handle(raw).Bind(handle => manager.KnownWindows.TryFirst(w => w.Handle == handle)
            .ToResult($"No open window has handle {raw}. `windows` lists them."));

    static bool Matches(string name, Guid id, string nameOrId) =>
        name.Trim().Equals(nameOrId.Trim(), StringComparison.OrdinalIgnoreCase)
        || (Guid.TryParse(nameOrId, out var asId) && asId == id);

    // --- what a reply says about things --------------------------------------------------------

    // A workspace as every command reports it, read back from the state AFTER the change, so the reply
    // is where it actually ended up rather than where it was asked to go.
    object Describe(Guid workspaceId)
    {
        var workspace = manager.State.Workspaces.First(w => w.Id == workspaceId);
        var group = manager.State.GroupOf(workspaceId);
        return new
        {
            name = workspace.Name,
            id = workspace.Id,
            group = group?.Name,
            row = manager.RowOf(workspaceId).GetValueOrDefault(),
            // Position inside the group's box, from 1, for a grouped workspace: the number `reorder`
            // takes for it.
            groupPosition = group is null ? (int?)null : manager.State.MembersOf(group.Id).ToList().FindIndex(m => m.Id == workspaceId) + 1,
            color = workspace.Color,
            folded = workspace.Folded,
            pinned = workspace.Pinned,
            current = manager.CurrentWorkspaceId == workspaceId,
            // Tiling's two per-workspace switches, so `keep-space` (and every other command) replies with the state it
            // left them in, as the rest of this description does (final review, M5).
            tiled = workspace.Tiled,
            keepsSpace = workspace.TileKeepsSpace,
        };
    }

    object DescribeGroup(Guid groupId)
    {
        var group = manager.State.Groups.First(g => g.Id == groupId);
        return new
        {
            name = group.Name,
            id = group.Id,
            color = group.Color,
            folded = group.Folded,
            members = manager.State.MembersOf(groupId).Select(m => m.Name),
            row = manager.State.MembersOf(groupId).Select(m => manager.RowOf(m.Id).GetValueOrDefault()).FirstOrDefault(),
        };
    }

    static string Hex(WindowHandle handle) => $"0x{(long)handle.Value:X}";

    static ControlReply Success(object value) => new(ControlReply.Ok, Serialize(value));

    static ControlReply Reply(Result result, Func<object> describe) =>
        result.IsSuccess ? Success(describe()) : ControlReply.Fail(result.Error);

    // The running build as JSON. The CI smoke step reads "packaged" from here to prove the installed
    // package really runs with package identity.
    ControlReply Version() =>
        identity?.Invoke() is { } build
            ? Success(new { version = build.Version, exe = build.Exe, packaged = build.Packaged })
            : ControlReply.Fail("version is not available in this build");

    // Quick find, opened from outside: every word after `find` becomes the box's text, joined by single spaces,
    // which is exactly how the box splits it again (FindQuery).
    ControlReply Find(string text) =>
        showFind is null
            ? ControlReply.Fail("This copy of Deskbar cannot open quick find.")
            : Reply(showFind(text), () => new { opened = "quick find", text });

    static string Serialize(object value) => JsonSerializer.Serialize(value, Json);
}
