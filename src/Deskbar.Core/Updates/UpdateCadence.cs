namespace Deskbar.Core.Updates;

// How often the automatic check may put an update in front of someone (task 5e1e1a12). Petre: "introduce
// importance to deskbar updates so users are not annoyed at many updates; i should still have all updates
// coming to me, but not others; they should get it daily, unless it's a critical update".
//
// Every merge to main releases itself, so on a busy day there are twenty releases, and the old rule (offer each
// new release once, on the five-minute tick) put twenty dialogs in front of everyone. Now:
//
//   - EveryRelease (AppState.UpdateEveryRelease, a box in Manage, off by default): the old rule, unchanged.
//     That is Petre's copy, which follows main as it lands.
//   - CRITICAL: the running build is older than the newest release a `critical-update` pull request went into
//     (ReleaseInfo.CriticalSince, UpdateCheck.IsCriticalFor), and no offer has covered that release yet. Offered
//     at once, whoever is running it.
//   - Everything else waits until a day has passed since the last automatic offer, so an ordinary user sees at
//     most one update dialog a day, and it is always the newest release, which carries every release before it.
//
// ONCE PER CRITICAL RELEASE, not once per release that carries its marker. Every release after a critical one
// copies its floor forward (scripts/ReleaseNotes.psm1), so someone who says "not now" to the critical offer would
// otherwise get a fresh dialog for each of the day's ordinary releases until they update: the very stream this
// rule exists to stop. The last offered version (AppState.LastUpdateOfferVersion) settles it: an offer of the
// critical release, or of anything newer, has told them, and after that the daily clock applies.
//
// The clock is AppState.LastUpdateOffer, stamped whenever the automatic check shows an offer, critical ones too
// (a critical fix in the morning is that day's update). A copy that has never offered anything (null: a new
// install, or the first start of a build with this rule) starts the clock instead of offering, so the day counts
// from then. App.CheckForUpdate also starts it on a check that finds nothing newer, so a copy that is current
// counts its day from its first check, not from the first release after it.
//
// The five-minute tick stays, because that is how quickly a critical release reaches people. A release held back
// is not offered: no dialog and no balloon. "Check for updates…" by hand still finds it, because asking is the
// request.
//
// Pure, so the rule is pinned by Deskbar.Core.Tests.Updates.UpdateCadenceTests.
public static class UpdateCadence
{
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    public enum Verdict
    {
        // Show the offer now (and stamp the clock).
        Offer,
        // Say nothing yet; the next tick asks again.
        Wait,
        // Never offered before: stamp the clock to now and say nothing.
        StartClock,
    }

    public static Verdict Decide(string? running, ReleaseInfo release, bool everyRelease,
        DateTimeOffset? lastOffer, string? lastOfferedVersion, DateTimeOffset now) =>
        everyRelease || CriticalNotYetOffered(running, release, lastOfferedVersion) ? Verdict.Offer
        : lastOffer is not { } last ? Verdict.StartClock
        // A clock in the future (the machine's time was wrong when it was stamped) would hold updates back
        // until that date; let the offer through instead, which restamps it.
        : now - last >= Interval || last > now + Interval ? Verdict.Offer
        : Verdict.Wait;

    // Critical for the running build, and no earlier offer was of the critical release or a newer one.
    static bool CriticalNotYetOffered(string? running, ReleaseInfo release, string? lastOfferedVersion) =>
        UpdateCheck.IsCriticalFor(running, release)
        && (lastOfferedVersion is null || UpdateCheck.IsNewer(lastOfferedVersion, release.CriticalSince));
}
