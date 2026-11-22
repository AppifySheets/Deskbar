using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using CSharpFunctionalExtensions;
using Deskbar.Core.Licensing;
using Deskbar.Windows.Packaging;

namespace Deskbar.App;

// Manage's Deskbar Pro tab (XAML in ManageWindow.xaml; what it says in each state is ProTab, in Core, which has the
// approved mockup's three states). This file only puts ProTab's strings into the controls and hands the buttons
// to App, which owns buying (StorePurchase), opening the Store, the report window and the licence refresh.
//
// Repainted when the window opens, when the Store's price arrives, after "Check again", and from App whenever a
// licence refresh lands (App.RefreshEntitlementAsync calls PaintPro), so a purchase made from the bar's reminder
// line while this window is open turns it into the thank-you at once.
public partial class ManageWindow
{
    // What the tab's buttons do, all of it App's: GetPro buys through the Store's own dialog (the Store copy),
    // OpenStore opens Deskbar's Store page (the GitHub copy's button, which needs no dialog first because the tab
    // already explains), ReportProblem opens Report a problem…, CheckAgain reads the Store licence now and
    // completes once the verdict is repainted, and Price looks up the add-on's price in the user's currency.
    public sealed record ProActions(
        Action GetPro, Action OpenStore, Action ReportProblem, Func<Task> CheckAgain, Func<Task<Result<string>>> Price);

    // Null in the tests that build this window without App and in no other case; the buttons then do nothing.
    readonly ProActions? pro;

    // The Store's formatted price once it has answered; null until then, and for good when it fails or this is
    // the GitHub copy. ProTab shows the list price meanwhile.
    string? storePrice;

    // Grey for "no Pro", green for "has Pro". Frozen, as static WPF brushes must be (the briefing's trap).
    static readonly Brush NoProDot = Frozen(Color.FromRgb(0x9A, 0xA0, 0xA6));
    static readonly Brush ProDot = Frozen(Color.FromRgb(0x0F, 0x7B, 0x0F));

    static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    // Called once from the constructor: paints the tab from what is known now, then asks the Store for the price
    // (Store copy only: the GitHub copy has no package identity to ask with, Store packaging design,
    // docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md) and paints again when it answers.
    void LoadPro()
    {
        PaintPro();
        if (pro is not null && PackageIdentity.IsPackaged) _ = LoadProPriceAsync(pro);
    }

    // Awaited on the UI thread (the constructor's), so the continuation comes back to it and may touch controls.
    // A failed lookup only means the list price stays; it is traced, so a log shows why the button has no price.
    async Task LoadProPriceAsync(ProActions actions)
    {
        var price = await actions.Price();
        if (price.IsFailure)
        {
            ClickTrace.Write($"pro: price lookup failed: {price.Error}");
            return;
        }
        storePrice = price.Value;
        PaintPro();
    }

    // Puts the current state into the tab. Public because App repaints it after every licence refresh. Reads the
    // live verdict (Entitlement.Current) each time rather than holding one, so it can never show a stale answer.
    public void PaintPro()
    {
        var view = ProTab.Of(PackageIdentity.IsPackaged, Entitlement.Current, storePrice);

        ProStatus.Text = view.Status;
        ProStatusDot.Fill = view.HasPro ? ProDot : NoProDot;

        // The sentence with the price in bold between its two halves, as in the mockup.
        ProPitch.Inlines.Clear();
        ProPitch.Inlines.Add(new Run(ProTab.PitchLead));
        ProPitch.Inlines.Add(new Bold(new Run(Unbroken(view.Price))));
        ProPitch.Inlines.Add(new Run(ProTab.PitchTail));
        ProPitch.Visibility = Shown(view.ShowPitch);

        ProStoreNote.Text = view.StoreNote ?? "";
        ProStoreNote.Visibility = Shown(view.StoreNote is not null);

        ProBuyText.Text = view.BuyLabel ?? "";
        ProBuy.Visibility = Shown(view.BuyLabel is not null);
        ProBuy.Tag = view.BuyOpensStore;

        ProSupport.Text = view.Support;
        ProCheckRow.Visibility = Shown(view.ShowCheckAgain);
    }

    static Visibility Shown(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;

    // A price must not wrap in the middle. WPF broke "US$18.99" after "US" at the window's width (seen in a render),
    // and a Store price such as "18,99 €" has a space it could break at too. A word joiner (U+2060, zero width)
    // between every character forbids a break inside, and spaces become no-break spaces.
    static string Unbroken(string price) => string.Join("⁠", price.Replace(' ', ' ').ToCharArray());

    // The blue button: buy in place in the Store copy, open the Store page in the GitHub copy. PaintPro left the
    // choice in Tag, so the click does what the label said even if the state changed in between.
    void OnProBuy(object sender, RoutedEventArgs e)
    {
        if (ProBuy.Tag is true) pro?.OpenStore();
        else pro?.GetPro();
    }

    void OnProReport(object sender, RoutedEventArgs e) => pro?.ReportProblem();

    // Reads the licence now instead of at the next five-minute refresh: for Pro bought on another PC, or bought
    // here a moment ago while the Store was slow to say so. The link is disabled while the read runs, and the note
    // under it says when the answer came, so a click that changes nothing still visibly did something.
    async void OnProCheckAgain(object sender, RoutedEventArgs e)
    {
        if (pro is null) return;
        ProCheckAgain.IsEnabled = false;
        ProCheckNote.Text = "Asking the Microsoft Store…";
        ProCheckNote.Visibility = Visibility.Visible;
        await pro.CheckAgain();
        PaintPro();
        ProCheckNote.Text = $"Checked at {DateTime.Now:t}.";
        ProCheckAgain.IsEnabled = true;
    }
}
