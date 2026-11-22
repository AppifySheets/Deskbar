using CSharpFunctionalExtensions;
using Deskbar.Core.Licensing;

namespace Deskbar.Core.Tests.Licensing;

// Whether this copy is Deskbar Pro. Since the Store listing went free (2026-11-22), Pro is the Deskbar Pro
// add-on bought inside the Store copy, the loose exe from GitHub is never Pro, and every rule of that is a
// pure decision over facts gathered elsewhere (is the process packaged, what the Store said about the app's
// licence and the add-on, the developer override and whether the root is overridden), so each rule is
// settled here without a package or a Store anywhere near it.
public class ProEntitlementTests
{
    // LicenceFacts(IsActive, IsTrial, TrialEnds, OwnsProAddOn), named once so each test reads as its rule.
    static Result<LicenceFacts> Read(bool active = true, bool trial = false, DateTimeOffset? ends = null, bool addOn = false) =>
        Result.Success(new LicenceFacts(active, trial, ends, addOn));

    [Fact] public void Unpackaged_is_free_tier() => Assert.Equal(new Verdict(false, "Free tier"), ProEntitlement.Decide(false, Read(addOn: true), false, false));
    [Fact] public void Inactive_licence_is_free_tier() => Assert.Equal(new Verdict(false, "Free tier"), ProEntitlement.Decide(true, Read(active: false), false, false));
    [Fact] public void Failed_licence_read_is_pro_but_says_unavailable() => Assert.Equal(new Verdict(true, "Store licence unavailable"), ProEntitlement.Decide(true, Result.Failure<LicenceFacts>("boom"), false, false));
    [Fact] public void Trial_names_its_end() => Assert.Equal(new Verdict(true, "Store trial until 2026-11-08"), ProEntitlement.Decide(true, Read(trial: true, ends: new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero)), false, false));
    [Fact] public void Override_counts_only_under_a_root_override()
    {
        Assert.Equal(new Verdict(true, "Developer override"), ProEntitlement.Decide(false, Result.Failure<LicenceFacts>("x"), true, true));
        Assert.Equal(new Verdict(false, "Free tier"), ProEntitlement.Decide(false, Result.Failure<LicenceFacts>("x"), true, false));
    }

    // A trial the Store reports without an end date still counts as Pro; it says "unknown" rather than
    // claiming the licence could not be read, because it was read.
    [Fact] public void Trial_without_an_end_says_unknown() => Assert.Equal(new Verdict(true, "Store trial until unknown"), ProEntitlement.Decide(true, Read(trial: true), false, false));

    // The free listing: every Store copy has an active, full app licence now, so that alone must NOT be Pro,
    // or nobody would ever see the reminder to buy it. This was "Active_full_licence_is_pro" while the
    // listing itself was the purchase.
    [Fact] public void Active_full_licence_alone_is_free_tier() => Assert.Equal(new Verdict(false, "Free tier"), ProEntitlement.Decide(true, Read(), false, false));

    // The purchase: owning the Deskbar Pro add-on is Pro.
    [Fact] public void Owning_the_add_on_is_pro() => Assert.Equal(new Verdict(true, "Deskbar Pro add-on"), ProEntitlement.Decide(true, Read(addOn: true), false, false));

    // The add-on wins over a trial: someone who bought Pro during a trial is a buyer, and the log says so.
    [Fact] public void The_add_on_wins_over_a_trial() => Assert.Equal(new Verdict(true, "Deskbar Pro add-on"), ProEntitlement.Decide(true, Read(trial: true, addOn: true), false, false));
}
