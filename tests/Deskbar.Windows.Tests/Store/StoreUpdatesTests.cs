using Deskbar.Windows.Store;
using Windows.Services.Store;

namespace Deskbar.Windows.Tests.Store;

// What the App is told after it asks the Store to install an update. The Store itself cannot be reached from
// an unpackaged test host, so only the two parts that never touch it are tested: the mapping from the Store's
// state to the App's four cases, and the refusal without a window.
// docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md
public class StoreUpdatesTests
{
    // A Completed answer that came back at all means this process was not ended, so the package waits for the
    // next start: Deferred, never Installed.
    [Fact]
    public void Completed_is_deferred() =>
        Assert.IsType<StoreInstallOutcome.Deferred>(StoreUpdates.OutcomeOf(StorePackageUpdateState.Completed));

    [Fact]
    public void Canceled_is_cancelled() =>
        Assert.IsType<StoreInstallOutcome.Cancelled>(StoreUpdates.OutcomeOf(StorePackageUpdateState.Canceled));

    [Theory]
    [InlineData(StorePackageUpdateState.OtherError)]
    [InlineData(StorePackageUpdateState.ErrorWiFiRequired)]
    [InlineData(StorePackageUpdateState.Pending)]
    public void Anything_else_is_a_failure_naming_the_state(StorePackageUpdateState state)
    {
        var failed = Assert.IsType<StoreInstallOutcome.Failed>(StoreUpdates.OutcomeOf(state));
        Assert.Equal($"The Microsoft Store did not install the update ({state}).", failed.Message);
    }

    // Compatibility mode has no bar, and the Store's install UI needs a window: refused before the Store is
    // asked, so this runs on any machine.
    [Fact]
    public async Task Install_without_a_window_fails_before_asking_the_store()
    {
        var failed = Assert.IsType<StoreInstallOutcome.Failed>(await StoreUpdates.InstallAsync(IntPtr.Zero));
        Assert.Contains("needs a Deskbar window", failed.Message);
    }
}
