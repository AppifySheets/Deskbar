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
}
