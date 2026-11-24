using Deskbar.App;

namespace Deskbar.Windows.Tests;

// The trace's one switch (%APPDATA%\Deskbar\trace.on, or DESKBAR_TRACE=1) belongs to the app, never to a test host.
// The test host resolves the marker through AppPaths like the app does, and without DESKBAR_APPDATA that is the
// real profile: on a machine where tracing was left on, every run of this suite (on the hidden desktop or not)
// appended the tests' own bar, strip and click lines to the real %TEMP%\deskbar-trace.log, interleaved with the
// running Deskbar's, so a log read after a test run described windows that never existed.
public class ClickTraceTests
{
    // The rule itself, so it is checked on every machine, whatever the marker says there.
    [Theory]
    [InlineData("Deskbar", true, false, false, true)]
    [InlineData("Deskbar", false, true, false, true)]
    [InlineData("Deskbar", false, false, true, true)]
    [InlineData("Deskbar", false, false, false, false)]
    [InlineData("testhost", true, true, true, false)]
    [InlineData(null, true, true, true, false)]
    [InlineData("TaskSpaces.App", true, true, true, false)]
    public void Only_the_app_traces_and_only_when_a_switch_is_on(
        string? entryAssembly, bool environment, bool marker, bool oldMarker, bool on) =>
        Assert.Equal(on, ClickTrace.SwitchedOn(entryAssembly, environment, marker, oldMarker));

    // This process, a test host: off even on a machine where trace.on exists. On Petre's machine, with the marker
    // in place, this failed before the fix.
    [Fact]
    public void A_test_host_never_traces() => Assert.False(ClickTrace.On);
}
