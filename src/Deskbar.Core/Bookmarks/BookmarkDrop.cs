using CSharpFunctionalExtensions;

namespace Deskbar.Core.Bookmarks;

// What a drop onto a workspace's bookmark row carried, already pulled off the data object by the WPF layer so the
// parsing is plain, testable code. Every member is optional; a drop usually has one or two.
//
// Url is the first of UniformResourceLocatorW, UniformResourceLocator and UnicodeText that held anything
// (the formats a browser address-bar drag offers were not measured, see the probes note, decision d). UrlTitle is
// the FileGroupDescriptorW file name exactly as given ("Portainer.url"), the page's title in Chromium. Text is
// the plain text of the drop. Files are the dropped file paths in drop order.
public sealed record BookmarkDrop(string? Url, string? UrlTitle, string? Text, IReadOnlyList<string> Files);

// What a .lnk file points at, read by the Windows layer (IShellLink). A shortcut to a Store app has an AppId and no
// Target; a shortcut to a program has a Target (and maybe Arguments and WorkingDirectory).
public sealed record ShortcutTarget(string? Target, string? Arguments, string? WorkingDirectory, string? AppId);

// The disk and shell lookups the parser and the window rule need, behind one interface so Core stays free of IO and
// tests can supply a fake. Each returns null / none when it cannot answer; callers fall back to a plain name.
public interface IBookmarkSources
{
    // The target of a .lnk file, or none when it cannot be read.
    Maybe<ShortcutTarget> ResolveShortcut(string lnkPath);

    // The exe's FileDescription ("RustDesk"), or null.
    string? DescribeExe(string exePath);

    // The display name of an installed app from its AppUserModelID, or null.
    string? NameOfApp(string appId);

    // The text of a small file (a .url), or null when it cannot be read.
    string? ReadText(string path);

    // PACKAGED PROGRAMS whose windows carry no AppUserModelID (new Teams is one: its window has none, its process
    // has "MSTeams_8wekyb3d8bbwe!MSTeams"). Both default to "no answer" so a source that cannot ask, such as a test
    // fake, leaves such a program without a bookmark rather than with a wrong one.
    //
    // The AppUserModelID of a running packaged process, or null.
    string? AppIdOfProcess(int processId) => null;

    // The AppUserModelID of the one app an installed package family lists in the Start menu, or null when the
    // family is not installed or lists more than one app (then nothing says which was meant).
    string? AppIdOfPackage(string family) => null;
}
