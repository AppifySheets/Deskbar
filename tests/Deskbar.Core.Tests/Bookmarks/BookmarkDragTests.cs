using Deskbar.Core.Bookmarks;

namespace Deskbar.Core.Tests.Bookmarks;

// Where a chip sits (its screen's lane), which strips show it, and what dropping a dragged chip does and says.
public sealed class BookmarkDragTests
{
    static readonly Guid Work = Guid.NewGuid();
    static readonly Guid Home = Guid.NewGuid();

    static Bookmark Tool(int? screen = null) => new(Guid.NewGuid(), "Tool", BookmarkKind.Program, @"C:\t\tool.exe", Screen: screen);
    static Bookmark Site() => BookmarkRules.Link("https://portainer.local/").Value;

    // Rank 0 is display 2 and rank 1 display 1 (reading order need not follow Windows' numbers).
    static readonly IReadOnlyDictionary<int, int> Lanes = new Dictionary<int, int> { [0] = 2, [1] = 1 };

    [Fact]
    public void A_chip_sits_in_its_screens_lane_or_else_the_first()
    {
        Assert.Equal(1, ChipLayout.LaneOf(Tool(screen: 1), Lanes, firstRank: 0));
        Assert.Equal(0, ChipLayout.LaneOf(Tool(screen: 2), Lanes, firstRank: 0));
        Assert.Equal(0, ChipLayout.LaneOf(Tool(), Lanes, firstRank: 0));
        // A screen no longer connected, or a bar with no screen lanes at all.
        Assert.Equal(0, ChipLayout.LaneOf(Tool(screen: 3), Lanes, firstRank: 0));
        Assert.Equal(0, ChipLayout.LaneOf(Tool(screen: 1), new Dictionary<int, int>(), firstRank: 0));
    }

    [Fact]
    public void A_chip_with_a_screen_shows_only_on_that_screens_strip()
    {
        var strips = new HashSet<int> { 1, 2 };

        Assert.True(ChipLayout.OnStrip(Tool(screen: 2), 2, strips));
        Assert.False(ChipLayout.OnStrip(Tool(screen: 2), 1, strips));
        Assert.True(ChipLayout.OnStrip(Tool(), 1, strips));
        Assert.True(ChipLayout.OnStrip(Tool(), 2, strips));
        // Its screen has no strip now: every strip shows it, as for no screen.
        Assert.True(ChipLayout.OnStrip(Tool(screen: 3), 1, strips));
        Assert.True(ChipLayout.OnStrip(Tool(screen: 3), 2, strips));
    }

    // Chips drawn with the same picture get the windows' ordinal band: 1, 2, ... in bar order among those without a
    // colour of their own; a unique picture, an unknown one or an own colour gets none.
    [Fact]
    public void Chips_that_look_alike_are_numbered_in_bar_order_skipping_own_colours()
    {
        var portainerA = BookmarkRules.Link("https://100.65.0.11:9443/", "Portainer A").Value;
        var coloured = BookmarkRules.Link("https://100.65.0.9:9443/", "Portainer C").Value with { Color = "#2E86DE" };
        var portainerB = BookmarkRules.Link("https://100.65.0.8:9443/", "Portainer B").Value;
        var github = BookmarkRules.Link("https://github.com/", "GitHub").Value;
        var loading = BookmarkRules.Link("https://slow.example/", "Slow").Value;
        var keys = new Dictionary<Guid, string?>
        {
            [portainerA.Id] = "portainer", [coloured.Id] = "portainer", [portainerB.Id] = "portainer",
            [github.Id] = "github", [loading.Id] = null,
        };

        var ordinals = ChipLayout.Ordinals([portainerA, github, coloured, loading, portainerB], chip => keys[chip.Id]);

        Assert.Equal(new Dictionary<Guid, int> { [portainerA.Id] = 1, [portainerB.Id] = 2 }, ordinals);
    }

    [Fact]
    public void A_chip_that_shares_its_picture_only_with_a_coloured_chip_still_gets_a_band()
    {
        var plain = Tool();
        var coloured = Tool() with { Color = "#2E86DE" };

        Assert.Equal(new Dictionary<Guid, int> { [plain.Id] = 1 }, ChipLayout.Ordinals([coloured, plain], _ => "same"));
        Assert.Empty(ChipLayout.Ordinals([plain], _ => "same"));
    }

    [Fact]
    public void On_its_own_row_a_screen_half_sets_the_screen()
    {
        var drop = BookmarkDrag.Plan(Tool(), Work, Work, "Work", aimed: 2, targetHasSame: false);

        Assert.True(drop.Allowed);
        Assert.Null(drop.To);
        Assert.Equal(ScreenChange.On(2), drop.Screen);
        Assert.Equal("→ put on screen 2", drop.Readout);
    }

    [Fact]
    public void On_its_own_row_its_own_screen_or_no_aim_does_nothing()
    {
        Assert.False(BookmarkDrag.Plan(Tool(screen: 2), Work, Work, "Work", aimed: 2, targetHasSame: false).Allowed);
        Assert.False(BookmarkDrag.Plan(Tool(), Work, Work, "Work", aimed: null, targetHasSame: false).Allowed);
    }

    [Fact]
    public void A_link_is_not_put_on_a_screen_and_the_readout_says_why()
    {
        var drop = BookmarkDrag.Plan(Site(), Work, Work, "Work", aimed: 2, targetHasSame: false);

        Assert.False(drop.Allowed);
        Assert.Contains("browser", drop.Readout);
    }

    [Fact]
    public void Another_row_moves_it_there_with_the_aimed_screen_for_a_program_only()
    {
        var program = BookmarkDrag.Plan(Tool(), Work, Home, "Home", aimed: 1, targetHasSame: false);
        var link = BookmarkDrag.Plan(Site(), Work, Home, "Home", aimed: 1, targetHasSame: false);
        var unaimed = BookmarkDrag.Plan(Tool(screen: 2), Work, Home, "Home", aimed: null, targetHasSame: false);

        Assert.Equal((Home, ScreenChange.On(1), "→ move to Home, screen 1"), (program.To!.Value, program.Screen, program.Readout));
        Assert.Equal((Home, ScreenChange.Keep, "→ move to Home"), (link.To!.Value, link.Screen, link.Readout));
        Assert.Equal((Home, ScreenChange.Keep, "→ move to Home"), (unaimed.To!.Value, unaimed.Screen, unaimed.Readout));
    }

    [Fact]
    public void A_row_that_already_has_the_bookmark_refuses_it()
    {
        var drop = BookmarkDrag.Plan(Tool(), Work, Home, "Home", aimed: null, targetHasSame: true);

        Assert.False(drop.Allowed);
        Assert.Equal("can't move: Home already has this bookmark", drop.Readout);
    }
}
