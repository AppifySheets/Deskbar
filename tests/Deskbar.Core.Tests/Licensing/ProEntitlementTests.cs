using CSharpFunctionalExtensions;
using Deskbar.Core.Licensing;

namespace Deskbar.Core.Tests.Licensing;

// Whether this copy is Deskbar Pro. The Store copy is Pro, the loose exe from GitHub is the free tier,
// and every rule of that is a pure decision over facts gathered elsewhere (is the process packaged,
// what the Store said about its licence, the developer override and whether the root is overridden),
// so each rule is settled here without a package or a Store anywhere near it.
// Design: docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md, "Pro is the package".
public class ProEntitlementTests
{
    [Fact] public void Unpackaged_is_free_tier() => Assert.Equal(new Verdict(false, "Free tier"), ProEntitlement.Decide(false, Result.Success(new LicenceFacts(true, false, null)), false, false));
    [Fact] public void Inactive_licence_is_free_tier() => Assert.Equal(new Verdict(false, "Free tier"), ProEntitlement.Decide(true, Result.Success(new LicenceFacts(false, false, null)), false, false));
    [Fact] public void Failed_licence_read_is_pro_but_says_unavailable() => Assert.Equal(new Verdict(true, "Store licence unavailable"), ProEntitlement.Decide(true, Result.Failure<LicenceFacts>("boom"), false, false));
    [Fact] public void Trial_names_its_end() => Assert.Equal(new Verdict(true, "Store trial until 2026-11-08"), ProEntitlement.Decide(true, Result.Success(new LicenceFacts(true, true, new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero))), false, false));
    [Fact] public void Override_counts_only_under_a_root_override()
    {
        Assert.Equal(new Verdict(true, "Developer override"), ProEntitlement.Decide(false, Result.Failure<LicenceFacts>("x"), true, true));
        Assert.Equal(new Verdict(false, "Free tier"), ProEntitlement.Decide(false, Result.Failure<LicenceFacts>("x"), true, false));
    }

    // Beyond the brief's five. A trial the Store reports without an end date still counts as Pro; it says
    // "unknown" rather than claiming the licence could not be read, because it was read.
    [Fact] public void Trial_without_an_end_says_unknown() => Assert.Equal(new Verdict(true, "Store trial until unknown"), ProEntitlement.Decide(true, Result.Success(new LicenceFacts(true, true, null)), false, false));

    // A full, active licence is the plain case the Store copy is in after a purchase.
    [Fact] public void Active_full_licence_is_pro() => Assert.Equal(new Verdict(true, "Store licence"), ProEntitlement.Decide(true, Result.Success(new LicenceFacts(true, false, null)), false, false));
}
