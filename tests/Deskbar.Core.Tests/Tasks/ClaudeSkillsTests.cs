using Deskbar.Core.Tasks;
using Xunit;

namespace Deskbar.Core.Tests.Tasks;

// The add box's "/" list (task 3a872301): reading Claude Code's init line, what is being typed, the matching, the
// merge across a workspace's folders, the completion, and the skill a title starts with.
public class ClaudeSkillsTests
{
    // Cut down from a real init line (claude 2.1.332, 2026-11-26): the skills array, beside fields the list ignores.
    const string InitLine =
        """{"type":"system","subtopic":"x","subtype":"init","cwd":"E:\\repos\\TaskSpaces","skills":["humanizer","refresh-pr","superpowers:brainstorming","bad name",""],"slash_commands":["compact","humanizer","bad name"]}""";

    [Fact]
    public void The_init_line_gives_the_skills_and_nothing_else_does()
    {
        Assert.Equal(["humanizer", "refresh-pr", "superpowers:brainstorming"], ClaudeSkills.FromInit(InitLine).Value.Skills);
        Assert.Equal(["compact", "humanizer"], ClaudeSkills.FromInit(InitLine).Value.Commands);
        Assert.True(ClaudeSkills.FromInit("""{"type":"system","subtype":"hook_started"}""").HasNoValue);
        Assert.True(ClaudeSkills.FromInit("""{"type":"assistant","skills":["x"]}""").HasNoValue);
        Assert.True(ClaudeSkills.FromInit("not json").HasNoValue);
        Assert.True(ClaudeSkills.FromInit("[1,2]").HasNoValue);
    }

    [Theory]
    [InlineData("/", 1, "")]
    [InlineData("/ref", 4, "ref")]
    [InlineData("/super:br", 9, "super:br")]
    // Words after the name do not stop the lookup while the caret is at the end of the name (a pick then keeps them).
    [InlineData("/ref more", 4, "ref")]
    public void The_query_is_the_name_being_typed_after_the_slash(string text, int caret, string expected) =>
        Assert.Equal(expected, ClaudeSkills.Query(text, caret).Value);

    [Theory]
    [InlineData("", 0)]
    [InlineData("fix /ref", 8)]
    [InlineData("/ref ", 5)]
    [InlineData("/refresh-pr 245", 15)]
    [InlineData("/ref", 2)]
    [InlineData("/etc/hosts", 10)]
    [InlineData("/ref", 0)]
    public void No_query_outside_the_first_word(string text, int caret) =>
        Assert.True(ClaudeSkills.Query(text, caret).HasNoValue);

    static SkillChoice Skill(string name) => new(name, []);

    [Fact]
    public void Names_that_start_with_it_come_first_then_a_plugins_skill_then_any_that_contain_it()
    {
        var skills = new[] { "verify", "superpowers:verification-before-completion", "verification-quality", "humanizer", "reverify" }
            .Select(Skill);

        Assert.Equal(["verification-quality", "verify", "superpowers:verification-before-completion", "reverify"],
            ClaudeSkills.Match(skills, "VER").Select(s => s.Name));
    }

    [Fact]
    public void An_empty_query_lists_every_skill_alphabetically() =>
        Assert.Equal(["b", "C", "d"], ClaudeSkills.Match(new[] { "d", "b", "C" }.Select(Skill), "").Select(s => s.Name));

    [Fact]
    public void A_skill_only_some_folders_have_names_them_and_a_shared_one_names_none()
    {
        var merged = ClaudeSkills.Merge([
            (@"E:\repos\TaskSpaces", ["humanizer", "refresh-pr"]),
            (@"E:\repos\deskbar-site", ["humanizer", "update-site"]),
        ]);

        Assert.Equal(
            [
                new SkillChoice("humanizer", []),
                new SkillChoice("refresh-pr", [@"E:\repos\TaskSpaces"]),
                new SkillChoice("update-site", [@"E:\repos\deskbar-site"]),
            ],
            merged,
            new ChoiceComparer());
    }

    // Petre: "i only want custom skills, like those /iam". What Claude Code lists at home is not a repo's own.
    [Fact]
    public void A_repos_own_skills_are_those_not_listed_at_home() =>
        Assert.Equal(["iam-check-data", "iam-deploy-staging"],
            ClaudeSkills.Own(["humanizer", "iam-check-data", "superpowers:brainstorming", "simplify", "iam-deploy-staging"],
                ["humanizer", "superpowers:brainstorming", "simplify", "summary"]));

    // Task b3fb4e8c: in personalized-health (claude 2.1.332, 2026-11-28) "skills" left iam-check-data out while
    // "slash_commands" had it, beside the built-ins that every folder's commands carry, the home folder's too.
    static readonly ClaudeInit Repo = new(["humanizer", "iam-logs"], ["compact", "humanizer", "iam-check-data", "iam-logs", "deskbar"]);
    static readonly ClaudeInit Home = new(["humanizer"], ["compact", "humanizer", "deskbar"]);

    [Fact]
    public void A_skill_only_the_commands_name_is_listed_and_a_built_in_is_not() =>
        Assert.Equal(["iam-logs", "iam-check-data"], ClaudeSkills.Own(Repo.All, Home.All));

    [Fact]
    public void A_title_may_run_the_folders_own_command_but_never_a_built_in()
    {
        Assert.Equal(["humanizer", "iam-logs", "iam-check-data"], Repo.Known(Home));
        Assert.Equal(["humanizer", "iam-logs"], Repo.Known(CSharpFunctionalExtensions.Maybe<ClaudeInit>.None));
    }

    [Fact]
    public void An_init_line_without_commands_still_gives_its_skills() =>
        Assert.Empty(ClaudeSkills.FromInit("""{"type":"system","subtype":"init","skills":["humanizer"]}""").Value.Commands);

    [Fact]
    public void With_one_folder_nothing_is_named() =>
        Assert.All(ClaudeSkills.Merge([(@"E:\repos\TaskSpaces", ["humanizer", "refresh-pr"])]), s => Assert.Empty(s.Folders));

    [Theory]
    [InlineData("/ref", 4, "/refresh-pr ", 12)]
    [InlineData("/ref 245", 4, "/refresh-pr 245", 12)]
    [InlineData("/", 1, "/refresh-pr ", 12)]
    public void A_pick_puts_the_skill_and_a_space_in_the_box(string text, int caret, string expected, int expectedCaret) =>
        Assert.Equal((expected, expectedCaret), ClaudeSkills.Complete(text, caret, "refresh-pr"));

    [Theory]
    [InlineData("/refresh-pr 245", "refresh-pr")]
    [InlineData("/summary", "summary")]
    [InlineData("/superpowers:brainstorming a talk", "superpowers:brainstorming")]
    public void A_title_names_the_skill_it_starts_with(string title, string skill) =>
        Assert.Equal(skill, ClaudeSkills.SkillOf(title).Value);

    [Theory]
    [InlineData("refresh-pr")]
    [InlineData("/etc/hosts is wrong")]
    [InlineData("/ starts with a space")]
    [InlineData("fix /summary")]
    public void Other_titles_name_no_skill(string title) =>
        Assert.True(ClaudeSkills.SkillOf(title).HasNoValue);

    sealed class ChoiceComparer : IEqualityComparer<SkillChoice>
    {
        public bool Equals(SkillChoice? x, SkillChoice? y) =>
            x is not null && y is not null && x.Name == y.Name && x.Folders.SequenceEqual(y.Folders);

        public int GetHashCode(SkillChoice obj) => obj.Name.GetHashCode();
    }
}
