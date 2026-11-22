using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Deskbar.App;
using Deskbar.Core.Domain;

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

    // A third of the current row's 0x38: a recent row is coloured, and still nowhere near the row you are on.
    [Fact]
    public void A_fresh_row_has_a_third_of_the_current_tint() =>
        Assert.Equal(0x13, ColourOf(FloatingBar.FadedTint("#9B6CF0", 0, 0)).A);

    [Fact]
    public void A_fully_faded_row_has_no_tint() =>
        Assert.Null(FloatingBar.FadedTint("#9B6CF0", 0, 16));

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
    // Harness: GEPHA (current, the rdm window) and Sparrow (the beeper window). Every window is stamped at T0 when the
    // manager first sees it, so moving the clock two days on and redrawing makes BOTH workspaces two days old.

    static readonly DateTimeOffset T0 = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);
    static readonly Color FaintGrey = Color.FromArgb(0x30, 0x4A, 0x4E, 0x58);

    sealed class Clock { public DateTimeOffset Now = T0; }

    static (Harness Harness, Clock Clock) Build(bool grouped = false)
    {
        var clock = new Clock();
        return (Harness.Build(grouped: grouped, clock: () => clock.Now), clock);
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

    [Fact]
    public void An_idle_workspace_row_is_drawn_faded() => StaThread.Run(() =>
    {
        var (harness, clock) = Build();
        using var bar = harness.ShowBar();
        TwoDaysLater(harness, clock);

        Assert.Equal(FaintGrey, RailOf(bar, "Sparrow"));
        Assert.Equal(0x6B, ColourOf(CaptionOf(bar, "Sparrow")).A);
        Assert.Empty(TintsOf(bar, "Sparrow"));
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

        Assert.Equal(0xFF, RailOf(bar, "Sparrow").A);
        Assert.NotEqual(FaintGrey with { A = 0xFF }, RailOf(bar, "Sparrow"));
        Assert.Contains(TintsOf(bar, "Sparrow"), c => c.A == 0x13);
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
