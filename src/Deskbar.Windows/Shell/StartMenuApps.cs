using Deskbar.Core.QuickFind;

namespace Deskbar.Windows.Shell;

// THE START MENU'S FULL APP LIST, for quick find's launch rows
// (docs/superpowers/specs/2026-11-04-quick-find-design.md, "Launching").
//
// Read through the shell's own "Applications" folder, shell:AppsFolder, which is what the Start menu's All list
// shows: desktop apps and packaged apps alike. Measured on Petre's machine before this was written
// (docs/superpowers/notes/2026-11-04-quick-find-probes.md): 494 entries; a desktop app's parsing name is a
// known-folder path or a short name and System.Link.TargetParsingPath gives its real exe (msedge.exe, Code.exe,
// rider64.exe); a packaged app's parsing name is its AppUserModelID and it has no target.
//
// Through Shell.Application's scripting interface rather than IShellItem P/Invokes: the same folder and
// properties, a tenth of the code, and it is what the probe proved. Slow enough (it walks every shortcut) that
// the caller reads it off the UI thread and keeps the answer for a while.
public static class StartMenuApps
{
    public static IReadOnlyList<StartMenuApp> Read()
    {
        var shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        try
        {
            dynamic folder = ((dynamic)shell).NameSpace("shell:AppsFolder");
            var apps = new List<StartMenuApp>();
            foreach (dynamic item in folder.Items())
            {
                string name = item.Name;
                string parsingName = item.Path;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(parsingName)) continue;
                apps.Add(new StartMenuApp(name, parsingName, Target(item)));
            }
            return apps;
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }

    // The exe a desktop app's shortcut points at, or null: packaged apps have none, and an entry whose property
    // cannot be read is treated the same rather than failing the whole list.
    static string? Target(dynamic item)
    {
        try
        {
            return item.ExtendedProperty("System.Link.TargetParsingPath") is string target && Path.IsPathRooted(target) ? target : null;
        }
        catch
        {
            return null;
        }
    }
}
