using System.Runtime.InteropServices;

namespace Deskbar.Windows.Control;

// THE 8.3 SHORT FORM OF A PATH, LongPath's opposite, for the manager session's shim (final review, item 3). The manager's
// one allow rule is `Bash(<shim> task:*)`, and a shim path with a space in it (a user folder like C:\Users\John Doe)
// never matches it: the model double-quotes the path, the unquoted rule does not match the quoted command, and under
// dontAsk every call is then denied without a word (measured, docs/superpowers/notes/2026-11-25-manager-session-probes.md,
// "Final review probes"). The short form has no space, needs no quotes, and was measured to run.
//
// The path comes back unchanged when it has no short form: a file that does not exist, or a volume with 8.3 names
// turned off (fsutil 8dot3name), where the short form IS the long one. ManagerActions.ShimCommand handles that case.
// Declared CharSet.Unicode with the W name, as LongPath is (the ANSI marshalling trap CLAUDE.md records).
public static class ShortPath
{
    public static string Of(string path)
    {
        var buffer = new char[1024];
        var length = GetShortPathNameW(path, buffer, buffer.Length);
        // 0 is a failure; a length at or past the buffer is the size it would need, and a shim path that long is not
        // worth a second try.
        return length > 0 && length < buffer.Length ? new string(buffer, 0, (int)length) : path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetShortPathNameW(string longPath, char[] shortPath, int length);
}
