using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Deskbar.App;
using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;

namespace Deskbar.Windows.Tests;

// RECENT WORKSPACES KEEP THEIR COLOUR (FloatingBar.Recency.cs). Petre: "i get lost in so many workspaces", then
// "b gradual", "let's fade windows not based on individual age, but the age of the workspace", and "let's also fade
// the rails on the left, so i don't get tempted to look at them". A row's rail, tint and name fade with the time
// since any of its windows was last used. Design: docs/superpowers/specs/2026-11-22-recent-workspaces-design.md.
public class FloatingBarRecencyTests
{
    static Color ColourOf(Brush? brush) => Assert.IsType<SolidColorBrush>(brush).Color;

    // --- the brushes ------------------------------------------------------------------------------------------

    [Fact]
    public void A_fresh_rail_is_the_workspace_colour() =>
        Assert.Equal(Color.FromRgb(0x9B, 0x6C, 0xF0), ColourOf(FloatingBar.FadedAccent("#9B6CF0", 0, 0)));

    // Grey AND faint: a grey stripe at full strength would still draw the eye down every idle row.
    [Fact]
    public void A_fully_faded_rail_is_a_faint_grey() =>
        Assert.Equal(Color.FromArgb(0x30, 0x4A, 0x4E, 0x58), ColourOf(FloatingBar.FadedAccent("#9B6CF0", 0, 16)));

    [Fact]
    public void A_row_with_no_colour_has_no_rail_at_any_age() =>
        Assert.Null(FloatingBar.FadedAccent(WorkspacePalette.None, 0, 5));

    // THE PLATE (Petre picked "b" from the grain mockup): the bar stands on grain (BarSurface), and a workspace row
    // you used lately sits on a smooth plate of the bar's flat base, with its tint mixed in, that wears away as the
    // row fades. A fresh row is smooth; an idle one has sunk into the grain.
    static Color Over(Color under, Color tint, byte alpha) =>
        Color.FromRgb(
            (byte)Math.Round(under.R + (tint.R - under.R) * alpha / 255.0),
            (byte)Math.Round(under.G + (tint.G - under.G) * alpha / 255.0),
            (byte)Math.Round(under.B + (tint.B - under.B) * alpha / 255.0));

    static readonly Color Purple = Color.FromRgb(0x9B, 0x6C, 0xF0);

    // Fresh: fully opaque, so no grain shows, and coloured by a third of the current row's 0x38.
    [Fact]
    public void A_fresh_row_stands_on_a_smooth_plate_with_a_third_of_the_current_tint() =>
        Assert.Equal(Over(BarSurface.Base, Purple, 0x13), ColourOf(FloatingBar.Plate("#9B6CF0", 0, 0, current: false)));

    // The row you are on: smooth too, with the full 0x38 tint it has always had.
    [Fact]
    public void The_current_row_stands_on_a_smooth_plate_with_its_full_tint() =>
        Assert.Equal(Over(BarSurface.Base, Purple, 0x38), ColourOf(FloatingBar.Plate("#9B6CF0", 0, 16, current: true)));

    // Half way, half worn: the grain shows through at half strength.
    [Fact]
    public void Half_faded_the_plate_is_half_worn() =>
        Assert.Equal(0x80, ColourOf(FloatingBar.Plate("#9B6CF0", 0, 8, current: false)).A);

    // Idle: no plate at all, the row is the bar's grain.
    [Fact]
    public void A_fully_faded_row_has_no_plate() =>
        Assert.Null(FloatingBar.Plate("#9B6CF0", 0, 16, current: false));

    // A row that opted out of colour still gets the plate, plain: being recent is about the grain, not the colour.
    [Fact]
    public void A_row_with_no_colour_gets_a_plain_plate() =>
        Assert.Equal(BarSurface.Base, ColourOf(FloatingBar.Plate(WorkspacePalette.None, 0, 0, current: false)));

    // From today's resting white down to a floor that every name stays readable at.
    [Fact]
    public void The_caption_dims_from_resting_to_its_floor()
    {
        Assert.Equal(0xA8, ColourOf(FloatingBar.RestingCaption(0)).A);
        Assert.Equal(0x6B, ColourOf(FloatingBar.RestingCaption(16)).A);
    }

    // A group's name dims by the same ratio as a workspace's.
    [Fact]
    public void A_group_header_dims_by_the_same_ratio()
    {
        Assert.Equal(0xB0, ColourOf(FloatingBar.GroupHeaderAt(0)).A);
        Assert.Equal((byte)Math.Round(0xB0 * 0x6B / (double)0xA8), ColourOf(FloatingBar.GroupHeaderAt(16)).A);
    }

