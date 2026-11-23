using CSharpFunctionalExtensions;

namespace Deskbar.Core.Control;

// Deskbar runs on Windows 11 only: build 22000 (21H2) or later. The README says so, and the Store package
// enforces it through its manifest (TargetDeviceFamily MinVersion="10.0.22000.0", so the Store never offers
// it to an older Windows). The direct download from GitHub had nothing that did: on Windows 10 it started
// and then met a virtual-desktop API it was never built or tested against (the interop library itself says
// desktop names are not supported there, and a workspace IS a named desktop).
//
// So App.OnStartup asks this before anything else, and on an older build shows the message and exits,
// rather than running half-working on an OS nobody tests. Pure, so the cut-off and the wording are settled
// in WindowsRequirementTests.
//
// The version handed in is Environment.OSVersion.Version, which on .NET 5 and later is the real one
// (RtlGetVersion), not the compatibility-shimmed value older .NET reported without a manifest entry.
public static class WindowsRequirement
{
    // Windows 11 kept the 10.0 version number; only the build tells it from Windows 10. Server 2022
    // (10.0.20348) is also below this line, and it has no Windows 11 shell either.
    public const int FirstWindows11Build = 22000;

    public static Result Check(Version osVersion) =>
        IsWindows11OrLater(osVersion)
            ? Result.Success()
            : Result.Failure(MessageFor(osVersion));

    // A major version above 10 is a newer Windows, whatever its build number.
    static bool IsWindows11OrLater(Version v) =>
        v.Major > 10 || (v.Major == 10 && v.Build >= FirstWindows11Build);

    // Says what is needed, what this PC has (by build, which is accurate for Windows 10 and Server alike),
    // and how to stop the message coming back: a copy set to start with Windows would show it at every
    // sign-in. On Windows 10 that box is on Task Manager's "Startup" tab ("Startup apps" is Windows 11's name).
    static string MessageFor(Version v) =>
        $"Deskbar needs Windows 11.\n\n" +
        $"This PC runs build {v.Build} of Windows, and Windows 11 starts at build {FirstWindows11Build}. " +
        "Deskbar is built on Windows 11's virtual desktops, so it will not start here.\n\n" +
        "If it is set to start with Windows, turn that off on Task Manager's Startup tab.";
}
