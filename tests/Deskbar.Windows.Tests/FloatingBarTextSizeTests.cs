using System.Windows;
using System.Windows.Controls;
using Deskbar.Core.Domain;

namespace Deskbar.Windows.Tests;

// The text size setting reaches the bar, and live. Petre: "increase font size by one more and make it
// configurable". A workspace caption is 9.5 pt in the code; the bar draws it at 9.5 plus the offset, and a
// change of offset redraws it without a restart, because the change pulses and the bar rebuilds on the pulse.
public class FloatingBarTextSizeTests
{
    static IEnumerable<DependencyObject> Descendants(DependencyObject node) =>
        LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()
            .SelectMany(child => new[] { child }.Concat(Descendants(child)));

    static double CaptionSize(Panel rows, string caption) =>
        Descendants(rows).OfType<TextBlock>().First(t => t.Text == caption).FontSize;

    [Fact]
    public void By_default_captions_are_drawn_at_the_default_offset() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = harness.ShowBar();

        Assert.Equal(9.5 + BarText.Default, CaptionSize(bar.Rows, "Sparrow"));
    });

    // The clock keeps its own sizes at any setting. Petre, at the +4 default: "watch and date are too big". It
    // matches the taskbar's clock, not the bar's captions. Found by its date line, which only changes at
    // midnight.
    [Fact]
    public void The_clock_ignores_the_text_size() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = harness.ShowBar();

        harness.Manager.SetBarTextOffset(BarText.Maximum);
        bar.Bar.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        var date = Descendants(bar.Bar).OfType<TextBlock>().Single(t => t.Text == DateTime.Now.ToString("d"));
        Assert.Equal(13, date.FontSize);
    });

    [Fact]
    public void Changing_the_setting_redraws_the_captions_at_once() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var bar = harness.ShowBar();

        harness.Manager.SetBarTextOffset(2);
        bar.Bar.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        Assert.Equal(11.5, CaptionSize(bar.Rows, "Sparrow"));
    });
}
