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
//   * the bar's header, after Start, search and the tile button (JumpLine), so the items exist when the window list is
//     not on the screens and there are no strips.
//
// The look is "Look 2" from the mockup Petre picked: a dark chip with a dot and a 2 DIP underline in the colour of the
// workspace the click goes to. Not a solid chip in that colour (Look 1), because the strip's name tag is already a
// solid block in the current workspace's colour, and two solid blocks side by side made "where I am" and "where this
// goes" look alike.
//
// Built inside the bar's rebuild, with the tabs, never by a rebuild of their own (the re-entrancy trap, CLAUDE.md), and
// a fresh element for every strip and for the header, because a WPF element has one parent.
public partial class FloatingBar
{
    // Tag on every item, so tests and the strip can tell an item from a window tab.
    internal const string WorkspaceJumpTag = "workspace-jump";

    // The chip's own fill: one step lighter than the strip (#141518) and the active tab (#1F232B), so it reads as a
    // control rather than as a tab, and darker than the name tag's neutral grey. From the mockup.
    static readonly Brush JumpFill = Frozen(0xFF, 0x26, 0x2A, 0x33);

    // An item with nowhere to go (inside, nothing visited before) wears the strip's neutral grey, the colour the name
    // tag wears on an unnamed desktop.
    static readonly Brush JumpNowhere = Frozen(0xFF, 0x3A, 0x3F, 0x49);

    // The header's copy of the items, created here and placed as the last child of ShellStart (BuildShellStrip), so it
    // sits after Start, search and the tile button and travels with the header wherever the header goes.
    internal StackPanel JumpLine { get; } = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

    // The items for this overview: the current workspace is the overview's, the same one the strips' name tag shows,
    // and "back" is the manager's MRU answer.
    IReadOnlyList<WorkspaceJump> JumpsFor(Overview overview) =>
        WorkspaceJumps.Of(
            manager.State.Workspaces,
            overview.Workspaces.FirstOrDefault(w => w.IsCurrent) is { } current ? current.Workspace.Id : Maybe<Guid>.None,
            manager.ByRecentUse().Back);

    // Refills the header's items. Called from the full rebuild and from the strips-only rebuild of a minimized bar.
    void FillJumpLine(Overview overview, IReadOnlyList<WorkspaceJump> jumps)
    {
        JumpLine.Children.Clear();
        jumps.ToList().ForEach(jump => JumpLine.Children.Add(JumpButton(jump, overview)));
        JumpLine.Visibility = jumps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // One item, a new element on every call. The colour is that of the workspace the click goes to, from the same
    // method that colours its row (WorkspaceAccent), so the item, the row and the strip's name tag agree.
    FrameworkElement JumpButton(WorkspaceJump jump, Overview overview)
    {
        var colour = jump.Target
            .Bind(target => overview.Workspaces.FirstOrDefault(w => w.Workspace.Id == target) is { } group
                && WorkspaceAccent(overview, group) is { } accent
                ? Maybe<Brush>.From(accent)
                : Maybe<Brush>.None)
            .GetValueOrDefault(JumpNowhere);
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
