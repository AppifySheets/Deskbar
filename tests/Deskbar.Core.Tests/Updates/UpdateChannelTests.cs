using Deskbar.Core.Updates;

namespace Deskbar.Core.Tests.Updates;

// What the update offer says, by channel. The Store copy updates through the Store, which cannot
// replace a running package, so its question says where the update comes from and, until the spike has
// shown that Windows brings the app back after a Store update, warns that Deskbar closes and has to be
// started again. The direct build's wording is exactly what it was before the Store existed.
// Design: docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md
public class UpdateChannelTests
{
    [Fact] public void Packaged_offer_asks_about_the_store_and_warns_until_restart_is_proven()
    {
        Assert.Equal("Update Deskbar from the Store?\n\nDeskbar will close to update; open it from Start afterwards.", UpdateChannel.OfferQuestion(true, "2.1.50", restartProven: false));
        Assert.Equal("Update Deskbar from the Store?", UpdateChannel.OfferQuestion(true, "2.1.50", restartProven: true));
        Assert.Equal("Update from the Store", UpdateChannel.OfferButton(true));
    }

    [Fact] public void Unpackaged_offer_is_unchanged() { Assert.Equal("Update Deskbar to 2.1.50?", UpdateChannel.OfferQuestion(false, "2.1.50", false)); Assert.Equal("Update", UpdateChannel.OfferButton(false)); }
}
