using System.IO;
using Microsoft.Win32;
using TaskSpaces.App;

namespace TaskSpaces.Windows.Tests;

// Petre: "taskspaces didn't start with windows".
//
// The Run key pointed at tests\TaskSpaces.Windows.Tests\bin\...\testhost.exe. ManageWindow sets its
// "Start with Windows" box from the registry when it is built, that raises Checked, and the handler
// calls Enable(), which writes Environment.ProcessPath: under a test run, the test host. Three test
// classes build a ManageWindow, so every run of the suite repointed his real login entry. It went
// unnoticed since August because the next app start re-asserts the value; it bit when the machine
// rebooted after a test run and before the app was started again. Measured: running only
// ManageSettingsTabTests turned the value from TaskSpaces.App.exe into testhost.exe.
//
// So only the app itself may write or remove the entry. These tests run under exactly the host that
// broke it, which is what makes them the regression test.
public class StartupRegistrationTests
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    static object? RunValue() => Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue("Deskbar");

    [Theory]
    [InlineData("Deskbar", true)]
    // The old entry assembly name is refused on purpose, so a 1.x build never registers under the new value.
    [InlineData("TaskSpaces.App", false)]
    [InlineData("testhost", false)]
    [InlineData("ReSharperTestRunner", false)]
    [InlineData(null, false)]
    public void Only_the_app_itself_owns_the_entry(string? entryAssembly, bool expected) =>
        Assert.Equal(expected, StartupRegistration.IsTheApp(entryAssembly));

    // Only a published, single-file build re-asserts the entry on start; a development copy, an exe beside
    // its dlls, leaves it alone (Petre: "fix autostart only", after dev copies had taken the entry over).
    [Theory]
    [InlineData("", true)]
    [InlineData(@"C:\repos\TaskSpaces\src\TaskSpaces.App\bin\Debug\net10.0-windows10.0.19041.0\Deskbar.dll", false)]
    [InlineData(@"C:\Users\me\AppData\Local\Temp\claude\scratchpad\live-stats\Deskbar.dll", false)]
    public void Only_a_published_build_counts_as_installed(string assemblyLocation, bool published) =>
        Assert.Equal(published, StartupRegistration.IsPublished(assemblyLocation));

    // This suite runs as a multi-file build, so re-asserting from here must leave the entry as it was even if
    // the entry-assembly guard were ever loosened.
    [Fact]
    public void Reasserting_from_a_development_build_leaves_the_entry_alone()
    {
        var before = RunValue();
        StartupRegistration.ReassertIfEnabled();
        Assert.Equal(before, RunValue());
    }

    // The Start menu shortcut (Petre: "add deskbar to start, so if it stops i have a way to start it
    // manually") is rewritten only when missing or starting another exe, such as the version before an update.
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData(@"C:\Apps\Deskbar\Deskbar-2.1.5-win-x64.exe", true)]
    [InlineData(@"C:\Apps\Deskbar\Deskbar-2.1.8-win-x64.exe", false)]
    [InlineData(@"c:\apps\deskbar\DESKBAR-2.1.8-WIN-X64.EXE", false)]
    public void The_start_menu_shortcut_is_rewritten_only_when_it_is_missing_or_stale(string? current, bool rewrite) =>
        Assert.Equal(rewrite, StartMenuShortcut.NeedsWriting(current, @"C:\Apps\Deskbar\Deskbar-2.1.8-win-x64.exe"));

    // From the test host it writes nothing: the shortcut (or its absence) is exactly as it was.
    [Fact]
    public void The_start_menu_shortcut_is_left_alone_from_a_test_host()
    {
        var link = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Deskbar.lnk");
        var before = File.Exists(link) ? File.GetLastWriteTimeUtc(link) : (DateTime?)null;

        Assert.True(StartMenuShortcut.Ensure().IsSuccess);

        Assert.Equal(before, File.Exists(link) ? File.GetLastWriteTimeUtc(link) : null);
    }

    [Fact]
    public void Enabling_from_a_test_host_refuses_and_leaves_the_entry_alone()
    {
        var before = RunValue();

        Assert.True(StartupRegistration.Enable().IsFailure);

        Assert.Equal(before, RunValue());
    }

    // The other direction would be worse than the bug: a test that unticks the box would delete his
    // real registration outright.
    [Fact]
    public void Disabling_from_a_test_host_refuses_and_leaves_the_entry_alone()
    {
        var before = RunValue();

        Assert.True(StartupRegistration.Disable().IsFailure);

        Assert.Equal(before, RunValue());
    }
}
