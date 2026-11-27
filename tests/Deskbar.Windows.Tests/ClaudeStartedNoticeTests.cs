using System.Windows;
using System.Windows.Input;
using Deskbar.App;
using Deskbar.Windows.Monitoring;

namespace Deskbar.Windows.Tests;

// The "Claude started" card (Deskbar task 7b17a573; Petre: "confirm it has started as a mild notification, don't even
// take me away from my current thread"). Mild means it never takes the foreground and has no taskbar button, so both
// are pinned on the window's real extended style, not on the WPF properties alone.
public class ClaudeStartedNoticeTests
{
    const long WS_EX_NOACTIVATE = 0x08000000;

    [Fact]
    public void The_card_never_activates_and_has_no_taskbar_button() => StaThread.Run(() =>
    {
        var notice = new ClaudeStartedNotice("Fix the build");
        try
        {
            var style = (long)NativeMethods.GetWindowLongPtr(notice.EnsureHandle(), NativeMethods.GWL_EXSTYLE);

            Assert.NotEqual(0, style & WS_EX_NOACTIVATE);
            Assert.NotEqual(0, style & NativeMethods.WS_EX_TOOLWINDOW);
            Assert.False(notice.ShowActivated);
            Assert.False(notice.ShowInTaskbar);
            Assert.True(notice.Topmost);
        }
        finally
        {
            notice.Close();
        }
    });

    // A click on the card is asking for the session: Clicked fires once and the card closes.
    [Fact]
    public void A_click_raises_Clicked_and_closes_the_card() => StaThread.Run(() =>
    {
        var notice = new ClaudeStartedNotice("Fix the build");
        var clicks = 0;
        var closed = false;
        notice.Clicked += () => clicks++;
        notice.Closed += (_, _) => closed = true;
        notice.EnsureHandle();

        notice.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });

        Assert.Equal(1, clicks);
        Assert.True(closed);
    });
}
