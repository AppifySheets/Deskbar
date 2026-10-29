using System.Runtime.InteropServices;
using Deskbar.Core.Domain;

namespace Deskbar.Windows.Shell;

// THE FOLDERS OPEN IN FILE EXPLORER, read before Explorer is restarted so they can be opened again afterwards
// (ExplorerRestart, FloatingBar.RestartExplorer). A folder window's title is only the folder's NAME ("Downloads"),
// which is why Deskbar's folder tier cannot read an Explorer window ("container read explorer ... -> none" in the
// trace); the shell's own list of its windows, Shell.Application's Windows(), has the full location.
//
// What it gives, measured on Petre's machine on 2026-10-29 with two windows opened for the purpose:
//
//   1D0B74 | C:\Users\petre.chitashvili\repos\TaskSpaces | TaskSpaces
//   D0C38  | ::{20D04FE0-3AEA-1069-A2D8-08002B30309D}    | This PC
//
// A virtual folder comes back as its parsing name, which explorer.exe accepts as an argument as it is: that is how
// the This PC window above was opened. Windows 11 lists each TAB of a window as an item with the same HWND, so one
// window can give several folders; each is returned, and the caller opens each as a window of its own rather than
// silently dropping all but one.
//
// Late-bound COM (dynamic), the way a script reads it, because the interfaces behind Windows() are the old
// Shell/IE automation ones and declaring them would be more code than this needs. The app is not trimmed (see
// release.yml), so the runtime's built-in IDispatch support is there. Must be called on an STA thread, which the
// bar's UI thread is. Anything that cannot be read is skipped: a window that cannot be reopened is no reason not to
// restart Explorer.
public sealed record ExplorerFolder(WindowHandle Window, string Folder);

public static class ExplorerFolders
{
    public static IReadOnlyList<ExplorerFolder> Read()
    {
        try
        {
            if (Type.GetTypeFromProgID("Shell.Application") is not { } type || Activator.CreateInstance(type) is not { } shell)
                return [];
            try
            {
                dynamic windows = ((dynamic)shell).Windows();
                var count = (int)windows.Count;
                return Enumerable.Range(0, count)
                    .Select(i => FolderOf(windows.Item(i)))
                    .OfType<ExplorerFolder>()
                    .ToList();
            }
            finally
            {
                Marshal.ReleaseComObject(shell);
            }
        }
        catch (Exception)
        {
            return [];
        }
    }

    static ExplorerFolder? FolderOf(dynamic? window)
    {
        try
        {
            if (window is null) return null;
            var handle = (nint)(long)window.HWND;
            var folder = (string?)window.Document.Folder.Self.Path;
            return handle == 0 || string.IsNullOrWhiteSpace(folder) ? null : new ExplorerFolder(new WindowHandle(handle), folder);
        }
        catch (Exception)
        {
            // Not a folder view (an old Internet Explorer window, a window closing as it is read).
            return null;
        }
    }
}
