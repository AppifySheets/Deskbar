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

    // An entry an older Deskbar wrote, `cmd.exe /c "...claude-hook.cmd"`, never ran under Claude Code (see ClaudeSkill's
    // header). The next start swaps it for the current command, once, and leaves the bell beside it alone.
    [Fact]
    public void An_older_cmd_exe_hook_is_replaced()
    {
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        var old = $"cmd.exe /c \"{Path.Combine(Deskbar, "claude-hook.cmd")}\"";
        File.WriteAllText(Settings, $$"""
            {
              "hooks": {
                "Stop": [
                  { "matcher": "", "hooks": [ { "type": "command", "command": "cmd.exe /c C:\\bell.bat" } ] },
                  { "hooks": [ { "type": "command", "command": {{System.Text.Json.JsonSerializer.Serialize(old)}} } ] }
                ]
              }
            }
            """);

        ClaudeSkill.Sync(true, home, Deskbar);

        var json = File.ReadAllText(Settings);
        Assert.Equal([@"cmd.exe /c C:\bell.bat", ClaudeSkill.HookCommand(Path.Combine(Deskbar, "claude-hook.cmd"))],
            Commands(json, "Stop"));
    }

    // The command Deskbar writes, run the way Claude Code runs a hook on Windows: through Git Bash, with the hook's
    // JSON on stdin and CLAUDE_PROJECT_DIR set. A stand-in deskbar.cmd records what it was called with. The old
    // `cmd.exe /c` command fails this: Git Bash turns /c into C:/, cmd.exe sits at a prompt eating stdin, and the
    // stand-in is never called.
    [Fact]
    public void The_hook_reaches_deskbar_when_git_bash_runs_it()
    {
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        ClaudeSkill.Sync(true, home, Deskbar);
        var called = Path.Combine(home, "called.txt");
        File.WriteAllText(Path.Combine(Deskbar, "deskbar.cmd"), $"@echo %*> \"{called}\"\r\n");
        var project = Path.Combine(home, "My Project");
        var command = Commands(File.ReadAllText(Settings), "Stop").Single(c => c.Contains("claude-hook.cmd"));

        // Git for Windows' own bash, by path: a bare `bash` on PATH can be WSL's, which is not what Claude Code uses.
        var bash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
        Assert.True(File.Exists(bash), $"Git Bash not found at {bash}");
        var start = new System.Diagnostics.ProcessStartInfo(bash) { RedirectStandardInput = true, CreateNoWindow = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(command);
        start.Environment["CLAUDE_PROJECT_DIR"] = project;
        using var hook = System.Diagnostics.Process.Start(start)!;
        hook.StandardInput.Write("""{"hook_event_name":"Stop"}""");
        hook.StandardInput.Close();
        Assert.True(hook.WaitForExit(30_000), "the hook did not finish");

        Assert.Equal(0, hook.ExitCode);
        Assert.True(File.Exists(called), "the hook never called deskbar.cmd");
        Assert.Equal($"attention --from \"{project}\"", File.ReadAllText(called).Trim());
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

    string Settled(string json)
    {
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        File.WriteAllText(Settings, json);
        return json;
    }

    const string Repeated = """{"hooks":{"Stop":[]},"hooks":{"Notification":[]}}""";

    // A repeated key, anywhere in the file, counts as a file that does not parse: rewriting it would drop one of the
    // values. Sync says so by returning false, and nothing is written, not even the backup.
    [Theory]
    [InlineData(Repeated, true)]
    [InlineData(Repeated, false)]
    [InlineData("""{"a":1,"a":2}""", true)]
    [InlineData("""{"a":1,"a":2}""", false)]
    [InlineData("""{"x":{"a":1,"a":2}}""", true)]
    [InlineData("""{"x":{"a":1,"a":2}}""", false)]
    public void A_settings_file_that_does_not_parse_is_left_alone(string odd, bool on)
    {
        Settled(odd);

        var result = ClaudeHooks.Sync(on, Settings, "\"C:/x/claude-hook.cmd\"");

        Assert.False(result);
        Assert.Equal(odd, File.ReadAllText(Settings));
        Assert.False(File.Exists(Settings + ".deskbar-backup"));
        Assert.False(File.Exists(Settings + ".deskbar-new"));
    }

    const string ForeignCommandNumber = """{"hooks":{"Stop":[{"hooks":[{"type":"command","command":5}]}]}}""";
    const string ForeignCommandObject = """{"hooks":{"Stop":[{"hooks":[{"type":"command","command":{}}]}]}}""";
    const string ForeignString = """{"hooks":{"Stop":["x"]}}""";
    const string ForeignNull = """{"hooks":{"Stop":[null]}}""";
    const string ForeignHooksNotObject = """{"hooks":{"Stop":[{"hooks":[5]}]}}""";

    // The text of the foreign Stop entry, as it is written back: the entry itself is what must survive.
    static string ForeignEntryOf(string json) =>
        System.Text.Json.Nodes.JsonNode.Parse(json)!["hooks"]!["Stop"]!.AsArray()[0]?.ToJsonString() ?? "null";

    static int HookEntries(string json, string hookEvent) =>
        System.Text.Json.Nodes.JsonNode.Parse(json)!["hooks"]![hookEvent]!.AsArray()
            .Count(e => e?.ToJsonString().Contains("claude-hook.cmd") == true);

    // One odd entry that is not Deskbar's must not stop Deskbar's own from being added; it stays where it was.
    [Theory]
    [InlineData(ForeignCommandNumber)]
    [InlineData(ForeignCommandObject)]
    [InlineData(ForeignString)]
    [InlineData(ForeignNull)]
    [InlineData(ForeignHooksNotObject)]
    public void A_foreign_entry_of_an_odd_shape_is_kept_and_does_not_stop_the_hooks(string odd)
    {
        Settled(odd);

        var result = ClaudeHooks.Sync(true, Settings, "\"C:/x/claude-hook.cmd\"");

        Assert.True(result);
        var json = File.ReadAllText(Settings);
        Assert.Equal(ForeignEntryOf(odd), ForeignEntryOf(json));
        Assert.Equal(1, HookEntries(json, "Stop"));
        Assert.Equal(1, HookEntries(json, "Notification"));
        Assert.Equal(odd, File.ReadAllText(Settings + ".deskbar-backup"));
    }

    // Off, there is nothing of Deskbar's to take out, so the file is not touched.
    [Theory]
    [InlineData(ForeignCommandNumber)]
    [InlineData(ForeignCommandObject)]
    [InlineData(ForeignString)]
    [InlineData(ForeignNull)]
    [InlineData(ForeignHooksNotObject)]
    public void A_foreign_entry_of_an_odd_shape_is_not_touched_when_the_hooks_are_off(string odd)
    {
        Settled(odd);

        var result = ClaudeHooks.Sync(false, Settings, "\"C:/x/claude-hook.cmd\"");

        Assert.True(result);
        Assert.Equal(odd, File.ReadAllText(Settings));
        Assert.False(File.Exists(Settings + ".deskbar-backup"));
    }

    // Beside an entry of Deskbar's, a foreign odd one stays when Deskbar's goes.
    [Fact]
    public void Turning_the_hooks_off_leaves_a_foreign_odd_entry_beside_deskbars()
    {
        Settled("""{"hooks":{"Stop":["x",{"hooks":[{"type":"command","command":"\"C:/x/Deskbar/claude-hook.cmd\""}]}]}}""");

        var result = ClaudeHooks.Sync(false, Settings, "\"C:/x/Deskbar/claude-hook.cmd\"");

        Assert.True(result);
        var stop = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Settings))!["hooks"]!["Stop"]!.AsArray();
        Assert.Single(stop);
        Assert.Equal("x", stop[0]!.GetValue<string>());
    }

    // The skill must not claim hooks that are not there. With a settings file that cannot be parsed, the skill is still
    // written, says the hooks are missing, and points at `attention`; once the file is fixed the next sync brings the
    // claim back.
    [Fact]
    public void The_skill_does_not_claim_hooks_that_were_not_installed()
    {
        Settled(Repeated);

        ClaudeSkill.Sync(true, home, Deskbar);

        var text = File.ReadAllText(Skill);
        Assert.DoesNotContain("hooks already ask", text);
        Assert.Contains("attention", text);
        Assert.Equal(Repeated, File.ReadAllText(Settings));

        File.WriteAllText(Settings, "{}");
        ClaudeSkill.Sync(true, home, Deskbar);

        Assert.Contains("hooks already ask", File.ReadAllText(Skill));
    }

    [Fact]
    public void The_skill_says_the_hooks_ask_when_they_are_installed()
    {
        Settled(WithABell);

        ClaudeSkill.Sync(true, home, Deskbar);

        Assert.Contains("hooks already ask", File.ReadAllText(Skill));
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
