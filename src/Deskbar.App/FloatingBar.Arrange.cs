using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CSharpFunctionalExtensions;

namespace Deskbar.App;

// THE BAR'S ARRANGE MODE, the mode half of "drag rows to arrange" (design:
// docs/superpowers/specs/2026-11-20-bar-edit-mode-design.md, "Turning it on and off" and "What changes on the bar while
// it is on"). This file is only the MODE: the flag, how it is entered and left, and what the bar looks like while it
// is on. The drag itself is a later part and hangs off the same hooks.
//
// WHY A MODE AT ALL. A row's press already means "switch to it" and a fold toggle's press means "fold it"
// (OnPreviewMouseLeftButtonDown, with the click-reliability work of #48 behind it). Making rows draggable on top of
// that would have made every click a possible drag. So arranging is a state the user enters on purpose, and while it is
// on, a press on the bar never switches, folds or drags a window; it is free to mean "grab this row".
//
// WHAT CHANGES WHILE IT IS ON:
//   * a grip, the two columns of dots, before every workspace row's name and every group header's name (ArrangeGrip),
//     so the rows look grabbable. No outlines: Petre picked look 1A of the mockup.
//   * icons at 45% opacity, so the rows read as the thing being arranged rather than as windows to click.
//   * a strip at the bottom of the bar with what to do and a Done button (BuildArrangeStrip).
//   * no press switches a workspace, toggles a fold or starts a window drag (the early return in both bar-level mouse
//     handlers, and WindowDragSource's canStart).
//
// HOW IT ENDS: the Done button, Esc, the menu item (it flips to "Done arranging"), or `arrange off` on the control pipe.
// It is never persisted: a bar that starts up arranging, with no way to know why, would be a trap.
//
// ESC NEEDS THE BAR TO BE ACTIVE. The bar normally takes the foreground on a click, but a bar entered through the pipe
// or the menu of a strip may not be the foreground window, and a key goes to the foreground window. Entering the mode
// therefore calls Activate().
public partial class FloatingBar
{
    // The grip's tag, so a test (and a later drag) can find it without knowing how a caption is laid out.
    internal const string ArrangeGripTag = "arrange-grip";

    // Opacity of an icon while arranging. The one number the spec gives for it.
    internal const double ArrangingIconOpacity = 0.45;

    // Whether the mode is on. Set only by SetArranging, on the UI thread. Not persisted, see above.
    bool arranging;

    // The strip with the instruction and the Done button, while the mode is on; null otherwise. Kept so a press on it
    // can be told from a press on a row (PressIsOnArrangeStrip) and so leaving the mode can take it out again.
    Border? arrangeStrip;

    internal bool Arranging => arranging;

    // Turns the mode on or off and redraws the bar, which is how the grips, the dimmed icons and the strip come and go:
    // every one of them is built by the normal rebuild, reading `arranging`, so they cannot drift from a bar that was
    // rebuilt for some other reason mid-mode. Idempotent: asking for the state the bar is already in changes nothing.
    internal Result SetArranging(bool on)
    {
        if (on == arranging) return Result.Success();

        arranging = on;
        if (ClickTrace.On) ClickTrace.Write($"arrange: {(on ? "on" : "off")}");

        if (on)
        {
            arrangeStrip = BuildArrangeStrip();
            // Docked to the bottom and declared AFTER the shell footer, so it sits straight under the rows and above the
            // taskbar strip; the stack of rows takes what is left (BarContent's LastChildFill).
            DockPanel.SetDock(arrangeStrip, Dock.Bottom);
            BarContent.Children.Insert(1, arrangeStrip);
            // So Esc reaches the bar (see the file's comment).
            Activate();
        }
        else if (arrangeStrip is not null)
        {
            BarContent.Children.Remove(arrangeStrip);
            arrangeStrip = null;
        }

        Rebuild();
        return Result.Success();
    }

    // What the Esc handler calls. A separate method so the key handler is one line and a test can end the mode the
    // way the key does.
    internal void EndArrangingOnEscape() => SetArranging(false);

