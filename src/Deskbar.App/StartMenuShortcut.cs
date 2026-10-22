using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using CSharpFunctionalExtensions;

namespace Deskbar.App;

// DESKBAR IN START, so it can be started by hand. Petre: "i also want you to add deskbar to start, so if it
// stops i have a way to start it manually". The app is portable, with no installer to put it there, and the
// only way back after a crash or an Exit was finding the exe in Explorer.
//
// A shortcut in the per-user Start menu (%APPDATA%\Microsoft\Windows\Start Menu\Programs\Deskbar.lnk),
// kept pointing at the running exe on every start, for the reason the Run entry is (see
// StartupRegistration.ReassertIfEnabled): an update is a NEW FILE beside the old one, so a shortcut written
// once would go on starting the old version. Same two guards as well: only Deskbar itself writes it, never
// a test host, and only a PUBLISHED build, so a development copy never points Start at itself.
//
// Written only when it is missing or starts a different exe, so an ordinary start does not touch the file.
// A shortcut deleted by hand comes back at the next start: Start having Deskbar is the point of it.
//
// Deliberately no AppUserModelID on it. Windows would then group the bar's windows and toasts under that ID
// (see App.xaml.cs on #123), which is a change of its own and not what this is for.
public static class StartMenuShortcut
{
    static string ShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Deskbar.lnk");

    // Whether the shortcut has to be written: there is none, or it starts some other exe (an older version,
    // a moved folder).
    internal static bool NeedsWriting(string? currentTarget, string exe) =>
        string.IsNullOrEmpty(currentTarget)
        || !string.Equals(Path.GetFullPath(currentTarget), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase);

    // Called on every start, after the Run entry. Success when there was nothing to do (a development build,
    // a test host, a shortcut already right); a failure only when writing it went wrong, which is no reason
    // for the app not to start, so the caller only traces it.
    public static Result Ensure()
    {
        if (!StartupRegistration.IsTheApp(Assembly.GetEntryAssembly()?.GetName().Name) || !StartupRegistration.RunsAsPublished)
            return Result.Success();
        if (Environment.ProcessPath is not { } exe) return Result.Failure("the running exe has no path");

        return Result.Try(() =>
        {
            // WScript.Shell's shortcut object: the shell's own .lnk reader and writer, reached late-bound so no
            // interop assembly ships for four properties.
            var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell is not available");
            dynamic shell = Activator.CreateInstance(shellType)!;
            try
            {
                dynamic link = shell.CreateShortcut(ShortcutPath);
                string? current = File.Exists(ShortcutPath) ? (string)link.TargetPath : null;
                if (!NeedsWriting(current, exe)) return;
                link.TargetPath = exe;
                link.WorkingDirectory = Path.GetDirectoryName(exe);
                link.IconLocation = $"{exe},0";
                link.Description = "Deskbar: named virtual desktops and a bar showing all their windows";
                link.Save();
            }
            finally
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }, error => $"could not write the Start menu shortcut: {error.Message}");
    }
}
