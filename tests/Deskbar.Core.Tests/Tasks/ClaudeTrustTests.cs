using System.Text.Json.Nodes;
using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests.Tasks;

// Trusting a workspace's Claude folder in Claude Code's ~/.claude.json (Deskbar task 50b61120). The file is Claude Code's,
// so what matters most is what is NOT changed: every other key, and the folder's other fields, stay as they were.
public class ClaudeTrustTests
{
    [Theory]
    [InlineData(@"C:\Users\me\repos\Deskbar", "C:/Users/me/repos/Deskbar")]
    [InlineData(@"C:\Users\me\repos\Deskbar\", "C:/Users/me/repos/Deskbar")]
    [InlineData(@" E:\repos\stacks-contabo ", "E:/repos/stacks-contabo")]
    [InlineData(@"E:\", "E:/")]
    public void A_folder_is_keyed_the_way_Claude_Code_keys_it(string folder, string key) =>
        Assert.Equal(key, ClaudeTrust.Key(folder));

    [Theory]
    [InlineData(@"C:\repos\X", "C:/repos/X", "c:/repos/X")]
    [InlineData(@"c:\repos\X", "c:/repos/X", "C:/repos/X")]
    public void A_folder_is_marked_under_both_drive_letter_spellings(string folder, string first, string second) =>
        Assert.Equal([first, second], ClaudeTrust.Keys(folder));

    [Fact]
    public void Trusting_a_new_folder_adds_its_entries_and_leaves_the_rest_alone()
    {
        const string before = """
            {
              "numStartups": 12,
              "projects": {
                "C:/repos/Other": { "hasTrustDialogAccepted": false, "allowedTools": ["Bash"] }
              },
              "userID": "ა-b+c"
            }
            """;

        var after = JsonNode.Parse(ClaudeTrust.Accepted(before, @"C:\repos\Rustdesk").Value)!.AsObject();

        Assert.Equal(12, after["numStartups"]!.GetValue<int>());
        Assert.Equal("ა-b+c", after["userID"]!.GetValue<string>());
        Assert.False(after["projects"]!["C:/repos/Other"]!["hasTrustDialogAccepted"]!.GetValue<bool>());
        Assert.Equal("Bash", after["projects"]!["C:/repos/Other"]!["allowedTools"]![0]!.GetValue<string>());
        Assert.True(after["projects"]!["C:/repos/Rustdesk"]!["hasTrustDialogAccepted"]!.GetValue<bool>());
        Assert.True(after["projects"]!["c:/repos/Rustdesk"]!["hasTrustDialogAccepted"]!.GetValue<bool>());
    }

    // Written as Claude Code writes it: "\n" line ends, and text as it is, not as \uXXXX.
    [Fact]
    public void Trusting_keeps_the_files_own_line_ends_and_text()
    {
        var after = ClaudeTrust.Accepted("{\n  \"name\": \"ა+b\"\n}", @"C:\repos\X").Value;

        Assert.Contains("ა+b", after);
        Assert.DoesNotContain("\r", after);
    }

    // Claude Code looks a folder up by its exact key: an entry spelled with other case is another folder to it, and a
    // yes there does not count. Its own fields are kept and the exact keys are added beside it.
    [Fact]
    public void Only_an_exact_key_counts_as_trusted()
    {
        const string before = """{ "projects": { "c:/Repos/Rustdesk": { "history": [1, 2], "hasTrustDialogAccepted": true } } }""";

        Assert.False(ClaudeTrust.IsAccepted(before, @"C:\repos\Rustdesk"));

        var after = ClaudeTrust.Accepted(before, @"C:\repos\Rustdesk").Value;
        var projects = JsonNode.Parse(after)!["projects"]!.AsObject();

        Assert.Equal(2, projects["c:/Repos/Rustdesk"]!["history"]!.AsArray().Count);
        Assert.True(ClaudeTrust.IsAccepted(after, @"C:\repos\Rustdesk"));
        Assert.True(ClaudeTrust.IsAccepted(after, @"c:\repos\Rustdesk\"));
    }

    [Fact]
    public void A_known_folders_other_fields_are_kept()
    {
        const string before = """{ "projects": { "C:/repos/R": { "history": [1], "hasTrustDialogAccepted": false } } }""";

        var entry = JsonNode.Parse(ClaudeTrust.Accepted(before, @"C:\repos\R").Value)!["projects"]!["C:/repos/R"]!;

        Assert.True(entry["hasTrustDialogAccepted"]!.GetValue<bool>());
        Assert.Single(entry["history"]!.AsArray());
    }

    [Fact]
    public void A_missing_file_becomes_one_holding_only_the_trust()
    {
        var after = ClaudeTrust.Accepted(null, @"C:\repos\Rustdesk").Value;

        Assert.True(ClaudeTrust.IsAccepted(after, @"C:\repos\Rustdesk\"));
    }

    // A yes on a drive's root would trust everything on the drive, since Claude Code trusts what is under a trusted folder.
    [Theory]
    [InlineData(@"E:\")]
    [InlineData("e:/")]
    [InlineData("E:")]
    public void A_drives_root_is_never_trusted(string folder) =>
        Assert.True(ClaudeTrust.Accepted("{}", folder).IsFailure);

    // Half a file (a session in the middle of its own write) is never overwritten: the caller tries again.
    [Theory]
    [InlineData("{ \"projects\": {")]
    [InlineData("[1, 2]")]
    [InlineData("{ \"a\": 1, \"a\": 2 }")]
    public void Text_that_is_not_a_JSON_object_is_refused(string before) =>
        Assert.True(ClaudeTrust.Accepted(before, @"C:\repos\Rustdesk").IsFailure);

    [Fact]
    public void A_blank_folder_is_refused() =>
        Assert.True(ClaudeTrust.Accepted("{}", "  ").IsFailure);

    [Fact]
    public void A_folder_not_listed_or_not_accepted_is_not_trusted()
    {
        Assert.False(ClaudeTrust.IsAccepted("""{ "projects": {} }""", @"C:\repos\X"));
        Assert.False(ClaudeTrust.IsAccepted("""{ "projects": { "C:/repos/X": { "hasTrustDialogAccepted": false }, "c:/repos/X": { "hasTrustDialogAccepted": true } } }""", @"C:\repos\X"));
        Assert.False(ClaudeTrust.IsAccepted("not json", @"C:\repos\X"));
    }
}
