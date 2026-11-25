using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

/// <summary>
/// The workspace a repo's work lives on, read from the board: the workspace of the task whose session in that repo
/// joined last. A session's new task or note falls back on it when no VS Code window shows the session's folder
/// (task f905b1a7, RemoteControl.SessionWorkspace).
/// </summary>
//
// Why the board and not a window: since sessions run in Windows Terminal windows Deskbar opens (`<title> · Claude`,
// ClaudeStart.AttachTitle), Petre often has no VS Code window open at all (measured 2026-11-25: none among 16 windows),
// so the editor-window lookup found nothing and every `task new`, `task later` and `task add` from a session landed on
// no workspace. The board already knows where each repo's work went: every task a session joined carries the session's
// folder, and Petre puts the task where it belongs. So a repo goes where its latest task went.
//
// The repo, not the folder: a worktree (`<repo>\.claude\worktrees\<name>`) is the repo's work too (ClaudeStart.RepoOf),
// and each topic gets a fresh worktree, so its exact folder has never been seen before. Closed tasks count: a finished
// task still says where that repo's work goes. A task on no workspace says nothing and is passed over, and so is a
// workspace that no longer exists (`exists`), since a task can outlive the workspace it was on.
public static class RepoWorkspace
{
    public static Maybe<Guid> Latest(TaskBoard board, string folder, Func<Guid, bool> exists)
    {
        var repo = Key(folder);
        return board.Tasks
            .Where(t => t.WorkspaceId.Map(exists).GetValueOrDefault(false))
            .SelectMany(t => t.Sessions
                .Where(s => s.Folder != "" && Key(s.Folder).Equals(repo, StringComparison.OrdinalIgnoreCase))
                .Select(s => (Workspace: t.WorkspaceId.Value, s.Joined)))
            .OrderByDescending(x => x.Joined)
            .Select(x => x.Workspace)
            .TryFirst();
    }

    // The repo a folder belongs to, spelled one way: C:\repos\X\ and C:\repos\X are one repo.
    static string Key(string folder) => ClaudeStart.RepoOf(folder).TrimEnd('\\', '/');
}
