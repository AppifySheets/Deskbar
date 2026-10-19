using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TaskSpaces.App;

// "Update Deskbar to 2.0.1?", as a window of the app's own rather than a MessageBox. Petre: "when there are
// multiple new versions, app shows many popups with all the versions; let's close all previous versions when
// showing a new version".
//
// The cause was the MessageBox. The update check runs every five minutes, and a MessageBox waits in a nested
// message loop that keeps the dispatcher, and so that timer, running underneath it. Every release published
// while one question sat unanswered opened another on top, and a MessageBox cannot be closed from code, so
// nothing could take the old ones away. This one can: App keeps the one open offer and closes it before showing
// the next (see App.OfferUpdate), so only the newest version is ever being asked about.
//
// Styled like UpdateProgress, which follows it, so the two read as one flow.
public sealed class UpdateOffer : Window
{
    // `accept` is the primary button's label ("Update", "Open release page"); `onAccept` runs when it is
    // pressed. Closing the window any other way is the No. `alternative` with `onAlternative` adds a
    // second action button beside it (the Microsoft Store link, once there is a listing); left null, the
    // window has only the primary button and "Not now".
    public UpdateOffer(string question, string accept, Action onAccept, string? alternative = null, Action? onAlternative = null)
    {
        Title = "Deskbar";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));

        var text = new TextBlock
        {
            Text = question,
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };

        var yes = new Button { Content = accept, Padding = new Thickness(14, 4, 14, 4), IsDefault = true, MinWidth = 90 };
        yes.Click += (_, _) =>
        {
            Close();
            onAccept();
        };
        var no = new Button { Content = "Not now", Padding = new Thickness(14, 4, 14, 4), IsCancel = true, Margin = new Thickness(8, 0, 0, 0), MinWidth = 90 };
        no.Click += (_, _) => Close();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(yes);

        // The optional second action, between the primary button and "Not now". Closes the offer first, like
        // the primary one, so the offer never outlives the choice made in it.
        if (alternative is not null && onAlternative is not null)
        {
            var other = new Button { Content = alternative, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(8, 0, 0, 0), MinWidth = 90 };
            other.Click += (_, _) =>
            {
                Close();
                onAlternative();
            };
            buttons.Children.Add(other);
        }

        buttons.Children.Add(no);

        var body = new StackPanel { Margin = new Thickness(16) };
        body.Children.Add(text);
        body.Children.Add(buttons);
        Content = body;
    }
}
