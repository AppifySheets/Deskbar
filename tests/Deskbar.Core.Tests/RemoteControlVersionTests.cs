using Deskbar.Core.Control;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Tests;

// `deskbar version`: which build answers, from where, and whether it is the Microsoft Store package.
// The Store packaging CI smoke step asks the installed package this and expects "packaged": true.
// The identity is handed in by the composition root because the package check is the Windows side's business.
public class RemoteControlVersionTests
{
    // The version command never touches the manager, so a bare one over the fakes is enough.
    static WorkspaceManager Manager() =>
        new(new FakeDesktops(), new FakeMonitor(), new FakeTitles(), new FakeStore());

    [Fact]
    public void Version_answers_the_identity_handed_in()
    {
        var control = new RemoteControl(Manager(), identity: () => ("2.1.44", @"C:\x\Deskbar.exe", false));
        var reply = control.Execute(["version"]);
        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.Contains("\"version\": \"2.1.44\"", reply.Output);
        Assert.Contains("\"packaged\": false", reply.Output);
    }

    [Fact]
    public void Version_fails_when_no_identity_is_handed_in()
    {
        var reply = new RemoteControl(Manager()).Execute(["version"]);
        Assert.NotEqual(ControlReply.Ok, reply.ExitCode);
        Assert.Contains("version is not available in this build", reply.Output);
    }

    [Fact]
    public void Version_is_listed_in_usage() =>
        Assert.Contains(ControlUsage.Sections.SelectMany(s => s.Commands), c => c.Verb == "version");
}
