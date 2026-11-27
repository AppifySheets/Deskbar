using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;

namespace Deskbar.Core.Domain;

// The rules behind a workspace's folder (Workspace.Folder) and the one repos folder new workspaces are offered a folder
// in (AppState.ReposFolder), task 6edcc65a. Petre: "New workspace: option to set its folder path; default to a single
// repos folder and offer a subfolder in it".
//
// Pure string work, no disk: the dialog lists the repos folder's subfolders itself and hands them in, so every rule
// here can be tested without a file system.
public static class WorkspaceFolders
{
    // A folder as it is stored: trimmed, without a trailing separator (a drive's root keeps its own, since "C:" alone
    // means the drive's current folder), and null for blank, which is "no folder". So "E:\repos\x\" and "E:\repos\x"
    // are the same folder and SetWorkspaceFolder sees no change between them.
    public static string? Normal(string? folder) =>
        string.IsNullOrWhiteSpace(folder) || folder.Trim().TrimEnd('\\', '/').Length == 0
            ? null
            : folder.Trim() is var trimmed && trimmed.TrimEnd('\\', '/') is var bare && bare.EndsWith(':')
                ? bare + "\\"
                : trimmed.TrimEnd('\\', '/');

    // The folder a stored folder sits in, for remembering the repos folder. None for no folder or a drive's root.
    public static Maybe<string> ParentOf(string? folder) =>
        Normal(folder) is { } f && Path.GetDirectoryName(f) is { Length: > 0 } parent
            ? Normal(parent)!
            : Maybe<string>.None;

    // The repos folder when none has been stored: the folder most of the task sessions so far ran under. Each session's
    // folder counts as its REPO (ClaudeStart.RepoOf, so a worktree counts as the repo it belongs to), and the repo's
    // parent is the candidate: E:\repos\TaskSpaces\.claude\worktrees\x and E:\repos\stacks-contabo both vote E:\repos.
    // The earliest-listed wins a tie, which keeps the answer stable. `fallback` (the app passes
    // %USERPROFILE%\source\repos, Visual Studio's own default) when there is no session at all.
    public static string GuessReposFolder(IEnumerable<string> sessionFolders, string fallback) =>
        sessionFolders
            .Select(ClaudeStart.RepoOf)
            .Select(ParentOf)
            .Where(p => p.HasValue)
            .Select(p => p.Value)
            .GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault() ?? fallback;

    // The subfolder name a workspace name turns into: runs of spaces and characters a folder name cannot hold become
    // one dash, and dots and dashes at either end go (Windows drops a trailing dot silently). "Contabo stacks" becomes
    // "Contabo-stacks". Case is kept, since the user typed it.
    public static string SubfolderName(string workspaceName) =>
        string.Join("-", workspaceName
                .Split([.. Path.GetInvalidFileNameChars(), ' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
            .Trim('.', '-');

    // The folder the dialog offers a workspace named `workspaceName`: a subfolder of `reposFolder` already there whose
    // name matches it (case, spaces, dashes and underscores aside, so "Personalized health" finds
    // personalized-health), or a new one named SubfolderName. `existing` is the repos folder's subfolders, full paths.
    // None for a name with nothing usable in it, where the box stays empty.
    public static Maybe<string> Suggested(string reposFolder, string workspaceName, IEnumerable<string> existing) =>
        SubfolderName(workspaceName) is { Length: > 0 } name
            ? existing.TryFirst(e => Key(Path.GetFileName(Normal(e) ?? "")) == Key(name))
                .Map(e => Normal(e)!)
                .Or(() => Path.Combine(Normal(reposFolder) ?? "", name))
            : Maybe<string>.None;

    static string Key(string name) =>
        new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
}
