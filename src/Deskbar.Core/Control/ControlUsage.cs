namespace Deskbar.Core.Control;

// The one description of every remote-control command. `help` prints it, a command given the wrong
// arguments prints its own line from it, and the README points here rather than repeating it, so what
// the running app says it accepts and what it actually accepts cannot drift apart.
//
// Grouped the way the bar's menus are, because "expose all the commands that are available via context
// menu" (Petre) is the brief: each section names the menu it mirrors.
public static class ControlUsage
{
    public sealed record Command(string Verb, string Arguments, string Description);

    public sealed record Section(string Title, IReadOnlyList<Command> Commands);

    public static readonly IReadOnlyList<Section> Sections =
    [
        new("Listing", [
            new("workspaces", "", "every workspace as JSON: name, group, row, colour, folded, pinned, current"),
            new("windows", "", "every window as JSON: hwnd, title, originalTitle, process, workspace"),
            new("desktops", "", "every virtual desktop as JSON, including unnamed ones"),
            new("version", "", "the running build as JSON: version, exe, packaged"),
        ]),
        new("Quick find", [
            new("find", "[text...]",
                "open quick find, as its shortcut does, with the text already typed: every window, browser tab and\n"
                + "      VS Code tab on every workspace"),
        ]),
        new("Creating, and moving windows", [
            new("create", "<name> [placement]", "create a workspace; already existing is not an error"),
            new("move", "<workspace> [--create [placement]] [--no-follow] (--title <text> | --hwnd <handle>)...",
                "move windows; every selector must match or nothing moves (exit 3). Moving the window you are\n"
                + "      in takes you along, as a drag does; --no-follow leaves you where you are"),
            new("start", "<workspace> [--create [placement]] [--cwd <folder>] -- <program> [arguments...]",
                "start a program and put its window in that workspace as it opens, without taking you along.\n"
                + "      Its windows for the next 30 s go there, and the first new window of the same exe (a browser or\n"
                + "      editor that hands the request to a copy already running). Deskbar starts nothing on its own"),
            new("open", "<link> [--from <folder>]",
                "open a link where the caller works: when the VS Code window whose folder holds --from (the caller's\n"
                + "      own folder if not given) is in a background workspace, a new default-browser window opens\n"
                + "      there, without taking you along; otherwise the link opens as usual. The reply says which"),
            new("attention", "[--from <folder>]",
                "ask for the user: the workspace of the VS Code window whose folder holds --from (the caller's own\n"
                + "      folder if not given) blinks on the bar and that window's icon glows, until the user goes there.\n"
                + "      Nothing happens when the user is already on that workspace"),
            new("claude-hook", "[--entry <value>] [--attended <value>] [--job <value>] [--from <folder>]",
                "what Claude Code's hooks run (claude-hook.cmd for Notification and Stop, claude-task-hook.cmd for\n"
                + "      SessionStart, UserPromptSubmit and SessionEnd), with the hook's JSON on stdin: Notification and\n"
                + "      Stop ask for the user as attention does, and Stop asks a session whose task is still in progress,\n"
                + "      with no background work, to run task ask when the next move is the user's (once per turn).\n"
                + "      Prints only what the session should read, and exits 0\n"
                + "      even when Deskbar is not running; JSON that is not a hook's exits 1"),
            new("claude-wait", "[--entry <value>] [--attended <value>] [--job <value>] [--from <folder>]",
                "what claude-wait-hook.cmd runs once after each of a session's turns, with the hook's JSON on stdin:\n"
                + "      registers the session's comment waiter and prints the inbox file it watches, its gen file and\n"
                + "      its generation, one per line, so a comment you post on the session's task wakes it. Prints\n"
                + "      nothing for a headless session or with the task events off"),
        ]),
        new("Tasks", [
            new("task", "list [--closed]",
                "the open tasks in the bar's order (waiting on you, then quadrants I to IV, each in its own order),\n"
                + "      as JSON: id, title, workspace, status, statusLine, question, claudes, sessions, comments (a\n"
                + "      count), updatedAt, important, urgent, quadrant (I to IV) and order (its place in the quadrant,\n"
                + "      from 1; null while it waits on you);\n"
                + "      --closed lists the closed ones instead, the latest closed first, to find one to reopen"),
            new("task", "add <title>|- [--workspace <name>|none] [--important] [--urgent] [--parent <id>]",
                "add a note to the backlog; without --workspace it goes on the workspace of the VS Code window\n"
                + "      whose folder holds --from (the caller's own folder if not given); - reads the title from stdin;\n"
                + "      --important and --urgent set its flags, and it goes to the top of the quadrant they give"),
            new("task", "close <id> [--stop-claude]", "close a task: it leaves the bar and the window's open tasks; one Claude is still working on needs --stop-claude, which stops its background sessions"),
            new("task", "reopen <id>", "reopen a closed task: in progress while a session is live on it, else backlog"),
            new("task", "status <id> <backlog|in-progress|waiting|done>",
                "set a task's status by hand (waiting is waiting on you); close closes"),
            new("task", "rename <id> <title>|-", "rename a task; - reads the title from stdin"),
            new("task", "move <id> <workspace>|none", "move a task to a workspace, or to none"),
            new("task", "comment <id> <markdown>|-", "add a comment to a task's thread, as you; - reads the markdown from stdin"),
            new("task", "delete <id>", "delete a task; the reply is the task as it was"),
            new("task", "flag <id> important|urgent on|off",
                "set or clear a flag, as the toggles on a line do; the task moves to the top of the quadrant it lands\n"
                + "      in (I urgent and important, II important, III urgent, IV neither)"),
            new("task", "order <id> top|end|<n>",
                "move a task within its quadrant, as a drag does: to the top, the end or place n counting from 1.\n"
                + "      A task waiting on you stays on top until it is answered"),
            // Task 9 (docs/superpowers/specs/2026-11-23-tasks-open-claude-sessions-design.md): the twins of a line's
            // double-click, "Start Claude on this" and the thread's Open (RemoteControl.Tasks.cs, TaskSessionCommands).
            new("task", "go <id>",
                "go to a task as a double-click on its line does: its workspace, then its Claude session's window (a\n"
                + "      background session's terminal is brought forward or opened, an ended one is resumed first)"),
            new("task", "start <id> [--folder <path>]",
                "start a background Claude session on the task, with the task as its first message, and open a window\n"
                + "      on it; without --folder it starts where exactly one folder is known and otherwise names them"),
            new("task", "open-session <id> <session>",
                "open one of the task's sessions (its full id or short id) as its thread's Open button does"),
            // A Claude Code session's own forms (RemoteControl.ClaudeHook.cs). --session is the session's id, which the
            // hooks' briefing hands it; every form but join, new and later acts on that session's own task. `-` in a
            // text's place reads the text from stdin (ControlArguments.WithTextInput): free text on cmd's command line
            // is not safe, since deskbar.cmd hands it to cmd, which runs what follows a quote it misreads.
            new("task", "join <id> --session <session>",
                "a Claude session takes up a task (one of your notes becomes its work, in progress)"),
            new("task", "new <title>|- --session <session>",
                "a Claude session creates its task, on the workspace of the VS Code window showing its folder, or on none"),
            new("task", "status-line <text>|- --session <session>", "the short line a session keeps current on its task"),
            new("task", "ask <question>|- --session <session>",
                "the session's task waits on you with the question, and its workspace blinks as attention does"),
            new("task", "answered --session <session>",
                "the session's question is settled: its task goes back to in progress and the blink stops. A prompt\n"
                + "      never settles it; the session runs this"),
            new("task", "done --session <session>", "a session marks its own task done; only you close it"),
            new("task", "comment <markdown>|- --session <session>", "a comment on the session's own task, signed by the session"),
            new("task", "later <title>|- --session <session>",
                "a backlog note on the caller's workspace, a follow-up for later. In every form that takes text, -\n"
                + "      reads the text from stdin (a heredoc in Git Bash): free text never goes on the command line"),
            new("task", "subtask <title>|- --session <session>",
                "a Claude session splits its work: a backlog subtask of its own task, assigned to Claude so the next\n"
                + "      session is offered it; when its own task is already a subtask, a sibling under the same parent"),
            new("task", "family --session <session>",
                "the parent, the siblings and their sessions (with transcript files) of the session's own task, as the\n"
                + "      task join reply gives them; read it again when the siblings may have moved on"),
            new("task", "comment-settle <comment-id> --session <session>",
                "settle one of the session's own comments: it stays but folds away in the tasks window. task answered\n"
                + "      settles the session's comments written since its question was asked; another session's is refused"),
            new("tasks-window", "show|hide|list|matrix",
                "open the tasks panel beside the bar, as the bar's \"All tasks\" does (every task grouped as on the bar,\n"
                + "      the closed ones under their filter, each with its comments), or close it; list or matrix switches its view\n"
                + "      (Covey's 2 by 2: urgent and not urgent across, important and not important down), remembered"),
            new("task", "assign <id> me|claude",
                "hand a task to yourself or to Claude, as the tasks window's \"Assign to\" menu does. The bar lists only the tasks\n"
                + "      assigned to you; a note assigned to Claude is offered to a new session on its workspace"),
            new("task", "describe <id> <markdown>|-",
                "set your own description under a task's title, as Edit in the tasks window does; empty clears it. - reads the\n"
                + "      markdown from stdin. A Claude task's status line is the session's and stays as it is"),
            new("task", "comment-edit <comment-id> <markdown>|-",
                "rewrite one of your own comments (ids are in task list's thread); a comment a session wrote is refused"),
            new("task", "parent <id> <parent-id>|none",
                "make a task a subtask of another, or a plain task again (none). One level: a subtask cannot have\n"
                + "      subtasks, and a task with subtasks cannot become one"),
            new("task", "family <id>",
                "what a Claude session on the task reads about its family: the parent, the siblings or its subtasks, and\n"
                + "      their sessions with transcript files; plain text"),
            new("task", "goal <id> <goal-id>|none",
                "count a task toward an open goal of any period, as the tasks window's \"...\" menu \"Goal\" does, or toward none"),
            new("task", "snooze <id> 1h|3h|tomorrow|monday|<n>m|<n>h|<n>d",
                "hide a task from the bar and the tasks panel for a while, as a line's snooze button does: an hour, three\n"
                + "      hours, until 09:00 tomorrow or next Monday, or any length in minutes, hours or days. It comes back by\n"
                + "      itself, or sooner when a session asks or finishes or its status changes; task list shows snoozedUntil"),
            new("task", "wake <id>",
                "bring a snoozed task back now, as \"Wake now\" does"),
        ]),
        new("Goals", [
            new("goal", "list [--week [<date>] | --month [<date>] | --all]",
                "this month's then this week's goals as JSON: id, title, kind (week or month), period (its first day),\n"
                + "      status (open, done or dropped), order, parent, progress (\"3 of 5 done\") with tasksDone, tasks,\n"
                + "      weeksDone and weeks (null on a weekly goal), and taskIds; --week or --month with a yyyy-MM-dd date\n"
                + "      lists the period holding it, bare the current one; --all every goal, the oldest period first"),
            new("goal", "add <title>|- [--week [<date>] | --month [<date>]]",
                "add a goal at the end of a period: this week without an option, the current month with a bare\n"
                + "      --month, the week or month holding a yyyy-MM-dd date otherwise; - reads the title from stdin"),
            new("goal", "done <id>", "mark a goal done (your call: finishing its tasks never does)"),
            new("goal", "drop <id>", "drop a goal that did not happen; it stays in its period, as dropped"),
            new("goal", "reopen <id>", "open a done or dropped goal again"),
            new("goal", "rename <id> <title>|-", "rename a goal; - reads the title from stdin"),
            new("goal", "delete <id>",
                "delete a goal made by mistake; its tasks and weekly goals are freed; the reply is the goal as it was"),
            new("goal", "carry <id> [--week [<date>] | --month [<date>]]",
                "move a goal, its tasks still attached, to another period of its kind; without an option to the\n"
                + "      current week or month"),
            new("goal", "parent <id> <monthly-id>|none",
                "set the monthly goal a weekly goal serves, one of a month its week touches, or none"),
            new("goal", "prompt [dismiss]",
                "whether a weekly or monthly review is due, as JSON {due: week, month or null}; dismiss marks this\n"
                + "      week and month as reviewed"),
        ]),
        new("Workspace row menu", [
            new("rename", "<workspace> <new name>", "Rename…"),
            new("reorder", "<workspace> up|down|top|end|<n>", "Move up / down / to top / to end, or to position n among its peers"),
            new("fold", "<workspace>", "Fold row (the name line only, no icons, except while you are on it)"),
            new("unfold", "<workspace>", "Unfold row"),
            new("pin-workspace", "<workspace>", "Pin to strip: an item on every strip, the bar and the taskbar that goes in, and back out"),
            new("unpin-workspace", "<workspace>", "Unpin from strip"),
            new("jump", "<workspace>", "Go to a workspace, or back where you came from if you are on it"),
            new("tile", "<workspace>", "Tile windows: a grid per screen, kept tidy as windows come and go"),
            new("untile", "<workspace>", "Stop tiling; windows stay where they are"),
            new("even", "<workspace>", "Even out tiles: equal splits, your shape kept"),
            new("reset-grid", "<workspace>", "Back to the automatic grid"),
            new("keep-space", "<workspace> on|off", "Keep the space when a tiled window closes, for the next window"),
            new("color", "<workspace> <colour>", "Colour (a grouped workspace sets its group's colour)"),
            new("delete", "<workspace> [--close-windows]", "Delete workspace…; refuses while windows are open unless --close-windows"),
            new("switch", "<workspace>", "go to that workspace, as clicking its row does"),
        ]),
        new("Group menu", [
            new("group-create", "<group name> <first workspace>", "New group…"),
            new("group-join", "<workspace> <group>", "Move into group (joins at the bottom)"),
            new("group-leave", "<workspace>", "Move out of group"),
            new("group-rename", "<group> <new name>", "Rename group…"),
            new("group-color", "<group> <colour>", "the group's colour"),
            new("group-fold", "<group>", "Fold group (one line: the group's name and its members' names)"),
            new("group-unfold", "<group>", "Unfold group (each member as it was)"),
            new("group-reorder", "<group> up|down|top|end|<n>", "Move group up / down / to top / to end, or to row n of the bar"),
            new("ungroup", "<group>", "Ungroup (members keep their places)"),
        ]),
        new("Unnamed desktop menu", [
            new("name-desktop", "<desktop id or number from desktops> <name>", "Name this desktop…"),
        ]),
        new("Window icon menu", [
            new("rename-window", "<hwnd> <short name>", "Rename this window…"),
            new("rename-app", "<hwnd> <short name>", "Rename all <app> windows… (refused for a Store app's shared frame)"),
            new("rename-pattern", "<title pattern> <short name>", "Rename by title pattern… (* is a wildcard)"),
            new("name-by-folder", "<hwnd> on|off", "Name <app> windows by folder"),
            new("remember-workspace", "<hwnd> on|off",
                "Remember <app>'s workspace (on: the app's windows open in the workspace this window is in, and\n"
                + "follow it when you move one; off: they open where they are opened)"),
            new("restore-title", "<hwnd>", "Restore title"),
            new("lock-tile", "<hwnd> on|off", "Don't split this tile: a new window never takes half of it"),
            new("split-last", "<hwnd> on|off",
                "Split <app> windows last: a new window halves the app's three latest windows only when no other tile is free"),
            new("pin", "<hwnd>", "show the window on every workspace, as dropping it on the pin row does"),
            new("unpin", "<hwnd>", "undo pin"),
            new("close-window", "<hwnd>", "Close (asks the window to close, as its X does)"),
            new("force-close", "<hwnd>", "Force close (ends the process that owns the window; for a Store app, the app inside its shared frame)"),
            new("close-all", "<hwnd>", "Close all <app> processes (ends every process running the window's exe; for a Store app, the app inside its frame)"),
            new("restart-app", "<hwnd>",
                "Restart all <app> windows (ends every process running the window's exe, waits for them to go, and\n"
                + "      starts the exe again bare; VS Code and a browser then restore their own windows, and the folder\n"
                + "      each editor window has open puts it back in the workspace it was in)"),
        ]),
        new("Bar", [
            new("bar", "minimize|restore|dock|undock|across|edge",
                "Minimize bar, or bring it back; turn the Dock to screen edge setting on or off; dock across the line between two screens, or back to one screen's edge"),
            new("stats", "detach|attach",
                "Detach stats into a window (the CPU, memory, disk and GPU line in a small always-on-top window), or Put stats back on the bar"),
            new("arrange", "on|off",
                "Arrange workspaces (drag rows on the bar) / Done arranging"),
            new("window-lines", "split|whole",
                "Right third of window lines switches workspace, or Whole window lines bring the window up"),
            new("window-list", "row|bottom|screens",
                "Where the current workspace's titled window list goes: in its row on the bar, at the bottom of the bar, or in a strip along the top of every screen"),
            new("collapsed-click", "bar|icon",
                "Clicking a collapsed bar switches to its workspace, icons included, or Icons on collapsed bars bring their window up"),
            new("full-screen", "step|top",
                "Step aside for full-screen windows (the floating bar goes behind a video or game on its screen), or Stay on top of full-screen windows"),
            new("claude-skill", "on|off",
                "Manage's \"Teach Claude Code\" setting: keep ~/.claude/skills/deskbar/SKILL.md written, or remove it"),
            new("claude-tasks", "on|off",
                "Manage's \"Claude Code sessions keep their tasks in the task list\" setting: keep Deskbar's SessionStart,\n"
                + "      UserPromptSubmit and SessionEnd hooks in ~/.claude/settings.json, or remove them (off while\n"
                + "      claude-skill is off)"),
            new("claude-resume", "on|off",
                "Manage's \"Resume the sessions of open tasks after a restart or a crash\" setting: wake an open task's Claude\n"
                + "      session with claude --bg --resume when claude agents no longer lists it, or leave it ended (off while\n"
                + "      claude-tasks is off)"),
            new("focus-ring", "on|off",
                "Outline the focused window in its workspace's colour, and the window in front on each other screen in dim white"),
            new("see-through", "on|off",
                "Let the desktop show faintly through the floating bar behind workspaces you have not used lately, or keep it flat"),
            new("previous-workspace", "on|off",
                "Show the previous workspace (\"↩ <name>\") at the end of every screen strip, in the titled list and on the taskbar, or hide it\n"
                + "(until chosen: on while the window list is on the screens and the taskbar auto-hides)"),
        ]),
        new("Diagnostics", [
            new("dump", "", "Save debug dump: write a diagnostics file (screens, the bar, every window in z-order) and print its path"),
        ]),
    ];

