using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Control;

// WHICH EDITOR WINDOW A CALLER IS WORKING IN, for `open` (RemoteControl.Open). Petre: "let's have all vscode based
// claude sessions open new browser windows if they're not working in the current deskbar", "if they're in a
// background bar, let them start new processes".
//
// A Claude session, or a terminal, inside VS Code has no way of its own to say which VS Code window it belongs to,
// and measuring found nothing in the process tree that could say it either:
//
//   * every VS Code window is one process (the main Code.exe), so the windows' owner is the same for all of them;
//   * each window does have its own extension host, and a session runs below it, but two extension hosts' command
//     lines are identical apart from a random trace id, and the environment names only the shared main process
//     (VSCODE_PID) and Claude's own ids.
//
// What DOES tie a caller to a window is the folder. A session runs in its window's folder, or below it (a git
// worktree under .claude\worktrees, say), and VS Code puts that folder's name in the window title:
//
//   caller   C:\Users\me\repos\TaskSpaces\.claude\worktrees\debug-dump
//   title    Active window and icon h… - TaskSpaces - Visual Studio Code
//
// So the window is the one whose title folder is a segment of the caller's path, and the DEEPEST such segment wins:
// a window on C:\src\app\api is a better answer for a caller in C:\src\app\api\tests than a window on C:\src\app.
// Pure, so the rule is unit-tested without a window or a pipe.
public static class EditorFolder
{
    // The process names of VS Code's windows: the stable build and Insiders.
    public static bool IsEditor(WindowInfo window) =>
        window.ProcessName.Equals("Code", StringComparison.OrdinalIgnoreCase)
        || window.ProcessName.Equals("Code - Insiders", StringComparison.OrdinalIgnoreCase);

    // The folder a VS Code title names: the last " - " part before the product name, without the qualifiers VS
    // Code appends to it ("(Workspace)" for a .code-workspace, "[Administrator]", "[WSL: Ubuntu]"). A title that
    // is not VS Code's shape (a window renamed by a rule, say) names no folder.
    public static Maybe<string> Of(string title)
    {
        var suffix = new[] { " - Visual Studio Code - Insiders", " - Visual Studio Code" }
            .FirstOrDefault(s => title.EndsWith(s, StringComparison.OrdinalIgnoreCase));
        if (suffix is null) return Maybe<string>.None;

        var folder = title[..^suffix.Length].Split(" - ").Last().Trim();
        while (folder.Length > 0 && (folder.EndsWith(')') || folder.EndsWith(']')))
        {
            var open = folder.LastIndexOfAny(['(', '[']);
            if (open <= 0) break;
            folder = folder[..open].TrimEnd();
        }
        return folder.Length == 0 ? Maybe<string>.None : folder;
    }

    // The editor windows whose folder is the deepest segment of `path` any of them names; empty when none does.
    // More than one comes back when several windows show the same folder name (two clones of one repo), and the
    // caller decides what that means: one answer if they all sit in the same workspace, none otherwise.
    //
    // Each window offers every title it is known by, because a rename rule can shorten what it says now; the
    // title it had before the rename still carries the folder.
    public static IReadOnlyList<WindowHandle> Best(string path, IEnumerable<(WindowHandle Window, IEnumerable<string> Titles)> editors)
    {
        var segments = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        var depths = editors
            .Select(e => (e.Window, Depth: e.Titles
                .Select(Of)
                .Where(f => f.HasValue)
                .Select(f => Array.FindLastIndex(segments, s => s.Equals(f.Value, StringComparison.OrdinalIgnoreCase)))
                .DefaultIfEmpty(-1)
                .Max()))
            .Where(e => e.Depth >= 0)
            .ToList();

        return depths.Count == 0
            ? []
            : depths.Where(e => e.Depth == depths.Max(d => d.Depth)).Select(e => e.Window).Distinct().ToList();
    }

    // WHICH OF SEVERAL WINDOWS ON ONE FOLDER A SESSION IS IN, by the session's name (SessionTitle). Several VS Code
    // windows on one folder are the normal case for a repo worked on in worktrees: on Petre's machine five windows
    // showed TaskSpaces at once, so the folder alone named none of them and an asking session's icon never glowed.
    //
    // VS Code's window title starts with the active tab's name, and a Claude panel's tab is the session's name, cut
    // at 24 characters with an ellipsis (measured: "Deskbar: tasks board, wi… - TaskSpaces - Visual Studio Code"
    // for the session "Deskbar: tasks board, windows and Claude sessions"). So a window is the session's when the
    // part of its title before the folder IS the name, or, ending in the ellipsis, is the start of it.
    //
    // Only the windows that pass come back, so the caller reads one as an answer and anything else as "the title
    // does not say": none when the Claude tab is not the window's active tab (the title then names a file), several
    // when two sessions share a name's first 24 characters. Ordinal, because the tab shows the name as written.
    public static IReadOnlyList<WindowHandle> ByTitle(string sessionTitle, IEnumerable<(WindowHandle Window, IEnumerable<string> Titles)> editors) =>
        editors
            .Where(e => e.Titles.Any(t => Label(t).Map(label => Names(label, sessionTitle.Trim())).GetValueOrDefault(false)))
            .Select(e => e.Window)
            .Distinct()
            .ToList();

    // The part of a VS Code title before its folder: "Deskbar: tasks board, wi…" in the example above. A title
    // with no folder (not VS Code's shape) has no label either.
    public static Maybe<string> Label(string title) =>
        Of(title).Bind(folder =>
        {
            var at = title.LastIndexOf($" - {folder}", StringComparison.OrdinalIgnoreCase);
            return at > 0 ? Maybe.From(title[..at].Trim()) : Maybe<string>.None;
        });

    // A label names the session when it is the whole name, or the name's start cut with an ellipsis. The ellipsis
    // alone, or a label that is empty, names nothing.
    static bool Names(string label, string name) =>
        label.EndsWith('…')
            ? label[..^1].TrimEnd() is { Length: > 0 } start && name.StartsWith(start, StringComparison.Ordinal)
            : label.Length > 0 && label.Equals(name, StringComparison.Ordinal);
}
