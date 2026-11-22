using Deskbar.App;

namespace Deskbar.Windows.Tests;

// #396, and Petre: "when connecting through rdp with a different scaling and then coming back to the console, console
// width would be fucked up" (of the bar). Measured in his trace on 2026-11-23: back at the 150% console from an RDP
// session at 225%, the docked bar was granted 316 px (211 DIP), then 474 px half a second later, because the window's
// Width had become 316 DIP and the re-dock read it back as the width chosen. The width the bar docks at and saves is
// now the one somebody CHOSE (FloatingBar.chosenWidth), whatever the window's live Width has been rewritten to.
//
// These run without a real dock, which would reserve a strip on the screen of whoever runs them: they set the live
// Width the way WPF does when Windows resizes the window, and read what a dock would use.
public class FloatingBarChosenWidthTests
{
    [Fact]
    public void A_rewritten_window_width_is_not_the_width_the_bar_docks_at() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = harness.ShowBar();

        bar.Bar.ApplyWidth(211);
        // What WPF does to Width when Windows resizes the window, a DPI change included.
        bar.Bar.Width = 316;

        Assert.Equal(211, bar.Bar.DockedWidth());
    });

    [Fact]
    public void A_newly_chosen_width_is_the_one_the_bar_docks_at() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = harness.ShowBar();

        bar.Bar.ApplyWidth(211);
        bar.Bar.Width = 316;
        bar.Bar.ApplyWidth(250);

        Assert.Equal(250, bar.Bar.DockedWidth());
    });

    // A bar that never had a width chosen docks at what it measures the first time, and keeps that: the docks after it
    // do not read the strip's width back from the window either.
    [Fact]
    public void A_bar_with_no_chosen_width_keeps_the_width_it_first_docked_at() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = harness.ShowBar();

        var first = bar.Bar.DockedWidth();
        bar.Bar.Width = first * 1.5;

        Assert.Equal(first, bar.Bar.DockedWidth());
    });

    // Undocking gives the bar back the chosen width, not the strip's.
    [Fact]
    public void Letting_go_of_the_strip_puts_the_chosen_width_back() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = harness.ShowBar();

        bar.Bar.ApplyWidth(211);
        bar.Bar.Width = 316;
        bar.Bar.LetGoOfStrip();

        // Within a pixel: WPF rounds the window to whole pixels and writes that back into Width (211 DIP at 150% is
        // 316.5 px, so 317 px, read back as 211.33), the same write-back that drifted the docked width, here harmless.
        Assert.InRange(bar.Bar.Width, 210.5, 212);
    });
}
