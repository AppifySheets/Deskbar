using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Deskbar.Windows.Packaging;

// The one question the Microsoft Store (MSIX) packaging design keys on: is this process running from
// a package? Everything that differs between the Store copy and the loose exe (autostart, updates,
// the single-instance rule, entitlement) switches on IsPackaged, so it is answered here once.
// Design: docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md
//
// Asked through kernel32's GetCurrentPackageFullName with the usual length probe: a first call with
// a zero-length buffer returns APPMODEL_ERROR_NO_PACKAGE (15700) when the process has no package
// identity, or ERROR_INSUFFICIENT_BUFFER (122) when it has one and wants a bigger buffer. A process
// cannot gain or lose package identity while it runs, so the answer is computed on first use and
// cached for the process.
public static class PackageIdentity
{
    const int AppModelErrorNoPackage = 15700;
    const int ErrorInsufficientBuffer = 122;
    const int ErrorSuccess = 0;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetCurrentPackageFullName(ref int packageFullNameLength, StringBuilder? packageFullName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetCurrentPackageFamilyName(ref int packageFamilyNameLength, StringBuilder? packageFamilyName);

    // Answered once for the process. Lazy so a test that only uses Override never touches kernel32.
    static readonly Lazy<bool> actual = new(() => IsPackagedProbe());

    static readonly Lazy<string?> familyName = new(() => ReadFamilyName());

    // Test-only stand-in for the real answer, null meaning "ask Windows". An AsyncLocal, not a plain
    // static and not a thread-static: xunit runs test classes in parallel, so a plain static would let
    // one test's override leak into every other running test; a thread-static would be invisible to
    // the fresh STA thread a Windows test starts to build real WPF windows. An AsyncLocal flows to
    // threads and tasks a test starts (ExecutionContext is captured on start) and nowhere else.
    static readonly AsyncLocal<bool?> overrideValue = new();

    // True when the process runs from an MSIX package (the Store copy).
    public static bool IsPackaged => overrideValue.Value ?? actual.Value;

    // Not affected by Override: always the real process's family name.
    // The package family name (Petre.Deskbar_n8ch8v53xw03y for the Store copy), or null unpackaged.
    public static string? PackageFamilyName => familyName.Value;

    // Where the package's execution alias lands: %LOCALAPPDATA%\Microsoft\WindowsApps\deskbar.exe.
    public static string AliasPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft", "WindowsApps", "deskbar.exe");

    // Makes IsPackaged answer the given value until the returned handle is disposed, then restores
    // whatever was set before. For tests only, hence internal.
    internal static IDisposable Override(bool packaged)
    {
        var previous = overrideValue.Value;
        overrideValue.Value = packaged;
        return new Restore(() => overrideValue.Value = previous);
    }

    // Length probe: no buffer, so the call can only say "no package" or "I need N characters".
    static bool IsPackagedProbe()
    {
        var length = 0;
        return GetCurrentPackageFullName(ref length, null) switch
        {
            ErrorInsufficientBuffer => true,
            AppModelErrorNoPackage => false,
            // Anything else is not a documented answer; treat it as unpackaged, the safe default
            // (the loose-exe behaviour is the one that works everywhere).
            _ => false,
        };
    }

    // Same probe, then a second call with a buffer of the size the first one asked for.
    static string? ReadFamilyName()
    {
        var length = 0;
        if (GetCurrentPackageFamilyName(ref length, null) != ErrorInsufficientBuffer) return null;
        var buffer = new StringBuilder(length);
        return GetCurrentPackageFamilyName(ref length, buffer) == ErrorSuccess ? buffer.ToString() : null;
    }

    sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}
