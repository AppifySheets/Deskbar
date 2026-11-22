using CSharpFunctionalExtensions;
using Windows.Services.Store;

namespace Deskbar.Windows.Store;

// The Deskbar Pro add-on's price as the Store formats it for this user ("$18.99", "18,99 €"), for the Get Pro
// button on Manage's Deskbar Pro tab (ProTab in Core). The Store sets the price in every other currency from the
// US$18.99 base, so a buyer in Europe should see euros, not a dollar figure that is not what they will pay.
//
// Only meaningful in the Store package, like StorePurchase: the caller asks only when PackageIdentity says so.
// The lookup shows no UI, so the window only matters for tying the process's one context (StoreContextHolder);
// IntPtr.Zero still works.
//
// Never throws. Every failure, a timeout included, comes back as a Result, and the tab then shows the list
// price instead. Not cached here: Manage asks once each time it is opened, and a failure must not stick
// (the briefing's trap about caching a lookup failure that can succeed later).
public static class StorePrice
{
    // A product lookup goes to the Store's servers, unlike the licence read, so it can be slow on a cold
    // start. Ten seconds, the licence read's figure, keeps the button from changing long after the window
    // opened.
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static async Task<Result<string>> ProPriceAsync(IntPtr ownerHwnd)
    {
        try
        {
            var result = await StoreContextHolder.Get(ownerHwnd)
                .GetStoreProductsAsync(["Durable"], [StorePurchase.ProAddOnStoreId])
                .AsTask().WaitAsync(Timeout).ConfigureAwait(false);
            return result.ExtendedError is { } error
                ? Result.Failure<string>($"the Microsoft Store said {error.Message}")
                : result.Products.Values.FirstOrDefault()?.Price?.FormattedPrice is { Length: > 0 } price
                    ? Result.Success(price)
                    : Result.Failure<string>("the Microsoft Store returned no price for Deskbar Pro");
        }
        catch (Exception ex)
        {
            return Result.Failure<string>($"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
