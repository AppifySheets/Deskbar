using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;

namespace Deskbar.Core.Tests;

// Task 6edcc65a, Petre: "New workspace: option to set its folder path; default to a single repos folder and offer a
// subfolder in it". The rules (WorkspaceFolders) and what creating or changing a workspace stores (WorkspaceManager).
public class WorkspaceFolderTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeStore store = new();

    WorkspaceManager Started()
    {
        store.Stored = AppState.Empty;
        var manager = new WorkspaceManager(desktops, new FakeMonitor(), new FakeTitles(), store);
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    [InlineData(@"\", null)]
    [InlineData(" / ", null)]
    [InlineData(@" E:\repos\x\ ", @"E:\repos\x")]
    [InlineData(@"E:\repos\x//", @"E:\repos\x")]
    [InlineData(@"E:\", @"E:\")]
    [InlineData(@"E:", @"E:\")]
    public void A_folder_is_stored_trimmed_and_without_a_trailing_separator(string? given, string? stored) =>
        Assert.Equal(stored, WorkspaceFolders.Normal(given));

    [Theory]
    [InlineData("Contabo stacks", "Contabo-stacks")]
    [InlineData("  a   b  ", "a-b")]
    [InlineData("What? Now: yes.", "What-Now-yes")]
    [InlineData("...", "")]
    public void A_workspace_name_becomes_a_folder_name(string name, string folder) =>
        Assert.Equal(folder, WorkspaceFolders.SubfolderName(name));

    [Fact]
    public void The_repos_folder_is_guessed_from_where_most_sessions_ran()
    {
        var sessions = new[]
        {
            @"C:\work\one",
            @"E:\repos\TaskSpaces\.claude\worktrees\topic",
            @"E:\repos\stacks-contabo",
            @"e:\repos\personalized-health",
        };

        Assert.Equal(@"E:\repos", WorkspaceFolders.GuessReposFolder(sessions, @"C:\fallback"), ignoreCase: true);
        Assert.Equal(@"C:\fallback", WorkspaceFolders.GuessReposFolder([], @"C:\fallback"));
    }

    [Fact]
    public void A_new_workspace_is_offered_its_existing_folder_or_a_new_one_named_after_it()
    {
        var existing = new[] { @"E:\repos\personalized-health", @"E:\repos\TaskSpaces" };

        Assert.Equal(@"E:\repos\personalized-health",
            WorkspaceFolders.Suggested(@"E:\repos\", "Personalized health", existing).Value);
        Assert.Equal(@"E:\repos\Contabo-stacks", WorkspaceFolders.Suggested(@"E:\repos", "Contabo stacks", existing).Value);
        Assert.True(WorkspaceFolders.Suggested(@"E:\repos", "???", existing).HasNoValue);
    }

    [Fact]
    public void Creating_a_workspace_with_a_folder_keeps_it_and_remembers_its_parent_as_the_repos_folder()
    {
        var manager = Started();

        var made = manager.InsertWorkspace("Fresh", 0, folder: @"E:\repos\Fresh\");

        Assert.Equal(@"E:\repos\Fresh", made.Value.Folder);
        Assert.Equal(@"E:\repos\Fresh", manager.State.Workspaces.Single().Folder);
        Assert.Equal(@"E:\repos", manager.State.ReposFolder);
    }

    [Fact]
    public void Creating_a_workspace_with_no_folder_leaves_the_repos_folder_alone()
    {
        var manager = Started();
        manager.InsertWorkspace("First", 0, folder: @"E:\repos\First");

        manager.InsertWorkspace("Second", 1);

        Assert.Null(manager.State.Workspaces[1].Folder);
        Assert.Equal(@"E:\repos", manager.State.ReposFolder);
    }

    [Fact]
    public void A_workspaces_folder_can_be_set_and_cleared()
    {
        var manager = Started();
        var id = manager.InsertWorkspace("Fresh", 0).Value.Id;

        Assert.True(manager.SetWorkspaceFolder(id, @"D:\elsewhere\x").IsSuccess);
        Assert.Equal(@"D:\elsewhere\x", manager.State.Workspaces.Single().Folder);
        // Setting a folder on an existing workspace does not move where new ones are offered.
        Assert.Null(manager.State.ReposFolder);

        Assert.True(manager.SetWorkspaceFolder(id, "").IsSuccess);
        Assert.Null(manager.State.Workspaces.Single().Folder);
        Assert.True(manager.SetWorkspaceFolder(Guid.NewGuid(), @"D:\x").IsFailure);
    }
}