    // Esc ends the mode. Wired once, in the constructor, as a PREVIEW handler so a focused button inside the bar cannot
    // swallow the key first. Only while arranging, and only marks the key handled then, so Esc keeps meaning whatever it
    // meant everywhere else (a menu, a dialog) the rest of the time.
    void OnArrangePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!arranging || e.Key != Key.Escape) return;
        e.Handled = true;
        EndArrangingOnEscape();
    }

    // The two bar-level mouse handlers return early while arranging, so that no row switches and no fold toggles
    // (that is what makes the mode a mode). Returns true when the handler must stop there.
    //
    // A press or release on the Done strip is let through UNHANDLED, so it reaches its button, but the handler still
    // stops: carrying on would arm a bar drag from a press on a button, and the bar's own press/release bookkeeping
    // (#48) has nothing to say about a strip that is not a row.
    bool SwallowedWhileArranging(MouseButtonEventArgs e, string what)
    {
        if (!arranging) return false;

        var onStrip = PressIsOnArrangeStrip(e.OriginalSource);
        if (ClickTrace.On)
            ClickTrace.Write($"arrange: {what} {(onStrip ? "passed to the strip" : "swallowed")} source={e.OriginalSource.GetType().Name}");
        if (!onStrip) e.Handled = true;
        return true;
    }

    bool PressIsOnArrangeStrip(object source)
    {
        for (var node = source as DependencyObject; node is not null; node = ParentOf(node))
            if (ReferenceEquals(node, arrangeStrip)) return true;
        return false;
    }

    // The grip before a name, or null when the mode is off (so a caller can write `if (ArrangeGrip() is { } grip)`).
    // A TextBlock of its own, never part of the name's Text: the name stays exactly the name, which is what screen
    // readers and everything that finds a row by its name read (see RowLabel). The line height is clamped to the same
    // box as the captions so that gaining a grip cannot make a row taller.
    TextBlock? ArrangeGrip() => !arranging
        ? null
        : new TextBlock
        {
            Text = "⋮⋮",
            Tag = ArrangeGripTag,
            FontSize = GroupHeaderFontSize,
            FontWeight = FontWeights.Bold,
            Foreground = DimForeground,
            LineHeight = GroupHeaderFontSize + 2,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 3, 0),
            IsHitTestVisible = false,
        };

    // The strip at the foot of the rows: what to do, and the button that does it.
    Border BuildArrangeStrip()
    {
        var text = new TextBlock
        {
            Text = "Drag rows to arrange · Esc",
            FontSize = GroupHeaderFontSize,
            Foreground = DimForeground,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var done = new Button
        {
            Content = new TextBlock { Text = "Done", FontSize = GroupHeaderFontSize, FontWeight = FontWeights.Bold, Foreground = CurrentRowForeground },
            // Chrome-less like every other button on the bar (see BareButton), so the stock template's hover layer does
            // not paint a square highlight round it; the strip's own tint is the only fill.
            Template = BareButton,
            Padding = new Thickness(8, 1, 8, 1),
            Background = MiddleWash,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Done arranging",
        };
        done.Click += (_, _) => Report(SetArranging(false));
        DockPanel.SetDock(done, Dock.Right);

        var line = new DockPanel { LastChildFill = true };
        line.Children.Add(done);
        line.Children.Add(text);

        return new Border
        {
            Child = line,
            Margin = new Thickness(0, 3, 0, 0),
            Padding = new Thickness(4, 2, 2, 2),
            Tag = "arrange-strip",
        };
    }

    // The menu item, on the bar's own menu (AddBarItems) and on a group header's (GroupMenu). One item that flips,
    // naming what it would do, like the dock items beside it.
    void AddArrangeItem(ContextMenu menu)
    {
        var item = new MenuItem
        {
            Header = arranging ? "Done arranging" : "Arrange workspaces",
            Icon = MenuGlyph("⇅"),
        };
        item.Click += (_, _) => Report(SetArranging(!arranging));
        menu.Items.Add(item);
    }
}
