using Deskbar.Core.Control;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Tests;

// Which VS Code window a caller works in, from the folder in the window's title (EditorFolder). The titles are the
// shapes VS Code writes, including the one on Petre's bar that started this: "… - TaskSpaces - Visual Studio Code"
// for a session running in a worktree below that folder.
public class EditorFolderTests
{
    [Theory]
    [InlineData("Active window and icon h… - TaskSpaces - Visual Studio Code", "TaskSpaces")]
    [InlineData("TaskSpaces - Visual Studio Code", "TaskSpaces")]
    [InlineData("● main.cs - api - Visual Studio Code", "api")]
    [InlineData("readme.md - platform (Workspace) - Visual Studio Code", "platform")]
    [InlineData("main.cs - api [Administrator] - Visual Studio Code", "api")]
    [InlineData("main.cs - api - Visual Studio Code - Insiders", "api")]
    public void The_folder_is_read_from_the_title(string title, string folder) =>
        Assert.Equal(folder, EditorFolder.Of(title).Value);

    [Theory]
    [InlineData("Welcome - Microsoft Edge")]
    [InlineData("TaskSpaces")]
    public void A_title_that_is_not_VS_Codes_names_no_folder(string title) =>
        Assert.True(EditorFolder.Of(title).HasNoValue);

    static (WindowHandle, IEnumerable<string>) Editor(nint hwnd, params string[] titles) => (new WindowHandle(hwnd), titles);

    // The deepest folder on the caller's path wins, so a window on a subfolder beats one on the repo around it.
    [Fact]
    public void The_deepest_folder_on_the_path_wins()
    {
        var best = EditorFolder.Best(@"C:\src\app\api\tests",
        [
            Editor(1, "x - app - Visual Studio Code"),
            Editor(2, "x - api - Visual Studio Code"),
            Editor(3, "x - other - Visual Studio Code"),
        ]);

        Assert.Equal([new WindowHandle(2)], best);
    }

    // A session in a worktree below the window's folder still finds it, which is Petre's own setup.
    [Fact]
    public void A_worktree_below_the_folder_finds_its_window() =>
        Assert.Equal([new WindowHandle(1)], EditorFolder.Best(@"C:\Users\me\repos\TaskSpaces\.claude\worktrees\debug-dump",
            [Editor(1, "Active window and icon h… - TaskSpaces - Visual Studio Code")]));

    // A rename rule can shorten the title; the one it had before still carries the folder.
    [Fact]
    public void A_renamed_window_is_found_by_its_original_title() =>
        Assert.Equal([new WindowHandle(1)], EditorFolder.Best(@"C:\src\api", [Editor(1, "API", "main.cs - api - Visual Studio Code")]));

    // Two windows on folders of the same name both come back; the caller decides what that means.
    [Fact]
    public void Two_windows_on_the_same_folder_name_both_come_back() =>
        Assert.Equal(2, EditorFolder.Best(@"C:\a\shared", [Editor(1, "x - shared - Visual Studio Code"), Editor(2, "y - shared - Visual Studio Code")]).Count);

    [Fact]
    public void No_folder_on_the_path_means_no_window() =>
        Assert.Empty(EditorFolder.Best(@"C:\elsewhere", [Editor(1, "x - api - Visual Studio Code")]));
}
