using System.Text.RegularExpressions;

namespace Deskbar.Core.Control;

// The first sentence of the "already running" dialog, naming WHICH Deskbar won the single-instance race.
// Store packaging design, "One machine, one running Deskbar":
// docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md
//
// With the Store copy and the direct build both on one machine, both can be set to start with Windows,
// and the loser of the login race shows this dialog. Saying which copy is running tells the person which
// box to untick. The loser reads the other process's exe path (App.OnStartup) and hands it here; it
// needs no pipe command, so it works against every direct build ever shipped.
//
// Pure, so every wording is settled in a unit test. The App appends the existing "Look for the tiled
// icon..." line after this in every case.
public static class OtherInstance
{
    public const string StoreSentence = "The copy installed from the Microsoft Store is running.";
    public const string FallbackSentence = "Deskbar is already running.";

    // otherExePaths: one entry per other Deskbar process found, null where its path could not be read (an
    // elevated process refuses an unelevated one). A Store path anywhere wins, then the first readable
    // path; with none readable, or no process found at all, the plain sentence of before.
    public static string Describe(IReadOnlyList<string?> otherExePaths)
    {
        var readable = otherExePaths.OfType<string>().Where(path => path.Length > 0).ToList();
        return readable.Any(IsStorePath) ? StoreSentence
            : readable.Select(FolderOf).FirstOrDefault(folder => folder.Length > 0) is { } folder ? $"The copy at {folder} is running."
            : FallbackSentence;
    }

    // Packages install under %ProgramFiles%\WindowsApps. Matched as a folder name, case-insensitively.
    static bool IsStorePath(string path) => path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);

    // The folder, with backslashes handled on any OS: the path is always a Windows one, and Core is
    // platform-neutral, so Path.GetDirectoryName (which splits on '/' only off Windows) is not used.
    static string FolderOf(string path) =>
        path.LastIndexOfAny(['\\', '/']) is var cut and > 0 ? path[..cut] : "";

    // Which process names are a Deskbar app start, for the loser to look for. Not just "Deskbar": the
    // release exe is named after its version (release.yml: Deskbar-2.1.40-win-x64.exe), so its process is
    // called Deskbar-2.1.40-win-x64. The packaged and Debug exes are plain Deskbar. deskbar-cli (the Store
    // alias's console client) and TaskSpaces 1.x are not matched: neither holds the Deskbar mutex.
    public static bool IsDeskbarProcessName(string processName) => DeskbarProcessName.IsMatch(processName);

    static readonly Regex DeskbarProcessName = new(@"^Deskbar(-\d[\w.\-]*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
