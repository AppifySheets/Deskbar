using CSharpFunctionalExtensions;
using Deskbar.App.Startup;
using Deskbar.Windows.Packaging;

namespace Deskbar.Windows.Tests.Startup;

// "Start Deskbar with Windows" has two mechanisms under one switch: the direct build writes an HKCU Run
// value, the Store package asks Windows for its startup task (TaskId Deskbar in AppxManifest.xml).
// docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md
//
// Only the chooser is tested here. RunValueStartup is a thin wrapper over StartupRegistration, whose own
// tests (StartupRegistrationTests) cover it without writing the registry, and PackagedStartup needs a real
// package identity to say anything, which the test host does not have.
public class StartupSwitchTests
{
    [Fact]
    public void Unpackaged_chooser_is_the_run_value_switch() => Assert.IsType<RunValueStartup>(StartupSwitch.Current);

    [Fact]
    public void Packaged_chooser_is_the_startup_task_switch()
    {
        using var _ = PackageIdentity.Override(true);
        Assert.IsType<PackagedStartup>(StartupSwitch.Current);
    }

    [Fact]
    public void Override_replaces_the_switch_until_disposed()
    {
        var fake = new FakeStartupSwitch(StartupAvailability.Available, enabled: false);
        using (StartupSwitch.Override(fake))
            Assert.Same(fake, StartupSwitch.Current);
        Assert.IsType<RunValueStartup>(StartupSwitch.Current);
    }

    // The override has to reach the STA thread a Windows test starts to build a ManageWindow, or the window
    // would read the real switch while the test thinks it is faking it.
    [Fact]
    public void Override_flows_to_a_thread_the_test_starts()
    {
        var fake = new FakeStartupSwitch(StartupAvailability.Available, enabled: false);
        using var _ = StartupSwitch.Override(fake);
        IStartupSwitch? seen = null;
        StaThread.Run(() => seen = StartupSwitch.Current);
        Assert.Same(fake, seen);
    }
}

// A stand-in for either mechanism, so Manage's box can be driven without touching the Run value or a
// startup task. Counts the calls, and flips IsEnabled the way the real switches would on success.
sealed class FakeStartupSwitch(StartupAvailability availability, bool enabled) : IStartupSwitch
{
    public bool IsEnabled { get; private set; } = enabled;
    public StartupAvailability Availability => availability;
    public int Enables { get; private set; }
    public int Disables { get; private set; }

    public Result Enable()
    {
        Enables++;
        IsEnabled = true;
        return Result.Success();
    }

    public Result Disable()
    {
        Disables++;
        IsEnabled = false;
        return Result.Success();
    }
}
