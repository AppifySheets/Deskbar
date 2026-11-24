using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Deskbar.App;

// THE GLOW OF A TASK JUST ADDED (task ee75f046, Petre: "adding a new task in the bar should highlight it when added").
// He picked option A of the mockup (https://claude.ai/artifact/8r8fRpLoTqvrgpjELDgEd9): the new task becomes the
// selected one, and its line starts in a blue glow that fades into the selected look (the lighter fill a click gives)
// and then simply stays selected. Option 3 of the same mockup: the tasks panel's own add box does the same.
//
// Both views paint a selected line with a frozen fill (FloatingBar.TaskSelectedFill, TasksWindow.SelectedFill); the glow
// swaps in a fresh, unfrozen brush whose colour is animated from the glow's blue to that fill's colour. Once it has run
// the brush holds the selected colour (FillBehavior.HoldEnd), so the line looks exactly like any selected line, and the
// next paint of the selection puts the frozen fill back. A repaint of the lines during the glow simply ends it early.
//
// With Windows' animations switched off (Settings, Accessibility, Visual effects, "Animation effects"; read as
// SystemParameters.ClientAreaAnimation), there is no glow: the new line is only selected.
internal static class TaskGlow
{
    // How long the fade takes: long enough for the eye to land on the line after Enter, short enough to be over before
    // the next thing he does.
    internal static readonly TimeSpan Length = TimeSpan.FromSeconds(1.8);

    // The glow's start: the selection edge's blue (TaskPickedEdge, #4F8CFF) at about 42%, the mockup's value.
    static readonly Color Start = Color.FromArgb(0x6B, 0x4F, 0x8C, 0xFF);

    /// <summary>
    /// The fill for a line that has just been added: a brush fading from the glow to <paramref name="selected"/>'s colour,
    /// or <paramref name="selected"/> itself when Windows' animations are off.
    /// </summary>
    internal static Brush Fading(Brush selected)
    {
        if (!SystemParameters.ClientAreaAnimation || selected is not SolidColorBrush { Color: var end }) return selected;
        var brush = new SolidColorBrush(Start);
        brush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(Start, end, Length) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        return brush;
    }

    /// <summary>For the tests: whether a line's fill is a glow (the only unfrozen fill either view gives a line).</summary>
    internal static bool IsGlow(Brush? fill) => fill is SolidColorBrush { IsFrozen: false };
}
