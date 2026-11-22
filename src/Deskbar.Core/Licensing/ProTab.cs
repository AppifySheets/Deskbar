namespace Deskbar.Core.Licensing;

// What Manage's Deskbar Pro tab shows, decided from three facts: whether this is the Store copy (packaged),
// the Pro verdict (ProEntitlement) and the add-on's price as the Store formats it for this user, when the Store
// gave one. Pure, so each state is a unit test; ManageWindow.Pro.cs only puts these strings into controls.
//
// The tab was designed with Petre on 2026-11-22 from a mockup of three states, approved as shown ("okay on
// mockup"): a Store copy without Pro (status, the bar reminder's sentence, a blue Get Pro button with the
// price, priority support with a Report a problem… button, and "Check again" for a purchase made on another
// PC), a Store copy with Pro (a thank-you and priority support only), and the GitHub copy (the same sentence,
// a note that Pro is bought in the Store copy, and a button that opens Deskbar in the Store). The model behind
// it is ProReminder's header: free for personal use, every feature in every copy, Pro for work.
//
// Fields, for the view:
//   Status          the line at the top, beside a dot that is green only when HasPro.
//   HasPro          the copy owns Pro (or a developer said so): no pitch, no buy button, the thank-you wording.
//   ShowPitch       show the sentence asking work users to buy Pro, with Price in it in bold.
//   Price           the Store's own formatted price (in the user's currency) or, without one, ListPrice.
//   StoreNote       the GitHub copy's note on where Pro is bought; null elsewhere.
//   BuyLabel        the blue button's text; null hides it.
//   BuyOpensStore   the button opens Deskbar's Store page (the GitHub copy) instead of buying in place.
//   ShowCheckAgain  offer "Check again", which reads the Store licence at once instead of at the next
//                   five-minute refresh. Only the Store copy has a licence to read.
//   Support         the sentence under "Priority support".
public sealed record ProTabView(
    string Status, bool HasPro, bool ShowPitch, string Price, string? StoreNote,
    string? BuyLabel, bool BuyOpensStore, bool ShowCheckAgain, string Support);

public static class ProTab
{
    // The base price in Partner Center (tier Tier1120). Shown when the Store has not given its own formatted
    // price: always in the GitHub copy, which has no Store context to ask, and in the Store copy until the
    // lookup answers or when it fails. "US$" rather than "$", because a reader elsewhere would otherwise
    // read it as their own dollar.
    public const string ListPrice = "US$18.99";

    // The pitch is the bar reminder's sentence (FloatingBar.ProReminder.cs) at more length. The price goes in
    // bold between the two halves, so the view builds it from these rather than from one string.
    public const string PitchLead = "Deskbar is free for personal use, and every feature works without paying. If you use it for work, please buy Deskbar Pro: ";
    public const string PitchTail = " once, not a subscription.";

    const string FreeStatus = "This copy: free for personal use";
    const string OwnedStatus = "This copy has Deskbar Pro. Thank you.";
    const string UnavailableStatus = "The Microsoft Store could not be reached, so this copy counts as Pro until it answers.";
    const string GitHubNote = "Pro is bought in the Microsoft Store copy of Deskbar. Install it from the Store and your workspaces and settings come along, because both copies keep them in the same place.";
    const string OwnedSupport = "Your reports from Report a problem… are marked priority and answered first. Leave a way to reach you so we can answer.";
    const string OfferSupport = "With Pro, a report sent with Report a problem… is marked priority and answered first.";

    public static ProTabView Of(bool packaged, Verdict verdict, string? storePrice) =>
        // Owned: the add-on, or the developer override (which is how the owned state is seen in a dev build).
        verdict.IsPro && verdict.Reason is ProEntitlement.AddOnReason or ProEntitlement.DeveloperReason
            ? new ProTabView(OwnedStatus, true, false, ListPrice, null, null, false, false, OwnedSupport)
        // The GitHub copy: never Pro, cannot buy in place and has no licence to check again.
        : !packaged
            ? new ProTabView(FreeStatus, false, true, ListPrice, GitHubNote, "Open Deskbar in the Store", true, false, OfferSupport)
        // The Store copy whose licence could not be read counts as Pro (ProEntitlement rule 3) without owning it.
        // Saying "Thank you" would be wrong, and so would a buy button that cannot reach the Store; checking
        // again is the useful thing. This is also what a Store copy shows in its first seconds, before the first
        // licence read has finished.
        : verdict.Reason == ProEntitlement.UnavailableReason
            ? new ProTabView(UnavailableStatus, false, false, ListPrice, null, null, false, true, OfferSupport)
        // The Store copy without Pro: the free tier, or a trial left over from the paid listing, whose status
        // names its end date ("This copy: Store trial until 2026-11-08"). Both can buy.
        : new ProTabView(
            verdict.IsPro ? $"This copy: {verdict.Reason}" : FreeStatus, false, true, storePrice ?? ListPrice, null,
            storePrice is null ? "Get Pro" : $"Get Pro, {storePrice}", false, true, OfferSupport);
}
