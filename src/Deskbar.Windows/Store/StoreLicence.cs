using CSharpFunctionalExtensions;
using Deskbar.Core.Licensing;
using Windows.Services.Store;

namespace Deskbar.Windows.Store;

// Reads this copy's licence from the Microsoft Store, for ProEntitlement to decide on.
// Design: docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md, "Pro is the package".
//
// Only meaningful in the Store package: the App's Entitlement calls it only when PackageIdentity says
// the process is packaged, because an unpackaged process has no Store licence to read and is the free
// tier regardless.
//
// Never throws. Every failure (no Store, offline, a COM error, the Store taking longer than the
// timeout) comes back as a Result.Failure carrying the message, and ProEntitlement treats a failed read
// as Pro: a buyer is never punished for a Store that did not answer.
public static class StoreLicence
{
    // Long enough for a cold Store service to answer, short enough that a hung one does not keep the
    // verdict at its provisional value for long. A licence read is local and normally answers at once.
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // ownerHwnd is the window the Store context is tied to, the bar's. A desktop (non-UWP) app must
    // initialise a StoreContext with a window before using it, so any Store UI it raises has an owner;
    // IntPtr.Zero (no bar, as in compatibility mode) skips that step, which a licence read does not
    // need since it shows no UI.
    public static async Task<Result<LicenceFacts>> ReadAsync(IntPtr ownerHwnd)
    {
        try
        {
            var context = StoreContext.GetDefault();
            // The projection's own helper for IInitializeWithWindow (3E68D4BD-7135-4D10-8018-9FB6D9F33FA1),
            // so no hand-written COM interface or P/Invoke is needed.
            if (ownerHwnd != IntPtr.Zero)
                WinRT.Interop.InitializeWithWindow.Initialize(context, ownerHwnd);

            var licence = await context.GetAppLicenseAsync().AsTask().WaitAsync(Timeout).ConfigureAwait(false);
            return Result.Success(FactsOf(licence));
        }
        catch (Exception ex)
        {
            // A TimeoutException from WaitAsync lands here too, with its own message.
            return Result.Failure<LicenceFacts>($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ExpirationDate is the trial's end only while the licence IS a trial; for a full licence it means
    // nothing to the decision, so it is dropped. The two extremes are what the Store reports for "no
    // date" and would throw when ProEntitlement converts them to local time in a zone that shifts them
    // past the representable range, so they become null, which ProEntitlement prints as "unknown".
    static LicenceFacts FactsOf(StoreAppLicense licence) => new(
        licence.IsActive,
        licence.IsTrial,
        licence.IsTrial && licence.ExpirationDate != DateTimeOffset.MinValue && licence.ExpirationDate != DateTimeOffset.MaxValue
            ? licence.ExpirationDate
            : null);
}
