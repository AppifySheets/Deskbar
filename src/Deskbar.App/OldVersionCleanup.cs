using System.IO;
using System.Reflection;
using Deskbar.Core.Updates;

namespace Deskbar.App;

// The IO half of "a new version removes the older exes beside it": it lists the running exe's folder,
// asks Deskbar.Core.OldVersions what to delete, and deletes it. Which names count, and which version
// is kept as the way back, are decided there (OldVersions has the reasoning); nothing here decides
// anything.
//
// Runs once per start, in App.OnStartup, AFTER the Run entry and the Start menu shortcut have been
// pointed at the running exe. That order is the point: the autostart entry and the shortcut then name a
// file this start will not delete (the running one), instead of the older exe an update left them on.
//
// A file that cannot be deleted is skipped and the rest still go: an older version that is still
// running is locked (an update starts the new copy while the old one is on its way out), and a
// read-only file or a folder without delete rights is not a reason to interrupt anyone. Whatever
// happened is traced when tracing is on, and nothing else is said.
static class OldVersionCleanup
{
    // Only from the published exe, the same gate StartMenuShortcut uses and for the same reason: a
    // development build never owns its folder. The name rule in OldVersions already makes Deskbar.exe
    // do nothing; this keeps a test host and a copy under a published name from reaching a real folder.
    //
    // Off the UI thread, because listing a OneDrive folder can be slow and the start must not wait for
    // it. Fire and forget: Run catches the failures it expects, and an exception nobody awaits does not
    // end a .NET process.
    public static void StartIfPublished()
    {
        if (!StartupRegistration.IsTheApp(Assembly.GetEntryAssembly()?.GetName().Name) || !StartupRegistration.RunsAsPublished)
            return;
        if (Environment.ProcessPath is not { } exe) return;

        _ = Task.Run(() => Run(exe, ClickTrace.Write));
    }

    // The running exe's path is an argument rather than Environment.ProcessPath so the tests can hand in
    // a temporary folder. The folder is the one that exe is in; subfolders are never entered.
    public static void Run(string runningExe, Action<string> trace)
    {
        if (Path.GetDirectoryName(runningExe) is not { } folder) return;

        IReadOnlyList<string> doomed;
        try
        {
            // Files only, so a folder that happens to carry a matching name is not even a candidate.
            var names = new DirectoryInfo(folder).EnumerateFiles().Select(file => file.Name).ToList();
            doomed = OldVersions.ToDelete(Path.GetFileName(runningExe), names);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            trace($"old versions: could not list {folder} ({e.Message})");
            return;
        }

        if (doomed.Count == 0)
        {
            trace("old versions: nothing to remove");
            return;
        }

        foreach (var name in doomed)
        {
            try
            {
                File.Delete(Path.Combine(folder, name));
                trace($"old versions: removed {name}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                trace($"old versions: kept {name} ({e.Message})");
            }
        }
    }
}
