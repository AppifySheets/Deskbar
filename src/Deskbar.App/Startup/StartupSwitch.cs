using Deskbar.Windows.Packaging;

namespace Deskbar.App.Startup;

// Picks the start-with-Windows mechanism for this process: the package's startup task in the Store copy,
// the HKCU Run value in the direct build.
//
// Chosen once per process in effect: PackageIdentity.IsPackaged is answered once and cached, and both
// switches are stateless singletons, so Current always hands back the same instance in the real app. The
// choice is still made on each read rather than frozen in a field, so that PackageIdentity.Override in a
// test sees the packaged switch even after another test has read the unpackaged one.
//
// Override replaces the switch for a test's scope. An AsyncLocal for the reason PackageIdentity.Override
// gives: xunit runs test classes side by side, so a plain static would leak one test's fake into another,
// and a thread-static would be invisible to the STA thread a Windows test starts to build a ManageWindow.
// An AsyncLocal flows to threads and tasks the test starts and nowhere else.
public static class StartupSwitch
{
    static readonly Lazy<IStartupSwitch> runValue = new(() => new RunValueStartup());
    static readonly Lazy<IStartupSwitch> packaged = new(() => new PackagedStartup());
    static readonly AsyncLocal<IStartupSwitch?> overrideValue = new();

    public static IStartupSwitch Current =>
        overrideValue.Value
        // Store packaging design (docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md):
        // the Store copy starts through its startup task, the direct build through the Run value.
        ?? (PackageIdentity.IsPackaged ? packaged.Value : runValue.Value);

    // Makes Current answer the fake until the returned handle is disposed, then restores whatever was set
    // before. For tests only, hence internal.
    internal static IDisposable Override(IStartupSwitch fake)
    {
        var previous = overrideValue.Value;
        overrideValue.Value = fake;
        return new Restore(() => overrideValue.Value = previous);
    }

    sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}
