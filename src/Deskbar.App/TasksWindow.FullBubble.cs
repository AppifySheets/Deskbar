using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Deskbar.App;

// THE PAGE'S SIDE OF "EVERY BUBBLE IN FULL" (FullBubble has the rule and its reasons). Every place that clipped or resized a
// text on the List's thread checks FullBubbles first and, on a page, hands the text to the guard instead.
public partial class TasksWindow
{
    // A page is open (in the panel, or the task window's own, ShowPage): its comments, question and description are drawn in
    // full. Nothing on the List runs with it, since a page covers the List while it is open.
    // The manager's page (task 6c4c067f) is a page too.
    bool FullBubbles => inboxPage is not null || managerPage;

    // The guard's button: a flat, borderless button that can take the keyboard, as the stream's own (ChatStreamPanel).
    Button GuardButton(UIElement content, string name, Action press)
    {
        var button = Flat(content, name, press, new Thickness(0), ClickIgnored);
        button.Background = Brushes.Transparent;
        button.Focusable = true;
        return button;
    }

    // A comment on a page: the three-line clip, "Show more" and the resize grip go, and the guard comes. `key` names the
    // comment in the stream state's OpenMessages, where "Show all" is remembered across redraws.
    void FullInPage(StackPanel parts, RichTextBox body, UIElement grip, UIElement more, string key, double size)
    {
        parts.Children.Remove(grip);
        parts.Children.Remove(more);
        parts.Children.Insert(parts.Children.IndexOf(body) + 1, FullBubble.Guard(body, size, streamState.OpenMessages, key, GuardButton));
    }

    // The description on a page, in full under the same guard. It takes no hits itself (a click falls through to the line,
    // DescriptionBlock), so the button under it is a separate element.
    UIElement FullText(RichTextBox text, string key, double size) =>
        new StackPanel { Children = { text, FullBubble.Guard(text, size, streamState.OpenMessages, key, GuardButton) } };
}
