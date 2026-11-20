using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;
using Deskbar.Core.Overview;

namespace Deskbar.App;

// PINNED WORKSPACES' ITEMS (docs/superpowers/specs/2026-11-20-pinned-workspaces-design.md). Petre: "i'd have a
// messengers workspace pinned and i'd go to it each time i need to do something there, and have a simple way to go back
// to the previous workspace", "i need to jump in and out of the messengers".
//
// One item per pinned workspace, and it FLIPS: outside the workspace it reads the workspace's name and goes in; inside,
// the same item in the same place reads "↩ <where you came from>" and goes back. What it reads and where it goes is
// WorkspaceJumps.Of, in Core, with the back target the bar's ↩ button and a Win+Ctrl+Tab tap use.
//
// Where the items are:
//   * every screen strip, at the END of the window tabs (Petre: "put it at the end of the list of windows"), so it can
//     be reached from every screen ("so i can go to it from both strips");
//   * Windows' taskbar, one button each (TaskbarJumps), on every desktop.
// NOT in the bar's own header: they were there at first, after Start, search and the tile button, and Petre, with a
// screenshot of it: "i don't want pinned at the top". The bar shows a pinned workspace by its row, drawn first with a pin.
//
// The look is "Look 2" from the mockup Petre picked: a dark chip with a dot and a 2 DIP underline in the colour of the
// workspace the click goes to. Not a solid chip in that colour (Look 1), because the strip's name tag is already a
// solid block in the current workspace's colour, and two solid blocks side by side made "where I am" and "where this
// goes" look alike.
//
// Built inside the bar's rebuild, with the tabs, never by a rebuild of their own (the re-entrancy trap, CLAUDE.md), and
// a fresh element for every strip, because a WPF element has one parent.
public partial class FloatingBar
{
    // Tag on every item, so tests and the strip can tell an item from a window tab.
    internal const string WorkspaceJumpTag = "workspace-jump";

    // Tag on a pinned workspace's row mark, below.
    internal const string PinnedWorkspaceMarkTag = "pinned-workspace-mark";

    // The mark beside a pinned workspace's name on its row (Petre: "let's also have the pinned workspace showing that
    // it's pinned"). The pin pinned WINDOWS wear on their icon (WithPinBadge), drawn bare in the caption's own ink and
    // a little smaller than the letters, so it reads as part of the name line rather than as a badge on something.
    // Pinned rows are also drawn first (the rebuild's walk); the mark is what tells a pinned row from a row that merely
    // happens to be first.
    static UIElement PinnedWorkspaceMark(double fontSize, Brush ink) =>
        new Viewbox
        {
            Tag = PinnedWorkspaceMarkTag,
            Width = fontSize,
            Height = fontSize,
            Margin = new Thickness(3, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Child = new Path
            {
                Data = PinBadgeShape,
                Fill = ink,
                Stroke = ink,
                StrokeThickness = 0.7,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                // The shape lives in the middle of a 10 unit square; the box keeps that square, so the pin is centred.
                Width = 10,
                Height = 10,
            },
        };

    // The chip's own fill: one step lighter than the strip (#141518) and the active tab (#1F232B), so it reads as a
    // control rather than as a tab, and darker than the name tag's neutral grey. From the mockup.
    static readonly Brush JumpFill = Frozen(0xFF, 0x26, 0x2A, 0x33);

    // An item with nowhere to go (inside, nothing visited before) wears the strip's neutral grey, the colour the name
    // tag wears on an unnamed desktop.
    static readonly Brush JumpNowhere = Frozen(0xFF, 0x3A, 0x3F, 0x49);

    // The items for this overview: the current workspace is the overview's, the same one the strips' name tag shows,
    // and "back" is the manager's MRU answer.
    IReadOnlyList<WorkspaceJump> JumpsFor(Overview overview) =>
        WorkspaceJumps.Of(
            manager.State.Workspaces,
            overview.Workspaces.FirstOrDefault(w => w.IsCurrent) is { } current ? current.Workspace.Id : Maybe<Guid>.None,
            manager.ByRecentUse().Back);

    // The pinned workspaces' buttons on Windows' taskbar (TaskbarJumps), kept in step with the strip items. Set by the
    // composition root; left unset, as every test but the taskbar ones leaves it, there are no buttons.
    public TaskbarJumps? TaskbarJumps { get; init; }

    // Brings the taskbar buttons in line with the items. Called from the full rebuild and from the strips-only rebuild
    // of a minimized bar, which is why it is not folded into FillStrips: it runs with the list off the screens too.
    void FillTaskbarJumps(Overview overview, IReadOnlyList<WorkspaceJump> jumps) =>
        TaskbarJumps?.Show(jumps, jump => JumpColour(jump, overview));

    // The colour of the workspace an item's click goes to, from the same method that colours its row
    // (WorkspaceAccent), so the item, its taskbar button, the row and the strip's name tag agree. The neutral grey
    // when it goes nowhere.
    Brush JumpColour(WorkspaceJump jump, Overview overview) =>
        jump.Target
            .Bind(target => overview.Workspaces.FirstOrDefault(w => w.Workspace.Id == target) is { } group
                && WorkspaceAccent(overview, group) is { } accent
                ? Maybe<Brush>.From(accent)
                : Maybe<Brush>.None)
            .GetValueOrDefault(JumpNowhere);

    // One item, a new element on every call, in its destination's colour (JumpColour).
    FrameworkElement JumpButton(WorkspaceJump jump, Overview overview)
    {
        var colour = JumpColour(jump, overview);
        var targetName = jump.Target
            .Bind(target => manager.State.Workspaces.TryFirst(w => w.Id == target))
            .Map(w => w.Name);

        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = colour, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = jump.Label, Foreground = Brushes.White, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });

        var button = new Button
        {
            Template = BareButton,
            Content = content,
            Background = JumpFill,
            BorderBrush = colour,
            BorderThickness = new Thickness(0, 0, 0, 2),
            Padding = new Thickness(9, 0, 10, 0),
            Margin = new Thickness(2, 0, 0, 0),
            Cursor = Cursors.Hand,
            Focusable = false,
            Tag = WorkspaceJumpTag,
            ToolTip = targetName.Match(
                name => jump.IsBack ? $"Back to {name}" : $"Go to {name}",
                () => "Nothing to go back to yet"),
        };
        AutomationProperties.SetName(button, jump.Label);
        button.Click += (_, _) => jump.Target.Tap(target => Report(manager.Switch(target)));
        return button;
    }
}
