using System.Runtime.InteropServices;

namespace Deskbar.Windows.Control;

// THE LONG FORM OF AN 8.3 SHORT PATH, for FolderPath in Core (plan Task 8). A Claude Code session started with a short
// path reports its folder that way (C:\Users\PETRE~1.CHI\repos\TASKSP~1, measured in
// docs/superpowers/notes/2026-11-22-claude-hook-probes.md), and a segment like TASKSP~1 names no VS Code window, so
// the session's workspace would not be found. GetLongPathNameW asks the file system for each segment's real name.
//
// The path comes back unchanged when it cannot be expanded: a folder deleted since, a network path that does not
// answer, or one that has no short segment. Declared CharSet.Unicode with the W name (the trap CLAUDE.md records for
// FindWindowW: without it the call is marshalled as ANSI and quietly answers about a different string).
public static class LongPath
{
    public static string Of(string path)
    {
        var buffer = new char[1024];
        var length = GetLongPathNameW(path, buffer, buffer.Length);
        // 0 is a failure; a length at or past the buffer is the size it would need (a path that long is not a
        // session's folder worth a second try).
        return length > 0 && length < buffer.Length ? new string(buffer, 0, (int)length) : path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetLongPathNameW(string shortPath, char[] longPath, int length);
}
