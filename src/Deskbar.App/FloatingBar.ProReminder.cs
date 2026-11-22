using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Deskbar.App;

// THE DESKBAR PRO REMINDER, one line at the bottom of the bar. Petre, 2026-11-22: "a gentle nudge per day for the
// paid version, similar to how startallback does it", then "line at the bottom, okay" from two mockups (a line in
// the bar, or a card beside it) and "add the pro option in the support".
//
// The model: Deskbar is free for personal use and every feature works in every copy; Deskbar Pro is a one-time
// US$18.99 add-on in the Microsoft Store, with priority support (in-app reports from a Pro copy are labelled
// priority, Deskbar #387), asked of anyone who uses Deskbar for work. WHEN the line shows is ProReminder.Due in
// Core (never for Pro, quiet for the first two weeks, at most once a calendar day); the App asks it through
// ProReminderDue and this file only paints the answer.
//
// Why a line in the bar and not a window: the bar is on screen all day, so a line inside it is seen without
// covering anything, taking focus or needing to be closed to keep working, which is what "gentle" means here.
// It sits in the footer above the stats line, so it never shifts the workspace rows.
//
// Painted by PaintProReminder, which the App calls after every Pro verdict refresh (startup and the five-minute
// update tick, so a purchase or a new day is picked up within minutes) and right after either of the two
// buttons. Built once in the constructor, like the now-playing line, and resized by ApplyTextSize.
public partial class FloatingBar
{
    // Whether the line is due right now (ProReminder.Due with the live verdict, the two dates and today), what
    // "Get Pro" does (the Store's purchase dialog in the Store copy, the Store page in the GitHub copy), and what
    // "Not now" does (records today). Set by the App; left at the defaults, the line never shows.
    internal Func<bool> ProReminderDue { get; init; } = () => false;
    internal Action? GetPro { get; init; }
    internal Action? DismissProReminder { get; init; }

    // A blue panel rather than grey text on the bar's own background. Petre, after the first build: "make it more
    // pronounced, perhaps by giving it a different color". Blue because it is the colour Windows uses for an offer
    // or a link and nothing else on the bar is blue; a tint of it, not a solid block, so it reads as a note on the
    // bar and not as an error. The sentence is near-white on it, and "Get Pro" is a solid blue button, the one
    // thing on the line meant to be pressed.
    static readonly Brush ProReminderPanel = Frozen(0x33, 0x4C, 0xC2, 0xFF);
    static readonly Brush ProReminderPanelEdge = Frozen(0x80, 0x4C, 0xC2, 0xFF);
    static readonly Brush ProReminderInk = Frozen(0xD9, 0xFF, 0xFF, 0xFF);
    static readonly Brush ProReminderEmphasis = Frozen(0xFF, 0xFF, 0xFF, 0xFF);
    static readonly Brush ProReminderQuiet = Frozen(0xB3, 0xFF, 0xFF, 0xFF);
    static readonly Brush ProGetFill = Frozen(0xFF, 0x4C, 0xC2, 0xFF);
    static readonly Brush ProGetInk = Frozen(0xFF, 0x00, 0x26, 0x3A);

    TextBlock? proReminderText;
    TextBlock? proGetText;
    TextBlock? proNotNowText;

    // The words, in one place so the README, the site and the Store listing can be checked against them. "Free for
    // personal use" leads because it is the licence; the price and what it brings follow.
    const string ProReminderLead = "Free for personal use.";
    const string ProReminderRest = " Using it for work? Deskbar Pro is $18.99 once, with priority support.";

    // Two parts stacked inside the panel: the sentence, WRAPPED to the bar's width (Petre: "doesn't fit; multiline
    // if so", after a narrow bar cut it to "Free for person..."), then the two buttons on their own line at the
    // right. On a wide bar that is two short lines; on a narrow one the sentence takes as many lines as it needs and
    // nothing is ever cut off.
    void BuildProReminder()
    {
        proReminderText = new TextBlock
        {
            FontSize = Text(10.5),
            Foreground = ProReminderInk,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
        };
        proReminderText.Inlines.Add(new System.Windows.Documents.Run(ProReminderLead) { Foreground = ProReminderEmphasis, FontWeight = FontWeights.SemiBold });
        proReminderText.Inlines.Add(new System.Windows.Documents.Run(ProReminderRest));
        proReminderText.ToolTip = ProReminderLead + ProReminderRest;

        (proGetText, var getPro) = ProReminderButton("Get Pro", ProGetInk, ProGetFill, () => GetPro?.Invoke());
        (proNotNowText, var notNow) = ProReminderButton("Not now", ProReminderQuiet, null, () =>
        {
            DismissProReminder?.Invoke();
            PaintProReminder();
        });
        getPro.Margin = new Thickness(0, 0, 4, 0);

        ProReminderLine.Background = ProReminderPanel;
        ProReminderLine.BorderBrush = ProReminderPanelEdge;
        ProReminderLine.BorderThickness = new Thickness(1);
        ProReminderLine.CornerRadius = new CornerRadius(5);
        ProReminderLine.Padding = new Thickness(8, 6, 6, 6);
        ProReminderLine.Child = new StackPanel
        {
            Children =
            {
                proReminderText,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { getPro, notNow } },
            },
        };
    }

    // A text button. With a fill it is a solid pill (Get Pro); without one it is flat text in the bar's style (the
    // shell strip's template, so it hovers like the bar's other small controls). Returns the label too, so
    // ApplyTextSize can resize it.
    (TextBlock Label, Button Button) ProReminderButton(string label, Brush ink, Brush? fill, Action press)
    {
        var text = new TextBlock { Text = label, FontSize = Text(10.5), Foreground = ink, VerticalAlignment = VerticalAlignment.Center, FontWeight = fill is null ? FontWeights.Normal : FontWeights.SemiBold };
        var button = new Button
        {
            Content = fill is null ? text : new Border { Background = fill, CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 3, 10, 3), Child = text },
            Padding = fill is null ? new Thickness(6, 3, 6, 3) : new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Template = ShellButtonTemplate,
            Focusable = false,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        button.Click += (_, _) =>
        {
            MarkPressConsumed();
            press();
        };
        return (text, button);
    }

    // Shows or hides the line from the App's answer. Cheap, so it is safe to call as often as the App likes.
    internal void PaintProReminder() =>
        ProReminderLine.Visibility = ProReminderDue() ? Visibility.Visible : Visibility.Collapsed;

    void ApplyProReminderTextSize()
    {
        Sized(proReminderText, 10.5);
        Sized(proGetText, 10.5);
        Sized(proNotNowText, 10.5);
    }
}
