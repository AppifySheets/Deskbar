using System.Windows.Controls;
using Deskbar.App;

namespace Deskbar.Windows.Tests;

// The update question's optional Microsoft Store button. App.OfferUpdate passes it only once there is
// a listing (UpdateService.StoreProductId set); until then the offer must look exactly as it did, with
// the primary button and "Not now" and nothing else.
//
// The window is real WPF, so these run on the STA thread like every other surface test here.
public class UpdateOfferTests
{
    static IReadOnlyList<string> ButtonLabels(UpdateOffer window) =>
        ((StackPanel)window.Content).Children.OfType<StackPanel>().Single()
            .Children.OfType<Button>().Select(button => (string)button.Content).ToList();

    [Fact]
    public void With_a_store_listing_the_store_button_sits_between_update_and_not_now() => StaThread.Run(() =>
    {
        var window = new UpdateOffer("Update Deskbar to 2.0.1?", "Update", () => { }, "Microsoft Store", () => { });

        Assert.Equal(["Update", "Microsoft Store", "Not now"], ButtonLabels(window));

        window.Close();
    });

    [Fact]
    public void Without_a_store_listing_there_is_no_store_button() => StaThread.Run(() =>
    {
        var window = new UpdateOffer("Update Deskbar to 2.0.1?", "Update", () => { });

        Assert.Equal(["Update", "Not now"], ButtonLabels(window));

        window.Close();
    });
}
