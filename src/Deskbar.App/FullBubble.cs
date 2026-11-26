using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Deskbar.App;

// EVERY BUBBLE ON A TASK'S PAGE SHOWS ITS TEXT IN FULL (the owner, on his first live look at the Inbox, 2026-11-27: a message
// from Claude was cut to about three lines in a small box with its own scrollbar and "Show more", and he had to scroll inside
// the little box to read it). On the Inbox page, in the panel and in a task window, a comment (his, Claude's, the manager's),
// the question, the session's messages from its transcript and the description are drawn at their natural height: no clip to
// three lines, no inner scrollbar, no resize grip (the grip under a comment, a description or a question, task 2088e1ed,
// is the List's). The page's own scroller is the only thing that scrolls.
//
// ONE GUARD against a monster message: a bubble taller than about GuardLines lines is cut there, with a "Show all" button
// under it that opens it in place for good. "For good" is the stream state's OpenMessages (ChatStreamState: Ids only, kept
// while the window lives), so a redraw of the page, which builds every comment again, does not fold it. The List's own
// thread keeps what main gives it (three lines, "Show more", the grip); nothing here runs there.
internal static class FullBubble
{
    /// <summary>The lines after which a bubble is cut (about: the line is the font size times 1.57, as the thread's three lines are).</summary>
    internal const int GuardLines = 40;

    /// <summary>The Name of the guard's button, for the tests.</summary>
    internal const string ShowAllName = "Show all";

    /// <summary>The height at which a text of <paramref name="fontSize"/> is cut.</summary>
    internal static double GuardHeight(double fontSize) => Math.Ceiling(fontSize * 1.57 * GuardLines);

    /// <summary>
    /// Sets <paramref name="body"/> to show in full: no clip, no inner scrolling, up to the guard, past which it is cut
    /// until <paramref name="opened"/> holds <paramref name="key"/>. Returns the "Show all" button to put under it, which is
    /// collapsed until the laid-out text is taller than the guard (the width, and so the wrapping, is known only then).
    /// </summary>
    internal static Button Guard(RichTextBox body, double fontSize, ISet<string> opened, string key, Func<UIElement, string, Action, Button> button)
    {
        var tall = GuardHeight(fontSize);
        body.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        body.MinHeight = 0;
        body.MaxHeight = double.PositiveInfinity;
        body.ClipToBounds = false;
        if (!opened.Contains(key))
        {
            body.MaxHeight = tall;
            body.ClipToBounds = true;
        }
        var words = new TextBlock { Text = ShowAllName, FontSize = 10.5, Foreground = InboxLook.Ink2 };
        Button? show = null;
        show = button(words, ShowAllName, () =>
        {
            opened.Add(key);
            body.MaxHeight = double.PositiveInfinity;
            body.ClipToBounds = false;
            show!.Visibility = Visibility.Collapsed;
        });
        show.HorizontalAlignment = HorizontalAlignment.Left;
        show.Visibility = Visibility.Collapsed;
        if (!opened.Contains(key))
            body.LayoutUpdated += (_, _) =>
            {
                if (body.ActualWidth <= 0 || opened.Contains(key)) return;
                var wanted = body.ExtentHeight > tall + 0.5 ? Visibility.Visible : Visibility.Collapsed;
                if (show.Visibility != wanted) show.Visibility = wanted;
            };
        return show;
    }
}
