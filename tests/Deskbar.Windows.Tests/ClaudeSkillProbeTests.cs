using System.IO;
using Deskbar.Windows.Claude;
using Xunit;

namespace Deskbar.Windows.Tests;

// The add box's skill list asks the real Claude Code (task 3a872301). Integration: it runs claude, so it needs Claude
// Code installed and signed in, and it is left out of the routine run. It costs nothing on the account: the probe reads
// the init line, which comes before any model call, and points the API at a closed port as well.
public class ClaudeSkillProbeTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task This_repo_lists_its_own_refresh_pr_skill()
    {
        // The repo root: the folder above tests\ that holds Deskbar.sln.
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "Deskbar.sln"))) repo = repo.Parent;
        Assert.NotNull(repo);

        var skills = await ClaudeSkillProbe.Skills(repo.FullName, TimeSpan.FromSeconds(60));

        Assert.True(skills.IsSuccess, skills.IsFailure ? skills.Error : "");
        Assert.Contains("refresh-pr", skills.Value);
    }

    [Fact]
    public async Task A_folder_that_is_gone_is_a_failure_not_an_exception()
    {
        var skills = await ClaudeSkillProbe.Skills(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), TimeSpan.FromSeconds(5));

        Assert.True(skills.IsFailure);
        Assert.Contains("no longer exists", skills.Error);
    }
}
