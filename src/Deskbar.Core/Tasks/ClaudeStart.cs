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
        return (head.Length > room ? head[..room] : head) + "\n" + join;
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
    public static ProgramLaunch AttachWindow(string shortId, string folder, string title) =>
        new("wt.exe", ["-w", "new", "--title", title, "-p", "Deskbar Claude", "-d", folder, "claude", "attach", shortId], folder);
}
