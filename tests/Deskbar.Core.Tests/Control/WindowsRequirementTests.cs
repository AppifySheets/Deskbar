using Deskbar.Core.Control;

namespace Deskbar.Core.Tests.Control;

// Deskbar needs Windows 11 (build 22000 or later). The Store package already enforces it through its
// manifest's MinVersion; the direct download from GitHub has nothing else that does, so App.OnStartup asks
// WindowsRequirement and, on an older build, shows its message and exits.
public class WindowsRequirementTests
{
    [Theory]
    [InlineData(10, 0, 22000)] // the first Windows 11 release, 21H2
    [InlineData(10, 0, 22631)] // 23H2
    [InlineData(10, 0, 26200)] // 25H2, Petre's machine
    [InlineData(11, 0, 0)]     // a Windows that bumps the major version is newer, not older
    public void Windows_11_and_later_pass(int major, int minor, int build) =>
        Assert.True(WindowsRequirement.Check(new Version(major, minor, build)).IsSuccess);

    [Theory]
    [InlineData(10, 0, 19045)] // Windows 10 22H2, the last Windows 10
    [InlineData(10, 0, 17763)] // Windows 10 1809 / Server 2019
    [InlineData(10, 0, 20348)] // Server 2022: 10.0, under 22000, no Windows 11 shell
    [InlineData(6, 3, 9600)]   // Windows 8.1
    public void Older_builds_fail(int major, int minor, int build) =>
        Assert.True(WindowsRequirement.Check(new Version(major, minor, build)).IsFailure);

    // The message says what is needed, what this PC has, and how to stop it coming back at every sign-in.
    [Fact]
    public void Message_names_the_requirement_and_this_build()
    {
        var message = WindowsRequirement.Check(new Version(10, 0, 19045)).Error;
        Assert.StartsWith("Deskbar needs Windows 11.", message);
        Assert.Contains("build 19045", message);
        Assert.Contains("Startup tab", message); // Windows 10's name for it, not Windows 11's "Startup apps"
    }
}
