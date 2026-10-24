using System.Globalization;
using CSharpFunctionalExtensions;

namespace Deskbar.Core.Licensing;

// What the Microsoft Store said about this copy's licence, reduced to the three facts the decision
// below reads. TrialEnds is only meaningful when IsTrial is true, and can be null even then: the
// Store does not promise an expiry date for every trial.
public sealed record LicenceFacts(bool IsActive, bool IsTrial, DateTimeOffset? TrialEnds);

// The answer: is this copy Deskbar Pro, and the one human-readable reason why. The reason is what the
// trace and the debug dump print, so it is worded for someone reading a log, not for code to parse.
public sealed record Verdict(bool IsPro, string Reason);

// The one question the free tier will ask: is this copy Deskbar Pro?
//
// Pro is the package. The copy bought from the Microsoft Store is Pro, the loose exe from GitHub is the
// free tier, and nothing else (no key, no account, no server of our own) decides it. Nothing is gated
// on the answer yet; this only provides it, so the features that become Pro later have one place to ask.
// Design: docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md, "Pro is the package".
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
    //   3. The licence could not be read: Pro. Someone running the Store copy paid for it or is in a
    //      trial the Store granted, and a Store outage, a timeout or an offline machine is not their
    //      fault. Failing closed here would punish exactly the people who paid.
    //   4. Active trial: Pro, naming the trial's last day so a log shows when it runs out.
    //   5. Active full licence: Pro.
    //   6. Inactive (a lapsed trial, a refunded purchase): the free tier.
    public static Verdict Decide(bool packaged, Result<LicenceFacts> licence, bool developerOverride, bool rootOverridden) =>
        (developerOverride && rootOverridden) ? new Verdict(true, "Developer override")
        // Packaged or not is the line between the Store copy and the GitHub copy (Store packaging design).
        : !packaged ? new Verdict(false, "Free tier")
        : licence.IsFailure ? new Verdict(true, "Store licence unavailable")
        : licence.Value is { IsActive: true, IsTrial: true } trial ? new Verdict(true, $"Store trial until {TrialEndText(trial.TrialEnds)}")
        : licence.Value.IsActive ? new Verdict(true, "Store licence")
        : new Verdict(false, "Free tier");

    // The trial's last day as the user would read it on their own calendar: the LOCAL date of the end
    // instant, yyyy-MM-dd with invariant digits so the trace reads the same on every locale. A trial the
    // Store reported without an end says "unknown" rather than "Store licence unavailable": the licence
    // WAS read, it simply carried no date.
    static string TrialEndText(DateTimeOffset? end) =>
        end is { } instant ? instant.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "unknown";
}
