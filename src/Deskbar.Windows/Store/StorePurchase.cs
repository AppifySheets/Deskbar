using CSharpFunctionalExtensions;
using Windows.Services.Store;

namespace Deskbar.Windows.Store;

// Buys the Deskbar Pro add-on inside the Store copy, through the Store's own purchase dialog. The add-on was
// created in Partner Center on 2026-11-22: a durable, lifetime Forever, US$18.99, Store ID 9N5D13B3PR87
// (product ID DeskbarPro, which StoreLicence matches on when it reads the licence back).
//
// Only meaningful in the Store package: the caller checks PackageIdentity first and sends the GitHub copy to
// the Store web page instead, since an unpackaged process has no Store context to buy through.
//
// The dialog is Store UI, so the context must be tied to a window (StoreContextHolder explains why there is
// only one context and how it is tied). A zero hwnd is refused here rather than letting the Store fail in a
// way that says nothing useful.
//
// Never throws: every outcome comes back as a Result. Success carries whether the user now owns Pro (true
// for a purchase that went through and for one made earlier); a cancelled dialog is a success with false,
// because nothing went wrong. Network and server errors, and anything thrown, are failures with a message
// the caller can show.
public static class StorePurchase
{
    const string ProAddOnStoreId = "9N5D13B3PR87";

    public static async Task<Result<bool>> BuyProAsync(IntPtr ownerHwnd)
    {
        if (ownerHwnd == IntPtr.Zero) return Result.Failure<bool>("no window to show the Store's purchase dialog over");
        try
        {
            var result = await StoreContextHolder.Get(ownerHwnd).RequestPurchaseAsync(ProAddOnStoreId);
            return result.Status switch
            {
                StorePurchaseStatus.Succeeded or StorePurchaseStatus.AlreadyPurchased => Result.Success(true),
                StorePurchaseStatus.NotPurchased => Result.Success(false),
                StorePurchaseStatus.NetworkError => Result.Failure<bool>("the Microsoft Store could not be reached"),
                _ => Result.Failure<bool>($"the Microsoft Store said {result.Status}: {result.ExtendedError?.Message}"),
            };
        }
        catch (Exception ex)
        {
            return Result.Failure<bool>($"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
