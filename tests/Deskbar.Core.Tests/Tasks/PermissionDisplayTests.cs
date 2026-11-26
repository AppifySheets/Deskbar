using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests.Tasks;

// THE CARD NEVER HIDES PART OF A REQUEST (a hard property, the PM's ruling): characters that draw as nothing or as
// something else are shown as marks, every field is shown, long input folds by whole lines, and nothing is dropped.
public class PermissionDisplayTests
{
    static PermissionRequest Request(string tool, params (string Name, string Value)[] fields) =>
        new("s1", tool, @"C:\repos\proj1", "default", fields.Select(f => new PermissionField(f.Name, f.Value)).ToList());

    [Fact]
    public void A_shell_command_leads_and_claudes_description_is_set_apart()
    {
        var text = PermissionDisplay.Of(Request("Bash", ("description", "List files"), ("command", "ls && rm -rf build"), ("timeout", "5")));

        Assert.Equal("command", text.Parts[0].Label);
        Assert.True(text.Parts[0].Main);
        Assert.Equal("ls && rm -rf build", text.Parts[0].Text);
        Assert.Equal("timeout", text.Parts[1].Label);
        Assert.False(text.Parts[1].Main);
        Assert.Equal("List files", text.ClaudeSays);
        Assert.DoesNotContain(text.Parts, p => p.Label == "description");
    }

    [Fact]
    public void An_edit_leads_with_its_file_and_keeps_every_other_field()
    {
        var text = PermissionDisplay.Of(Request("Edit", ("old_string", "a"), ("file_path", @"C:\x\y.cs"), ("new_string", "b"), ("description", "not Claude's words here")));

        Assert.Equal(new[] { "file_path", "old_string", "new_string", "description" }, text.Parts.Select(p => p.Label));
        Assert.Null(text.ClaudeSays);
    }

    [Fact]
    public void An_unknown_tool_leads_with_its_first_field() =>
        Assert.Equal("sql", PermissionDisplay.Of(Request("mcp__db__query", ("sql", "drop table t"), ("db", "main"))).Parts.Single(p => p.Main).Label);

    // Nothing dropped: every field's value is on the card, as a part or as Claude's words.
    [Fact]
    public void Every_field_is_on_the_card()
    {
        var fields = new[] { ("command", "a"), ("description", "b"), ("run_in_background", "true"), ("x", "c") };
        var text = PermissionDisplay.Of(Request("Bash", fields));

        Assert.Equal(fields.Length, text.Parts.Count + (text.ClaudeSays is null ? 0 : 1));
    }

    [Theory]
    [InlineData("echo safe \u202E txt.exe", "⟨U+202E⟩")]   // right-to-left override
    [InlineData("a\u2066b\u2069c", "⟨U+2066⟩")]            // bidi isolate
    [InlineData("rm\u200B -rf", "⟨U+200B⟩")]               // zero width space
    [InlineData("echo \u001b[2Kok", "⟨U+001B⟩")]           // ESC, which can rewrite a terminal line
    [InlineData("first\rsecond", "⟨U+000D⟩")]              // a bare CR, which hides what comes before it in a terminal
    [InlineData("a\tb", "⟨U+0009⟩")]
    [InlineData("a\u2028b", "⟨U+2028⟩")]                   // line separator
    [InlineData("a\uFEFFb", "⟨U+FEFF⟩")]
    [InlineData("a\u3164b", "⟨U+3164⟩")]                   // Hangul filler, drawn blank
    [InlineData("a\uFE0Fb", "⟨U+FE0F⟩")]
    public void Characters_that_hide_or_disguise_text_are_shown_as_marks(string raw, string mark)
    {
        var shown = PermissionDisplay.Visible(raw);

        Assert.Contains(mark, shown);
        Assert.DoesNotContain(raw.First(c => !char.IsAsciiLetterOrDigit(c) && c != ' ' && c != '-' && c != '[' && c != '.'), shown.Replace(mark, ""));
    }

    [Fact]
    public void A_tag_character_outside_the_basic_plane_is_marked() =>
        Assert.Equal("a⟨U+E0041⟩b", PermissionDisplay.Visible("a\U000E0041b"));

    [Fact]
    public void A_lone_surrogate_is_marked_not_replaced() =>
        Assert.Equal("a⟨U+D800⟩b", PermissionDisplay.Visible("a\uD800b"));

    // A command cannot pass plain text off as a mark: the brackets themselves are marked.
    [Fact]
    public void A_fake_mark_in_the_text_is_itself_marked() =>
        Assert.Equal("⟨U+27E8⟩U+202E⟨U+27E9⟩", PermissionDisplay.Visible("⟨U+202E⟩"));

    [Fact]
    public void Ordinary_text_line_breaks_and_other_scripts_stay_as_they_are()
    {
        const string text = "git push fork feature/x\nსალამი Привет 你好 🙂 é";
        Assert.Equal(text, PermissionDisplay.Visible(text));
    }

    [Fact]
    public void A_long_line_is_never_cut()
    {
        var line = "echo " + new string('a', 5000) + " && rm -rf /";
        var fold = PermissionDisplay.Fold(PermissionDisplay.Visible(line));

        Assert.Equal(line, fold.Shown.Single());
        Assert.Empty(fold.Rest);
    }

    [Fact]
    public void Long_input_folds_by_whole_lines_and_loses_none()
    {
        var lines = Enumerable.Range(1, 40).Select(i => $"line {i} && step {i}").ToArray();
        var fold = PermissionDisplay.Fold(string.Join('\n', lines));

        Assert.Equal(PermissionDisplay.FoldLines, fold.Shown.Count);
        Assert.Equal(40 - PermissionDisplay.FoldLines, fold.Rest.Count);
        Assert.Equal(lines, fold.Shown.Concat(fold.Rest));
    }

    [Fact]
    public void Folding_text_with_marks_keeps_every_line_whole()
    {
        var text = PermissionDisplay.Visible(string.Join('\n', Enumerable.Range(1, 20).Select(i => $"x{i}\r\u202E")));
        var fold = PermissionDisplay.Fold(text);

        Assert.Equal(text, string.Join('\n', fold.Shown.Concat(fold.Rest)));
    }

    [Fact]
    public void The_tool_and_the_folder_are_shown_with_marks_too()
    {
        var text = PermissionDisplay.Of(new PermissionRequest("s", "Ba\u202Esh", "C:\\a\u200Bb", default, []));

        Assert.Equal("Ba⟨U+202E⟩sh", text.Tool);
        Assert.Equal("C:\\a⟨U+200B⟩b", text.Folder);
    }

    // The review's L2: an MCP tool names its own fields, so a field's name is marked as its value is.
    [Fact]
    public void A_fields_name_is_shown_with_marks_too()
    {
        var text = PermissionDisplay.Of(Request("mcp__x__run", ("sql\u202E", "drop table t"), ("db\nname", "main")));

        Assert.Equal(new[] { "sql⟨U+202E⟩", "db⟨U+000A⟩name" }, text.Parts.Select(p => p.Label));
        Assert.Equal("sql⟨U+202E⟩", text.Parts.Single(p => p.Main).Label);
    }
}
