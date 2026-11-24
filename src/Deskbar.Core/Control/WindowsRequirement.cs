using CSharpFunctionalExtensions;

namespace Deskbar.Core.Control;

// Deskbar runs on Windows 11 (build 22000, 21H2, or later) and on Windows Server 2022 (build 20348). The
// README says so, and the Store package enforces the Windows 11 half through its manifest (TargetDeviceFamily
// MinVersion="10.0.22000.0", so the Store never offers it to an older Windows; Server has no Store anyway).
// The direct download from GitHub had nothing that did: on Windows 10 it started and then met a
// virtual-desktop API it was never built or tested against (the interop library itself says desktop names
// are not supported there, and a workspace IS a named desktop).
//
// Server 2022 is let through on purpose. Petre runs Deskbar on a Server 2022 machine and it worked there
// until this check refused it ("used to work and should continue working on this server"). The interop
// library (Slions.VirtualDesktop) ships a provider of its own for build 20348, between its Windows 10 one
// (10240) and its first Windows 11 one (22000), so the desktops there are driven by COM interfaces the
// library knows, not by a Windows 10 guess.
//
// So App.OnStartup asks this before anything else, and on an older build shows the message and exits,
// rather than running half-working on an OS nobody tests. Pure, so the cut-off and the wording are settled
// in WindowsRequirementTests.
//
// The version handed in is Environment.OSVersion.Version, which on .NET 5 and later is the real one
// (RtlGetVersion), not the compatibility-shimmed value older .NET reported without a manifest entry.
public static class WindowsRequirement
{
    // Windows 11 kept the 10.0 version number; only the build tells it from Windows 10.
    public const int FirstWindows11Build = 22000;

    // Windows Server 2022, also 10.0. The lowest build Deskbar starts on. Windows 10's last release is
    // 19045 (22H2), and the builds between 20348 and 22000 were only ever Insider previews, so "20348 or
    // later" lets in Server 2022 and Windows 11 and keeps every Windows 10 out, with no edition check.
    public const int WindowsServer2022Build = 20348;

    public static Result Check(Version osVersion) =>
        IsSupported(osVersion)
            ? Result.Success()
            : Result.Failure(MessageFor(osVersion));

    // A major version above 10 is a newer Windows, whatever its build number.
    static bool IsSupported(Version v) =>
        v.Major > 10 || (v.Major == 10 && v.Build >= WindowsServer2022Build);

    // Says what is needed, what this PC has (by build, which is accurate for Windows 10 and Server alike),
    // and how to stop the message coming back: a copy set to start with Windows would show it at every
    // sign-in. On Windows 10 that box is on Task Manager's "Startup" tab ("Startup apps" is Windows 11's name).
    // Server 2022 gets no mention: nearly everyone who reads this is on a Windows 10 desktop, where
    // "Windows 11" is the upgrade that applies.
    static string MessageFor(Version v) =>
        $"Deskbar needs Windows 11.\n\n" +
        $"This PC runs build {v.Build} of Windows, and Windows 11 starts at build {FirstWindows11Build}. " +
        "Deskbar is built on Windows 11's virtual desktops, so it will not start here.\n\n" +
        "If it is set to start with Windows, turn that off on Task Manager's Startup tab.";
}
