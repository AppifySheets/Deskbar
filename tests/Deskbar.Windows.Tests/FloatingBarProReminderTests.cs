using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Deskbar.App;

namespace Deskbar.Windows.Tests;

// The Deskbar Pro reminder line at the bottom of the bar (FloatingBar.ProReminder.cs). WHEN it is due is
// ProReminder.Due, tested in Core; here the bar's ProReminderDue seam stands in for the App's answer, and the test
// checks that the bar paints that answer and wires its two buttons.
//
// The harness: GEPHA (current) and Sparrow, one window each (see Harness in FloatingBarRebuildTests).
public class FloatingBarProReminderTests
{
    static FrameworkElement Line(FloatingBar bar) => (FrameworkElement)bar.FindName("ProReminderLine")!;

    // Everything inside the panel, walked through the logical tree, since the sentence and the buttons sit in
    // nested panels (the buttons on their own line under the wrapped sentence).
    static IEnumerable<object> Inside(object node) =>
        new[] { node }.Concat(node is DependencyObject d ? LogicalTreeHelper.GetChildren(d).Cast<object>().SelectMany(Inside) : []);

    static Button ButtonNamed(FloatingBar bar, string name) =>
        Inside(Line(bar)).OfType<Button>().Single(b => AutomationProperties.GetName(b) == name);

    static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [Fact]
    public void Hidden_when_not_due() => StaThread.Run(() =>
    {
        var bar = new FloatingBar(Harness.Build().Manager) { Left = -32000, Top = -32000, ProReminderDue = () => false };
        using var shown = new BarUnderTest(bar);
        bar.Show();
        bar.PaintProReminder();
        Assert.Equal(Visibility.Collapsed, Line(bar).Visibility);
    });

    [Fact]
    public void Shown_when_due_with_the_licence_sentence_and_both_buttons() => StaThread.Run(() =>
    {
        var bar = new FloatingBar(Harness.Build().Manager) { Left = -32000, Top = -32000, ProReminderDue = () => true };
        using var shown = new BarUnderTest(bar);
        bar.Show();
        bar.PaintProReminder();

        Assert.Equal(Visibility.Visible, Line(bar).Visibility);
        var sentence = Inside(Line(bar)).OfType<TextBlock>().Single(t => t.ToolTip is string);
        Assert.StartsWith("Free for personal use.", sentence.ToolTip as string);
        // Wrapped, never cut off: a narrow bar gets more lines, not an ellipsis.
        Assert.Equal(TextWrapping.Wrap, sentence.TextWrapping);
        Assert.Contains("$18.99", sentence.ToolTip as string);
        Assert.NotNull(ButtonNamed(bar, "Get Pro"));
        Assert.NotNull(ButtonNamed(bar, "Not now"));
    });

    [Fact]
    public void Get_Pro_calls_the_App() => StaThread.Run(() =>
    {
        var asked = 0;
        var bar = new FloatingBar(Harness.Build().Manager) { Left = -32000, Top = -32000, ProReminderDue = () => true, GetPro = () => asked++ };
        using var shown = new BarUnderTest(bar);
        bar.Show();
        bar.PaintProReminder();
        Click(ButtonNamed(bar, "Get Pro"));
        Assert.Equal(1, asked);
    });

    // "Not now" records the dismissal through the App and repaints at once, so the line goes away on the click.
    [Fact]
    public void Not_now_dismisses_and_hides_the_line() => StaThread.Run(() =>
    {
        var dismissed = false;
        var bar = new FloatingBar(Harness.Build().Manager)
        {
            Left = -32000,
            Top = -32000,
            ProReminderDue = () => !dismissed,
            DismissProReminder = () => dismissed = true,
        };
        using var shown = new BarUnderTest(bar);
        bar.Show();
        bar.PaintProReminder();
        Assert.Equal(Visibility.Visible, Line(bar).Visibility);

        Click(ButtonNamed(bar, "Not now"));
        Assert.True(dismissed);
        Assert.Equal(Visibility.Collapsed, Line(bar).Visibility);
    });
}
