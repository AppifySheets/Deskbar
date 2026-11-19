using System.Text.Json;
using Deskbar.Core.Control;

namespace Deskbar.Core.Tests.Control;

// `keep-space <workspace> on|off` on the control pipe: the twin of the row menu's "Keep the space when a
// window closes". It is a tiling command, so it is refused like `even` is, with the same message, where the copy
// may not tile. Any word other than on or off matches no dispatch arm and is answered the way a known verb with
// the wrong arguments is.
public class RemoteControlKeepSpaceTests
{
    static WorkspaceManager Manager()
    {
        var manager = new WorkspaceManager(new FakeDesktops(), new FakeMonitor(), new FakeTitles(), new FakeStore());
        manager.AddWorkspace("Work");
        return manager;
    }

    static bool Keeps(WorkspaceManager manager) =>
        manager.State.Workspaces.Single(w => w.Name == "Work").TileKeepsSpace;

    [Fact]
    public void Keep_space_on_turns_it_on()
    {
        var manager = Manager();

        var reply = new RemoteControl(manager, isPro: () => true).Execute(["keep-space", "Work", "on"]);

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.True(Keeps(manager));
        // The reply shows the state the command left (final review, M5), as every workspace command's reply does.
        Assert.True(JsonDocument.Parse(reply.Output).RootElement.GetProperty("keepsSpace").GetBoolean());
    }

    [Fact]
    public void Keep_space_off_turns_it_off()
    {
        var manager = Manager();
        var remote = new RemoteControl(manager, isPro: () => true);
        remote.Execute(["keep-space", "Work", "on"]);

        var reply = remote.Execute(["keep-space", "Work", "off"]);

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.False(Keeps(manager));
        Assert.False(JsonDocument.Parse(reply.Output).RootElement.GetProperty("keepsSpace").GetBoolean());
    }

    [Fact]
    public void Keep_space_needs_Pro()
    {
        var manager = Manager();

        var reply = new RemoteControl(manager, isPro: () => false).Execute(["keep-space", "Work", "on"]);

        Assert.NotEqual(ControlReply.Ok, reply.ExitCode);
        Assert.Contains("Tiling needs Deskbar Pro.", reply.Output);
        Assert.False(Keeps(manager));
    }

    [Fact]
    public void Keep_space_with_another_word_is_a_usage_error()
    {
        var manager = Manager();
        var remote = new RemoteControl(manager, isPro: () => true);

        var reply = remote.Execute(["keep-space", "Work", "maybe"]);

        Assert.NotEqual(ControlReply.Ok, reply.ExitCode);
        // Exactly what a known verb with an argument shape no arm matches gets: the dispatcher's own
        // "Wrong arguments" reply carrying the command's usage line.
        Assert.Equal($"Wrong arguments for keep-space.\n{ControlUsage.LineFor("keep-space")}", reply.Output);
        Assert.False(Keeps(manager));
    }

    [Fact]
    public void Keep_space_on_an_unknown_workspace_fails()
    {
        var reply = new RemoteControl(Manager(), isPro: () => true).Execute(["keep-space", "Nope", "on"]);

        Assert.NotEqual(ControlReply.Ok, reply.ExitCode);
        Assert.Contains("Nope", reply.Output);
    }
}
