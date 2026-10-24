using Deskbar.Windows.Packaging;

namespace Deskbar.Windows.Tests.Packaging;

// PackageIdentity is the one question the Store packaging design keys on: does this process run
// from an MSIX package? The test host is a plain unpackaged exe, so the real answer is false, and
// Override stands in for the packaged case.
// docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md
[Collection("PackageIdentity")]
public class PackageIdentityTests
{
    [Fact]
    public void Unpackaged_test_host_is_not_packaged() => Assert.False(PackageIdentity.IsPackaged);

    [Fact]
    public void Unpackaged_test_host_has_no_family_name() => Assert.Null(PackageIdentity.PackageFamilyName);

    [Fact]
    public void Override_makes_it_packaged_until_disposed()
    {
        using (PackageIdentity.Override(true))
            Assert.True(PackageIdentity.IsPackaged);
        Assert.False(PackageIdentity.IsPackaged);
    }

    [Fact]
    public void Alias_path_is_under_the_user_WindowsApps_folder() =>
        Assert.EndsWith(@"\Microsoft\WindowsApps\deskbar.exe", PackageIdentity.AliasPath);
}
