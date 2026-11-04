using CSharpFunctionalExtensions;
using Deskbar.Core.Control;

namespace Deskbar.Core.Tests.Control;

// `deskbar find [text]`: the remote twin of quick find's shortcut, since every command the app offers has one.
// The box is the app's to open, so the composition root hands the opener in; left null, `find` refuses.
public class RemoteControlFindTests
{
    static WorkspaceManager Manager() =>
        new(new FakeDesktops(), new FakeMonitor(), new FakeTitles(), new FakeStore());

    [Fact]
    public void Find_opens_quick_find_with_the_text()
    {
        string? opened = null;
        var control = new RemoteControl(Manager(), showFind: text => { opened = text; return Result.Success(); });

        var reply = control.Execute(["find", "inv", "10"]);

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.Equal("inv 10", opened);
    }

    [Fact]
    public void Find_with_nothing_typed_opens_an_empty_box()
    {
        string? opened = null;
        var control = new RemoteControl(Manager(), showFind: text => { opened = text; return Result.Success(); });

        Assert.Equal(ControlReply.Ok, control.Execute(["find"]).ExitCode);
        Assert.Equal("", opened);
    }

    [Fact]
    public void Find_without_the_app_refuses() =>
        Assert.NotEqual(ControlReply.Ok, new RemoteControl(Manager()).Execute(["find"]).ExitCode);
}
