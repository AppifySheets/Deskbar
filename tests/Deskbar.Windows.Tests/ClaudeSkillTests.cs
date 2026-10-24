using System.IO;
using Deskbar.App;
using Deskbar.Windows.Control;

namespace Deskbar.Windows.Tests;

// The Claude Code skill Deskbar keeps in ~/.claude/skills/deskbar (ClaudeSkill). Petre: "add a skill for claude per
// user". Each test gets its own scratch home folder, so nothing touches the real ~/.claude.
public sealed class ClaudeSkillTests : IDisposable
{
    readonly string home = Path.Combine(Path.GetTempPath(), "deskbar-skill-" + Guid.NewGuid().ToString("N"));

    string Skill => Path.Combine(home, ".claude", "skills", "deskbar", "SKILL.md");

    // Where deskbar.cmd and the hook script live, inside the scratch home.
    string Deskbar => Path.Combine(home, "AppData", "Deskbar");

    string Settings => Path.Combine(home, ".claude", "settings.json");

    public ClaudeSkillTests() => Directory.CreateDirectory(home);

    // --- the hooks (ClaudeHooks) ----------------------------------------------------------------

    // The user's own hooks, the shape Petre's bell has.
    const string WithABell = """
        {
          "model": "opus",
          "hooks": {
            "Stop": [ { "matcher": "", "hooks": [ { "type": "command", "command": "cmd.exe /c C:\\bell.bat" } ] } ]
          }
        }
        """;

    static string[] Commands(string json, string hookEvent) =>
        System.Text.Json.Nodes.JsonNode.Parse(json)!["hooks"]?[hookEvent]?.AsArray()
            .SelectMany(e => e!["hooks"]!.AsArray().Select(h => h!["command"]!.GetValue<string>())).ToArray() ?? [];

    // Added beside the user's own hooks on both events, with the script they run written next to deskbar.cmd.
    [Fact]
    public void The_hooks_are_added_beside_the_users_own()
    {
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        File.WriteAllText(Settings, WithABell);

        ClaudeSkill.Sync(true, home, Deskbar);

        var json = File.ReadAllText(Settings);
        Assert.Equal(2, Commands(json, "Stop").Length);
        Assert.Contains(@"cmd.exe /c C:\bell.bat", Commands(json, "Stop"));
        Assert.Single(Commands(json, "Notification"));
        Assert.Contains("claude-hook.cmd", Commands(json, "Notification")[0]);
        Assert.Equal("opus", System.Text.Json.Nodes.JsonNode.Parse(json)!["model"]!.GetValue<string>());
        Assert.Contains("@exit /b 0", File.ReadAllText(Path.Combine(Deskbar, "claude-hook.cmd")));
        // The file as it was, kept once.
        Assert.Equal(WithABell, File.ReadAllText(Settings + ".deskbar-backup"));
    }

    // A second start changes nothing, so the file is not rewritten.
    [Fact]
    public void The_hooks_are_not_added_twice()
    {
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        File.WriteAllText(Settings, WithABell);
        ClaudeSkill.Sync(true, home, Deskbar);
        var first = File.ReadAllText(Settings);
        File.SetLastWriteTimeUtc(Settings, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        ClaudeSkill.Sync(true, home, Deskbar);

        Assert.Equal(first, File.ReadAllText(Settings));
        Assert.Equal(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), File.GetLastWriteTimeUtc(Settings));
    }

    // Off takes out Deskbar's entries only, and an event left empty by that goes too.
    [Fact]
    public void Turning_it_off_removes_only_deskbars_hooks()
    {
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        File.WriteAllText(Settings, WithABell);
        ClaudeSkill.Sync(true, home, Deskbar);

        ClaudeSkill.Sync(false, home, Deskbar);

        var json = File.ReadAllText(Settings);
        Assert.Equal([@"cmd.exe /c C:\bell.bat"], Commands(json, "Stop"));
        Assert.Null(System.Text.Json.Nodes.JsonNode.Parse(json)!["hooks"]!["Notification"]);
    }

    // A file a parser would lose something from (comments) is not touched.
    [Fact]
    public void A_settings_file_with_comments_is_left_alone()
    {
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        const string commented = "{\n  // mine\n  \"model\": \"opus\"\n}";
        File.WriteAllText(Settings, commented);

        ClaudeSkill.Sync(true, home, Deskbar);

        Assert.Equal(commented, File.ReadAllText(Settings));
    }

    public void Dispose() => Directory.Delete(home, recursive: true);

    // No Claude Code on the machine, no folder of its own: nothing is written.
    [Fact]
    public void Nothing_is_written_without_a_claude_folder()
    {
        ClaudeSkill.Sync(true, home, Deskbar);

        Assert.False(Directory.Exists(Path.Combine(home, ".claude")));
    }

    [Fact]
    public void The_skill_is_written_and_names_the_commands()
    {
        Directory.CreateDirectory(Path.Combine(home, ".claude"));

        ClaudeSkill.Sync(true, home, Deskbar);

        var text = File.ReadAllText(Skill);
        Assert.StartsWith("---\nname: deskbar\n", text.ReplaceLineEndings("\n"));
        Assert.Contains("deskbar.cmd open", text);
        Assert.Contains("--no-follow", text);
    }

    // Rewritten only when the text differs: an unchanged file keeps its timestamp.
    [Fact]
    public void An_unchanged_skill_is_not_rewritten()
    {
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        ClaudeSkill.Sync(true, home, Deskbar);
        var written = File.GetLastWriteTimeUtc(Skill);
        File.SetLastWriteTimeUtc(Skill, written.AddHours(-1));

        ClaudeSkill.Sync(true, home, Deskbar);

        Assert.Equal(written.AddHours(-1), File.GetLastWriteTimeUtc(Skill));
    }

    // Turning it off removes the file and the folder it made, and leaves the rest of ~/.claude alone.
    [Fact]
    public void Turning_it_off_removes_the_skill()
    {
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        ClaudeSkill.Sync(true, home, Deskbar);

        ClaudeSkill.Sync(false, home, Deskbar);

        Assert.False(Directory.Exists(Path.GetDirectoryName(Skill)));
        Assert.True(Directory.Exists(Path.Combine(home, ".claude", "skills")));
    }

    // A file Deskbar did not write is never deleted, even at its path.
    [Fact]
    public void A_skill_someone_else_wrote_is_left_alone()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Skill)!);
        File.WriteAllText(Skill, "my own notes");

        ClaudeSkill.Sync(false, home, Deskbar);

        Assert.Equal("my own notes", File.ReadAllText(Skill));
    }

    // The exe part of a browser's open command, quoted or not; only an exe that exists counts.
    [Fact]
    public void The_browser_exe_is_read_from_its_open_command()
    {
        var exe = Path.Combine(home, "browser.exe");
        File.WriteAllText(exe, "");

        Assert.Equal(exe, DefaultBrowser.ExeOf($"\"{exe}\" --single-argument %1").Value);
        Assert.True(DefaultBrowser.ExeOf("\"C:\\nowhere\\browser.exe\" %1").HasNoValue);
    }
}
