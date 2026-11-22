using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;

namespace Deskbar.App;

// THE TASK LIST'S WORKSPACE MENUS, one builder for all of them: the add box's "other…", a line's workspace chip (move
// the task) and the tasks window's "Move to". Petre, at the live check, of a long plain list of names: "when selecting,
// give them their formatting and colors". So each item is drawn as the workspace's chip (its name on its own lane
// colour, the rounded shape and text of the chips on the task lines), the items come in the bar's order (the pinned
// rows first, then the rest, as WorkspaceManager.DrawnBlocks and the bar's rebuild walk them), and the members of a
// group sit under the group's caption, its name in the group's colour, which cannot be clicked. "none" comes first
// when the menu offers it, in the neutral chip; the current choice is ticked.
//
// The colours are LaneColour's, the resolution the switcher and quick find share with the bar: a group's colour for its
// members, a workspace's own by its defined position otherwise. A workspace that opted out of colour gets the neutral
// chip.
//
// A LONG MENU (Petre has about twenty workspaces) must not run off the screen. WPF's ContextMenu templates put the
// items in a ScrollViewer, and a popup is kept on its screen, so a menu taller than the screen scrolls; MaxHeight is
// set as well, to the height of the work area of the screen it opens on, so the ScrollViewer has a bound even where the popup's own constraint does
// not reach it (checked by WorkspaceChipMenuTests on the template the tests load; the Fluent template the app runs has the
// same ScrollViewer).
internal static class WorkspaceChipMenu
{
    /// <summary>The Tag of a group's caption in the menu, for the tests.</summary>
    internal const string GroupTag = "workspace-group";

    // The chips' fills and inks, the task list's own (FloatingBar.TaskList.cs), so the menu looks like the lines.
    static readonly Brush Ink = Frozen(0xFF, 0xE6, 0xE6, 0xE6);
    static readonly Brush NoneFill = Frozen(0xFF, 0x33, 0x33, 0x33);
    static readonly Brush NoneInk = Frozen(0xFF, 0x99, 0x99, 0x99);

    /// <summary>
    /// One line of the menu: a group's caption (<see cref="IsGroup"/>, never clickable), or a choice of a workspace or
    /// of none. <see cref="Colour"/> is the lane colour, null for none and for a workspace without one.
    /// </summary>
    internal sealed record Entry(bool IsGroup, Maybe<Guid> Workspace, string Name, Brush? Colour);

    /// <summary>
    /// The menu's lines in the bar's order, with "none" first when <paramref name="withNone"/>. A workspace
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
            var choices = members.Select(w => new Entry(false, w.Id, w.Name, BrushOf(LaneColour.Of(state, w))));
            return group is null || members.Length == 0
                ? choices
                : choices.Prepend(new Entry(true, Maybe<Guid>.None, group.Name,
                    BrushOf(WorkspacePalette.For(group.Color, Math.Max(0, state.ColourSlotOf(group))))));
        });
        return (withNone ? lines.Prepend(new Entry(false, Maybe<Guid>.None, "none", null)) : lines).ToArray();
    }

    /// <summary>
    /// Fills <paramref name="menu"/> (a context menu, or a menu item whose submenu it is) with the entries, each choice
    /// a chip whose click calls <paramref name="pick"/>, the one equal to <paramref name="current"/> ticked (none ticks
    /// nothing when the menu has no "none").
    /// </summary>
    internal static T Fill<T>(T menu, IReadOnlyList<Entry> entries, Maybe<Guid> current, Action<Maybe<Guid>> pick) where T : ItemsControl
    {
        // A context menu is bounded by the work area; a submenu's popup is kept on the screen by WPF itself, and a
        // MaxHeight on its MenuItem would clip the item, not the submenu.
        if (menu is ContextMenu context) context.MaxHeight = WorkAreaHeightAt(context.PlacementTarget);
        entries.ToList().ForEach(entry => menu.Items.Add(entry.IsGroup ? Caption(entry) : Choice(entry, current, pick)));
        return menu;
    }

    // The height of the work area of the screen the menu opens on (fix round 1): the screen holding its placement
    // target, in that screen's DIPs. SystemParameters.WorkArea is the primary screen's, which is the wrong bound for a
    // bar on a shorter second screen. With no target on screen, the primary's it is.
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

    static MenuItem Choice(Entry entry, Maybe<Guid> current, Action<Maybe<Guid>> pick)
    {
        var item = new MenuItem { Header = Chip(entry), IsChecked = entry.Workspace.Equals(current) };
        item.Click += (_, _) => pick(entry.Workspace);
        return item;
    }

    // A group's caption: its name in its colour, small and bold like the bar's group header. Neither hit-testable nor
    // focusable, so neither the pointer nor the arrow keys can choose it.
    static MenuItem Caption(Entry entry) => new()
    {
        Tag = GroupTag,
        Header = new TextBlock { Text = entry.Name, FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = entry.Colour ?? NoneInk },
        IsHitTestVisible = false,
        Focusable = false,
    };

    // The task lines' chip (FloatingBar.WorkspaceChip): the name on its colour, faded so the name stays readable, or
    // the grey "none". A TextBlock rather than a string, so an underscore in a name is not read as an access key.
    static Border Chip(Entry entry) => new()
    {
        Tag = "workspace-chip",
        Background = entry.Workspace.HasValue && entry.Colour is SolidColorBrush { Color: var c } ? Frozen(0x66, c.R, c.G, c.B)
            : NoneFill,
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(8, 1, 8, 2),
        Child = new TextBlock { Text = entry.Name, Foreground = entry.Workspace.HasValue ? Ink : NoneInk },
    };

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