    public static IEnumerable<Command> All => Sections.SelectMany(s => s.Commands);

    public static bool IsVerb(string verb) => All.Any(c => c.Verb == verb);

    // Written the way the usual caller types it: through deskbar.cmd, which adds `ctl` itself. The
    // help used to say "TaskSpaces ctl <command>", and an agent following that literally through the
    // batch file would have sent "ctl ctl". The exe's own form is named once, in the header.
    const string Caller = "deskbar.cmd";

    public static string LineFor(string verb) =>
        string.Join("\n", All.Where(c => c.Verb == verb).Select(c => $"usage: {Caller} {c.Verb} {c.Arguments}".TrimEnd()));

    const string Footer =
        """
        placement, for create, move --create and start --create (at most one; none means the end of the bar):
          --group <group>         the bottom of that group
          --position <n>          top-level row n of the bar, a whole group counting as one row
          --before <workspace>    just before it, joining its group if it has one
          --after <workspace>     just after it, the same way

        --wait <seconds>          on any command: retry while the answer is exit 3 (a window not open yet)

        Workspaces and groups are named as they appear on the bar (case does not matter). Windows are
        named by the hwnd that `windows` prints. Colours: a palette name (indigo, plum, steel, violet,
        teal, orchid, amber, slate, rust), #RRGGBB, none (transparent) or default (by position).

        exit codes: 0 done, 1 refused, 2 Deskbar is not running, 3 no window matched
        """;

    // A blank line between sections, so the menu headings can be found by eye in a terminal.
    public static string Text { get; } =
        $"usage: {Caller} <command> [arguments]\n"
        + "   (%APPDATA%\\Deskbar\\deskbar.cmd; the exe itself takes the same as: Deskbar.exe ctl <command>)\n\n"
        + string.Join("\n\n", Sections.Select(s =>
            $"{s.Title}:\n" + string.Join("\n", s.Commands.Select(c => $"  {(c.Verb + " " + c.Arguments).TrimEnd()}\n      {c.Description}"))))
        + "\n\n" + Footer;
}
