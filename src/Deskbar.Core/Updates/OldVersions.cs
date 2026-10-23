using System.Text.RegularExpressions;

namespace Deskbar.Core.Updates;

// Which published exes beside the running one are old enough to delete.
//
// An update is a NEW FILE downloaded next to the running exe (UpdateService.DownloadAsync), and nothing
// ever removed the old ones: at 74 MB each a folder of releases grew without end. So when a published
// version starts it clears the versions older than the one it replaced, and KEEPS that one, so a person
// who dislikes the new version can still go back a step. With 1, 2 and 3 in the folder, starting 3
// deletes 1 and leaves 2 and 3.
//
// Only a decision lives here; the folder listing and the deleting are in Deskbar.App
// (OldVersionCleanup). The rule is deliberately narrow, because the cost of deleting a file that was
// not ours to delete is not the same size as the cost of leaving an old exe behind:
//
// - Only names that are EXACTLY what the release workflow produces count, for the running exe and for
//   every sibling: Deskbar-<major>.<minor>.<patch>-win-x64.exe, case aside. A development build
//   (Deskbar.exe), a renamed copy, "Deskbar-2.1.26-win-x64 (1).exe", a .part file left by a dropped
//   download, an arm64 or beta exe or anything else in the folder matches nothing, so nothing is done
//   when the running exe is one of those and none of those is ever touched.
// - Versions are compared as numbers: 2.1.9 is older than 2.1.18, which as text it is not.
// - A sibling NEWER than the running exe is kept. Starting an older version by hand cleans up
//   nothing above itself.
public static partial class OldVersions
{
    // Three numbers and nothing else. [0-9] and not \d, because \d also matches digits of other
    // scripts, and \z and not $, which would also accept a trailing newline.
    [GeneratedRegex(@"^Deskbar-(?<version>[0-9]+\.[0-9]+\.[0-9]+)-win-x64\.exe\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PublishedName();

    // Null for any name that is not a published exe, including a number too large to be a version.
    static Version? VersionOf(string fileName) =>
        PublishedName().Match(fileName) is { Success: true } m && Version.TryParse(m.Groups["version"].Value, out var version)
            ? version
            : null;

    // The names (from siblingFileNames, as given) to delete: every published exe older than the newest
    // one that is older than the running exe. File NAMES, not paths, and the running exe's own name may
    // be in the list; it is never returned, because it is not older than itself.
    //
    // Empty when the running exe is not a published name, and when there is no previous version to keep
    // the older ones behind.
    public static IReadOnlyList<string> ToDelete(string runningFileName, IEnumerable<string> siblingFileNames)
    {
        if (VersionOf(runningFileName) is not { } running) return [];

        var older = siblingFileNames
            .Select(name => (Name: name, Version: VersionOf(name)))
            .Where(sibling => sibling.Version is { } version && version < running)
            .ToList();
        if (older.Count == 0) return [];

        var previous = older.Max(sibling => sibling.Version);
        return older
            .Where(sibling => sibling.Version < previous)
            .Select(sibling => sibling.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
