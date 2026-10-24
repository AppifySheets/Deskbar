using CSharpFunctionalExtensions;
using Windows.Services.Store;

namespace Deskbar.Windows.Store;

// An update the Microsoft Store has waiting for this package. Version is the package version without
// its fourth part (the Store's 2.1.50.0 is the app's 2.1.50, since the package version is always
// VersionPrefix plus ".0"), so it reads the same as every other version the app shows. Mandatory is
// true when the Store marked any of the waiting packages as mandatory.
public sealed record StoreUpdate(string Version, bool Mandatory);

// The Store copy's update check and install, in place of the GitHub download the direct build does.
// Design: docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md
//
// WHY THE STORE AND NOT GITHUB. A package is updated by the Store and by nothing else: the direct build's
// trick of downloading a new exe and starting it would run a loose exe outside the package, losing the
// package identity (and with it the Store licence and the startup task). And the Store cannot update a
// package that is running, which the bar always is, so the update cannot simply wait in the background
// for the Store's own schedule to land it either. The App asks, and on yes this hands the install to the
// Store, which closes Deskbar to do it.
//
// Only meaningful in the Store package: the App calls this only when PackageIdentity says the process is
// packaged. An unpackaged process has no package for the Store to update.
//
// Never throws. Every failure (no Store, offline, a COM error, the Store taking too long to answer) comes
// back as a Result.Failure carrying the message, so the background check can stay silent about it and
// the manual check can say it out loud, the same two manners the GitHub check has.
//
// Cannot be exercised on a development machine: an unpackaged process has nothing for the Store to look
// up. It is covered by the build and by the spike, which runs it from an installed package.
public static class StoreUpdates
{
    // Generous on purpose: unlike a licence read, which is local, an update check goes to the Store's
    // service, and a cold Store on a slow network can take many seconds. Still bounded, so a Store that
    // never answers cannot keep the manual check's "checking" tooltip up forever.
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    // Is there a newer package in the Store? None when the Store has nothing waiting for this app.
    //
    // ownerHwnd is the bar's window, which the Store context is tied to (see Context); IntPtr.Zero (no
    // bar, as in compatibility mode) skips that step, which the check itself does not need since it shows
    // no UI.
    public static async Task<Result<Maybe<StoreUpdate>>> NewerAsync(IntPtr ownerHwnd)
    {
        try
        {
            var updates = await PendingAsync(Context(ownerHwnd)).ConfigureAwait(false);
            return Result.Success(updates.Count == 0
                ? Maybe<StoreUpdate>.None
                : Maybe.From(new StoreUpdate(VersionOf(updates[0]), updates.Any(update => update.Mandatory))));
        }
        catch (Exception ex)
        {
            // A TimeoutException from WaitAsync lands here too, with its own message.
            return Result.Failure<Maybe<StoreUpdate>>($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // Asks the Store to download and install the waiting updates. The Store shows its own progress UI,
    // owned by ownerHwnd, which is why a window matters here: without one the Store has nothing to put
    // its UI in front of.
    //
    // When it works, this method never returns: the Store ends this process to replace the package. So
    // any result that comes back is a failure, and the state the Store reports is named in it (Canceled
    // when the user said no in the Store's UI, an error state otherwise), for the App to show.
    //
    // The list is fetched again rather than carried over from NewerAsync. The offer can sit on screen for
    // hours before anyone answers it, and a StorePackageUpdate from that long ago may no longer describe
    // what the Store has; a fresh list is the one the Store will accept.
    //
    // Registering for restart is the App's job, done before it calls this (see App.InstallFromStore), so
    // this class stays about the Store alone.
    public static async Task<Result> InstallAsync(IntPtr ownerHwnd)
    {
        try
        {
            var context = Context(ownerHwnd);
            // No ConfigureAwait(false) here, deliberately: the request below raises the Store's UI, which
            // has to be asked for from the UI thread the caller is on, so the continuation must come back
            // to it.
            var updates = await PendingAsync(context);
            if (updates.Count == 0)
                return Result.Failure("The Microsoft Store has no update waiting for Deskbar any more.");

            // No timeout on the install itself: it is a download of unknown size behind a UI the user is
            // answering, and cutting it short would only abandon a Store operation that carries on anyway.
            var outcome = await context.RequestDownloadAndInstallStorePackageUpdatesAsync(updates).AsTask();
            return outcome.OverallState == StorePackageUpdateState.Completed
                ? Result.Success()
                : Result.Failure($"The Microsoft Store did not install the update ({outcome.OverallState}).");
        }
        catch (Exception ex)
        {
            return Result.Failure($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // The same two steps StoreLicence takes: the default context, tied to the owner window when there is
    // one. A desktop (non-UWP) app must initialise a StoreContext with a window before any call that can
    // raise Store UI; the projection's own helper for IInitializeWithWindow means no hand-written COM
    // interface or P/Invoke.
    static StoreContext Context(IntPtr ownerHwnd)
    {
        var context = StoreContext.GetDefault();
        if (ownerHwnd != IntPtr.Zero)
            WinRT.Interop.InitializeWithWindow.Initialize(context, ownerHwnd);
        return context;
    }

    // The app's own package and any optional packages, each with a newer version in the Store. The
    // timeout covers only this question, never the install, which can rightly take minutes.
    static Task<IReadOnlyList<StorePackageUpdate>> PendingAsync(StoreContext context) =>
        context.GetAppAndOptionalStorePackageUpdatesAsync().AsTask().WaitAsync(Timeout);

    // major.minor.build: the package's fourth part is always 0 (package version = VersionPrefix + ".0"),
    // and dropping it makes the Store's version comparable with UpdateService.DisplayVersion by eye.
    static string VersionOf(StorePackageUpdate update) =>
        $"{update.Package.Id.Version.Major}.{update.Package.Id.Version.Minor}.{update.Package.Id.Version.Build}";
}
