using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests.Tasks;

// LinkHome (task 26f15d70, second round): a link to a GitHub repository opens in the workspace holding more than half of
// the tasks that mention that repository; a split, any other link, or a workspace that is gone gives no answer, and the
// clicked task's own workspace is used instead.
public class LinkHomeTests
{
    static readonly Guid Deskbar = Guid.NewGuid();
    static readonly Guid Personal = Guid.NewGuid();
    static readonly Guid Site = Guid.NewGuid();
    static readonly Guid Gone = Guid.NewGuid();

    readonly TaskService tasks = new(new MemoryTaskStore(), () => new DateTimeOffset(2026, 11, 26, 9, 0, 0, TimeSpan.Zero));

    sealed class MemoryTaskStore : ITaskStore
    {
        public Result<TaskBoard> Load() => TaskBoard.Empty;

        public Result Save(TaskBoard board) => Result.Success();
    }

    const string Pr587 = "https://github.com/AppifySheets/Deskbar/pull/587";

    Guid Note(string title, Maybe<Guid> workspace, string description = "")
    {
        var id = tasks.AddNote(title, workspace).Value.Id;
        if (description.Length > 0) Assert.True(tasks.Describe(id, description).IsSuccess);
        return id;
    }

    Maybe<Guid> Home(string address) => LinkHome.WorkspaceFor(address, tasks.Board.Tasks, id => id != Gone);

    // Petre's case: a Deskbar pull request linked from a task filed under Personal belongs in Deskbar, where most of the
    // repository's tasks are, whichever field mentions it.
    [Fact]
    public void A_repository_link_goes_where_most_of_its_tasks_are()
    {
        Note("subtask drag", Personal, $"Merge [Deskbar #587]({Pr587})");
        var asking = tasks.Create("bar fold", Deskbar, tasks.NewSession("s1", @"C:\repos\TaskSpaces")).Value.Id;
        Assert.True(tasks.Ask(asking, "Merge [Deskbar #586](https://github.com/AppifySheets/Deskbar/pull/586)").IsSuccess);
        var commented = Note("tasks panel", Deskbar);
        Assert.True(tasks.Comment(commented, Maybe<string>.None, "see https://github.com/appifysheets/deskbar/issues/9").IsSuccess);

        Assert.Equal(Deskbar, Home(Pr587).GetValueOrDefault());
        Assert.Equal(Deskbar, Home("https://github.com/AppifySheets/Deskbar").GetValueOrDefault());
    }

    // An even split names no home, and neither does a repository nobody mentioned: the caller falls back on the task.
    [Fact]
    public void A_split_or_an_unknown_repository_has_no_home()
    {
        Note("site redesign", Site, "https://github.com/AppifySheets/deskbar-site/pull/13");
        Note("site link", Deskbar, "https://github.com/AppifySheets/deskbar-site/pull/11");

        Assert.False(Home("https://github.com/AppifySheets/deskbar-site/pull/13").HasValue);
        Assert.False(Home("https://github.com/someone/else/pull/1").HasValue);
    }

    // deskbar-site is not deskbar: the repository is matched whole, never as a prefix.
    [Fact]
    public void A_repository_is_matched_whole()
    {
        Note("site", Site, "https://github.com/AppifySheets/deskbar-site/pull/13");
        Note("bar", Deskbar, Pr587);

        Assert.Equal(Deskbar, Home(Pr587).GetValueOrDefault());
        Assert.Equal(Site, Home("https://github.com/AppifySheets/deskbar-site").GetValueOrDefault());
    }

    // Links that are not to a repository: another site, a file, and GitHub pages of its own.
    [Theory]
    [InlineData("https://claret-ge.atlassian.net/browse/SHAR-13")]
    [InlineData("file:///C:/work/index.html")]
    [InlineData("https://github.com/settings/tokens")]
    [InlineData("https://github.com/orgs/AppifySheets/people")]
    [InlineData("https://github.com/AppifySheets")]
    public void Other_links_have_no_repository(string address) =>
        Assert.False(LinkHome.RepositoryOf(address).HasValue);

    [Theory]
    [InlineData(Pr587, "appifysheets/deskbar")]
    [InlineData("https://github.com/EuroCreditDigital/EuroCredit.CRM2Alta.API/pull/287", "eurocreditdigital/eurocredit.crm2alta.api")]
    [InlineData("https://github.com/AppifySheets/Deskbar.git", "appifysheets/deskbar")]
    [InlineData("https://github.com/AppifySheets/Deskbar?tab=readme", "appifysheets/deskbar")]
    // A bare address inside a sentence or markdown: the punctuation after it is not part of the name (review finding).
    [InlineData("See https://github.com/AppifySheets/Deskbar.", "appifysheets/deskbar")]
    [InlineData("See https://github.com/AppifySheets/Deskbar.git.", "appifysheets/deskbar")]
    [InlineData("https://github.com/AppifySheets/Deskbar, then", "appifysheets/deskbar")]
    [InlineData("`https://github.com/AppifySheets/Deskbar`", "appifysheets/deskbar")]
    [InlineData("**https://github.com/AppifySheets/Deskbar**", "appifysheets/deskbar")]
    public void A_repository_is_read_from_any_address_inside_it(string address, string repository) =>
        Assert.Equal(repository, LinkHome.RepositoryOf(address).GetValueOrDefault());

    // Tasks on a workspace that is gone, and notes on none, do not vote.
    [Fact]
    public void Tasks_on_no_workspace_or_a_gone_one_do_not_count()
    {
        Note("old", Gone, Pr587);
        Note("old too", Gone, Pr587);
        Note("loose", Maybe<Guid>.None, Pr587);
        Note("bar", Personal, Pr587);

        Assert.Equal(Personal, Home(Pr587).GetValueOrDefault());
    }
}
