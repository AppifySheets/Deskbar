namespace Deskbar.Core.Control;

// THE ONE FORM A CALLER'S FOLDER IS KEPT AND COMPARED IN. Plan Task 8 (docs/superpowers/plans/2026-11-22-tasks-board.md);
// the forms it undoes were measured in docs/superpowers/notes/2026-11-22-claude-hook-probes.md:
//
//   * an 8.3 short path. A session started with a short path reports cwd (and CLAUDE_PROJECT_DIR) in that form:
//     C:\Users\PETRE~1.CHI\... A segment like TASKSP~1 matches no VS Code title, so the long path is asked for
//     (`longPath`, GetLongPathNameW on the Windows side; Core cannot call it, and a test hands in a stand-in).
//   * forward slashes. CLAUDE_PROJECT_DIR always has them (C:/repos/x); Git Bash's own form (/c/repos/x) is turned into
//     the drive form too, in case a hook ever passes $PWD.
//   * a trailing separator, and quotes or spaces around the whole, as a shell can leave them.
//   * case. Windows folders do not care, so Same compares ignoring case. The case is NOT changed by Normalise, because
//     the folder is stored on the task's session and shown back as the session gave it.
//
// A drive's root keeps its separator (C: alone means "the current folder on C:"). Nothing here touches the disk except
// through `longPath`, which answers the path unchanged when it cannot expand it (a folder that is gone, say).
public static class FolderPath
{
    public static string Normalise(string? raw, Func<string, string> longPath)
    {
        var text = (raw ?? "").Trim().Trim('"').Trim();
        if (text.Length == 0) return "";

        // Git Bash's /c/... is C:\...
        if (text.Length >= 3 && text[0] == '/' && char.IsAsciiLetter(text[1]) && text[2] == '/')
            text = $"{char.ToUpperInvariant(text[1])}:{text[2..]}";

        var backslashed = text.Replace('/', '\\');
        var trimmed = backslashed.Length > 3 ? backslashed.TrimEnd('\\') : backslashed;
        var rooted = trimmed.Length == 2 && trimmed[1] == ':' ? trimmed + "\\" : trimmed;
        // Only a path with a short segment needs the disk asked: a tilde is how every 8.3 name is spelt.
        return rooted.Contains('~') ? longPath(rooted) : rooted;
    }

    public static bool Same(string? a, string? b, Func<string, string> longPath) =>
        string.Equals(Normalise(a, longPath), Normalise(b, longPath), StringComparison.OrdinalIgnoreCase);
}
