using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Deskbar.Core.Overview;

namespace Deskbar.App;

// A SESSION ASKING FOR YOU, DRAWN. Petre: "i also want to tell claude through the deskbar that if it needs attention, it
// should alert the deskbar, which should alert me by having that bar on the deskbar blink", "and the app itself should
// also glow, most likely vscode", then "c" from the mockup of three blinks: the workspace's NAME and its colour RAIL
// flash orange, and the session's icon glows orange with them. Not the ring (the amber hover ring's shape) and not a
// filled row.
//
// Orange is the attention dot's colour (AttentionDot), so the bar keeps one colour for "something here wants you".
//
// Two brushes, one per bar, animated together at 1.2 s a cycle:
//
//   AskingPulse     orange, its opacity running 1 -> 0.15 -> 1. Laid OVER the rail, so the rail alternates between
//                   orange and its own colour, and used as the asking icon's ring and glow.
//   AskingCaption   the caption's colour running orange -> the resting caption white -> orange.
//
// Shared rather than one animation per element, so every blinking thing on the bar keeps one rhythm, and a rebuild,
// which replaces every element, never restarts it. Per BAR rather than static because an animated brush cannot be
// frozen, and an unfrozen one belongs to the thread that made it (the tests build each bar on its own STA thread).
//
// Running only while some row is asking (SyncAsking, at the end of every rebuild): an animation clock ticks every
// frame whether or not anything draws with it, and the bar is on screen all day.
public partial class FloatingBar
{
    static readonly Color AskingOrange = Color.FromRgb(0xFF, 0xA7, 0x26);
    static readonly Duration AskingHalfCycle = new(TimeSpan.FromMilliseconds(600));

    SolidColorBrush? askingPulse, askingCaption;
    bool askingRunning;

    internal SolidColorBrush AskingPulse => askingPulse ??= new SolidColorBrush(AskingOrange);

    internal SolidColorBrush AskingCaption => askingCaption ??= new SolidColorBrush(AskingOrange);

    // Whether the animation is running, for the tests.
    internal bool AskingAnimated => askingRunning;

    // Called with each fresh overview: start the two animations when the first row asks, stop them when the last one
    // stops. Stopping leaves both brushes at their base values, which nothing draws with by then.
    void SyncAsking(Core.Overview.Overview overview)
    {
        var any = overview.Workspaces.Any(g => g.WantsAttention || g.Running.Any(r => r.Asks));
        if (any == askingRunning) return;
        askingRunning = any;

        if (any)
        {
            AskingPulse.BeginAnimation(Brush.OpacityProperty, new DoubleAnimation(1, 0.15, AskingHalfCycle)
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            var resting = ((SolidColorBrush)RestingRowForeground).Color;
            AskingCaption.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(AskingOrange, resting, AskingHalfCycle)
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        }
        else
        {
            AskingPulse.BeginAnimation(Brush.OpacityProperty, null);
            AskingCaption.BeginAnimation(SolidColorBrush.ColorProperty, null);
        }
    }

    // The orange layer over a row's rail, the same size and shape as the rail itself so it reads as the rail changing
    // colour. Not hit-testable: the rail is part of the row's click target and this must not take that away.
    UIElement AskingRail(FrameworkElement rail) => new System.Windows.Controls.Border
    {
        Width = rail.Width,
        Background = AskingPulse,
        HorizontalAlignment = rail.HorizontalAlignment,
        VerticalAlignment = rail.VerticalAlignment,
        Margin = rail.Margin,
        CornerRadius = rail is System.Windows.Controls.Border b ? b.CornerRadius : default,
        IsHitTestVisible = false,
    };
}
