using System.Globalization;
using CSharpFunctionalExtensions;

namespace Deskbar.Core.Licensing;

// What the Microsoft Store said about this copy's licence, reduced to the four facts the decision
// below reads. TrialEnds is only meaningful when IsTrial is true, and can be null even then: the
// Store does not promise an expiry date for every trial. OwnsProAddOn is whether the Deskbar Pro
// add-on (product ID DeskbarPro, Store ID 9N5D13B3PR87) is among the app's active add-on licences.
public sealed record LicenceFacts(bool IsActive, bool IsTrial, DateTimeOffset? TrialEnds, bool OwnsProAddOn);

// The answer: is this copy Deskbar Pro, and the one human-readable reason why. The reason is what the
// trace and the debug dump print, so it is worded for someone reading a log, not for code to parse.
public sealed record Verdict(bool IsPro, string Reason);

// The one question the free tier will ask: is this copy Deskbar Pro?
//
// Pro is the add-on. Until 2026-11-22 the Store listing itself was the purchase ("Pro is the package" in
// docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md). Then the listing went free and
// Deskbar Pro became a one-time durable add-on bought inside the Store copy, so every Store copy now has
// an active, full app licence and only the add-on tells a buyer from everyone else. The loose exe from
// GitHub is never Pro: it has no Store licence to hold the add-on. Nothing else (no key, no account, no
// server of our own) decides it. Every feature stays free in every copy; what the answer drives is the
// daily reminder to buy Pro, which a buyer never sees. Corporate licensing is parked in Deskbar #384.
//
// Pure, so every rule is unit-tested in Deskbar.Core.Tests. The facts are gathered elsewhere: packaged
// from PackageIdentity, the licence from StoreLicence (both in Deskbar.Windows), the override and the
// root override by the App's Entitlement, which also caches the result.
public static class ProEntitlement
{
    // The rules, evaluated in this order; the first that matches wins.
    //
    //   1. Developer override: DESKBAR_PRO=1, honoured ONLY under a root override (DESKBAR_APPDATA set).
    //      The pairing is deliberate: a scratch root is how a development build is run on this machine,
    //      so the override cannot turn on by accident in an everyday copy, and a user who sets the
    //      variable on a real profile gets nothing for it.
    //   2. Not packaged: the GitHub build, the free tier, whatever the licence read says (it is not even
    //      attempted unpackaged).
    //   3. The licence could not be read: Pro. The answer only decides whether to show a reminder, and a
    //      Store outage, a timeout or an offline machine must not start reminding someone who paid. A
    //      free user who is offline simply skips that day's reminder, which costs nothing.
    //   4. Owns the Deskbar Pro add-on: Pro. Checked before the trial, so a buyer who paid during a
    //      trial is logged as a buyer.
    //   5. Active trial: Pro, naming the trial's last day so a log shows when it runs out. Only a copy
    //      installed while the listing was still paid with a trial can be in one; it runs out by itself.
    //   6. Everything else, an active full app licence included (that is every free Store copy now, and
    //      also the few bought while the listing was paid, whom the Store cannot tell apart; they get a
    //      promo code for the add-on instead): the free tier.
    public static Verdict Decide(bool packaged, Result<LicenceFacts> licence, bool developerOverride, bool rootOverridden) =>
        (developerOverride && rootOverridden) ? new Verdict(true, "Developer override")
        // Packaged or not is the line between the Store copy and the GitHub copy (Store packaging design).
        : !packaged ? new Verdict(false, "Free tier")
        : licence.IsFailure ? new Verdict(true, "Store licence unavailable")
        : licence.Value.OwnsProAddOn ? new Verdict(true, "Deskbar Pro add-on")
        : licence.Value is { IsActive: true, IsTrial: true } trial ? new Verdict(true, $"Store trial until {TrialEndText(trial.TrialEnds)}")
        : new Verdict(false, "Free tier");

    // The trial's last day as the user would read it on their own calendar: the LOCAL date of the end
    // instant, yyyy-MM-dd with invariant digits so the trace reads the same on every locale. A trial the
    // Store reported without an end says "unknown" rather than "Store licence unavailable": the licence
    // WAS read, it simply carried no date.
    static string TrialEndText(DateTimeOffset? end) =>
        end is { } instant ? instant.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "unknown";
}
