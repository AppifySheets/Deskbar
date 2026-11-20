using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CSharpFunctionalExtensions;

namespace Deskbar.App;

// THE BAR'S ARRANGE MODE, the mode half of "drag rows to arrange" (design:
// docs/superpowers/specs/2026-11-20-bar-edit-mode-design.md, "Turning it on and off" and "What changes on the bar while
// it is on"). This file is only the MODE: the flag, how it is entered and left, and what the bar looks like while it
// is on. The drag itself is FloatingBar.ArrangeDrag.cs, and hangs off the same hooks.
//
// WHY A MODE AT ALL. A row's press already means "switch to it" and a fold toggle's press means "fold it"
// (OnPreviewMouseLeftButtonDown, with the click-reliability work of #48 behind it). Making rows draggable on top of
// that would have made every click a possible drag. So arranging is a state the user enters on purpose, and while it is
// on, a press on the bar never switches, folds or drags a window; it is free to mean "grab this row".
//
// WHAT CHANGES WHILE IT IS ON:
//   * a grip, the two columns of dots, before every workspace row's name and every group header's name (ArrangeGrip),
//     so the rows look grabbable. No outlines: Petre picked look 1A of the mockup.
//   * the rows' icons at 45% opacity, so the rows read as the thing being arranged rather than as windows to click.
//     A screen strip's tabs are built by the same code but stay whole (WindowDragAllowed).
//   * a strip at the bottom of the bar with what to do and a Done button (BuildArrangeStrip).
//   * no press on a row switches a workspace, toggles a fold or starts a window drag (SwallowedWhileArranging, in both
//     bar-level mouse handlers, and WindowDragSource's canStart). Everything that is not a row (the header line, the
//     footer, Back and Minimize) works as it always does.
//   * it is not offered, and cannot start, while the bar is minimized (SetArranging).
//
// HOW IT ENDS: the Done button, Esc, the menu item (it flips to "Done arranging"), or `arrange off` on the control pipe.
// Each of them cancels a row drag that is still in flight first (an Esc during a drag ends only the drag, see below).
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

    // Whether a window button may start a window drag right now (WindowDragSource's canStart). The bar's own row icons
    // may not while arranging, since a press there is for the row. A screen strip's tab always may: it lives in a window
    // of its own, the bar's press handlers never see it, and its click still activates its window, so a tab that
    // clicked but would not drag would be half a button (a ruling on the branch review, finding 5).
    internal bool WindowDragAllowed(bool onStrip) => onStrip || !arranging;

    // How many times entering the mode took the foreground; read by the tests, which cannot see the real foreground.
    internal int ArrangeActivations { get; private set; }

    // Test hooks for a resize held across the start of the mode (review, Important 2): the bar's own resize state is
    // private and is begun by a real press on a side strip, which a test cannot make.
    internal void BeginResizeForTest() => resizing = (ResizeSide.Left, 0, Width, Left);
    internal bool IsResizingForTest => resizing is not null;

    // Turns the mode on or off and redraws the bar, which is how the grips, the dimmed icons and the strip come and go:
    // every one of them is built by the normal rebuild, reading `arranging`, so they cannot drift from a bar that was
    // rebuilt for some other reason mid-mode. Idempotent: asking for the state the bar is already in changes nothing.
    //
    // `activate` is true for a UI entry (the menu) and false for the control pipe. Esc needs the bar to be the foreground
    // window, but a script or another session sending `arrange on` while the user types elsewhere must not steal his
    // keystrokes; from the pipe, the bar is left where it is and Esc works once the user clicks it (Done always works).
    internal Result SetArranging(bool on, bool activate = false)
    {
        if (on == arranging) return Result.Success();

        // Arranging is done on the bar, so it needs the bar on screen. A minimized bar has no rows to drag and no Done
        // strip to end the mode with, and the strips' menu (the bar's own, FillBarMenu) is the one place it could be
        // asked for from. The menu does not offer it then (AddArrangeItem), and the pipe is told why.
        if (on && Minimized) return Result.Failure("The bar is minimized.");

        // A drag in flight belongs to the mode: ended with it, dropping nothing (FloatingBar.ArrangeDrag.cs). Before the
        // flag flips, so the rebuild below is not deferred by a drag that no longer has a mode to drop into.
        if (!on) CancelArrangeDrag("the mode ended");

        arranging = on;
        if (ClickTrace.On) ClickTrace.Write($"arrange: {(on ? "on" : "off")}");

        if (on)
        {
            arrangeStrip = BuildArrangeStrip();
            // Docked to the bottom and declared AFTER the shell footer, so it sits straight under the rows and above the
            // taskbar strip; the stack of rows takes what is left (BarContent's LastChildFill).
            DockPanel.SetDock(arrangeStrip, Dock.Bottom);
            BarContent.Children.Insert(1, arrangeStrip);
            // So Esc reaches the bar (see the file's comment), but only when the user asked from the bar itself.
            if (activate)
            {
                ArrangeActivations++;
                Activate();
            }
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
    //
    // During a row drag, Esc cancels the DRAG and leaves the mode on: the user who lets go of a drag he regrets is in the
    // middle of arranging, and throwing him out of the mode too would make him start over. A second Esc ends the mode.
    void OnArrangePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!arranging || e.Key != Key.Escape) return;
        e.Handled = true;
        if (arrangeDrag is not null) CancelArrangeDrag("Esc");
        else EndArrangingOnEscape();
    }

    // The two bar-level mouse handlers stop here while arranging, so that no row switches and no fold toggles
    // (that is what makes the mode a mode). Returns true when the handler must stop there.
    //
    // It is called AFTER the handler's #48 press/release bookkeeping, never before: the row's release handler and the
    // icon's orphan rescue run on handled events too (handledEventsToo), so they read the flags that bookkeeping
    // maintains, and a swallow placed first left them the last click's values (a click before the mode could switch a
    // row inside it). Both of those handlers also check `arranging` themselves, which does not depend on any flag.
    //
    // ONLY THE ROWS go inert (spec, "What changes on the bar while it is on"). The first version swallowed every press
    // but the Done strip's, which left Start, search, the pinned apps, the footer (tray chevron, clock, stats, media)
    // and the bar's own Back and Minimize dead for as long as the mode was on (review, Important 1). So where the press
    // landed decides (ArrangeAreaOf):
    //   * the rows (Rows, and the titled list under them): swallowed, marked handled, and the handler stops. This is the
    //     press a row drag starts from.
    //   * the Done strip: let through UNHANDLED so it reaches its button, but the handler still stops: carrying on would
    //     arm a bar drag from a press on a button.
    //   * anywhere else (the header line, the footer, the bar's own padding): not the mode's business at all, so the
    //     handler carries on as it would with the mode off. The header and the footer are click targets already
    //     (StartedOnClickTarget), so a press on them cannot arm a bar drag either.
    // A release while a row press is held belongs to that press wherever it lands: under the bar's capture its source
    // is the bar itself, which is in none of the areas above.
    bool SwallowedWhileArranging(MouseButtonEventArgs e, string what)
    {
        if (!arranging) return false;

        var area = arrangeDrag is not null ? ArrangeArea.Rows : ArrangeAreaOf(e.OriginalSource);
        if (ClickTrace.On)
            ClickTrace.Write($"arrange: {what} {area switch
            {
                ArrangeArea.Rows => "swallowed",
                ArrangeArea.Strip => "passed to the strip",
                _ => "left to the bar",
            }} source={e.OriginalSource.GetType().Name}");
        if (area == ArrangeArea.Rows) e.Handled = true;
        return area != ArrangeArea.Elsewhere;
    }

    // Where on the bar a press landed, as far as the mode is concerned (see SwallowedWhileArranging).
    enum ArrangeArea { Elsewhere, Rows, Strip }

    // Walks up from what was hit. The header line (BarHeader) is checked on the way up BEFORE Rows, because the rebuild
    // moves it INTO the pinned row's caption cell (TakeBarCommands), so it sits inside Rows without being a row.
    ArrangeArea ArrangeAreaOf(object source)
    {
        for (var node = source as DependencyObject; node is not null; node = ParentOf(node))
        {
            if (ReferenceEquals(node, arrangeStrip)) return ArrangeArea.Strip;
            if (ReferenceEquals(node, BarHeader)) return ArrangeArea.Elsewhere;
            if (ReferenceEquals(node, Rows) || ReferenceEquals(node, CurrentList)) return ArrangeArea.Rows;
        }
        return ArrangeArea.Elsewhere;
    }

    bool PressIsOnArrangeStrip(object source) => ArrangeAreaOf(source) == ArrangeArea.Strip;

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
    // naming what it would do, like the dock items beside it. Not offered while the bar is minimized (see SetArranging):
    // the strips open the bar's own menu then, and an item that could only fail does not belong on it.
    void AddArrangeItem(ContextMenu menu)
    {
        if (Minimized) return;
        var item = new MenuItem
        {
            Header = arranging ? "Done arranging" : "Arrange workspaces",
            Icon = MenuGlyph("⇅"),
        };
        item.Click += (_, _) => Report(SetArranging(!arranging, activate: true));
        menu.Items.Add(item);
    }
}
