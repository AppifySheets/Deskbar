using Deskbar.Core.Updates;

namespace Deskbar.Core.Tests;

// A new version deletes the published exes older than the one it replaced, and keeps that one so there is
// a way back. The rule is only worth having if it can never reach a file it was not meant for, so most of
// these are about what must NOT be deleted: the running exe, anything newer, the previous version, and
// every name that is not exactly what the release workflow produces.
public class OldVersionsTests
{
    static string Name(string version) => $"Deskbar-{version}-win-x64.exe";

    // Versions in, versions out, so a row reads as the folder it describes.
    static string[] Deleted(string running, string siblings) =>
        [.. OldVersions.ToDelete(Name(running), siblings.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Name))
            .Select(name => name["Deskbar-".Length..^"-win-x64.exe".Length])
            .Order(StringComparer.Ordinal)];

    static string[] Sorted(string versions) =>
        [.. versions.Split(' ', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal)];

    [Theory]
    // The folder from the owner's machine: the previous version (2.1.20) and the running one stay.
    [InlineData("2.1.26", "2.1.5 2.1.8 2.1.9 2.1.18 2.1.19 2.1.20", "2.1.5 2.1.8 2.1.9 2.1.18 2.1.19")]
    [InlineData("3.0.0", "1.0.0 2.0.0", "1.0.0")]
    // Starting an older version by hand: 1 is its previous, 3 is newer, so nothing is older than the previous.
    [InlineData("2.0.0", "1.0.0 3.0.0", "")]
    // Newer ones are kept, and only what is behind the previous one goes.
    [InlineData("2.1.20", "2.1.5 2.1.8 2.1.26 2.1.27", "2.1.5")]
    [InlineData("1.0.0", "2.0.0 3.0.0", "")]
    [InlineData("2.1.26", "2.1.20", "")]
    [InlineData("2.1.26", "", "")]
    // By number, not by text: as text 2.1.9 is "greater" than 2.1.18 and 2.9.5 than 2.10.0.
    [InlineData("2.1.26", "2.1.2 2.1.9 2.1.18", "2.1.2 2.1.9")]
    [InlineData("2.1.9", "2.1.3 2.1.5 2.1.18", "2.1.3")]
    [InlineData("2.10.0", "1.99.99 2.2.0 2.9.0 2.9.5", "1.99.99 2.2.0 2.9.0")]
    public void Keeps_the_running_exe_every_newer_one_and_the_previous_one(string running, string siblings, string deleted) =>
        Assert.Equal(Sorted(deleted), Deleted(running, siblings));

    // The folder listing includes the running exe itself. It is not older than itself, so it is not returned,
    // and a spelling that differs only in case is the same file.
    [Fact]
    public void The_running_exe_is_never_returned_even_when_it_is_in_the_list()
    {
        var folder = new[] { Name("2.1.5"), Name("2.1.20"), Name("2.1.26"), Name("2.1.27") };

        Assert.Equal([Name("2.1.5")], OldVersions.ToDelete(Name("2.1.26"), folder));
        Assert.Equal([Name("2.1.5")], OldVersions.ToDelete(Name("2.1.26").ToUpperInvariant(), folder));
        Assert.Empty(OldVersions.ToDelete(Name("2.1.26"), [Name("2.1.26")]));
    }

    // Case does not change which version a name is, nor how often it is returned.
    [Fact]
    public void Names_that_differ_only_in_case_are_the_same_version_and_listed_once()
    {
        var upper = Name("2.1.5").ToUpperInvariant();

        Assert.Equal([Name("2.1.5")], OldVersions.ToDelete(Name("2.1.26"), [Name("2.1.5"), upper, Name("2.1.20")]));
        // ...and a previous version spelled in capitals is still the previous version.
        Assert.Equal([Name("2.1.5")], OldVersions.ToDelete(Name("2.1.26"), [Name("2.1.5"), Name("2.1.20").ToUpperInvariant()]));
    }

    // Everything here is "close but not the name the release workflow makes". None may be deleted, and none
    // may count as the previous version either: the sibling list is the two real ones plus the name under
    // test, once as a LOW version (would be deleted if it matched) and once as a HIGH one (would become the
    // previous version and put the real 2.1.20 up for deletion).
    [Theory]
    [InlineData("Deskbar-{v}-win-x64 (1).exe")]
    [InlineData("Deskbar-{v}-win-x64.exe.part")]
    [InlineData("Deskbar-{v}-win-x64.exe.bak")]
    [InlineData("Deskbar-{v}-win-arm64.exe")]
    [InlineData("Deskbar-{v}-beta-win-x64.exe")]
    [InlineData("Deskbar-{v}-Setup-win-x64.exe")]
    [InlineData("Deskbar-v{v}-win-x64.exe")]
    [InlineData("xDeskbar-{v}-win-x64.exe")]
    [InlineData(" Deskbar-{v}-win-x64.exe")]
    [InlineData("Deskbar-{v}-win-x64.exe ")]
    [InlineData("Deskbar-{v}-win-x64.exe\n")]
    [InlineData("Deskbar-{v}.1-win-x64.exe")]
    [InlineData("Deskbar-{v}-win-x64.dll")]
    [InlineData("TaskSpaces-{v}-win-x64.exe")]
    [InlineData(@"sub\Deskbar-{v}-win-x64.exe")]
    // Not three numbers, whatever the version. (No {v}: the same name is tried twice, which is harmless.)
    [InlineData("Deskbar-2.1-win-x64.exe")]
    [InlineData("Deskbar-2.25-win-x64.exe")]
    [InlineData("Deskbar-2-win-x64.exe")]
    [InlineData("Deskbar-2.1.-win-x64.exe")]
    [InlineData("Deskbar-a.b.c-win-x64.exe")]
    [InlineData("Deskbar-99999999999.0.0-win-x64.exe")] // too big to be a version at all
    [InlineData("Deskbar.exe")]
    [InlineData("notes.txt")]
    public void Names_that_are_not_exactly_a_published_exe_are_ignored(string template)
    {
        foreach (var version in new[] { "2.1.1", "2.1.25" })
        {
            var ignored = template.Replace("{v}", version);

            Assert.Equal([Name("2.1.5")], OldVersions.ToDelete(Name("2.1.26"), [ignored, Name("2.1.20"), Name("2.1.5")]));
        }
    }

    // The same names as the running exe: a dev build, a renamed or copied exe and the rest do nothing at
    // all, even with plenty of older published exes beside them.
    [Theory]
    [InlineData("Deskbar.exe")]
    [InlineData("Deskbar-2.1.26-win-x64 (1).exe")]
    [InlineData("Deskbar-2.1.26-win-x64.exe.part")]
    [InlineData("Deskbar-2.1-win-x64.exe")]
    [InlineData("Deskbar-2.1.26-win-arm64.exe")]
    [InlineData("Deskbar-2.1.26-beta-win-x64.exe")]
    [InlineData("Deskbar-99999999999.0.0-win-x64.exe")]
    [InlineData("testhost.exe")]
    [InlineData("")]
    public void A_running_exe_that_is_not_a_published_name_deletes_nothing(string running)
    {
        string[] siblings = [Name("2.1.5"), Name("2.1.8"), Name("2.1.20")];

        Assert.Empty(OldVersions.ToDelete(running, siblings));
    }
}
