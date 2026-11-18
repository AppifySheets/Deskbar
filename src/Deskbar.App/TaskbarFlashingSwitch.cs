using System.IO;
using System.Reflection;
using CSharpFunctionalExtensions;
using Deskbar.Core.Shell;
using Deskbar.Windows.Packaging;
using Deskbar.Windows.Processes;
using Microsoft.Win32;

namespace Deskbar.App;

// The Windows half of holding "Show flashing on taskbar apps" off while Deskbar runs. TaskbarFlashing.Plan
// (Deskbar.Core) decides; this gathers its three facts and does what the step says. The why, and the
// measurements behind it, are in TaskbarFlashing's header and in
// docs/superpowers/specs/2026-11-18-taskbar-flashing-switch-design.md
//
// THE SWITCH is one registry value, the one Settings > Personalization > Taskbar > Taskbar behaviors writes.
// 1 or missing is on (Windows' default), 0 is off. Explorer reads it on every flash: measured on 2026-11-18,
// writing 1 with no broadcast made the very next flash raise the taskbar, and writing 0 with none kept it
// down. So a write is all it takes, and there is no WM_SETTINGCHANGE here on purpose.
//
// App calls Sync(setting) on start and on every state pulse, and Sync(false) on every orderly way out
// (ExitApp, which Restart goes through, and SessionEnding). A crash runs none of those: the switch stays off
// and the marker stays, and the next start's plan keeps the marker's value as the original.
static class TaskbarFlashingSwitch
{
    const string AdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    const string ValueName = "TaskbarFlashing";

    // Whether Windows flashes taskbar buttons now. Read-only, so a test host and Manage's note may ask it. A
    // packaged process reads the real value too: only its WRITES are virtualised, and this class never makes
    // one from inside the package (see Write).
    public static bool FlashingOn =>
        Registry.CurrentUser.OpenSubKey(AdvancedKey)?.GetValue(ValueName) is not 0;

    // The app's entry point. Only Deskbar itself may change the setting, decided by the entry assembly as
    // StartupRegistration does and for the reason it gives: three test classes build a ManageWindow, and a test
    // host that ticked the box would otherwise switch off flashing on the machine of whoever runs the suite.
    public static void Sync(bool wanted)
    {
        if (!StartupRegistration.IsTheApp(Assembly.GetEntryAssembly()?.GetName().Name)) return;
        Sync(wanted, AppPaths.NewStateDir, () => FlashingOn, Write, ClickTrace.Write);
    }

    // The seam the tests drive, with a scratch folder and a registry made of one variable.
    //
    // ORDER MATTERS, both ways, and each order is chosen so that dying in the middle leaves something a later
    // Sync can still put right:
    //   * taking hold: the marker is written BEFORE the registry. Dying between the two leaves a marker with
    //     the true original and a switch still on, which the next start simply finishes.
    //   * letting go: the marker is deleted only AFTER the registry write succeeded. A failed write keeps the
    //     marker, so the next Sync(false), or the next start and exit, tries again. Deleting first would leave
    //     the switch off with nothing remembering that it had been on.
    //
    // Nothing here throws or shows a message: it runs during startup and shutdown, and a setting that could
    // not be flipped is no reason to stop either. It is traced.
    internal static void Sync(bool wanted, string stateDir, Func<bool> flashingOn, Func<bool, Result> write, Action<string> trace)
    {
        var marker = Path.Combine(stateDir, TaskbarFlashing.MarkerName);
        var step = TaskbarFlashing.Plan(new(wanted, Remembered(marker), flashingOn()));

        var done = Result.Success()
            .Bind(() => step.Remember is { } before
                ? Result.Try(() => File.WriteAllText(marker, before ? "1" : "0"), e => $"remembering it: {e.Message}")
                : Result.Success())
            .Bind(() => step.Write is { } on ? write(on).MapError(e => $"turning it {(on ? "on" : "off")}: {e}") : Result.Success())
            .Bind(() => step.Forget
                ? Result.Try(() => File.Delete(marker), e => $"forgetting it: {e.Message}")
                : Result.Success());

        if (done.IsFailure) trace($"taskbar flashing: {done.Error}");
        else if (step.Write is { } wrote) trace($"taskbar flashing: turned {(wrote ? "back on" : "off while Deskbar runs")}");
    }

    // What the marker says the switch was before Deskbar took hold, or null when there is no marker. Anything
    // but "0" reads as on: on is Windows' default, so a marker that was cut short or hand-edited restores to
    // the state most machines are in, and the worst case is flashing coming back for someone who had it off.
    static bool? Remembered(string marker) =>
        Result.Try(() => File.Exists(marker) ? File.ReadAllText(marker).Trim() != "0" : (bool?)null, e => e.Message)
            .Match(value => value, _ => null);

    // Under the Store package an HKCU write made by the package's own process is VIRTUALISED: it would land in
    // the package's private copy and Explorer would never see it (measured 2026-11-02, see
    // StartupRegistration.DeleteValue). So the packaged copy hands the write to reg.exe outside the package.
    // Store packaging design: docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md
    static Result Write(bool on) =>
        PackageIdentity.IsPackaged
            ? DetachedLauncher.RunOutsidePackage(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "reg.exe"),
                ["add", $@"HKCU\{AdvancedKey}", "/v", ValueName, "/t", "REG_DWORD", "/d", on ? "1" : "0", "/f"], TimeSpan.FromSeconds(5))
            : Result.Try(() => Registry.CurrentUser.CreateSubKey(AdvancedKey).SetValue(ValueName, on ? 1 : 0, RegistryValueKind.DWord), e => e.Message);
}
