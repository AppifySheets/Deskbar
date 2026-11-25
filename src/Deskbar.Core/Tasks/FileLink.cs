using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// A LINK TO A FILE ON THIS MACHINE, in a task's question, status line, description or comment (task f9f25172, Petre:
// "when giving file links, they should be clickable too"). His screenshot: a session had asked him to pick a mockup
// and named the page as [soft blue variants](file:///C:/.../design/light/index.html), and the tasks panel showed the
// markdown written out, because only web addresses were links.
//
// They were web only for a reason that still stands: the text may come from a session, which may have read something
// hostile, and a click must never run a program. So a file link is a link, and what the click does depends on what
// the file is:
//
//   a document (Opens: pages, pictures, PDFs, plain text)   opens in its own program, as a double click would
//   a folder                                                opens in Explorer
//   any other file (an exe, a script, a shortcut, an Office
//   file that can carry macros, a type nobody listed)       is SHOWN in Explorer, selected, and never started
//   a path that is not there                                the nearest folder above it that is, in Explorer
//
// The list is the types that open, not the types that are dangerous: a type nobody thought of is shown, which is the
// harmless side to be wrong on.
//
// Only a path on a drive of this machine counts (LocalPath). A share (\\server\x, file://server/x) stays text: opening
// one makes Windows sign in to that server, which hands the account's hashed credentials to whoever runs it. A colon
// after the drive's own is refused too: "page.html:run.exe" names a stream hidden inside a file, and its extension is
// not what the shell would act on.
//
// The decisions are here in Core, with the file system handed in, so the tests cover them without touching a disk; the
// App's MarkdownBlocks does the starting.
public enum FileLinkAction
{
    /// <summary>Open the document in its own program.</summary>
    Open,

    /// <summary>Open the folder in Explorer.</summary>
    OpenFolder,

    /// <summary>Show the file in Explorer, selected, without starting it.</summary>
    Reveal,

    /// <summary>Nothing to show: not even a folder above the path exists.</summary>
    Nothing,
}

public static class FileLink
{
    // The types a click opens. Each is read by a viewer (a browser, an image viewer, an editor), never run by the shell.
    static readonly IReadOnlySet<string> Opens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".html", ".htm", ".pdf", ".txt", ".md", ".log", ".csv", ".json", ".xml",
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg",
    };

    /// <summary>
    /// The path a link's address names, when it is a file or folder on a drive of this machine: "file:///C:/a/b.html",
    /// or a plain "C:\a\b.html". None for a web address, a share and anything with a second colon (see the header).
    /// </summary>
    public static Maybe<string> LocalPath(Uri uri) =>
        uri is { IsAbsoluteUri: true, IsFile: true, IsUnc: false } && OnADrive(uri.LocalPath) ? uri.LocalPath : Maybe<string>.None;

    // "C:\..." with no further colon and nothing a path cannot hold (a quote would end the argument Explorer is given).
    static bool OnADrive(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/'
        && path.IndexOf(':', 2) < 0 && path.IndexOfAny(['"', '<', '>', '|', '*', '?']) < 0;

    /// <summary>
    /// What a click on a link to <paramref name="path"/> does, and to which path: the file or folder itself, or, when
    /// it is not there, the nearest folder above it that is.
    /// </summary>
    public static (FileLinkAction Action, string Path) Plan(string path, Func<string, bool> fileExists, Func<string, bool> folderExists) =>
        folderExists(path) ? (FileLinkAction.OpenFolder, path)
        : fileExists(path) ? (Opens.Contains(System.IO.Path.GetExtension(path)) ? FileLinkAction.Open : FileLinkAction.Reveal, path)
        : Above(path).FirstOrDefault(folderExists) is { } folder ? (FileLinkAction.OpenFolder, folder)
        : (FileLinkAction.Nothing, path);

    // The folders above a path, nearest first.
    static IEnumerable<string> Above(string path) =>
        System.IO.Path.GetDirectoryName(path) is { Length: > 0 } parent ? Above(parent).Prepend(parent) : [];
}
