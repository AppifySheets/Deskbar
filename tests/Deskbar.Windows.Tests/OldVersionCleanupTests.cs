using System.IO;
using Deskbar.App;

namespace Deskbar.Windows.Tests;

// The thin layer under "a new version removes the older exes beside it": it lists a folder, asks
// Deskbar.Core.OldVersions which files are old, and deletes them. The rule itself is tested in
// Deskbar.Core.Tests; these check that the right files go on a real disk, that a file which cannot be
// deleted is skipped without stopping the rest, and that nothing is ever thrown at the caller.
//
// Every test works in its own fresh folder under the temp folder, with tiny made-up files. Run takes the
// running exe's path as an argument, so nothing here can reach a real Deskbar folder.
public class OldVersionCleanupTests
{
    static string Exe(string version) => $"Deskbar-{version}-win-x64.exe";

    static void WithFolder(Action<string> test)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"ts-oldversions-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try { test(folder); }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }

    static void Touch(string folder, string name) => File.WriteAllText(Path.Combine(folder, name), "x");

    static string[] Entries(string folder) =>
        [.. Directory.GetFileSystemEntries(folder).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal)];

    [Fact]
    public void Run_removes_the_older_published_exes_and_nothing_else() =>
        WithFolder(folder =>
        {
            foreach (var version in new[] { "2.1.5", "2.1.9", "2.1.18", "2.1.20", "2.1.26", "2.1.27" })
                Touch(folder, Exe(version));
            // Near misses, other files, and a FOLDER with a published name, which holds a file of its own.
            foreach (var other in new[] { "Deskbar-2.1.3-win-x64 (1).exe", "Deskbar-2.1.3-win-x64.exe.part", "Deskbar-2.1.3-win-arm64.exe", "Deskbar.exe", "notes.txt" })
                Touch(folder, other);
            Directory.CreateDirectory(Path.Combine(folder, Exe("2.1.1")));
            Touch(Path.Combine(folder, Exe("2.1.1")), "inside.txt");
            var trace = new List<string>();

            OldVersionCleanup.Run(Path.Combine(folder, Exe("2.1.26")), trace.Add);

            Assert.Equal(
                new[]
                {
                    "Deskbar-2.1.3-win-arm64.exe", "Deskbar-2.1.3-win-x64 (1).exe", "Deskbar-2.1.3-win-x64.exe.part",
                    "Deskbar.exe", Exe("2.1.1"), Exe("2.1.20"), Exe("2.1.26"), Exe("2.1.27"), "notes.txt",
                }.Order(StringComparer.Ordinal),
                Entries(folder));
            Assert.True(File.Exists(Path.Combine(folder, Exe("2.1.1"), "inside.txt")));
            Assert.Equal(3, trace.Count(line => line.Contains("removed")));
            Assert.DoesNotContain(trace, line => line.Contains("kept"));
        });

    // An older version that is still running is locked, and a read-only file refuses deletion too. Both
    // are skipped, the files either side of them still go, and nothing reaches the caller.
    [Fact]
    public void A_file_that_cannot_be_deleted_is_skipped_and_the_others_still_go() =>
        WithFolder(folder =>
        {
            foreach (var version in new[] { "2.1.5", "2.1.8", "2.1.9", "2.1.10", "2.1.11", "2.1.20", "2.1.26" })
                Touch(folder, Exe(version));
            var readOnly = Path.Combine(folder, Exe("2.1.10"));
            File.SetAttributes(readOnly, FileAttributes.ReadOnly);
            var trace = new List<string>();

            try
            {
                using (File.Open(Path.Combine(folder, Exe("2.1.8")), FileMode.Open, FileAccess.Read, FileShare.None))
                    OldVersionCleanup.Run(Path.Combine(folder, Exe("2.1.26")), trace.Add);

                Assert.Equal(new[] { Exe("2.1.10"), Exe("2.1.20"), Exe("2.1.26"), Exe("2.1.8") }.Order(StringComparer.Ordinal), Entries(folder));
                Assert.Contains(trace, line => line.Contains("kept") && line.Contains(Exe("2.1.8")));
                Assert.Contains(trace, line => line.Contains("kept") && line.Contains(Exe("2.1.10")));
                Assert.Equal(3, trace.Count(line => line.Contains("removed")));
            }
            finally { File.SetAttributes(readOnly, FileAttributes.Normal); }
        });

    // A development build, or any exe not named like a release, owns no folder of versions.
    [Fact]
    public void A_running_exe_without_a_published_name_removes_nothing() =>
        WithFolder(folder =>
        {
            foreach (var version in new[] { "2.1.5", "2.1.8", "2.1.20" })
                Touch(folder, Exe(version));
            Touch(folder, "Deskbar.exe");
            var trace = new List<string>();

            OldVersionCleanup.Run(Path.Combine(folder, "Deskbar.exe"), trace.Add);

            Assert.Equal(4, Entries(folder).Length);
            Assert.DoesNotContain(trace, line => line.Contains("removed"));
        });

    // The folder is gone or unreadable: said in the trace, never thrown.
    [Fact]
    public void A_folder_that_cannot_be_listed_is_traced_and_not_thrown()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"ts-oldversions-{Guid.NewGuid():N}");
        var trace = new List<string>();

        OldVersionCleanup.Run(Path.Combine(missing, Exe("2.1.26")), trace.Add);

        Assert.Single(trace);
        Assert.Contains("could not list", trace[0]);
        Assert.False(Directory.Exists(missing));
    }
}
