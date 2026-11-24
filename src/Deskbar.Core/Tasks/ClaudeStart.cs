using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;
using Deskbar.Core.Control;

namespace Deskbar.Core.Tasks;

// THE PURE HALF OF STARTING A CLAUDE SESSION FROM A TASK: what the session is told first, which folder it starts in,
// and the command that puts a Windows Terminal window on it. Design:
// docs/superpowers/specs/2026-11-23-tasks-open-claude-sessions-design.md. Nothing here starts a process or touches a
// disk, so every rule is a test; the Windows side (ClaudeCli, ProgramStarter) does the starting.
public static class ClaudeStart
{
    // Claude Code takes a first message of about this size comfortably on a command line; a long title must not
    // push the ask to join the task off the end, so the title and status line are what get cut (see FirstMessage).
    const int MaxMessage = 1000;

    // `<repo>\.claude\worktrees\<topic>`: this repo's convention for a topic's worktree. Either slash, any case.
    static readonly Regex WorktreeMarker = new(@"[\\/]\.claude[\\/]worktrees[\\/]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The first message of a new session, exactly: <c>Deskbar task &lt;id&gt;: &lt;title&gt;</c>, the status line when
    /// there is one, then the ask to join. Cut to 1000 characters; the last line is built first and kept whole, and the
    /// title and status line are cut to fit in front of it, so the session can always join the task by its full id.
    /// </summary>
    public static string FirstMessage(TaskItem task)
    {
        var join = $"Join this task (deskbar task join {task.Id}) and start on it.";
        var head = string.Join("\n", new[] { $"Deskbar task {task.Id}: {task.Title}", task.StatusLine }
            .Where(line => !string.IsNullOrWhiteSpace(line)));
        // The room in front of the last line is the limit less that line and the newline that joins them.
        var room = Math.Max(0, MaxMessage - join.Length - 1);
        // A cut between the two halves of a surrogate pair (an emoji) would leave a lone high surrogate, which is not
        // valid text on a command line; step back one so the pair goes whole or not at all.
        var cut = room > 0 && room < head.Length && char.IsHighSurrogate(head[room - 1]) ? room - 1 : room;
        return (head.Length > room ? head[..cut] : head) + "\n" + join;
    }

    /// <summary>
    /// Where to start the session, the likeliest first. A task that has had sessions starts at the REPO of its latest
    /// one (a new session starts at the repo, not in an old worktree, because worktrees are deleted after the merge).
    /// A task with none offers the repos of every session of every task on its workspace (a free task matches only
    /// free tasks), the most recently used first, then the folders the workspace's editors have open.
    /// </summary>
    public static IReadOnlyList<string> Folders(TaskItem task, TaskBoard board, IEnumerable<string> editorFolders) =>
        task.Sessions.Where(s => s.Folder != "").OrderByDescending(s => s.Joined).FirstOrDefault() is { } latest
            ? [RepoOf(latest.Folder)]
            : board.Tasks
                .Where(other => other.WorkspaceId.Equals(task.WorkspaceId))
                .SelectMany(other => other.Sessions)
                .Where(s => s.Folder != "")
                .OrderByDescending(s => s.Joined)
                .Select(s => RepoOf(s.Folder))
                .Concat(editorFolders.Where(f => f != ""))
                // The first spelling of a folder wins, so the one used most recently is the one shown.
                .DistinctBy(f => f.TrimEnd('\\', '/'), StringComparer.OrdinalIgnoreCase)
                .ToList();

    /// <summary>The repo a worktree folder belongs to: the part before <c>\.claude\worktrees\</c>; any other folder as it is.</summary>
    public static string RepoOf(string folder) =>
        WorktreeMarker.Match(folder) is { Success: true } marker ? folder[..marker.Index] : folder;

    /// <summary>
    /// The Windows Terminal command that opens a NEW window on a background session: the "Deskbar Claude" profile
    /// (TerminalFragment), the folder as its directory, and `claude attach` with the session's short id.
    /// </summary>
    //
    // The folder goes WITHOUT a trailing separator (Task 9): wt reads its own command line, and a folder that ends in a
    // backslash arrives as `"C:\repos\x\"`, whose last backslash escapes the closing quote. A drive's root keeps its
    // own ("C:\"), since "C:" alone means the drive's current folder.
    public static ProgramLaunch AttachWindow(string shortId, string folder, string title) =>
        new("wt.exe", ["-w", "new", "--title", title, "-p", "Deskbar Claude", "-d", Bare(folder), "claude", "attach", shortId], Bare(folder));

    /// <summary>
    /// The window Deskbar opens when a folder was never trusted: a NEW Windows Terminal window running plain `claude`
    /// there, with Windows Terminal's default profile, so Petre answers Claude's trust prompt himself (the spec: Deskbar
    /// never accepts trust for him). Once he has, "Start Claude on this" works in that folder.
    /// </summary>
    public static ProgramLaunch TrustWindow(string folder) =>
        new("wt.exe", ["-w", "new", "-d", Bare(folder), "claude"], Bare(folder));

    // The longest title the window is given; the rest of a long title is in the task.
    const int MaxTitle = 80;

    /// <summary>
    /// The title of the window on a task's session: the task's title made safe for wt's command line, then " · Claude".
    /// Deskbar finds the window again by this exact title (Windows Terminal draws every window from one process, so the
    /// title is the only thing that tells them apart; the "Deskbar Claude" profile's suppressApplicationTitle keeps it).
    /// </summary>
    //
    // wt splits its command line into commands at every ';', so each becomes ','. A leading '-' would be read as one of
    // wt's options, so leading dashes and spaces go. A title can hold line breaks (the editors take Shift+Enter), which a
    // window title cannot show: its first line is used, the one the bar shows. Then the whole is cut to 80 characters,
    // stepping back over a split emoji as FirstMessage does.
    public static string AttachTitle(string taskTitle)
    {
        var firstLine = taskTitle.Split('\n')[0].Replace('\r', ' ');
        var clean = firstLine.Replace(';', ',').TrimStart('-', ' ', '\t').Trim();
        var cut = clean.Length > MaxTitle && char.IsHighSurrogate(clean[MaxTitle - 1]) ? MaxTitle - 1 : MaxTitle;
        return (clean.Length > MaxTitle ? clean[..cut] : clean) + TitleSuffix;
    }

    /// <summary>What every <see cref="AttachTitle"/> ends with, and so what tells a session's window from other terminals.</summary>
    public const string TitleSuffix = " · Claude";

    // Windows Terminal draws every one of its windows from this one process (no extension, as WindowInfo.ProcessName has it).
    const string TerminalProcess = "WindowsTerminal";

    /// <summary>
    /// Whether a window is one Deskbar opened on a Claude session: a Windows Terminal window titled by
    /// <see cref="AttachTitle"/>. The bar draws Claude's icon for it instead of the terminal's (task 0bd79d0e).
    /// </summary>
    //
    // The TITLE, because it is all there is: Windows Terminal hosts every window in one process, so the process and its
    // command line are the same for a Claude window and a PowerShell one, and the window's own icon is the terminal's
    // logo whatever profile its tab runs (measured: WM_GETICON's ICON_BIG on a "Deskbar Claude" window is the class
    // icon, Windows Terminal's own). The "Deskbar Claude" profile's suppressApplicationTitle keeps the title fixed, and a
    // second tab opened in the window takes the title with it, which is right: the window then shows that tab, not Claude.
    //
    // The process is matched by its exe's file name, so a path from any install location (Store, unpackaged, Preview's
    // WindowsTerminal.exe too) qualifies, and a stray "· Claude" title on any other app does not.
    public static bool IsSessionWindow(string? processPath, string? title) =>
        title is not null
        && title.EndsWith(TitleSuffix, StringComparison.Ordinal)
        && processPath is not null
        && Path.GetFileNameWithoutExtension(processPath).Equals(TerminalProcess, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Where the Claude Code CLI's own exe is, for its icon: the first <c>claude.exe</c> on the PATH, else the native
    /// installer's <c>%USERPROFILE%\.local\bin\claude.exe</c>; None when neither exists.
    /// </summary>
    //
    // An exe only, because the icon is read from it: an npm install puts a claude.cmd on the PATH, which runs node and
    // carries no icon, so it is passed over (and with nothing else the bar keeps the terminal's icon). The installer's
    // folder is the fallback because Deskbar's own PATH is the one it started with, which can predate the install.
    // <paramref name="exists"/> answers whether a file exists, so the rule is a test without a disk.
    public static Maybe<string> ExecutableIn(string? pathVariable, string home, Func<string, bool> exists) =>
        (pathVariable ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(folder => folder.Trim('"'))
            .Where(folder => folder.Length > 0)
            .Append(Path.Combine(home, ".local", "bin"))
            .Select(folder => Path.Combine(folder, "claude.exe"))
            .TryFirst(exists);

    /// <summary>
    /// The folders a workspace's VS Code windows show, as full paths (spec, "Choosing the folder": the editor windows'
    /// folders, "resolved to full paths through those session folders"). A VS Code title names only the folder
    /// (EditorFolder.Of), so each name is matched, ignoring case, against the last segment of the repo of every session
    /// the board knows; a name no repo ends in is left out, since a session cannot be started in a bare name. Each repo
    /// comes once, the most recently joined session's spelling first.
    /// </summary>
    public static IReadOnlyList<string> EditorRepos(IEnumerable<string> folderNames, TaskBoard board)
    {
        var repos = board.Tasks.SelectMany(t => t.Sessions)
            .Where(s => s.Folder != "")
            .OrderByDescending(s => s.Joined)
            .Select(s => Bare(RepoOf(s.Folder)))
            .DistinctBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return folderNames
            .SelectMany(name => repos.Where(repo => LastSegment(repo).Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)).Take(1))
            .DistinctBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    static string LastSegment(string folder) => folder.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";

    // A folder without its trailing separators, except a drive's root (see AttachWindow).
    static string Bare(string folder) =>
        folder.TrimEnd('\\', '/') is var trimmed && trimmed.Length == 2 && trimmed[1] == ':' && folder.Length > 2 ? folder[..3] : trimmed;
}
