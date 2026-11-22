using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;

namespace Deskbar.App;

// THE TASK LIST'S WORKSPACE MENUS, one builder for all of them: the add box's "other…", a line's workspace chip (move
// the task), and the tasks window's "Move to", workspace filter and add chip. Petre, at the live check, of a long plain
// list of names: "when selecting, give them their formatting and colors". So each workspace is drawn as its chip (its
// name on its own lane colour, the rounded shape and text of the chips on the task lines) in the bar's order (the
// pinned rows first, then the rest, as WorkspaceManager.DrawnBlocks and the bar's rebuild walk them), and the members of
// a group sit under the group's caption, its name in the group's colour, which cannot be clicked.
//
// COMPACT (Petre, of the first version with about 22 workspaces and 6 groups running the whole screen height: "compact
// it"). Not one menu row per chip: the chips flow side by side and wrap, a WrapPanel per block, so a group's workspaces
// sit on one or two lines. The first block is "none" (when offered) and every workspace in no group; then one block per
// group, its small caption 6 DIP below the block above and directly over its chips. A chip is its text with 7 DIP of
// padding left and right and 1 to 2 DIP above and below inside a 1 DIP edge, 3 DIP from its neighbours. The menu is
// about 260 DIP wide, or the bar's width when that is less, and each block wraps at that width.
//
// IT STAYS A CONTEXT MENU, so every rule the bar keeps for its menus still holds (HoldFadeWhileOpen holds the fade and
// defers rebuilds, #77; a click away and Esc close it), with its item container overridden (ChipMenu): the blocks are
// their own containers, so no MenuItem chrome pads them. The chips are focusable buttons, so the keyboard still works:
// Tab and Shift+Tab (and the arrow keys, through directional navigation) walk them in a cycle, Enter or Space picks,
// Esc closes. The menu opens with the keyboard on the current choice, which keeps its blue outline.
//
// The colours are LaneColour's, the resolution the switcher and quick find share with the bar: a group's colour for its
// members, a workspace's own by its defined position otherwise. A workspace that opted out of colour gets the neutral
// chip.
//
// A LONG MENU must not run off the screen: the context menu's template holds the items in a ScrollViewer, and MaxHeight
// is the height of the work area of the screen the menu opens on (WorkAreaHeightAt).
internal static class WorkspaceChipMenu
{
    /// <summary>The Tag of a group's caption in the menu, for the tests.</summary>
    internal const string GroupTag = "workspace-group";

    /// <summary>The automation item status of the chip that is the current choice.</summary>
    internal const string CurrentStatus = "current";

    /// <summary>The menu's width when nothing narrower asks for less (Petre: about 260 DIP).</summary>
    internal const double PreferredWidth = 260;

    // The chips' fills and inks, the task list's own (FloatingBar.TaskList.cs), so the menu looks like the lines; and
    // the blue the text box and a chosen chip use, for the current choice and the keyboard's place.
    static readonly Brush Ink = Frozen(0xFF, 0xE6, 0xE6, 0xE6);
    static readonly Brush NoneFill = Frozen(0xFF, 0x33, 0x33, 0x33);
    static readonly Brush NoneInk = Frozen(0xFF, 0x99, 0x99, 0x99);
    static readonly Brush CurrentEdge = Frozen(0xFF, 0x4F, 0x8C, 0xFF);

    /// <summary>
    /// One entry of the menu: a group's caption (<see cref="IsGroup"/>, never clickable), or a choice of a workspace or
    /// of none. <see cref="Colour"/> is the lane colour, null for none and for a workspace without one.
    /// <see cref="InGroup"/> marks a workspace that belongs to the caption before it.
    /// </summary>
    internal sealed record Entry(bool IsGroup, Maybe<Guid> Workspace, string Name, Brush? Colour, bool InGroup = false);

    /// <summary>
    /// A chip that is not a workspace, placed first (the window's workspace filter has "All workspaces"): its words,
    /// whether it is the current choice, and what picking it does.
    /// </summary>
    internal sealed record Extra(string Name, bool Current, Action Pick);

    /// <summary>
    /// The menu's entries in the bar's order, with "none" first when <paramref name="withNone"/>. A workspace
    /// <paramref name="include"/> turns down is left out, and a group with no member left loses its caption too.
    /// </summary>
    internal static IReadOnlyList<Entry> Entries(AppState state, bool withNone, Func<Workspace, bool>? include = null)
    {
        var shown = include ?? (_ => true);
        var blocks = state.Blocks();
        // The pinned rows first, each side in stored order: the order the bar draws its rows in.
        var drawn = blocks.Where(b => b is [{ Pinned: true }]).Concat(blocks.Where(b => b is not [{ Pinned: true }]));
        var lines = drawn.SelectMany(block =>
        {
            var members = block.Where(shown).ToArray();
            var group = block[0].GroupId is { } id ? state.Groups.FirstOrDefault(g => g.Id == id) : null;
            var choices = members.Select(w => new Entry(false, w.Id, w.Name, BrushOf(LaneColour.Of(state, w)), InGroup: group is not null));
            return group is null || members.Length == 0
                ? choices
                : choices.Prepend(new Entry(true, Maybe<Guid>.None, group.Name,
                    BrushOf(WorkspacePalette.For(group.Color, Math.Max(0, state.ColourSlotOf(group))))));
        });
        return (withNone ? lines.Prepend(new Entry(false, Maybe<Guid>.None, "none", null)) : lines).ToArray();
    }

