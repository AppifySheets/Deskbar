using CSharpFunctionalExtensions;
using Deskbar.Core.Licensing;
using Deskbar.Windows.Packaging;
using Deskbar.Windows.Store;

namespace Deskbar.App;

// The app's cached answer to "is this copy Deskbar Pro?", for whatever is gated on it later (nothing is
// yet). The decision itself is ProEntitlement's, in Core and unit-tested; this class only gathers the
// facts, asks it, remembers the verdict and traces it.
// Design: docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md, "Pro is the package".
//
// Refreshed at startup once the bar exists (its hwnd owns the Store context) and on every tick of the
// update timer (App.StartUpdateChecks), so a trial that ends or a purchase made while the app runs is
// picked up within minutes. A licence cannot be faked into a test, so this is exercised by the build and
// by its trace line, `entitlement: <reason>`, in %TEMP%\deskbar-trace.log.
public static class Entitlement
{
    // Set only by the developer, only for a development build: DESKBAR_PRO=1 together with DESKBAR_APPDATA
    // (see ProEntitlement.Decide for why the two are paired).
    const string OverrideVariable = "DESKBAR_PRO";

    // The provisional verdict, before any licence has been read: Decide with a read that has not happened,
    // which is the same as one that failed. Unpackaged that is "Free tier", exactly what a refresh would
    // say anyway. Packaged it is Pro, "Store licence unavailable", until the first refresh completes:
    // never punish a buyer for a slow first read, so a Store copy is not briefly the free tier while the
    // Store service wakes up.
    //
    // Volatile because a refresh finishes on a thread-pool thread (the licence read does not come back to
    // the dispatcher) while readers sit on the UI thread. The verdict is an immutable record, so replacing
    // the reference is the whole update and needs no lock.
    static volatile Verdict current = Decide(Result.Failure<LicenceFacts>("not read yet"));

    public static Verdict Current => current;

    // What the last refresh traced: its verdict, and the licence read's failure message (null when the read
    // worked or was not attempted). Null verdict means no refresh has run yet. The refresh runs on every tick
    // of the update timer, all day, so like the bar's other periodic traces it writes only when its output
    // CHANGES (the briefing's rule that keeps the trace readable with tracing on). Swapped with
    // Interlocked.Exchange, because a startup refresh and a timer refresh can finish together on two
    // thread-pool threads, and each must compare against the value it replaced.
    static Verdict? lastVerdict;
    static string? lastReadFailure;

    // Recomputes the verdict, caches it and traces it when it changed. Never throws: StoreLicence.ReadAsync
    // returns its failures as a Result, and everything else here is pure.
    public static async Task RefreshAsync(IntPtr ownerHwnd)
    {
        // The Store licence is only read inside the package (Store packaging design): an unpackaged copy
        // has none, and is the free tier whatever a read would say.
        var licence = PackageIdentity.IsPackaged
            ? await StoreLicence.ReadAsync(ownerHwnd).ConfigureAwait(false)
            : Result.Failure<LicenceFacts>("not packaged");

        var verdict = Decide(licence);
        current = verdict;

        // Always on the first refresh (nothing traced yet), then only when the verdict differs from the one
        // before (record equality: Pro or not, and the reason, which carries a trial's end date).
        if (Interlocked.Exchange(ref lastVerdict, verdict) != verdict)
            ClickTrace.Write($"entitlement: {verdict.Reason}");

        // The reason alone says "unavailable" without saying why; the Store's own message is what a
        // log reader needs to tell an outage from a timeout from a missing Store. Only when packaged,
        // since unpackaged the "failure" is just the read that was never attempted. Also only on change, so
        // a Store that stays unreachable writes its message once rather than every five minutes.
        var readFailure = PackageIdentity.IsPackaged && licence.IsFailure ? licence.Error : null;
        if (Interlocked.Exchange(ref lastReadFailure, readFailure) != readFailure && readFailure is not null)
            ClickTrace.Write($"entitlement: licence read failed: {readFailure}");
    }

    // Reads the override afresh each time rather than once: it costs nothing, and keeps the provisional
    // verdict and every refresh agreeing on the same rule.
    static Verdict Decide(Result<LicenceFacts> licence) => ProEntitlement.Decide(
        PackageIdentity.IsPackaged,
        licence,
        Environment.GetEnvironmentVariable(OverrideVariable) == "1",
        AppPaths.RootOverridden);
}
