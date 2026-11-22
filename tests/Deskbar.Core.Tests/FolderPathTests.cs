using Deskbar.Core.Control;

namespace Deskbar.Core.Tests;

// FolderPath: the one form a session's folder is kept and compared in (plan Task 8). The probes saw cwd in 8.3 short
// form and CLAUDE_PROJECT_DIR with forward slashes (docs/superpowers/notes/2026-11-22-claude-hook-probes.md). The long
// path comes from the Windows side (GetLongPathNameW); here a stand-in expands one short name, the probe's own.
public class FolderPathTests
{
    const string Long = @"C:\Users\petre.chitashvili\repos\TaskSpaces";
    const string Short = @"C:\Users\PETRE~1.CHI\repos\TaskSpaces";

    // GetLongPathNameW's answer for the one short path these tests use, and the path unchanged otherwise.
    static string LongPath(string path) =>
        path.Equals(Short, StringComparison.OrdinalIgnoreCase) ? Long : path;

    [Theory]
    [InlineData(Long)]
    [InlineData(Short)]
    [InlineData(@"C:/Users/PETRE~1.CHI/repos/TaskSpaces")]
    [InlineData(@"C:\Users\PETRE~1.CHI\repos\TaskSpaces\")]
    [InlineData(@"C:/Users/petre.chitashvili/repos/TaskSpaces/")]
    [InlineData(@"/c/Users/petre.chitashvili/repos/TaskSpaces")]
    [InlineData(" \"C:\\Users\\petre.chitashvili\\repos\\TaskSpaces\" ")]
    public void Every_form_comes_out_as_the_long_path(string raw) =>
        Assert.Equal(Long, FolderPath.Normalise(raw, LongPath), StringComparer.OrdinalIgnoreCase);

    // The case is not changed (it is shown back), but two forms that differ only in case are the same folder.
    [Fact]
    public void Case_does_not_make_another_folder()
    {
        Assert.Equal(@"c:\users\PETRE.chitashvili\Repos\taskspaces", FolderPath.Normalise(@"c:/users/PETRE.chitashvili/Repos/taskspaces/", LongPath));
        Assert.True(FolderPath.Same(@"c:\users\petre.chitashvili\repos\taskspaces", Short, LongPath));
        Assert.False(FolderPath.Same(Long, @"C:\Users\petre.chitashvili\repos\Other", LongPath));
    }

    // A drive's root keeps its separator (C: alone means "the current folder on C:"), and nothing becomes empty.
    [Theory]
    [InlineData(@"C:\", @"C:\")]
    [InlineData("C:/", @"C:\")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void A_root_and_nothing_stay_what_they_are(string raw, string expected) =>
        Assert.Equal(expected, FolderPath.Normalise(raw, LongPath));
}
