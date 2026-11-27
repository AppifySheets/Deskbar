using Deskbar.Core.Updates;

namespace Deskbar.Core.Tests.Updates;

// Deskbar task 7dd83c89: a test build names itself, by its worktree when it has one, and offers a restart
// into the newest installed release. These pin the name and the pick, the two decisions that live in Core.
public class TestBuildTests
{
    const string Worktree = @"E:\repos\TaskSpaces\.claude\worktrees\add-box-skills\src\Deskbar.App\bin\Debug\net10.0-windows10.0.19041.0\Deskbar.exe";

    [Fact]
    public void A_worktree_build_is_named_after_its_worktree() =>
        Assert.Equal("add-box-skills", TestBuild.WorktreeOf(Worktree));

    [Theory]
    // The main checkout and a scratch copy have no worktree to name.
    [InlineData(@"E:\repos\TaskSpaces\src\Deskbar.App\bin\Debug\net10.0-windows10.0.19041.0\Deskbar.exe")]
    [InlineData(@"C:\Users\someone\AppData\Local\Temp\deskbar-dev\Deskbar.exe")]
    // A folder called worktrees with the exe straight in it names no topic.
    [InlineData(@"E:\worktrees\Deskbar.exe")]
    public void A_build_outside_a_worktree_has_no_worktree_name(string path) =>
        Assert.Null(TestBuild.WorktreeOf(path));

    [Fact]
    public void The_label_carries_the_version_and_the_worktree()
    {
        Assert.Equal("Test build 2.6.208 (add-box-skills)", TestBuild.Label("2.6.208", Worktree));
        Assert.Equal("Test build 2.6.208", TestBuild.Label("2.6.208", @"E:\repos\TaskSpaces\src\Deskbar.App\bin\Debug\Deskbar.exe"));
    }

    [Fact]
    public void The_newest_release_is_picked_by_number_not_by_text()
    {
        const string folder = @"C:\Users\someone\AppData\Local\Programs\Deskbar\";
        var newest = TestBuild.NewestRelease(
        [
            folder + "Deskbar-2.6.9-win-x64.exe",
            folder + "Deskbar-2.6.58-win-x64.exe",
            folder + "Deskbar-2.6.48-win-x64.exe",
        ]);
        Assert.Equal(folder + "Deskbar-2.6.58-win-x64.exe", newest);
    }

    [Fact]
    public void Only_release_names_count()
    {
        // A development exe, a renamed copy and a half-finished download are never started.
        Assert.Null(TestBuild.NewestRelease(
        [
            @"C:\x\Deskbar.exe",
            @"C:\x\Deskbar-2.6.99-win-x64 (1).exe",
            @"C:\x\Deskbar-2.6.99-win-x64.exe.part",
        ]));
        Assert.Null(TestBuild.NewestRelease([]));
    }

    [Fact]
    public void A_release_path_gives_its_version() =>
        Assert.Equal("2.6.58", TestBuild.VersionOf(@"C:\x\Deskbar-2.6.58-win-x64.exe"));
}
