using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;
using Deskbar.Core.Geometry;
using Deskbar.Core.Persistence;
using Deskbar.Core.Tasks;

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
// Tab and Shift+Tab walk them in a cycle, the arrow keys move by where the chips are drawn, across group captions
// (ChipGrid, task f94524ce), Enter or Space picks, Esc closes. The menu opens with the keyboard on the current choice, which keeps its blue outline. Typing a name's
// first letters moves the keyboard to that chip (TypeAheadMatch, task 6184e8bf), and a held Enter's repeats pick nothing.
//
// RECENT FIRST (task 46eddcb0, Petre: "show recent workspaces at the top and let me select from them first, so i don't
// have to go through unused workspaces with a keyboard"). Given the tasks, the menu opens with a "Recent" row above
// everything else: the workspaces of the newest tasks (RecentTaskWorkspaces, which says why that measure), each drawn
// as its chip, with a hairline under the row. The same workspaces stay in their usual places below as well, so the
// bar's order Petre knows by heart does not shift each time he adds a task. The Recent row comes first in the menu's
// order, so the keyboard starts there (on the current choice when that is recent, else on the row's first chip), the
// arrows reach it first, and type to jump finds a recent workspace before its twin further down.
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
    // A little lighter than the task list's #999, so the "none" chip's name clears 4.5:1 on its grey (it was 4.4).
    static readonly Brush NoneInk = Frozen(0xFF, 0xA3, 0xA3, 0xA3);
    static readonly Brush CurrentEdge = Frozen(0xFF, 0x4F, 0x8C, 0xFF);

    // THE MENU'S OWN SURFACE (ruling g, 2026-11-23). The chips were designed on the bar's dark surface: lane colours at
    // 0x66 alpha with near-white names. Left to the system's ContextMenu template under ThemeMode="System", a light
    // Windows theme drew them on #F3F3F3 with an icon gutter down the menu, and Petre could barely read the names. So
    // the menu brings its own template (SurfaceTemplate): the tasks window's dark page and edge, no gutter, whatever the
    // theme. On it every palette chip's name is at least 4.65:1 (WorkspaceChipMenuTests checks every swatch).
    internal static readonly Brush Surface = Frozen(0xFF, 0x1E, 0x1F, 0x24);
    static readonly Brush SurfaceEdge = Frozen(0xFF, 0x33, 0x35, 0x3C);

    /// <summary>
    /// One entry of the menu: a group's caption (<see cref="IsGroup"/>, never clickable), or a choice of a workspace or
    /// of none. <see cref="Colour"/> is the lane colour, null for none and for a workspace without one.
    /// <see cref="InGroup"/> marks a workspace that belongs to the caption before it. <see cref="Recent"/> marks the
    /// "Recent" caption and its chips (task 46eddcb0), which the menu draws above everything else.
    /// </summary>
    internal sealed record Entry(bool IsGroup, Maybe<Guid> Workspace, string Name, Brush? Colour, bool InGroup = false, bool Recent = false);

    /// <summary>The caption over the menu's Recent row.</summary>
    internal const string RecentCaption = "Recent";

    /// <summary>
    /// A chip that is not a workspace, placed first (the window's workspace filter has "All workspaces"): its words,
    /// whether it is the current choice, and what picking it does.
    /// </summary>
    internal sealed record Extra(string Name, bool Current, Action Pick);

    /// <summary>
    /// The menu's entries in the bar's order, with "none" first when <paramref name="withNone"/>. A workspace
    /// <paramref name="include"/> turns down is left out, and a group with no member left loses its caption too.
    /// Given <paramref name="tasks"/>, the entries start with the Recent row: its caption and the workspaces of the
    /// newest tasks (<see cref="RecentTaskWorkspaces"/>), which then appear again in their usual places.
    /// </summary>
    internal static IReadOnlyList<Entry> Entries(AppState state, bool withNone, Func<Workspace, bool>? include = null,
        IEnumerable<TaskItem>? tasks = null)
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
        var all = withNone ? lines.Prepend(new Entry(false, Maybe<Guid>.None, "none", null)) : lines;
        return RecentRow(state, shown, tasks).Concat(all).ToArray();
    }

    // The Recent row's entries: its caption, then a chip per recent workspace in the order RecentTaskWorkspaces gives
    // (newest first). Nothing at all without tasks, or when no task has a workspace still on the menu, so the menu
    // never shows a caption over an empty row.
    static IReadOnlyList<Entry> RecentRow(AppState state, Func<Workspace, bool> shown, IEnumerable<TaskItem>? tasks)
    {
        if (tasks is null) return [];
        var byId = state.Workspaces.ToDictionary(w => w.Id);
        var recent = RecentTaskWorkspaces.Of(tasks, id => byId.TryGetValue(id, out var w) && shown(w))
            .Select(id => byId[id])
            .Select(w => new Entry(false, w.Id, w.Name, BrushOf(LaneColour.Of(state, w)), InGroup: true, Recent: true))
            .ToArray();
        return recent.Length == 0 ? [] : recent.Prepend(new Entry(true, Maybe<Guid>.None, RecentCaption, null, Recent: true)).ToArray();
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
            // Its own dark surface and template, never the theme's (see Surface).
            Template = SurfaceTemplate,
            Background = Surface,
            BorderBrush = SurfaceEdge,
            Foreground = Ink,
        };
        // Tab and Shift+Tab walk the chips in a cycle (see the header). The arrows never reach WPF's directional
        // navigation: the PreviewKeyDown handler below answers them with ChipGrid. The setting stays as the fallback
        // should that handler ever let an arrow through.
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
        // Each caption with the members that follow it. The Recent row (task 46eddcb0) goes above everything, the
        // block of extras, "none" and the ungrouped workspaces included, and carries a hairline under it: that next
        // block has no caption of its own, so without the line its chips would read as more of the Recent row.
        var captioned = entries.Select((entry, at) => (entry, at)).Where(x => x.entry.IsGroup)
            .Select(x => (x.entry, block: Block(x.entry,
                entries.Skip(x.at + 1).TakeWhile(e => e.InGroup).Select(e => Choice(e, current, pick, menu)).ToArray(), inner,
                atTop: x.entry.Recent, ruled: x.entry.Recent)))
            .ToArray();
        captioned.Where(x => x.entry.Recent).ToList().ForEach(x => menu.Items.Add(x.block));
        if (first.Length > 0) menu.Items.Add(Block(null, first, inner));
        captioned.Where(x => !x.entry.Recent).ToList().ForEach(x => menu.Items.Add(x.block));

        // The keyboard starts on the current choice, or on the first chip.
        menu.Opened += (_, _) => StartChip(menu).Tap(chip => chip.Focus());

        // A held Enter repeats, and a repeat reaching a chip would pick it: the add box's Enter opens this menu (task
        // 6184e8bf), so the same press held a moment too long would add the note to whichever chip the menu opened on
        // without Petre choosing. Only a fresh press picks.
        menu.PreviewKeyDown += (_, e) =>
        {
            if (e.IsRepeat && e.Key is Key.Enter or Key.Return) e.Handled = true;
        };

        // THE ARROWS, answered here (task f94524ce, Petre: "can't go to other grouped workspace, can't go to deskbar
        // from here with the keyboard"). Left to WPF they stopped at a block's edge, so no arrow left a group. ChipGrid
        // decides from where the chips are drawn: Left and Right step through every chip in order, Up and Down go to
        // the nearest chip on the line above or below, whichever block it is in, both wrapping at the ends. Held arrows
        // repeat on purpose. The chip the key came from is the event's source (the keyboard is on it); a key with no
        // chip under it, the menu having opened without the keyboard, puts the keyboard on the start chip first.
        menu.PreviewKeyDown += (_, e) =>
        {
            if (StepOf(e.Key) is not { } step) return;
            (ChipOf(menu, e.OriginalSource) is { HasValue: true } from ? ArrowTarget(menu, from.Value, step) : StartChip(menu))
                .Tap(chip =>
                {
                    chip.Focus();
                    // The menu scrolls when it is taller than the screen; keep the chip the keyboard is on in sight.
                    chip.BringIntoView();
                });
            e.Handled = true;
        };

        // TYPE TO JUMP (task 6184e8bf: a workspace "which i can select with the keyboard"). With twenty-odd workspaces
        // the arrows take many presses, so letters typed while the menu is open move the keyboard to the first chip
        // whose name starts with them, "sp" to Sparrow; after a pause of TypeAheadPause the next letter starts afresh.
        // Spaces are left to the chips, where Space picks. Enter then picks the chip the keyboard is on, as before.
        var typed = "";
        var typedAt = DateTime.MinValue;
        menu.PreviewTextInput += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Text) || e.Text.Any(char.IsControl)) return;
            var now = DateTime.UtcNow;
            typed = (now - typedAt > TypeAheadPause ? "" : typed) + e.Text;
            typedAt = now;
            TypeAheadMatch(menu, typed).Tap(chip => chip.Focus());
            e.Handled = true;
        };
        return menu;
    }

    /// <summary>How long a pause in typing ends the name being typed in the menu (type to jump).</summary>
    internal static readonly TimeSpan TypeAheadPause = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The chip that <paramref name="typed"/> names: the first, in the menu's order, whose name starts with it, else the
    /// first whose name holds it, ignoring case; none when no chip matches, and then the keyboard stays where it was.
    /// </summary>
    internal static Maybe<Button> TypeAheadMatch(ContextMenu menu, string typed)
    {
        var named = Chips(menu).Select(chip => (chip, name: AutomationProperties.GetName(chip))).ToArray();
        return Maybe.From(named.FirstOrDefault(x => x.name.StartsWith(typed, StringComparison.OrdinalIgnoreCase)).chip
                          ?? named.FirstOrDefault(x => x.name.Contains(typed, StringComparison.OrdinalIgnoreCase)).chip);
    }

    /// <summary>
    /// The chip the keyboard starts on when the menu opens: the current choice, or the first chip. Apart from the Opened
    /// handler so a test can check the choice without depending on the machine handing an opening popup the keyboard,
    /// which the CI runner never does (no chip had the focus there, before or after the dispatcher ran down).
    /// </summary>
    internal static Maybe<Button> StartChip(ContextMenu menu) =>
        Chips(menu) is var chips && chips.Count > 0
            ? chips.FirstOrDefault(c => AutomationProperties.GetItemStatus(c) == CurrentStatus) ?? chips[0]
            : Maybe<Button>.None;

    /// <summary>
    /// The chip an arrow key moves the keyboard to from <paramref name="from"/>, by where the chips are drawn
    /// (<see cref="ChipGrid"/>); none when the menu is not laid out, so nothing can be measured.
    /// </summary>
    internal static Maybe<Button> ArrowTarget(ContextMenu menu, Button from, ChipStep step)
    {
        var chips = Chips(menu);
        // Not laid out yet: a chip outside the menu's visual tree cannot be measured, and one with no size would put
        // every chip on a line of its own.
        if (!chips.All(c => menu.IsAncestorOf(c) && c.RenderSize is { Width: > 0, Height: > 0 })) return Maybe<Button>.None;
        var boxes = chips
            .Select(c => c.TransformToAncestor(menu).TransformBounds(new Rect(c.RenderSize)))
            .Select(r => new ChipBox(r.Left, r.Top, r.Width, r.Height))
            .ToArray();
        return ChipGrid.Next(boxes, chips.ToList().IndexOf(from), step).Map(at => chips[at]);
    }

    // The arrow keys the menu answers itself; any other key is left alone.
    static ChipStep? StepOf(Key key) => key switch
    {
        Key.Left => ChipStep.Left,
        Key.Right => ChipStep.Right,
        Key.Up => ChipStep.Up,
        Key.Down => ChipStep.Down,
        _ => null,
    };

    // The chip a key event came from: the chip itself, or the chip holding the element it came from.
    static Maybe<Button> ChipOf(ContextMenu menu, object source) =>
        source is Visual visual
            ? Maybe.From(Chips(menu).FirstOrDefault(chip => chip == visual || chip.IsAncestorOf(visual)))
            : Maybe<Button>.None;

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
    // What the menu's template draws around its items: a 1 DIP border either side, and room to spare, so the blocks
    // still fit when the scroll viewer's vertical bar shows on a long menu (its horizontal one is disabled).
    const double TemplateEdge = 8;

    // A block at the top of the menu (the Recent row) drops the 6 DIP above its caption, which only separates a block
    // from the one above it; a ruled block ends in a hairline in the menu's edge colour. The hairline is no WrapPanel,
    // so Chips never mistakes it for chips.
    static StackPanel Block(Entry? caption, IReadOnlyList<Button> chips, double width, bool atTop = false, bool ruled = false)
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
                Margin = new Thickness(1, atTop ? 0 : 6, 0, 2),
                IsHitTestVisible = false,
                Focusable = false,
            });
        var wrap = new WrapPanel { Width = width };
        chips.ToList().ForEach(chip => wrap.Children.Add(chip));
        block.Children.Add(wrap);
        if (ruled) block.Children.Add(new Border { Height = 1, Background = SurfaceEdge, Margin = new Thickness(0, 1, 0, 4), IsHitTestVisible = false });
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

    // The menu's whole look (see Surface): a rounded dark border, its padding inside, and the scroll viewer that keeps
    // a long menu inside the work area (MaxHeight) with its horizontal scrolling off, so the blocks wrap at the width
    // they are given (review I-1) instead of growing sideways. No icon gutter, no separator column, nothing from the
    // theme: the system template is what drew the light surface and the stripe.
    static readonly ControlTemplate SurfaceTemplate = Sealed((ControlTemplate)XamlReader.Parse(
        """
        <ControlTemplate TargetType="ContextMenu" xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
          <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1"
                  CornerRadius="6" Padding="{TemplateBinding Padding}">
            <ScrollViewer HorizontalScrollBarVisibility="Disabled" VerticalScrollBarVisibility="Auto" CanContentScroll="False">
              <ItemsPresenter />
            </ScrollViewer>
          </Border>
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