    // --- the bar ----------------------------------------------------------------------------------------------
    //
    // Harness: GEPHA (current, the rdm window) and Sparrow (the beeper window). Opening a window does not stamp it, so
    // Build gives both windows a T0 stamp: both are used at T0, and moving the clock two days on and redrawing makes
    // BOTH workspaces two days old.

    static readonly DateTimeOffset T0 = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);
    static readonly Color FaintGrey = Color.FromArgb(0x30, 0x4A, 0x4E, 0x58);

    sealed class Clock { public DateTimeOffset Now = T0; }

    static (Harness Harness, Clock Clock) Build(bool grouped = false)
    {
        var clock = new Clock();
        var harness = Harness.Build(grouped: grouped, clock: () => clock.Now);
        // Stamped as a restart restores them: focusing Sparrow's window here would also make Sparrow current.
        new[] { harness.First, harness.Second }.ToList()
            .ForEach(w => harness.Manager.RestoreLastUsed(w.Handle, w.ProcessId, T0));
        return (harness, clock);
    }

    // Two days on, then a window event to redraw (activating GEPHA's own window, which is current anyway).
    static void TwoDaysLater(Harness harness, Clock clock)
    {
        clock.Now = T0.AddDays(2);
        harness.Monitor.Pump.OnNext(new WindowEvent(WindowEventKind.Activated, harness.First));
    }

    static IReadOnlyList<DependencyObject> Tree(DependencyObject root) =>
        [root, .. LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>().SelectMany(Tree)];

    // A row: the bar's top-level Border whose tree holds a TextBlock reading exactly the workspace's name.
    static DependencyObject Row(BarUnderTest bar, string name) =>
        bar.Rows.Children.OfType<Border>().Single(b => Tree(b).OfType<TextBlock>().Any(t => t.Text == name));

    // The rail: the 3 DIP spine down the row's left edge.
    static Color RailOf(BarUnderTest bar, string name) =>
        ColourOf(Tree(Row(bar, name)).OfType<Border>().Single(b => b.Width == 3).Background);

    static Brush CaptionOf(BarUnderTest bar, string name) =>
        Tree(Row(bar, name)).OfType<TextBlock>().First(t => t.Text == name).Foreground;

    // Every faint background in the row, the tint if there is one. Capped at 0x38, the strongest tint there is (the
    // current row's), because the icons' own chrome carries near-opaque backgrounds that are no tint at all.
    static IReadOnlyList<Color> TintsOf(BarUnderTest bar, string name) =>
        [.. Tree(Row(bar, name)).OfType<Panel>().Select(p => p.Background)
            .Concat(Tree(Row(bar, name)).OfType<Border>().Where(b => b.Width != 3).Select(b => b.Background))
            .OfType<SolidColorBrush>().Select(b => b.Color).Where(c => c.A is > 0 and <= 0x38)];

    // Every solid background in the row, opaque ones included (the plate).
    static IReadOnlyList<Color> BackgroundsOf(BarUnderTest bar, string name) =>
        [.. Tree(Row(bar, name)).OfType<Panel>().Select(p => p.Background)
            .OfType<SolidColorBrush>().Select(b => b.Color)];

    [Fact]
    public void An_idle_workspace_row_is_drawn_faded() => StaThread.Run(() =>
    {
        var (harness, clock) = Build();
        using var bar = harness.ShowBar();
        TwoDaysLater(harness, clock);

        Assert.Equal(FaintGrey, RailOf(bar, "Sparrow"));
        Assert.Equal(0x6B, ColourOf(CaptionOf(bar, "Sparrow")).A);
        Assert.Empty(TintsOf(bar, "Sparrow"));
        // No plate: nothing opaque under the row, so it stands on the bar's grain.
        Assert.DoesNotContain(BackgroundsOf(bar, "Sparrow"), c => c.A == 0xFF);
    });

    // Being on it is using it: the current row's windows are two days old too, and it is drawn as it always was.
    [Fact]
    public void The_current_row_is_not_faded_however_old() => StaThread.Run(() =>
    {
        var (harness, clock) = Build();
        using var bar = harness.ShowBar();
        var railBefore = RailOf(bar, "GEPHA");
        var captionBefore = ColourOf(CaptionOf(bar, "GEPHA"));
        TwoDaysLater(harness, clock);

        Assert.Equal(railBefore, RailOf(bar, "GEPHA"));
        Assert.Equal(0xFF, RailOf(bar, "GEPHA").A);
        Assert.Equal(captionBefore, ColourOf(CaptionOf(bar, "GEPHA")));
    });

    [Fact]
    public void A_fresh_row_keeps_its_colour_and_a_faint_tint() => StaThread.Run(() =>
    {
        var (harness, _) = Build();
        using var bar = harness.ShowBar();

        var rail = RailOf(bar, "Sparrow");
        Assert.Equal(0xFF, rail.A);
        Assert.NotEqual(FaintGrey with { A = 0xFF }, rail);
        // The row stands on its smooth plate: the bar's base with a third of the current row's tint, fully opaque.
        Assert.Contains(BackgroundsOf(bar, "Sparrow"), c => c == Over(BarSurface.Base, rail, 0x13));
        Assert.Equal(0xA8, ColourOf(CaptionOf(bar, "Sparrow")).A);
    });

    // A folded row has no icons, but its name still says how long it has been.
    [Fact]
    public void A_folded_idle_row_dims_its_name() => StaThread.Run(() =>
    {
        var (harness, clock) = Build();
        using var bar = harness.ShowBar();
        var sparrow = harness.Manager.State.Workspaces.Single(w => w.Name == "Sparrow");
        Assert.True(harness.Manager.SetWorkspaceFolded(sparrow.Id, true).IsSuccess);
        TwoDaysLater(harness, clock);

        Assert.Equal(0x6B, ColourOf(CaptionOf(bar, "Sparrow")).A);
    });

    [Fact]
    public void A_row_asking_for_you_keeps_the_asking_colour() => StaThread.Run(() =>
    {
        var (harness, clock) = Build();
        using var bar = harness.ShowBar();
        var sparrow = harness.Manager.State.Workspaces.Single(w => w.Name == "Sparrow");
        TwoDaysLater(harness, clock);
        Assert.True(harness.Manager.RequestAttention(sparrow.Id, CSharpFunctionalExtensions.Maybe<WindowHandle>.None).Value);

        Assert.Same(bar.Bar.AskingCaption, CaptionOf(bar, "Sparrow"));
    });

    // A workspace whose windows have only opened, never been focused, is idle from the start: after a Windows restart
    // every window comes back at once, and none of that is use (Petre: "all bars still have full color").
    [Fact]
    public void A_workspace_never_focused_is_drawn_faded() => StaThread.Run(() =>
    {
        var harness = Harness.Build(clock: () => T0);
        using var bar = harness.ShowBar();

        Assert.Equal(FaintGrey, RailOf(bar, "Sparrow"));
    });

    // Using a window in it again brings the colour straight back.
    [Fact]
    public void A_workspace_used_again_regains_its_colour() => StaThread.Run(() =>
    {
        var (harness, clock) = Build();
        using var bar = harness.ShowBar();
        TwoDaysLater(harness, clock);
        Assert.Equal(FaintGrey, RailOf(bar, "Sparrow"));

        harness.Monitor.Pump.OnNext(new WindowEvent(WindowEventKind.Activated, harness.Second));

        Assert.Equal(0xFF, RailOf(bar, "Sparrow").A);
    });

    // A grouped member's rail fades like any other row's (Petre's bar, 2026-11-22: every grouped row's rail was still
    // drawn at full strength, even on workspaces with no windows at all).
    // Petre's bar keeps its window list on the screens (WindowListPlace.Screens), so the rows carry no icons.
    [Theory]
    [InlineData(WindowListPlace.Row)]
    [InlineData(WindowListPlace.Screens)]
    public void An_idle_grouped_rows_rail_is_faded(WindowListPlace place) => StaThread.Run(() =>
    {
        var (harness, clock) = Build(grouped: true);
        Assert.True(harness.Manager.SetWindowListPlace(place).IsSuccess);
        using var bar = harness.ShowBar();
        TwoDaysLater(harness, clock);

        var idle = harness.Manager.State.Workspaces.Where(w => w.GroupId is not null).Select(w => w.Name).ToList();
        Assert.NotEmpty(idle);
        // The family is one top-level element: the group's line and each member's rail, all idle, all faint.
        idle.ForEach(name => Assert.All(Tree(Row(bar, name)).OfType<Border>().Where(b => b.Width == 3),
            rail => Assert.Equal(FaintGrey, ColourOf(rail.Background))));
    });

    // A group's line and name follow its freshest member: with both members idle, both are faded.
    [Fact]
    public void An_idle_groups_name_dims() => StaThread.Run(() =>
    {
        var (harness, clock) = Build(grouped: true);
        using var bar = harness.ShowBar();
        TwoDaysLater(harness, clock);

        var header = bar.Rows.Children.OfType<DependencyObject>().SelectMany(Tree).OfType<TextBlock>().First(t => t.Text == "TEAM");
        Assert.Same(FloatingBar.GroupHeaderAt(16), header.Foreground);
    });
}
