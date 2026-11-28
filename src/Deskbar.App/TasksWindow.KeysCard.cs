using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// THE KEYS CARD (Deskbar task b61f79de; Petre: "i also want to have a panel which will tell me how to navigate"). A small
// card over the panel listing the keys that act in the place on show (PanelKeys.For: the Inbox's list, a page, the List,
// the Matrix or Stats), key on the left and what it does on the right.
//
//   opens    ? or F1 (TasksWindow.Keys.cs), or the quiet "? keys" button at the panel's foot (InboxFooterOf);
//   closes   ? or F1 again, Esc (first, before anything else Esc undoes), a click anywhere outside it, a view switch;
//   redraws  never close it: the card is the XAML's own KeysCard border, which no redraw rebuilds. Its rows are filled
//            when it opens, for the place shown then; a switch closes it, so it never lists another place's keys.
public partial class TasksWindow
{
    /// <summary>The foot's button that opens the card (its automation name and its words).</summary>
    internal const string KeysHintText = "? keys";

    /// <summary>For the tests: whether the card is up.</summary>
    internal bool KeysCardShown => KeysCard.Visibility == Visibility.Visible;

    /// <summary>For the tests: every text on the card, keys and what they do.</summary>
    internal IReadOnlyList<string> KeysCardTexts =>
        KeysCard.Child is Panel panel ? Descendants(panel).OfType<TextBlock>().Select(b => b.Text).ToArray() : [];

    /// <summary>? or F1, and the foot's "? keys": the card up for the place shown, or down again.</summary>
    internal bool ToggleKeysCard()
    {
        if (KeysCardShown) HideKeysCard();
        else ShowKeysCard();
        return true;
    }

    void ShowKeysCard()
    {
        var rows = new Grid();
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        PanelKeys.For(PlaceShown).Select((key, row) => (key, row)).ToList().ForEach(x =>
        {
            rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var keys = Text(x.key.Keys, 12, InboxLook.Ink, FontWeights.SemiBold, new Thickness(0, 3, 14, 3));
            keys.FontFamily = Mono;
            Grid.SetRow(keys, x.row);
            var does = Text(x.key.Does, 12, InboxLook.Ink2, margin: new Thickness(0, 3, 0, 3));
            does.TextWrapping = TextWrapping.Wrap;
            Grid.SetRow(does, x.row);
            Grid.SetColumn(does, 1);
            rows.Children.Add(keys);
            rows.Children.Add(does);
        });
        var card = new StackPanel();
        card.Children.Add(Text("Keys", 13, InboxLook.Ink, FontWeights.SemiBold, new Thickness(0, 0, 0, 6)));
        card.Children.Add(rows);
        card.Children.Add(Text("? or Esc closes this", 11, InboxLook.Ink3, margin: new Thickness(0, 8, 0, 0)));
        KeysCard.Child = card;
        KeysCard.Visibility = Visibility.Visible;
    }

    void HideKeysCard()
    {
        KeysCard.Visibility = Visibility.Collapsed;
        KeysCard.Child = null;
    }

    // A press outside the card closes it (wired in the constructor, ahead of the click it belongs to, which still goes
    // through: the card is a hint, not a dialog that eats the click). Not a press on the foot's hint: its own click
    // toggles the card, and closing it here first would make that click open it again.
    void CloseKeysCardOnPressOutside(MouseButtonEventArgs e)
    {
        if (KeysCardShown && !KeysCard.IsMouseOver && keysHint?.IsMouseOver != true) HideKeysCard();
    }

    // The hint the foot shows now (the foot is rebuilt with the view; the newest one is the one on screen).
    Button? keysHint;

    // The foot's quiet "? keys" button, which says the card exists without the keyboard.
    Button KeysHint()
    {
        var hint = InboxButton(Text(KeysHintText, 12, InboxLook.Ink3), KeysHintText, () => ToggleKeysCard(), new Thickness(7, 4, 7, 4),
            Brushes.Transparent, 7);
        hint.ToolTip = "The keys of this panel";
        keysHint = hint;
        return hint;
    }
}