    /// <summary>
    /// The menu, ready to open under <paramref name="target"/>: a chip per choice whose click calls
    /// <paramref name="pick"/> and closes the menu, the one equal to <paramref name="current"/> outlined (none outlines
    /// nothing when the menu has no "none"), <paramref name="extra"/> chips first. <paramref name="width"/> is the
    /// bar's width when that is narrower than <see cref="PreferredWidth"/>.
    /// </summary>
    internal static ContextMenu Build(IReadOnlyList<Entry> entries, Maybe<Guid> current, Action<Maybe<Guid>> pick, UIElement? target,
        double width = PreferredWidth, IReadOnlyList<Extra>? extra = null)
    {
        var menu = new ChipMenu
        {
            PlacementTarget = target,
            Placement = PlacementMode.Bottom,
            Width = Math.Max(120, Math.Min(PreferredWidth, width)),
            MaxHeight = WorkAreaHeightAt(target),
            Padding = new Thickness(6, 4, 6, 4),
        };
        // Tab, Shift+Tab and the arrows walk the chips in a cycle (see the header).
        KeyboardNavigation.SetTabNavigation(menu, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetDirectionalNavigation(menu, KeyboardNavigationMode.Cycle);

        var first = (extra ?? []).Select(x => ChipButton(x.Name, null, false, x.Current, () =>
            {
                menu.IsOpen = false;
                x.Pick();
            }))
            .Concat(entries.Where(e => !e.IsGroup && !e.InGroup).Select(e => Choice(e, current, pick, menu)))
            .ToArray();
        // The blocks wrap at the menu's inner width, given to them outright (review I-1): the menu's scroll viewer
        // measures its content at an infinite width, so a WrapPanel left to find its width never wraps and the menu's
        // Width only clips (measured: chips ran to 535 DIP in a menu 150 wide). The inner width is the menu's less its
        // padding and the template's edge.
        var inner = Math.Max(40, menu.Width - menu.Padding.Left - menu.Padding.Right - TemplateEdge);
        if (first.Length > 0) menu.Items.Add(Block(null, first, inner));

        // Each caption with the members that follow it.
        entries.Select((entry, at) => (entry, at)).Where(x => x.entry.IsGroup).ToList().ForEach(x =>
            menu.Items.Add(Block(x.entry, entries.Skip(x.at + 1).TakeWhile(e => e.InGroup).Select(e => Choice(e, current, pick, menu)).ToArray(), inner)));

        // The keyboard starts on the current choice, or on the first chip.
        menu.Opened += (_, _) =>
        {
            var chips = Chips(menu);
            (chips.FirstOrDefault(c => AutomationProperties.GetItemStatus(c) == CurrentStatus) ?? chips.FirstOrDefault())?.Focus();
        };
        return menu;
    }

    /// <summary>Every chip of a menu, in order, for the menu's own focus and for the tests.</summary>
    internal static IReadOnlyList<Button> Chips(ContextMenu menu) =>
        menu.Items.OfType<StackPanel>().SelectMany(block => block.Children.OfType<WrapPanel>()).SelectMany(wrap => wrap.Children.OfType<Button>()).ToArray();

    // A context menu whose items are their own containers: a block of chips is not wrapped in a MenuItem, so no menu
    // row chrome pads it and the chips can sit side by side (see the header).
    sealed class ChipMenu : ContextMenu
    {
        protected override bool IsItemItsOwnContainerOverride(object item) => item is UIElement;
    }

    // One block: an optional caption over a wrap of chips. The caption is 6 DIP below the block above it and sits
    // directly over its chips; the first block has none.
    // What the context menu's template draws around its items, at most, on either theme: a 1 DIP border and a little
    // inner margin.
    const double TemplateEdge = 8;

    static StackPanel Block(Entry? caption, IReadOnlyList<Button> chips, double width)
    {
        var block = new StackPanel();
        if (caption is not null)
            block.Children.Add(new TextBlock
            {
                Tag = GroupTag,
                Text = caption.Name,
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = caption.Colour ?? NoneInk,
                Margin = new Thickness(1, 6, 0, 2),
                IsHitTestVisible = false,
                Focusable = false,
            });
        var wrap = new WrapPanel { Width = width };
        chips.ToList().ForEach(chip => wrap.Children.Add(chip));
        block.Children.Add(wrap);
        return block;
    }

    static Button Choice(Entry entry, Maybe<Guid> current, Action<Maybe<Guid>> pick, ContextMenu menu) =>
        ChipButton(entry.Name, entry.Workspace.HasValue ? entry.Colour : null, entry.Workspace.HasValue, entry.Workspace.Equals(current), () =>
        {
            menu.IsOpen = false;
            pick(entry.Workspace);
        }, entry);

    // The task lines' chip (FloatingBar.WorkspaceChip) as a button: the name on its colour, faded so the name stays
    // readable, or the grey neutral chip. Its edge is always 1 DIP, blue on the current choice and white while the
    // keyboard is on it (the template), so nothing moves as focus walks. A TextBlock, so an underscore in a name is not
    // read as an access key. Its name is trimmed past ChipTextCeiling, the full name in its tooltip, so one very long
    // workspace name cannot run past the menu's edge (review minor 4 of 187cf4a).
    static Button ChipButton(string name, Brush? colour, bool isWorkspace, bool isCurrent, Action pick, Entry? entry = null)
    {
        var button = new Button
        {
            Tag = entry,
            Content = new TextBlock
            {
                Text = name,
                FontSize = 11,
                Foreground = isWorkspace ? Ink : NoneInk,
                MaxWidth = ChipTextCeiling,
                TextTrimming = TextTrimming.CharacterEllipsis,
            },
            ToolTip = name,
            Template = ChipTemplate,
            Background = colour is SolidColorBrush { Color: var c } ? Frozen(0x66, c.R, c.G, c.B) : NoneFill,
            BorderBrush = isCurrent ? CurrentEdge : Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(7, 1, 7, 2),
            Margin = new Thickness(0, 0, 3, 3),
            Cursor = Cursors.Hand,
            Focusable = true,
        };
        AutomationProperties.SetName(button, name);
        if (isCurrent) AutomationProperties.SetItemStatus(button, CurrentStatus);
        button.Click += (_, e) =>
        {
            e.Handled = true;
            pick();
        };
        return button;
    }

    // The widest a chip's name may be in the menu: about twenty-four characters at its 11 DIP text, wider than any
    // workspace name Petre uses, narrow enough that a chip always fits the menu's width.
    internal const double ChipTextCeiling = 160;

    // The chip's look: a rounded fill with the button's edge, a little lighter under the pointer, a white edge while
    // the keyboard is on it. No focus rectangle of WPF's own, which would draw a dotted box outside the round chip.
    static readonly ControlTemplate ChipTemplate = Sealed((ControlTemplate)XamlReader.Parse(
        """
        <ControlTemplate TargetType="Button" xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Border x:Name="Face" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                  BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="8" Padding="{TemplateBinding Padding}">
            <ContentPresenter VerticalAlignment="Center" />
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
              <Setter TargetName="Face" Property="Opacity" Value="0.85" />
            </Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True">
              <Setter TargetName="Face" Property="BorderBrush" Value="White" />
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
        """));

    static ControlTemplate Sealed(ControlTemplate template)
    {
        template.Seal();
        return template;
    }

    // The height of the work area of the screen the menu opens on: the screen holding its placement target, in that
    // screen's DIPs. SystemParameters.WorkArea is the primary screen's, which is the wrong bound for a bar on a shorter
    // second screen. With no target on screen, the primary's it is.
    static double WorkAreaHeightAt(UIElement? target)
    {
        if (target is null || PresentationSource.FromVisual(target) is null) return SystemParameters.WorkArea.Height;
        var at = target.PointToScreen(new Point(0, 0));
        var monitor = Windows.Monitoring.NativeMethods.MonitorFromPoint(new Windows.Monitoring.NativeMethods.POINT { X = (int)at.X, Y = (int)at.Y },
            Windows.Monitoring.NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new Windows.Monitoring.NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Windows.Monitoring.NativeMethods.MONITORINFO>() };
        if (!Windows.Monitoring.NativeMethods.GetMonitorInfo(monitor, ref info)) return SystemParameters.WorkArea.Height;
        Windows.Monitoring.NativeMethods.GetDpiForMonitor(monitor, Windows.Monitoring.NativeMethods.MDT_EFFECTIVE_DPI, out _, out var dpiY);
        return (info.rcWork.Bottom - info.rcWork.Top) / (dpiY / 96.0);
    }

    // A palette hex as a brush; none (opted out of colour) or an unreadable hex (a hand-edited state.json) as null.
    static Brush? BrushOf(string hex)
    {
        if (WorkspacePalette.IsNone(hex)) return null;
        try
        {
            var colour = (Color)ColorConverter.ConvertFromString(hex);
            return Frozen(colour.A, colour.R, colour.G, colour.B);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    static Brush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
