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

    public ClaudeSkillTests() => Directory.CreateDirectory(home);

    public void Dispose() => Directory.Delete(home, recursive: true);

    // No Claude Code on the machine, no folder of its own: nothing is written.
    [Fact]
    public void Nothing_is_written_without_a_claude_folder()
    {
        ClaudeSkill.Sync(true, home);

        Assert.False(Directory.Exists(Path.Combine(home, ".claude")));
    }

    [Fact]
    public void The_skill_is_written_and_names_the_commands()
    {
        Directory.CreateDirectory(Path.Combine(home, ".claude"));

        ClaudeSkill.Sync(true, home);

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
        ClaudeSkill.Sync(true, home);
        var written = File.GetLastWriteTimeUtc(Skill);
        File.SetLastWriteTimeUtc(Skill, written.AddHours(-1));

        ClaudeSkill.Sync(true, home);

        Assert.Equal(written.AddHours(-1), File.GetLastWriteTimeUtc(Skill));
    }

    // Turning it off removes the file and the folder it made, and leaves the rest of ~/.claude alone.
    [Fact]
    public void Turning_it_off_removes_the_skill()
    {
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        ClaudeSkill.Sync(true, home);

        ClaudeSkill.Sync(false, home);

        Assert.False(Directory.Exists(Path.GetDirectoryName(Skill)));
        Assert.True(Directory.Exists(Path.Combine(home, ".claude", "skills")));
    }

    // A file Deskbar did not write is never deleted, even at its path.
    [Fact]
    public void A_skill_someone_else_wrote_is_left_alone()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Skill)!);
        File.WriteAllText(Skill, "my own notes");

        ClaudeSkill.Sync(false, home);

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
