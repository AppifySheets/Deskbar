using Deskbar.Core.Licensing;

namespace Deskbar.Core.Tests.Licensing;

// What Manage's Deskbar Pro tab shows in each state of the mockup Petre approved on 2026-11-22 (ProTab's header):
// the Store copy without Pro, with Pro, and the GitHub copy, plus the two in-between states the mockup did not
// draw (a licence that could not be read, and a trial left over from the paid listing).
public class ProTabTests
{
    static readonly Verdict Free = new(false, ProEntitlement.FreeReason);
    static readonly Verdict Owned = new(true, ProEntitlement.AddOnReason);

    [Fact]
    public void Store_copy_without_pro_offers_get_pro_at_the_stores_price()
    {
        var view = ProTab.Of(packaged: true, Free, "18,99 €");

        Assert.False(view.HasPro);
        Assert.True(view.ShowPitch);
        Assert.Equal("18,99 €", view.Price);
        Assert.Equal("Get Pro, 18,99 €", view.BuyLabel);
        Assert.False(view.BuyOpensStore);
        Assert.True(view.ShowCheckAgain);
        Assert.Null(view.StoreNote);
    }

    // The price lookup has not answered yet, or failed: the list price in the sentence, no price on the button.
    [Fact]
    public void Store_copy_without_a_store_price_falls_back_to_the_list_price()
    {
        var view = ProTab.Of(packaged: true, Free, null);

        Assert.Equal(ProTab.ListPrice, view.Price);
        Assert.Equal("Get Pro", view.BuyLabel);
    }

    [Fact]
    public void Store_copy_with_pro_says_thank_you_and_offers_nothing_to_buy()
    {
        var view = ProTab.Of(packaged: true, Owned, "$18.99");

        Assert.True(view.HasPro);
        Assert.StartsWith("This copy has Deskbar Pro", view.Status);
        Assert.False(view.ShowPitch);
        Assert.Null(view.BuyLabel);
        Assert.False(view.ShowCheckAgain);
        Assert.Contains("Your reports", view.Support);
    }

    // The developer override is how a dev build sees the owned state, so it looks owned.
    [Fact]
    public void Developer_override_looks_owned() =>
        Assert.True(ProTab.Of(packaged: false, new Verdict(true, ProEntitlement.DeveloperReason), null).HasPro);

    // The GitHub copy cannot buy in place or read a licence: the button opens the Store and the note says why.
    [Fact]
    public void GitHub_copy_sends_to_the_store_with_the_list_price()
    {
        var view = ProTab.Of(packaged: false, Free, "ignored");

        Assert.Equal(ProTab.ListPrice, view.Price);
        Assert.Equal("Open Deskbar in the Store", view.BuyLabel);
        Assert.True(view.BuyOpensStore);
        Assert.False(view.ShowCheckAgain);
        Assert.Contains("Microsoft Store copy", view.StoreNote);
    }

    // Counted as Pro only because the Store did not answer: no thank-you, no buy button, but Check again.
    [Fact]
    public void Unreadable_licence_neither_thanks_nor_sells_but_offers_check_again()
    {
        var view = ProTab.Of(packaged: true, new Verdict(true, ProEntitlement.UnavailableReason), "$18.99");

        Assert.False(view.HasPro);
        Assert.False(view.ShowPitch);
        Assert.Null(view.BuyLabel);
        Assert.True(view.ShowCheckAgain);
        Assert.Contains("could not be reached", view.Status);
    }

    // A trial from the paid listing is not ownership: it names its end and can still buy.
    [Fact]
    public void Trial_names_its_end_and_can_buy()
    {
        var view = ProTab.Of(packaged: true, new Verdict(true, "Store trial until 2026-11-08"), null);

        Assert.False(view.HasPro);
        Assert.Equal("This copy: Store trial until 2026-11-08", view.Status);
        Assert.Equal("Get Pro", view.BuyLabel);
    }
}
