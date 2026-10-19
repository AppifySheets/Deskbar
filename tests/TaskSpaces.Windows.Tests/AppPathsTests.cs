using System.IO;
using TaskSpaces.App;

namespace TaskSpaces.Windows.Tests;

// DESKBAR_APPDATA, the scratch root that keeps a Debug 2.0 build away from the real profile. The
// variable is read once per process, so these test the rule that turns its value into a root, not the
// variable itself (setting it here would fix the root for every other test in the run).
public class AppPathsTests
{
    [Fact]
    public void An_absolute_override_is_the_root() =>
        Assert.Equal(@"C:\scratch\deskbar-root", AppPaths.OverrideRootFrom(@"  C:\scratch\deskbar-root "));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_value_means_no_override(string? value) =>
        Assert.Null(AppPaths.OverrideRootFrom(value));

    // A relative value resolves against the current directory, and for a copy started by the Run key
    // at login that is System32: the root meant to protect the real profile would land among Windows'
    // own files. No relative value can be made safe, so it is ignored and the real %APPDATA% is used.
    // The last two are rooted but not fully qualified: one leans on the current drive, the other on
    // that drive's current directory.
    [Theory]
    [InlineData("scratch")]
    [InlineData(@"..\scratch")]
    [InlineData(@"scratch\deskbar-root")]
    [InlineData(@"\scratch")]
    [InlineData(@"C:scratch")]
    public void A_relative_override_is_ignored(string value) =>
        Assert.Null(AppPaths.OverrideRootFrom(value));
}
