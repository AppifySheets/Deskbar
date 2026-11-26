using System.IO;
using System.Text.Json.Nodes;
using Deskbar.App;
using Deskbar.Core.Tasks;

namespace Deskbar.Windows.Tests;

// The permission hook's entry in ~/.claude/settings.json and its script (ClaudeHooks.PermissionEntry, ClaudeSkill), in a
// scratch home folder only: nothing here touches the real ~/.claude.
public sealed class ClaudePermissionHookInstallTests : IDisposable
{
    readonly string home = Path.Combine(Path.GetTempPath(), "deskbar-perm-hook-" + Guid.NewGuid().ToString("N"));

    string Deskbar => Path.Combine(home, "AppData", "Deskbar");

    string Settings => Path.Combine(home, ".claude", "settings.json");

    string Script => Path.Combine(Deskbar, ClaudeSkill.PermissionHookScriptName);

    public ClaudePermissionHookInstallTests() => Directory.CreateDirectory(Path.Combine(home, ".claude"));

    public void Dispose()
    {
        if (Directory.Exists(home)) Directory.Delete(home, true);
    }

    // The user's own PermissionRequest hook, which must stay whatever Deskbar does.
    const string WithTheirOwn = """
        {
          "hooks": {
            "PermissionRequest": [ { "matcher": "Bash", "hooks": [ { "type": "command", "command": "C:/mine/approve.cmd" } ] } ]
          }
        }
        """;

    JsonArray? Entries() => JsonNode.Parse(File.ReadAllText(Settings))!["hooks"]?["PermissionRequest"]?.AsArray();

    JsonObject? Ours() => Entries()?.Select(e => e!.AsObject())
        .FirstOrDefault(e => e["hooks"]![0]!["command"]!.GetValue<string>().Contains(ClaudeSkill.PermissionHookScriptName));

    [Fact]
    public void On_adds_one_entry_beside_the_users_own_with_the_script()
    {
        File.WriteAllText(Settings, WithTheirOwn);

        ClaudeSkill.Sync(true, true, home, Deskbar, waiter: true, permissions: true);

        Assert.Equal(2, Entries()!.Count);
        Assert.Equal("C:/mine/approve.cmd", Entries()![0]!["hooks"]![0]!["command"]!.GetValue<string>());
        var hook = Ours()!["hooks"]![0]!.AsObject();
        Assert.Equal(ClaudeSkill.HookCommand(Script), hook["command"]!.GetValue<string>());
        Assert.True(File.Exists(Script));
        Assert.Equal(ClaudeSkill.PermissionScript(), File.ReadAllText(Script));
    }

    // No matcher (every tool reaches the script), a timeout past the script's own wait, and NO onFailure: "block" would
    // make a failed hook deny the call, where a failure must leave the terminal's dialog.
    [Fact]
    public void The_entry_has_no_matcher_no_onFailure_and_outlasts_the_scripts_wait()
    {
        ClaudeSkill.Sync(true, true, home, Deskbar, waiter: true, permissions: true);

        var ours = Ours()!;
        Assert.Null(ours["matcher"]);
        var hook = ours["hooks"]![0]!.AsObject();
        Assert.Null(hook["onFailure"]);
        Assert.Null(hook["async"]);
        Assert.True(hook["timeout"]!.GetValue<long>() > PermissionDesk.ScriptSeconds + 60);
    }

    [Theory]
    [InlineData(true, true, false)]     // the setting off
    [InlineData(true, false, true)]     // the task hooks off
    [InlineData(false, true, true)]     // "Teach Claude Code about Deskbar" off
    public void Off_takes_out_only_deskbars_entry_and_its_script(bool on, bool tasks, bool permissions)
    {
        File.WriteAllText(Settings, WithTheirOwn);
        ClaudeSkill.Sync(true, true, home, Deskbar, waiter: true, permissions: true);

        ClaudeSkill.Sync(on, tasks, home, Deskbar, waiter: true, permissions: permissions);

        Assert.Equal("C:/mine/approve.cmd", Entries()!.Single()!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.False(File.Exists(Script));
    }

    // The default: the setting is off, so a first install adds no permission entry at all.
    [Fact]
    public void The_default_adds_nothing()
    {
        ClaudeSkill.Sync(true, true, home, Deskbar);

        Assert.Null(Entries());
        Assert.False(File.Exists(Script));
    }
}
